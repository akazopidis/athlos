using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace Athlos.Ordering.Api.Features.Payouts;

// Coach shares whose transfer failed after payment (design §2.2), tried again with backoff: 5 minutes,
// then doubling up to 6 hours. A row is claimed with FOR UPDATE SKIP LOCKED, like the outbox, so two
// replicas never retry the same share; the transfer is idempotent per order at Stripe anyway. Each
// retry enters the order's tenant to read the order, so RLS applies. The service scales to zero, so
// retries run while it is awake; the platform admin sees what waits and can retry at once.
public sealed partial class PayoutRetries(IServiceScopeFactory scopes, ILogger<PayoutRetries> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(5 * Math.Pow(2, Math.Max(0, attempts - 1)), MaxBackoff.TotalMinutes));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RetryDueAsync(scopes, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogRetryPassFailed(logger, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // One pass over the shares that are due; the worker calls it on a timer, tests call it directly.
    public static async Task<int> RetryDueAsync(IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var stripe = scope.ServiceProvider.GetRequiredService<ICheckoutSessions>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var due = await db.PayoutRetries
            .FromSql($"SELECT * FROM payout_retries WHERE next_attempt_at <= now() ORDER BY next_attempt_at LIMIT 10 FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        foreach (var retry in due)
        {
            await AttemptAsync(retry, db, stripe, scopes, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return due.Count;
    }

    // Tries one share now (the worker, or the admin's "Retry now"); true if the coach is paid.
    public static async Task<bool> AttemptAsync(
        PayoutRetry retry, OrderingDbContext db, ICheckoutSessions stripe, IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        Order? order = null;
        await scopes.InTenantAsync<OrderingDbContext>(retry.TenantId, async orders => order = await orders.Orders.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == retry.OrderId, cancellationToken), cancellationToken);

        // Refunded, or transferred meanwhile: nothing is owed.
        if (order is not { Status: OrderStatus.Paid, StripeTransferId: null, StripePaymentIntentId: { } paymentIntentId })
        {
            db.PayoutRetries.Remove(retry);
            await db.SaveChangesAsync(cancellationToken);
            return order is { StripeTransferId: not null };
        }

        try
        {
            var transferId = await stripe.TransferAsync(order, paymentIntentId, cancellationToken);
            await scopes.InTenantAsync<OrderingDbContext>(retry.TenantId, orders => orders.Orders
                .Where(o => o.Id == retry.OrderId && o.StripeTransferId == null)
                .ExecuteUpdateAsync(o => o.SetProperty(x => x.StripeTransferId, transferId), cancellationToken), cancellationToken);
            db.PayoutRetries.Remove(retry);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (StripeException e)
        {
            retry.Attempts++;
            var code = e.StripeError?.Code ?? "stripe_error";
            retry.LastError = code.Length > 200 ? code[..200] : code;
            retry.NextAttemptAt = DateTimeOffset.UtcNow + Backoff(retry.Attempts);
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A payout retry pass failed; trying again on the next tick")]
    private static partial void LogRetryPassFailed(ILogger logger, Exception exception);
}
