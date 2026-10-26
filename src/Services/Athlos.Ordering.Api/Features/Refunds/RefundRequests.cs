using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Refunds;

// Refunds Athlos stands behind (design §2.2). The refund page promises every program can be refunded
// within the refund window (Payments:RefundWindowDays after payment), so:
// 1. The customer asks on their order, within the window, with an optional note. The order records
//    it, the coach's owners are emailed, and refund_requests (a work queue of the service, like
//    payout_retries) holds it for the admin with a due date three working days later.
// 2. The coach refunds it in Sales, as any sale; a requested sale stays refundable after the window.
// 3. Still open on its due date, the platform admin refunds it (OrderRefunds, Cause.Admin), with a
//    reason in the audit trail.
// Any refund of the order, whoever makes it, closes the request (OrderRefunds removes the row).
public static class RefundRequests
{
    public const int WorkingDaysForTheCoach = 3;

    public sealed record Request([property: MaxLength(500)] string? Note);

    public sealed record Item(Guid OrderId, string CoachSlug, string ProgramTitle, long Amount, string Currency, DateTimeOffset RequestedAt, DateTimeOffset DueAt);

    public sealed record AdminRefund(string? Reason);

    // Working days are Monday to Friday, in UTC; public holidays are not counted out.
    public static DateTimeOffset AddWorkingDays(DateTimeOffset start, int days)
    {
        var date = start;
        while (days > 0)
        {
            date = date.AddDays(1);
            if (date.UtcDateTime.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days--;
            }
        }

        return date;
    }

    // POST /api/v1/me/orders/{orderId}/refund-request. The customer's own order is read across coaches
    // (the cross-tenant slice "a customer's own orders", design §3.2), then changed inside its tenant.
    // 204 again for an order already requested; another customer's order is 404.
    public static void MapRequestRefund(this IEndpointRouteBuilder me) =>
        me.MapPost("/orders/{orderId:guid}/refund-request", RequestAsync)
            .WithName("RequestRefund")
            .WithTags("Orders");

    public static void MapAdminRefundRequests(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin/refund-requests")
            .RequireAuthorization(PlatformAdminAuthorization.Policy)
            .WithTags("Admin");
        admin.MapGet("/", ListAsync).WithName("AdminListRefundRequests");
        admin.MapPost("/{orderId:guid}/refund", RefundAsync).WithName("AdminRefundRequest");
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RequestAsync(
        Guid orderId, Request request, ITenantContext tenant, OrderingDbContext db, IServiceScopeFactory scopes, IConfiguration configuration,
        TimeProvider time, CancellationToken cancellationToken)
    {
        Order? order;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            order = await db.Orders.AsNoTracking()
                .IgnoreQueryFilters([TenantDbContext.TenantFilter])
                .SingleOrDefaultAsync(o => o.Id == orderId && o.CustomerId == tenant.UserId, cancellationToken);
        }

        if (order is null || order.Status is OrderStatus.Pending or OrderStatus.Failed)
        {
            return TypedResults.NotFound();
        }

        if (order.Status == OrderStatus.Refunded)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This order was refunded already.");
        }

        if (order.RefundRequestedAt is not null)
        {
            return TypedResults.NoContent();
        }

        var now = time.GetUtcNow();
        var window = OrderRefunds.RefundWindow(configuration);
        if (order.PaidAt + window < now)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: $"Orders can be refunded for {window.TotalDays:0} days after payment; this one is older.");
        }

        var due = AddWorkingDays(now, WorkingDaysForTheCoach);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        await scopes.InTenantAsync<OrderingDbContext>(order.TenantId, async tenantDb =>
        {
            var changed = await tenantDb.Orders
                .Where(o => o.Id == orderId && o.Status == OrderStatus.Paid && o.RefundRequestedAt == null)
                .ExecuteUpdateAsync(o => o
                    .SetProperty(x => x.RefundRequestedAt, now)
                    .SetProperty(x => x.RefundRequestNote, note)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            if (changed == 0)
            {
                return;
            }

            tenantDb.RefundRequests.Add(new RefundRequest
            {
                OrderId = order.Id,
                TenantId = order.TenantId,
                CoachSlug = order.CoachSlug,
                ProgramTitle = order.ProgramTitle,
                Amount = order.Amount,
                Currency = order.Currency,
                RequestedAt = now,
                DueAt = due,
            });
            var email = OrderRefunds.CoachEmail(EmailTemplates.RefundRequested, order, configuration);
            tenantDb.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, email with
            {
                Data = new Dictionary<string, string>(email.Data)
                {
                    ["note"] = note ?? "none",
                    ["dueDate"] = due.UtcDateTime.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture),
                },
            }));
            await tenantDb.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return TypedResults.NoContent();
    }

    // Open requests, the earliest due first.
    private static async Task<Ok<List<Item>>> ListAsync(OrderingDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        return TypedResults.Ok(await db.RefundRequests.AsNoTracking()
            .OrderBy(r => r.DueAt)
            .Take(100)
            .Select(r => new Item(r.OrderId, r.CoachSlug, r.ProgramTitle, r.Amount, r.Currency, r.RequestedAt, r.DueAt))
            .ToListAsync(cancellationToken));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RefundAsync(
        Guid orderId, AdminRefund request, TenantContext tenant, OrderingDbContext db, ICheckoutSessions stripe, IServiceScopeFactory scopes,
        IConfiguration configuration, ILoggerFactory loggers, TimeProvider time, CancellationToken cancellationToken)
    {
        if (AdminActions.Valid(request.Reason) is not { } reason)
        {
            return AdminActions.MissingReason();
        }

        RefundRequest? open;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            open = await db.RefundRequests.AsNoTracking().SingleOrDefaultAsync(r => r.OrderId == orderId, cancellationToken);
        }

        if (open is null)
        {
            return TypedResults.NotFound();
        }

        if (open.DueAt > time.GetUtcNow())
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: $"The coach has until {open.DueAt.UtcDateTime:d MMMM yyyy HH:mm} UTC to refund it.");
        }

        if (!stripe.IsConfigured)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Payments are not configured for this environment.");
        }

        var logger = loggers.CreateLogger("Athlos.Ordering.Refunds");
        var outcome = await OrderRefunds.RefundAsync(open.TenantId, orderId, OrderRefunds.Cause.Admin, stripe, scopes, configuration, logger, cancellationToken);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            if (outcome == OrderRefunds.Outcome.Refunded)
            {
                db.AdminActions.Add(new AdminAction
                {
                    AdminId = tenant.UserId ?? Guid.Empty,
                    Action = AdminActionKind.RefundIssued,
                    TenantId = open.TenantId,
                    Subject = $"{open.ProgramTitle} (/c/{open.CoachSlug})",
                    Detail = $"{MoneyText.Format(open.Amount, open.Currency)}, order {orderId}",
                    Reason = reason,
                    At = time.GetUtcNow(),
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            else if (outcome == OrderRefunds.Outcome.NotFound)
            {
                // Refunded or gone already: the request is answered.
                await db.RefundRequests.Where(r => r.OrderId == orderId).ExecuteDeleteAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return outcome switch
        {
            OrderRefunds.Outcome.Refunded => TypedResults.NoContent(),
            OrderRefunds.Outcome.BeforeTheCart => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "This sale was paid before the cart; refund it in the Stripe dashboard, which updates Athlos."),
            OrderRefunds.Outcome.NotPaidYet => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The payment is not recorded yet; try again in a few minutes."),
            _ => TypedResults.NotFound(),
        };
    }
}
