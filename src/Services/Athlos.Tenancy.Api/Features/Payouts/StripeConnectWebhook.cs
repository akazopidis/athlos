using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace Athlos.Tenancy.Api.Features.Payouts;

// Stripe calls POST /webhooks/stripe-connect through the gateway with events about the coaches' Connect
// accounts (a Connect endpoint, separate from ordering-api's platform endpoint, with its own signing
// secret, Stripe:ConnectWebhookSecret). On account.updated the account's status is read from Stripe again,
// the source of truth, so events arriving out of order cannot set an old state, and stored on the
// workspace that holds the account (stripe_onboarding_complete). A coach whose account Stripe restricts
// stops selling at once (checkout finds no payout account) instead of collecting payouts that fail;
// one Stripe fixes becomes ready again without visiting the studio.
// All stages share one Stripe test account, so every stage receives every coach's events; an account no
// workspace here holds is acknowledged and ignored. Idempotent: the same status is written again.
public static partial class StripeConnectWebhook
{
    private const long MaxPayloadBytes = 64 * 1024;

    public static void MapStripeConnectWebhook(this IEndpointRouteBuilder app) =>
        app.MapPost("/webhooks/stripe-connect", Handle)
            .ExcludeFromDescription();

    private static async Task<Results<Ok, BadRequest, ProblemHttpResult>> Handle(
        HttpRequest request, IConfiguration configuration, IStripeConnect stripe, TenantContext tenant, TenancyDbContext db,
        ILogger<StripeConnect> logger, CancellationToken cancellationToken)
    {
        var secret = configuration["Stripe:ConnectWebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret) || !stripe.IsConfigured)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Stripe Connect webhooks are not configured.");
        }

        if (request.ContentLength > MaxPayloadBytes)
        {
            return TypedResults.BadRequest();
        }

        using var reader = new StreamReader(request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(payload, request.Headers["Stripe-Signature"], secret, tolerance: 300, throwOnApiVersionMismatch: false);
        }
        catch (StripeException)
        {
            return TypedResults.BadRequest();
        }

        if (stripeEvent.Type != "account.updated" || stripeEvent.Data.Object is not Stripe.Account { Id: { } accountId })
        {
            return TypedResults.Ok();
        }

        // Tenants are readable by every session (RLS tenants_read); the change enters the tenant.
        var tenantId = await db.Tenants.AsNoTracking()
            .Where(t => t.StripeAccountId == accountId && t.Status != TenantStatus.Deleted)
            .Select(t => (Guid?)t.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (tenantId is null)
        {
            return TypedResults.Ok();
        }

        var status = await stripe.GetStatusAsync(accountId, cancellationToken);
        tenant.EnterTenant(tenantId.Value, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var workspace = await db.Tenants.SingleAsync(t => t.Id == tenantId && t.StripeAccountId == accountId, cancellationToken);
        if (workspace.StripeOnboardingComplete != status.OnboardingComplete)
        {
            workspace.StripeOnboardingComplete = status.OnboardingComplete;
            await db.SaveChangesAsync(cancellationToken);
            LogStatusChanged(logger, tenantId.Value, status.OnboardingComplete);
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stripe changed workspace {TenantId}'s payout account: ready to sell = {Ready}")]
    private static partial void LogStatusChanged(ILogger logger, Guid tenantId, bool ready);
}
