using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Admin;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Workspace;

// Deleting a workspace as one durable job (design §3.4, backlog item 54), for the owner and the
// platform admin alike. Starting it does what can refuse or must happen at once; the rest is a job
// that is tried at once and again after a crash or a failed call, until it is done.
//
// Start (in the request):
// 1. ordering-api checks that nothing is still owed or being paid (sales inside the refund window
//    refuse, 409) and closes open payment pages.
// 2. catalog-api hides the programs (the suspension call), so nobody buys one from now on.
// 3. In one transaction inside the tenant: the workspace is Suspended, which closes the studio (the
//    gateway resolves memberships of active workspaces only), and the job's row is queued.
// If 1 or 2 fails nothing is queued and the caller tries again (both are idempotent).
//
// The job (RunAsync), at once and then by the WorkspaceDeletionRetries pass: an attempt first moves
// the row's next attempt forward (a lease, so a second replica leaves it alone without holding a
// database connection across the calls), then enrollment-api copies the bought programs for their
// buyers and catalog-api erases the programs and media, then one transaction removes the team and
// invites, marks the workspace Deleted with its name, branding and Stripe link cleared, and deletes
// the row. Every step is idempotent, so an attempt that stops anywhere is simply repeated.
public static partial class WorkspaceDeletions
{
    public enum Outcome
    {
        // Done: the workspace is Deleted.
        Deleted,

        // Queued; an attempt failed and the retries will finish it.
        Pending,
    }

    // Starts the deletion: null and the outcome, or why it was refused (409) or could not start (502).
    public static async Task<(Outcome? Outcome, int? Status, string? Problem)> StartAsync(
        IServiceScopeFactory scopes, Guid tenantId, Guid requestedBy, AdminAction? audit, CancellationToken cancellationToken)
    {
        DateTimeOffset now;
        await using (var scope = scopes.CreateAsyncScope())
        {
            now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            var erasure = scope.ServiceProvider.GetRequiredService<ITenantErasure>();
            var suspension = scope.ServiceProvider.GetRequiredService<ITenantSuspension>();
            try
            {
                if (await erasure.PrepareAsync(tenantId, cancellationToken) is { } refusal)
                {
                    return (null, StatusCodes.Status409Conflict, refusal);
                }

                await suspension.SetAsync(tenantId, suspended: true, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return (null, StatusCodes.Status502BadGateway, "The workspace could not be deleted yet. Nothing has changed; try again.");
            }
        }

        await scopes.InTenantAsync<TenancyDbContext>(tenantId, async db =>
        {
            await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(t => t.SetProperty(x => x.Status, TenantStatus.Suspended), cancellationToken);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO workspace_deletions (tenant_id, requested_by, requested_at, attempts, next_attempt_at)
                VALUES ({tenantId}, {requestedBy}, {now}, 0, {now})
                ON CONFLICT (tenant_id) DO NOTHING
                """, cancellationToken);
            if (audit is not null)
            {
                db.AdminActions.Add(audit);
                await db.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);

        return (await RunAsync(scopes, tenantId, cancellationToken) ? Outcome.Deleted : Outcome.Pending, null, null);
    }

    // One attempt at a queued deletion; true once the workspace is Deleted. Another attempt holding the
    // lease, or a deletion already finished, is false without doing anything.
    public static async Task<bool> RunAsync(IServiceScopeFactory scopes, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        // The backoff in SQL: 2, 4, 8 ... minutes after this attempt, at most an hour.
        var claimed = await db.Database.ExecuteSqlAsync($"""
            UPDATE workspace_deletions
            SET attempts = attempts + 1,
                next_attempt_at = {now} + least(interval '1 minute' * power(2, least(attempts + 1, 10)), interval '1 hour')
            WHERE tenant_id = {tenantId} AND next_attempt_at <= {now}
            """, cancellationToken);
        if (claimed == 0)
        {
            return false;
        }

        try
        {
            await scope.ServiceProvider.GetRequiredService<ITenantErasure>().EraseAsync(tenantId, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            var error = $"The other services could not erase their part yet ({(e.StatusCode is { } status ? (int)status : "no answer")}).";
            await db.WorkspaceDeletions.Where(d => d.TenantId == tenantId)
                .ExecuteUpdateAsync(d => d.SetProperty(x => x.LastError, error), cancellationToken);
            LogAttemptFailed(scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WorkspaceDeletions)), tenantId, e);
            return false;
        }

        await scopes.InTenantAsync<TenancyDbContext>(tenantId, async workspaces =>
        {
            await workspaces.StaffInvites.ExecuteDeleteAsync(cancellationToken);
            await workspaces.Memberships.ExecuteDeleteAsync(cancellationToken);
            var deleted = await workspaces.Tenants.SingleAsync(t => t.Id == tenantId, cancellationToken);
            deleted.Status = TenantStatus.Deleted;
            deleted.DisplayName = "Deleted workspace";
            deleted.Branding = new Branding();
            deleted.StripeAccountId = null;
            deleted.StripeOnboardingComplete = false;
            await workspaces.SaveChangesAsync(cancellationToken);
            await workspaces.WorkspaceDeletions.Where(d => d.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
        }, cancellationToken);
        return true;
    }

    // Every queued deletion whose next attempt is due, one attempt each.
    public static async Task<int> RunDueAsync(IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            due = await db.WorkspaceDeletions.AsNoTracking()
                .Where(d => d.NextAttemptAt <= now)
                .OrderBy(d => d.NextAttemptAt)
                .Select(d => d.TenantId)
                .Take(20)
                .ToListAsync(cancellationToken);
        }

        var deleted = 0;
        foreach (var tenantId in due)
        {
            if (await RunAsync(scopes, tenantId, cancellationToken))
            {
                deleted++;
            }
        }

        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting workspace {TenantId} did not finish this time; it is tried again later")]
    private static partial void LogAttemptFailed(ILogger logger, Guid tenantId, Exception exception);
}

// Retries the deletions that did not finish at once (design §3.4): a pass every minute. The service
// scales to zero, so a pending deletion continues the next time it is awake.
public sealed partial class WorkspaceDeletionRetries(IServiceScopeFactory scopes, ILogger<WorkspaceDeletionRetries> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await WorkspaceDeletions.RunDueAsync(scopes, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogPassFailed(logger, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A workspace deletion pass failed; trying again on the next tick")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
