using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Plans;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Admin;

// Platform metrics for the admin (design §1): how many workspaces there are and where they stand.
// Counts only, from tenancy's own table (database per service, §2.1). Traffic, latency, 429s and
// paid orders per coach are in Application Insights, in the "Athlos per coach" workbook (§6).
public static class GetPlatformMetrics
{
    public sealed record Response(
        int Workspaces, int Active, int Suspended, int Deleted, int ActiveFree, int ActivePro, int ReadyToSell, int NewLast30Days);

    public static void MapGetPlatformMetrics(this IEndpointRouteBuilder app) =>
        app.MapGet("/metrics", Handle)
            .WithName("AdminGetPlatformMetrics");

    private static async Task<Ok<Response>> Handle(TenancyDbContext db, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var rows = await db.Tenants.AsNoTracking()
            .GroupBy(t => new { t.Status, t.Plan, Ready = t.StripeOnboardingComplete && t.StripeAccountId != null, New = t.CreatedAt >= since })
            .Select(g => new Row(g.Key.Status, g.Key.Plan, g.Key.Ready, g.Key.New, g.Count()))
            .ToListAsync(cancellationToken);

        int Count(Func<Row, bool> match) => rows.Where(match).Sum(r => r.Count);

        return TypedResults.Ok(new Response(
            Workspaces: Count(_ => true),
            Active: Count(r => r.Status == TenantStatus.Active),
            Suspended: Count(r => r.Status == TenantStatus.Suspended),
            Deleted: Count(r => r.Status == TenantStatus.Deleted),
            ActiveFree: Count(r => r is { Status: TenantStatus.Active, Plan: PlanName.Free }),
            ActivePro: Count(r => r is { Status: TenantStatus.Active, Plan: PlanName.Pro }),
            ReadyToSell: Count(r => r is { Status: TenantStatus.Active, Ready: true }),
            NewLast30Days: Count(r => r.New)));
    }

    private sealed record Row(TenantStatus Status, PlanName Plan, bool Ready, bool New, int Count);
}
