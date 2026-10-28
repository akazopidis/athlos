using Athlos.Messaging.Contracts;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Tenants;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Payouts;

// One reminder to finish setting up payouts (design §5.3, backlog item 46), three days after the owner
// started Stripe onboarding. A row is claimed with FOR UPDATE SKIP LOCKED, like the outbox, so two
// replicas never send it twice; each enters its own tenant to read the workspace, so RLS applies. The
// reminder goes only if the workspace is active and still cannot sell; either way the row is done.
// The service scales to zero, so a reminder goes out the next time it is awake after it is due.
public sealed partial class PayoutReminders(IServiceScopeFactory scopes, ILogger<PayoutReminders> logger) : BackgroundService
{
    public static readonly TimeSpan After = TimeSpan.FromDays(3);

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RemindDueAsync(scopes, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogPassFailed(logger, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // One pass over the reminders that are due; the worker calls it on a timer, tests call it directly.
    public static async Task<int> RemindDueAsync(IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var due = await db.PayoutReminders
            .FromSql($"SELECT * FROM payout_reminders WHERE due_at <= now() ORDER BY due_at LIMIT 20 FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var reminder in due)
        {
            await scopes.InTenantAsync<TenancyDbContext>(reminder.TenantId, async workspaces =>
            {
                var workspace = await workspaces.Tenants.SingleOrDefaultAsync(t => t.Id == reminder.TenantId, cancellationToken);
                if (workspace is { Status: TenantStatus.Active, StripeOnboardingComplete: false, StripeAccountId: not null })
                {
                    WorkspaceEmails.ToOwners(workspaces, EmailTemplates.PayoutsReminder, workspace, configuration);
                    await workspaces.SaveChangesAsync(cancellationToken);
                    sent++;
                }
            }, cancellationToken);
            db.PayoutReminders.Remove(reminder);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return sent;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A payout reminder pass failed; trying again on the next tick")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
