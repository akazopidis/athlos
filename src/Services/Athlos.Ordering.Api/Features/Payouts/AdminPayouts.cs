using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Payouts;

// Coach shares waiting for a transfer, for the platform admin (design §1, §2.2): the list, and
// "Retry now". It reads only the payout_retries work queue, which carries what the list needs; the
// retry itself enters the order's tenant (PayoutRetries), so no tenant's orders are read across tenants.
// A retry states a reason, recorded with the admin and the outcome in admin_actions (/actions).
public static class AdminPayouts
{
    public sealed record Item(Guid OrderId, string CoachSlug, long Amount, string Currency, int Attempts, string? LastError, DateTimeOffset NextAttemptAt, DateTimeOffset CreatedAt);

    public sealed record RetryRequest(string? Reason);

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

        admin.MapPost("/{orderId:guid}/retry", async Task<Results<Ok<RetryResult>, NotFound, ProblemHttpResult>> (
                Guid orderId, RetryRequest request, TenantContext tenant, OrderingDbContext db, ICheckoutSessions stripe, IServiceScopeFactory scopes,
                TimeProvider time, CancellationToken cancellationToken) =>
            {
                if (AdminActions.Valid(request.Reason) is not { } reason)
                {
                    return AdminActions.MissingReason();
                }

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var retry = await db.PayoutRetries
                    .FromSql($"SELECT * FROM payout_retries WHERE order_id = {orderId} FOR UPDATE SKIP LOCKED")
                    .SingleOrDefaultAsync(cancellationToken);
                if (retry is null)
                {
                    return TypedResults.NotFound();
                }

                var paid = await PayoutRetries.AttemptAsync(retry, db, stripe, scopes, cancellationToken);
                db.AdminActions.Add(new AdminAction
                {
                    AdminId = tenant.UserId ?? Guid.Empty,
                    Action = AdminActionKind.PayoutRetried,
                    TenantId = retry.TenantId,
                    Subject = $"/c/{retry.CoachSlug}",
                    Detail = $"{retry.Amount / 100m:0.00} {retry.Currency}, order {orderId}, {(paid ? "paid" : "still refused")}",
                    Reason = reason,
                    At = time.GetUtcNow(),
                });
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return TypedResults.Ok(new RetryResult(paid));
            })
            .WithName("AdminRetryPayout");

        admin.MapAdminActions<OrderingDbContext>("/actions").WithName("AdminListPayoutActions");
    }
}
