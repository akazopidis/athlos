using Athlos.Tenancy.Api.Data;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy.Api.Features.Tenants;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace Athlos.Tenancy.Api.Features.Payouts;

// Getting a coach paid (design §5: "they are sent to Stripe Connect onboarding"). The owner starts
// onboarding and comes back to the studio; the status is read from Stripe, which is the source of
// truth, and cached on the tenant as stripe_onboarding_complete.
public static partial class PayoutEndpoints
{
    public sealed record OnboardingLink(string Url);

    public sealed record PayoutStatus(bool Connected, bool OnboardingComplete, bool PayoutsEnabled);

    public sealed record DashboardLink(string Url);

    // How many payouts the studio lists; the Stripe dashboard has the rest.
    private const int HistoryCount = 10;

    public static void MapPayouts(this IEndpointRouteBuilder studio)
    {
        studio.MapPost("/payouts/onboarding", StartOnboarding)
            .AddEndpointFilter(StripeErrors)
            .WithName("StartPayoutOnboarding")
            .WithTags("Payouts");
        studio.MapGet("/payouts", GetStatus)
            .AddEndpointFilter(StripeErrors)
            .WithName("GetPayoutStatus")
            .WithTags("Payouts");
        studio.MapPost("/payouts/dashboard", OpenDashboard)
            .AddEndpointFilter(StripeErrors)
            .WithName("OpenPayoutDashboard")
            .WithTags("Payouts");
        studio.MapGet("/payouts/history", GetHistory)
            .AddEndpointFilter(StripeErrors)
            .WithName("GetPayoutHistory")
            .WithTags("Payouts");
    }

    // Stripe said no (for example Connect not enabled on the platform account, found on dev) or is
    // unreachable: 502 with a message the studio can show, and Stripe's reason in the log for us.
    // Without this the error was an unhandled 500 and the coach saw only "Could not start payout setup."
    private static async ValueTask<object?> StripeErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (StripeException e)
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PayoutEndpoints));
            LogStripeFailed(logger, e, e.StripeError?.Code ?? "none");
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Stripe could not handle the payout request. Please try again later.");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Stripe refused a payout request (code {StripeCode})")]
    private static partial void LogStripeFailed(ILogger logger, Exception exception, string stripeCode);

    // Owners only: this creates the Stripe account that receives the workspace's money.
    private static async Task<Results<Ok<OnboardingLink>, NotFound, ProblemHttpResult>> StartOnboarding(
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, IStripeConnect stripe, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug && t.Status != TenantStatus.Deleted, cancellationToken);
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
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, IStripeConnect stripe, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug && t.Status != TenantStatus.Deleted, cancellationToken);
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
            if (status.OnboardingComplete)
            {
                WorkspaceEmails.ToOwners(db, EmailTemplates.PayoutsReady, workspace, configuration);
            }

            workspace.StripeOnboardingComplete = status.OnboardingComplete;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(new PayoutStatus(Connected: true, status.OnboardingComplete, status.PayoutsEnabled));
    }

    // Owners only, like onboarding: the account pays out to the owner's bank. A POST because Stripe's
    // login link works once and expires within minutes; the studio opens it straight away.
    private static async Task<Results<Ok<DashboardLink>, NotFound, ProblemHttpResult>> OpenDashboard(
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, IStripeConnect stripe, CancellationToken cancellationToken)
    {
        var owner = await OwnersAccount(tenantSlug, tenant, db, stripe, cancellationToken);
        return owner switch
        {
            { NotFound: true } => TypedResults.NotFound(),
            { Problem: { } problem } => problem,
            _ => TypedResults.Ok(new DashboardLink(await stripe.CreateDashboardLinkAsync(owner.Account!, cancellationToken))),
        };
    }

    // The balance (available and on its way) and the latest payouts to the bank, owners only.
    private static async Task<Results<Ok<PayoutHistory>, NotFound, ProblemHttpResult>> GetHistory(
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, IStripeConnect stripe, CancellationToken cancellationToken)
    {
        var owner = await OwnersAccount(tenantSlug, tenant, db, stripe, cancellationToken);
        return owner switch
        {
            { NotFound: true } => TypedResults.NotFound(),
            { Problem: { } problem } => problem,
            _ => TypedResults.Ok(await stripe.GetPayoutHistoryAsync(owner.Account!, HistoryCount, cancellationToken)),
        };
    }

    private sealed record Owner(string? Account, bool NotFound = false, ProblemHttpResult? Problem = null);

    // The workspace's Stripe account for its owner once onboarding is complete, or why not. Another
    // tenant's workspace is a 404 (design §3.2), like every studio route.
    private static async Task<Owner> OwnersAccount(
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, IStripeConnect stripe, CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug && t.Status != TenantStatus.Deleted, cancellationToken);
        if (workspace is null)
        {
            return new Owner(null, NotFound: true);
        }

        if (tenant.Role != TenantRole.Owner)
        {
            return new Owner(null, Problem: TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner sees the payouts."));
        }

        if (!stripe.IsConfigured)
        {
            return new Owner(null, Problem: StripeConnectExtensions.NotConfigured());
        }

        if (workspace.StripeAccountId is null || !workspace.StripeOnboardingComplete)
        {
            return new Owner(null, Problem: TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Finish setting up payouts first."));
        }

        return new Owner(workspace.StripeAccountId);
    }

    // The stage's public address (its gateway), set per environment (App:PublicUrl).
    private static Uri PublicUrl(IConfiguration configuration) =>
        new(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
}
