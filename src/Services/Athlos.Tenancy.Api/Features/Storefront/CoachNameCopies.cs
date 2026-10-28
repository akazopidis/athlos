using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Storefront;

// Catalog-api's copy of each workspace's name, which the shop's search matches (design §8, v17.9).
// tenants.catalog_name is the name last copied; a pass every minute copies every active workspace whose
// name differs: a new workspace, a rename, and once, every workspace from before. Nothing else needs to
// remember to call catalog, and a call that fails is simply tried again on the next pass. If the name
// changes while it is being copied, the old name is recorded, so the next pass copies the new one.
// Tenants are readable by every session (RLS tenants_read); recording the copy enters that tenant.
public sealed partial class CoachNameCopies(IServiceScopeFactory scopes, ILogger<CoachNameCopies> logger) : BackgroundService
{
    private const int Batch = 50;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await CopyAsync(scopes, logger, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogPassFailed(logger, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // One pass, through every workspace waiting, in id order; the timer calls it, tests call it directly.
    public static async Task<int> CopyAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken cancellationToken)
    {
        var copied = 0;
        var after = Guid.Empty;
        while (true)
        {
            List<(Guid Id, string DisplayName)> waiting;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
                waiting = (await db.Tenants.AsNoTracking()
                        .Where(t => t.Id.CompareTo(after) > 0 && t.Status == TenantStatus.Active && (t.CatalogName == null || t.CatalogName != t.DisplayName))
                        .OrderBy(t => t.Id)
                        .Take(Batch)
                        .Select(t => new { t.Id, t.DisplayName })
                        .ToListAsync(cancellationToken))
                    .Select(t => (t.Id, t.DisplayName))
                    .ToList();
            }

            foreach (var (id, name) in waiting)
            {
                try
                {
                    await using (var scope = scopes.CreateAsyncScope())
                    {
                        await scope.ServiceProvider.GetRequiredService<ICatalogCoachNames>().SetAsync(id, name, cancellationToken);
                    }

                    await scopes.InTenantAsync<TenancyDbContext>(id, workspaces => workspaces.Tenants
                        .Where(t => t.Id == id)
                        .ExecuteUpdateAsync(t => t.SetProperty(x => x.CatalogName, name), cancellationToken), cancellationToken);
                    copied++;
                }
                catch (HttpRequestException e)
                {
                    LogCopyFailed(logger, id, e);
                }
            }

            if (waiting.Count < Batch)
            {
                return copied;
            }

            after = waiting[^1].Id;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A coach name copy pass failed; trying again on the next tick")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not copy the name of workspace {TenantId} to catalog-api; trying again on the next pass")]
    private static partial void LogCopyFailed(ILogger logger, Guid tenantId, Exception exception);
}

public interface ICatalogCoachNames
{
    Task SetAsync(Guid tenantId, string displayName, CancellationToken cancellationToken);
}

public sealed class ServiceCatalogCoachNames(IHttpClientFactory clients) : ICatalogCoachNames
{
    public const string Catalog = "coach-names-catalog";

    public async Task SetAsync(Guid tenantId, string displayName, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient(Catalog).PutAsJsonAsync($"/internal/tenants/{tenantId}/coach-name", new { displayName }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
