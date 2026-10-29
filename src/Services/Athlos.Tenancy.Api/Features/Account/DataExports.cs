using System.Globalization;
using System.Text.Json;
using Athlos.Media;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Workspace;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Athlos.Tenancy.Api.Features.Account;

// Data exports as a queued job (design §5.4, backlog item 55): "Download my data" and the owner's
// workspace export no longer wait in one request, where a cold start of another service could time
// it out. Asking queues the job (202) and starts it after the response; the job gathers the same JSON
// as the direct downloads (ExportMyData, ExportWorkspace), keeps it in the private media container and
// emails the requester (data-export-ready, addressed ToUser). The file is downloaded signed in, by its
// requester only, through tenancy-api (no link to the blob), for seven days; then the DataExportJobs
// pass deletes it. A failed attempt is retried with backoff, eight times at most.
public static partial class DataExports
{
    public const int MaxAttempts = 8;

    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    public sealed record Item(
        Guid Id, DataExportKind Kind, string? Workspace, DateTimeOffset RequestedAt, string Status, DateTimeOffset? ReadyAt, DateTimeOffset? ExpiresAt);

    // POST, GET /api/v1/me/exports and GET /api/v1/me/exports/{id}/file: the signed-in user's own
    // exports. No transaction: data_exports is the service's work queue and tenants are readable by
    // every session; every query names the requester.
    public static void MapMyDataExports(this IEndpointRouteBuilder me)
    {
        me.MapPost("/exports", RequestAccount).WithName("RequestMyDataExport").WithTags("Account");
        me.MapGet("/exports", List).WithName("ListMyDataExports").WithTags("Account");
        me.MapGet("/exports/{exportId:guid}/file", Download).WithName("DownloadMyDataExport").WithTags("Account");
    }

    // POST /api/v1/studio/{slug}/workspace/exports: the owner's workspace export, in the studio's
    // tenant transaction. Staff are refused like the direct download.
    public static void MapWorkspaceDataExport(this IEndpointRouteBuilder studio) =>
        studio.MapPost("/workspace/exports", RequestWorkspace).WithName("RequestWorkspaceDataExport").WithTags("Workspace");

