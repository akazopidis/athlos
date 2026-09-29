using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Payouts;

// Getting a coach paid (design §5: "they are sent to Stripe Connect onboarding"). The owner starts
// onboarding and comes back to the studio; the status is read from Stripe, which is the source of
// truth, and cached on the tenant as stripe_onboarding_complete.
public static class PayoutEndpoints
{
    public sealed record OnboardingLink(string Url);

    public sealed record PayoutStatus(bool Connected, bool OnboardingComplete, bool PayoutsEnabled);

    public static void MapPayouts(this IEndpointRouteBuilder studio)
    {
        studio.MapPost("/payouts/onboarding", StartOnboarding)
            .WithName("StartPayoutOnboarding")
            .WithTags("Payouts");
        studio.MapGet("/payouts", GetStatus)
            .WithName("GetPayoutStatus")
            .WithTags("Payouts");
    }

    // Owners only: this creates the Stripe account that receives the workspace's money.
    private static async Task<Results<Ok<OnboardingLink>, NotFound, ProblemHttpResult>> StartOnboarding(
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, IStripeConnect stripe, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug, cancellationToken);
        if (workspace is null)
        {
            return TypedResults.NotFound();
        }

        if (tenant.Role != TenantRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can set up payouts.");
        }

        if (!stripe.IsConfigured)
        {
            return StripeConnectExtensions.NotConfigured();
        }

        if (workspace.StripeAccountId is null)
        {
            workspace.StripeAccountId = await stripe.CreateExpressAccountAsync(workspace.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Stripe sends the coach back to the studio (the SPA), which then reads the status.
        var studio = new Uri(PublicUrl(configuration), $"/studio/{workspace.Slug}");
        var url = await stripe.CreateOnboardingLinkAsync(
            workspace.StripeAccountId,
            returnUrl: new Uri($"{studio}?payouts=return"),
            refreshUrl: new Uri($"{studio}?payouts=refresh"),
            cancellationToken);

        return TypedResults.Ok(new OnboardingLink(url));
    }

    private static async Task<Results<Ok<PayoutStatus>, NotFound, ProblemHttpResult>> GetStatus(
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, IStripeConnect stripe, CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug, cancellationToken);
        if (workspace is null)
        {
            return TypedResults.NotFound();
        }

        if (workspace.StripeAccountId is null)
        {
            return TypedResults.Ok(new PayoutStatus(Connected: false, OnboardingComplete: false, PayoutsEnabled: false));
        }

        if (!stripe.IsConfigured)
        {
            return StripeConnectExtensions.NotConfigured();
        }

        var status = await stripe.GetStatusAsync(workspace.StripeAccountId, cancellationToken);
        if (workspace.StripeOnboardingComplete != status.OnboardingComplete)
        {
            workspace.StripeOnboardingComplete = status.OnboardingComplete;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(new PayoutStatus(Connected: true, status.OnboardingComplete, status.PayoutsEnabled));
    }

    // The stage's public address (its gateway), set per environment (App:PublicUrl).
    private static Uri PublicUrl(IConfiguration configuration) =>
        new(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
}
