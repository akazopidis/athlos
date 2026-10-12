using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Payouts;

// Coach shares waiting for a transfer, for the platform admin (design §1, §2.2): the list, and
// "Retry now". It reads only the payout_retries work queue, which carries what the list needs; the
// retry itself enters the order's tenant (PayoutRetries), so no tenant's orders are read across tenants.
public static class AdminPayouts
{
    public sealed record Item(Guid OrderId, string CoachSlug, long Amount, string Currency, int Attempts, string? LastError, DateTimeOffset NextAttemptAt, DateTimeOffset CreatedAt);

    public sealed record RetryResult(bool Paid);

    public static void MapAdminPayouts(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin/payouts")
            .RequireAuthorization(PlatformAdminAuthorization.Policy)
            .WithTags("Admin");

        admin.MapGet("/", async (OrderingDbContext db, CancellationToken cancellationToken) =>
                TypedResults.Ok(await db.PayoutRetries.AsNoTracking()
                    .OrderBy(r => r.CreatedAt)
                    .Take(100)
                    .Select(r => new Item(r.OrderId, r.CoachSlug, r.Amount, r.Currency, r.Attempts, r.LastError, r.NextAttemptAt, r.CreatedAt))
                    .ToListAsync(cancellationToken)))
            .WithName("AdminListPayouts");

        admin.MapPost("/{orderId:guid}/retry", async Task<Results<Ok<RetryResult>, NotFound>> (
                Guid orderId, OrderingDbContext db, ICheckoutSessions stripe, IServiceScopeFactory scopes, CancellationToken cancellationToken) =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var retry = await db.PayoutRetries
                    .FromSql($"SELECT * FROM payout_retries WHERE order_id = {orderId} FOR UPDATE SKIP LOCKED")
                    .SingleOrDefaultAsync(cancellationToken);
                if (retry is null)
                {
                    return TypedResults.NotFound();
                }

                var paid = await PayoutRetries.AttemptAsync(retry, db, stripe, scopes, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return TypedResults.Ok(new RetryResult(paid));
            })
            .WithName("AdminRetryPayout");
    }
}