    private static Task<Results<Accepted<Item>, ProblemHttpResult>> RequestAccount(
        ITenantContext tenant, TenancyDbContext db, IServiceScopeFactory scopes, TimeProvider time, HttpContext http, CancellationToken cancellationToken) =>
        tenant.UserId is { } userId
            ? QueueAsync(db, scopes, http, DataExportKind.Account, userId, tenantId: null, time.GetUtcNow(), cancellationToken)
            : Task.FromResult<Results<Accepted<Item>, ProblemHttpResult>>(TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized));

    private static async Task<Results<Accepted<Item>, NotFound, ProblemHttpResult>> RequestWorkspace(
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, IServiceScopeFactory scopes, TimeProvider time, HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug && t.Status == TenantStatus.Active, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        // Inside their own tenant a member may learn the workspace exists, so 403 is fine here.
        if (tenant.Role != TenantRole.Owner || tenant.UserId is not { } userId)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can download the workspace's data.");
        }

        return await QueueAsync(db, scopes, http, DataExportKind.Workspace, userId, tenant.TenantId, time.GetUtcNow(), cancellationToken) switch
        {
            { Result: Accepted<Item> accepted } => accepted,
            { Result: ProblemHttpResult problem } => problem,
            _ => throw new InvalidOperationException("Unexpected result."),
        };
    }

    // One open export of a kind (and workspace) per requester: asking again while one is being prepared
    // answers with that one.
    private static async Task<Results<Accepted<Item>, ProblemHttpResult>> QueueAsync(
        TenancyDbContext db, IServiceScopeFactory scopes, HttpContext http, DataExportKind kind, Guid userId, Guid? tenantId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var open = await db.DataExports.AsNoTracking()
            .FirstOrDefaultAsync(e => e.RequestedBy == userId && e.Kind == kind && e.TenantId == tenantId && e.ReadyAt == null && e.FailedAt == null, cancellationToken);
        var export = open ?? new DataExport { Kind = kind, RequestedBy = userId, TenantId = tenantId, RequestedAt = now, NextAttemptAt = now };
        if (open is null)
        {
            db.DataExports.Add(export);
            await db.SaveChangesAsync(cancellationToken);
            // After the response, so the request never waits for the other services.
            http.Response.OnCompleted(() => RunAsync(scopes, export.Id, CancellationToken.None));
        }

        var slug = tenantId is null ? null : await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Slug).SingleAsync(cancellationToken);
        return TypedResults.Accepted($"/api/v1/me/exports/{export.Id}", ToItem(export, slug, now));
    }

    private static async Task<Ok<List<Item>>> List(ITenantContext tenant, TenancyDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var rows = await db.DataExports.AsNoTracking()
            .Where(e => e.RequestedBy == tenant.UserId && (e.ExpiresAt == null || e.ExpiresAt > now))
            .OrderByDescending(e => e.RequestedAt)
            .Take(10)
            .Select(e => new { Export = e, Slug = db.Tenants.Where(t => t.Id == e.TenantId).Select(t => t.Slug).FirstOrDefault() })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(rows.Select(r => ToItem(r.Export, r.Slug, now)).ToList());
    }

    // Only the requester, only while it is kept: anything else is 404.
    private static async Task<Results<FileContentHttpResult, NotFound>> Download(
        Guid exportId, ITenantContext tenant, TenancyDbContext db, MediaStore media, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var export = await db.DataExports.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == exportId && e.RequestedBy == tenant.UserId && e.ReadyAt != null && e.ExpiresAt > now, cancellationToken);
        if (export is null || await media.DownloadAsync(export.BlobPath, cancellationToken) is not { } content)
        {
            return TypedResults.NotFound();
        }

        var slug = export.TenantId is null ? null : await db.Tenants.Where(t => t.Id == export.TenantId).Select(t => t.Slug).SingleAsync(cancellationToken);
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.File(content.ToArray(), "application/json",
            $"athlos-{slug ?? "data"}-{export.ReadyAt!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json");
    }

    private static Item ToItem(DataExport e, string? slug, DateTimeOffset now) =>
        new(e.Id, e.Kind, slug, e.RequestedAt, e.ReadyAt is not null ? "Ready" : e.FailedAt is not null ? "Failed" : "Preparing", e.ReadyAt, e.ExpiresAt);

    // One attempt at an export; true once it is ready. An export another attempt holds, or one that
    // is done, is false without doing anything.
    public static async Task<bool> RunAsync(IServiceScopeFactory scopes, Guid exportId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<TenancyDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        // The backoff in SQL: 2, 4, 8 ... minutes after this attempt, at most an hour.
        var claimed = await db.Database.ExecuteSqlAsync($"""
            UPDATE data_exports
            SET attempts = attempts + 1,
                next_attempt_at = {now} + least(interval '1 minute' * power(2, least(attempts + 1, 10)), interval '1 hour')
            WHERE id = {exportId} AND ready_at IS NULL AND failed_at IS NULL AND next_attempt_at <= {now}
            """, cancellationToken);
        if (claimed == 0)
        {
            return false;
        }

        var export = await db.DataExports.SingleAsync(e => e.Id == exportId, cancellationToken);
        string what;
        object document;
        try
        {
            if (export.Kind == DataExportKind.Account)
            {
                var own = await scopes.AsUserAsync<TenancyDbContext, (ExportMyData.Profile, List<ExportMyData.Workspace>)>(
                    export.RequestedBy, workspaces => ExportMyData.ReadOwnAsync(workspaces, export.RequestedBy, cancellationToken), cancellationToken);
                document = await ExportMyData.AssembleAsync(own, export.RequestedBy, services.GetRequiredService<ICustomerExport>(), now, cancellationToken);
                what = "Your Athlos data";
            }
            else
            {
                // Still the owner of a workspace still active, and not being deleted (WorkspaceDeletions).
                var tenantId = export.TenantId!.Value;
                var (workspace, team) = await scopes.InTenantAsync<TenancyDbContext, (Tenant?, List<ExportWorkspace.Member>)>(tenantId, async inTenant =>
                    await inTenant.Memberships.AnyAsync(m => m.UserId == export.RequestedBy && m.Role == TenantRole.Owner, cancellationToken)
                        && !await inTenant.WorkspaceDeletions.AnyAsync(d => d.TenantId == tenantId, cancellationToken)
                        ? (await inTenant.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, cancellationToken),
                            await ExportWorkspace.TeamAsync(inTenant, cancellationToken))
                        : (null, []), cancellationToken);
                if (workspace is null)
                {
                    await FailAsync(db, export, now, "The workspace is no longer active, or the requester no longer owns it.", cancellationToken);
                    return false;
                }

                document = await ExportWorkspace.AssembleAsync(workspace, team, services.GetRequiredService<ITenantExport>(), now, cancellationToken);
                what = $"The data of {workspace.DisplayName}";
            }

            var json = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
            await services.GetRequiredService<MediaStore>().UploadAsync(
                export.BlobPath, BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(document, document.GetType(), json)), "application/json", "no-store", cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or Azure.RequestFailedException)
        {
            LogAttemptFailed(services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DataExports)), exportId, e);
            if (export.Attempts >= MaxAttempts)
            {
                await FailAsync(db, export, now, "The data could not be gathered after several tries.", cancellationToken);
            }
            else
            {
                export.LastError = "The other services could not answer yet.";
                await db.SaveChangesAsync(cancellationToken);
            }

            return false;
        }

        // Ready, and the email, in one transaction (the email names the user; the worker finds the address).
        var publicUrl = new Uri(services.GetRequiredService<IConfiguration>()["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        export.ReadyAt = now;
        export.ExpiresAt = now + KeptFor;
        export.LastError = null;
        db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, new EmailNotification(
            EmailTemplates.DataExportReady, To: [], ToOwnersOfTenant: null, Locale: "en", new Dictionary<string, string>
            {
                ["what"] = what,
                ["expires"] = export.ExpiresAt.Value.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
                ["accountUrl"] = new Uri(publicUrl, "/me/account").ToString(),
            },
            ToUser: export.RequestedBy)));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task FailAsync(TenancyDbContext db, DataExport export, DateTimeOffset now, string error, CancellationToken cancellationToken)
    {
        export.FailedAt = now;
        export.LastError = error;
        await db.SaveChangesAsync(cancellationToken);
    }

    // The pass: due attempts, then the files and rows past their seven days (failed ones too).
    public static async Task<int> RunDueAsync(IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        List<Guid> due;
        List<DataExport> expired;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            due = await db.DataExports.AsNoTracking()
                .Where(e => e.ReadyAt == null && e.FailedAt == null && e.NextAttemptAt <= now)
                .OrderBy(e => e.NextAttemptAt)
                .Select(e => e.Id)
                .Take(20)
                .ToListAsync(cancellationToken);
            expired = await db.DataExports.AsNoTracking()
                .Where(e => e.ExpiresAt <= now || e.FailedAt <= now - KeptFor)
                .Take(50)
                .ToListAsync(cancellationToken);
            var media = scope.ServiceProvider.GetRequiredService<MediaStore>();
            foreach (var export in expired)
            {
                await media.DeleteAsync(export.BlobPath, cancellationToken);
                await db.DataExports.Where(e => e.Id == export.Id).ExecuteDeleteAsync(cancellationToken);
            }
        }

        var ready = 0;
        foreach (var exportId in due)
        {
            if (await RunAsync(scopes, exportId, cancellationToken))
            {
                ready++;
            }
        }

        return ready;
    }

    // Deleting an account (design §5.2): the user's exports and their files go.
    public static async Task DeleteForUserAsync(TenancyDbContext db, MediaStore media, Guid userId, CancellationToken cancellationToken)
    {
        foreach (var export in await db.DataExports.AsNoTracking().Where(e => e.RequestedBy == userId).ToListAsync(cancellationToken))
        {
            await media.DeleteAsync(export.BlobPath, cancellationToken);
        }

        await db.DataExports.Where(e => e.RequestedBy == userId).ExecuteDeleteAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Data export {ExportId} could not be prepared this time; it is tried again later")]
    private static partial void LogAttemptFailed(ILogger logger, Guid exportId, Exception exception);
}

// Runs the exports that did not finish after their request, and deletes them after seven days
// (design §5.4): a pass every minute while the service is awake.
public sealed partial class DataExportJobs(IServiceScopeFactory scopes, ILogger<DataExportJobs> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await DataExports.RunDueAsync(scopes, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogPassFailed(logger, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A data export pass failed; trying again on the next tick")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
