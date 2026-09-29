using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Internal;

// For other services (signed service-to-service calls): where a workspace's money goes and whether
// it can sell. ordering-api needs the Connect account for destination charges; catalog-api checks
// that onboarding is complete before a program is published (design §3.4).
public static class GetPayoutAccount
{
    public sealed record Response(Guid TenantId, string Slug, bool Active, string? StripeAccountId, bool OnboardingComplete);

    public static void MapGetPayoutAccount(this IEndpointRouteBuilder app) =>
        app.MapGet("/internal/tenants/{tenantId:guid}/payout-account", Handle)
            .ExcludeFromDescription();

    // Tenants are readable by every session (RLS tenants_read), so no tenant needs to be entered.
    private static async Task<Results<Ok<Response>, NotFound>> Handle(Guid tenantId, TenancyDbContext db, CancellationToken cancellationToken)
    {
        var account = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new Response(t.Id, t.Slug, t.Status == TenantStatus.Active, t.StripeAccountId, t.StripeOnboardingComplete))
            .SingleOrDefaultAsync(cancellationToken);

        return account is null ? TypedResults.NotFound() : TypedResults.Ok(account);
    }
}
