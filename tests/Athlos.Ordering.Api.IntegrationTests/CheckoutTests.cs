using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Athlos.Messaging.Contracts;
using Athlos.Ordering.Api.Data;
using Athlos.Testing;
using static Athlos.Testing.OrderingArrange;

namespace Athlos.Ordering.Api.IntegrationTests;

public sealed class CheckoutTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly OrderingApiFactory _api = new(databases);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Checkout_creates_a_pending_order_with_the_platform_fee_and_the_coachs_account()
    {
        var coach = NewCoach();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug, price: 4900);
        var customer = Guid.CreateVersion7();

        var checkout = await _api.Checkout(customer, offer.ProgramId);

        var order = Assert.Single(_api.OrdersOf(checkout));
        Assert.Equal($"acct_test_{coach.TenantId:N}", order.SellerAccountId);
        Assert.Equal((4900, 490, "EUR"), (order.Amount, order.PlatformFee, order.Currency));
        Assert.Equal((coach.TenantId, checkout.CheckoutId), (order.TenantId, order.CheckoutId));
        Assert.Equal(OrderStatus.Pending, (await MyOrders(customer)).Single().Status);
    }

    // The cart (design §2.2): one payment for programs of several coaches. Each order lands in its
    // coach's tenant; after payment each coach gets their share as a transfer.
    [Fact]
    public async Task A_cart_from_two_coaches_is_one_payment_and_each_coach_gets_their_share()
    {
        var (anna, ben) = (NewCoach(), NewCoach());
        var annaOffer = _api.Sellers.Sell(anna.TenantId, anna.Slug, price: 4900);
        var benOffer = _api.Sellers.Sell(ben.TenantId, ben.Slug, price: 2000);
        var customer = Guid.CreateVersion7();

        var checkout = await _api.Checkout(customer, [annaOffer.ProgramId, benOffer.ProgramId]);
        var paid = await _api.Pay(checkout);

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        var session = Assert.Single(_api.CheckoutSessions.Sessions, s => s.Key == checkout.SessionId).Value;
        Assert.Equal(2, session.Orders.Count);
        var orders = _api.OrdersOf(checkout).ToDictionary(o => o.TenantId);
        Assert.Equal(($"acct_test_{anna.TenantId:N}", 4410L), _api.CheckoutSessions.Transfers[orders[anna.TenantId].Id]);
        Assert.Equal(($"acct_test_{ben.TenantId:N}", 1800L), _api.CheckoutSessions.Transfers[orders[ben.TenantId].Id]);
        Assert.All(await MyOrders(customer), o => Assert.Equal(OrderStatus.Paid, o.Status));
        Assert.Single(await Sales(anna));
        Assert.Single(await Sales(ben));
        // OrderPaid and a sale email per coach. The app's own publisher may send some first, so count
        // what was sent rather than what this pass published.
        await _api.PublishOutbox();
        foreach (var order in orders.Values)
        {
            Assert.Single(await _api.Outbox(), m => m.Queue == OrderPaid.Queue && m.Message.Payload.Contains(order.Id.ToString(), StringComparison.Ordinal));
            Assert.Single(await _api.Outbox(), m => m.Queue == EmailNotification.Queue && m.Message.Payload.Contains(order.TenantId.ToString(), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Programs_not_for_sale_coaches_who_cannot_sell_and_mixed_currencies_are_named()
    {
        var (coach, other) = (NewCoach(), NewCoach());
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        var blocked = _api.Sellers.Sell(other.TenantId, other.Slug);
        _api.Sellers.CannotSell(other.TenantId);
        var dollars = _api.Sellers.Sell(coach.TenantId, coach.Slug, currency: "USD");
        var gone = Guid.CreateVersion7();

        var notForSale = await _api.StartCheckout(Guid.CreateVersion7(), [offer.ProgramId, gone]);
        var notPayable = await _api.StartCheckout(Guid.CreateVersion7(), [offer.ProgramId, blocked.ProgramId]);
        var mixed = await _api.StartCheckout(Guid.CreateVersion7(), [offer.ProgramId, dollars.ProgramId]);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.Conflict), (notForSale.StatusCode, notPayable.StatusCode));
        Assert.Equal(("notForSale", "cannotSell", "currencies"), (await ReasonOf(notForSale), await ReasonOf(notPayable), await ReasonOf(mixed)));
        Assert.Equal([gone], await ProgramIdsIn(notForSale));
        Assert.Equal([blocked.ProgramId], await ProgramIdsIn(notPayable));
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        Assert.DoesNotContain(_api.CheckoutSessions.Sessions.Values, s => s.Orders.Any(o => o.ProgramId == offer.ProgramId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task A_cart_holds_one_to_ten_programs(int count)
    {
        var response = await _api.StartCheckout(Guid.CreateVersion7(), Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).ToList());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Retrying_with_the_same_Idempotency_Key_returns_the_same_checkout()
    {
        var coach = NewCoach();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        var customer = Guid.CreateVersion7();

        var first = await _api.Checkout(customer, offer.ProgramId, idempotencyKey: "buy-once");
        var second = await _api.Checkout(customer, offer.ProgramId, idempotencyKey: "buy-once");

        Assert.Equal(first, second);
        Assert.Single(await MyOrders(customer));
    }

    [Fact]
    public async Task The_paid_webhook_marks_the_order_paid_and_publishes_OrderPaid_and_the_sale_email()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var orderId = await _api.Buy(customer, coach);

        // The app's own publisher runs every few seconds too, so what was sent is checked, not what
        // this pass published.
        await _api.PublishOutbox();

        var (queue, message) = Assert.Single(await _api.Outbox(), m => m.Message.Payload.Contains(orderId.ToString(), StringComparison.Ordinal));
        var (emailQueue, email) = Assert.Single(await _api.Outbox(), m => m.Message.Payload.Contains(coach.TenantId.ToString(), StringComparison.Ordinal)
            && m.Queue == EmailNotification.Queue);
        var sale = JsonSerializer.Deserialize<EmailNotification>(email.Payload, Json)!;
        Assert.Equal((EmailTemplates.SaleMade, coach.TenantId), (sale.Template, sale.ToOwnersOfTenant));
        Assert.Empty(sale.To);
        Assert.DoesNotContain(customer.ToString(), email.Payload, StringComparison.Ordinal);
        Assert.Equal(("49.00 EUR", "44.10 EUR"), (sale.Data["price"], sale.Data["earnings"]));
        var orderPaid = JsonSerializer.Deserialize<OrderPaid>(message.Payload, Json)!;
        Assert.Equal(OrderPaid.Queue, queue);
        Assert.Equal((orderId, coach.TenantId, customer), (orderPaid.OrderId, orderPaid.TenantId, orderPaid.CustomerId));
        Assert.Equal(OrderStatus.Paid, (await MyOrders(customer)).Single().Status);
        await _api.PublishOutbox();
        Assert.Single(await _api.Outbox(), m => m.Message.Payload.Contains(orderId.ToString(), StringComparison.Ordinal)); // sent once
    }

    [Fact]
    public async Task A_retried_webhook_changes_nothing()
    {
        var coach = NewCoach();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        var checkout = await _api.Checkout(Guid.CreateVersion7(), offer.ProgramId);

        var first = await _api.Pay(checkout, "evt_retried");
        var retry = await _api.Pay(checkout, "evt_retried");
        var otherEvent = await _api.Pay(checkout); // checkout.session.async_payment_succeeded after completed, say

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, retry.StatusCode, otherEvent.StatusCode));
        var order = _api.OrdersOf(checkout).Single();
        await _api.PublishOutbox();
        Assert.Single(await _api.Outbox(), m => m.Queue == OrderPaid.Queue && m.Message.Payload.Contains(order.Id.ToString(), StringComparison.Ordinal));
        Assert.Single(await _api.Outbox(), m => m.Queue == EmailNotification.Queue && m.Message.Payload.Contains(coach.TenantId.ToString(), StringComparison.Ordinal));
        Assert.Single(_api.CheckoutSessions.Transfers, t => t.Key == order.Id);
    }

    // The customer has paid, so a coach whose transfer fails does not hold up their order or the rest
    // of the cart; the share waits on a Paid order without a transfer id.
    [Fact]
    public async Task A_failed_transfer_still_completes_the_cart()
    {
        var (anna, ben) = (NewCoach(), NewCoach());
        var annaOffer = _api.Sellers.Sell(anna.TenantId, anna.Slug);
        var benOffer = _api.Sellers.Sell(ben.TenantId, ben.Slug);
        _api.CheckoutSessions.FailTransfersTo(anna.TenantId);
        var customer = Guid.CreateVersion7();
        var checkout = await _api.Checkout(customer, [annaOffer.ProgramId, benOffer.ProgramId]);

        var paid = await _api.Pay(checkout);

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.All(await MyOrders(customer), o => Assert.Equal(OrderStatus.Paid, o.Status));
        var orders = _api.OrdersOf(checkout).ToDictionary(o => o.TenantId);
        Assert.False(_api.CheckoutSessions.Transfers.ContainsKey(orders[anna.TenantId].Id));
        Assert.True(_api.CheckoutSessions.Transfers.ContainsKey(orders[ben.TenantId].Id));
    }

    // Sessions created before the cart used destination charges: paid, with nothing to transfer.
    [Fact]
    public async Task A_session_from_before_the_cart_still_completes_its_order()
    {
        var coach = NewCoach();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        var customer = Guid.CreateVersion7();
        var checkout = await _api.Checkout(customer, offer.ProgramId);
        var order = _api.OrdersOf(checkout).Single();

        var paid = await _api.SendWebhook(StripeEvents.LegacyCheckoutCompleted($"evt_{Guid.NewGuid():N}", checkout.SessionId, order.Id, coach.TenantId));

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.Equal(OrderStatus.Paid, (await MyOrders(customer)).Single().Status);
        Assert.False(_api.CheckoutSessions.Transfers.ContainsKey(order.Id));
    }

    [Fact]
    public async Task Webhooks_with_a_bad_or_stale_signature_are_rejected()
    {
        var payload = StripeEvents.CheckoutCompleted("evt_forged", "cs_test_x", [(Guid.CreateVersion7(), Guid.CreateVersion7())]);

        var forged = await _api.SendWebhook(payload, StripeEvents.Signature(payload, secret: "whsec_not_ours"));
        var stale = await _api.SendWebhook(payload, StripeEvents.Signature(payload, at: DateTimeOffset.UtcNow.AddMinutes(-6)));
        var missing = await _api.SendWebhook(payload, signature: "");

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task An_expired_checkout_fails_the_order_and_a_delayed_payment_waits()
    {
        var coach = NewCoach();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        var customer = Guid.CreateVersion7();
        var expired = await _api.Checkout(customer, offer.ProgramId, idempotencyKey: "a");
        var delayed = await _api.Checkout(customer, _api.Sellers.Sell(coach.TenantId, coach.Slug).ProgramId, idempotencyKey: "b");

        await _api.SendWebhook(StripeEvents.CheckoutExpired("evt_expired", expired.SessionId, _api.OrdersOf(expired).Pairs()));
        await _api.SendWebhook(StripeEvents.CheckoutCompleted("evt_delayed", delayed.SessionId, _api.OrdersOf(delayed).Pairs(), paymentStatus: "unpaid"));

        var orders = await MyOrders(customer);
        Assert.Equal(_api.OrdersOf(delayed).Single().Id, Assert.Single(orders).Id); // failed orders are not listed
        Assert.Equal(OrderStatus.Pending, orders[0].Status);
    }

    [Fact]
    public async Task A_program_is_bought_only_once()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        var other = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        await _api.Pay(await _api.Checkout(customer, offer.ProgramId));

        var again = await _api.StartCheckout(customer, [offer.ProgramId, other.ProgramId]);

        Assert.Equal((HttpStatusCode.Conflict, "owned"), (again.StatusCode, await ReasonOf(again)));
        Assert.Equal([offer.ProgramId], await ProgramIdsIn(again));
    }

    // Found on dev: three checkout pages open for one program, two of them paid. A new checkout now
    // closes the older pages for the same programs first.
    [Fact]
    public async Task A_new_checkout_closes_older_open_pages_for_the_same_program()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        var first = await _api.Checkout(customer, offer.ProgramId);

        var second = await _api.Checkout(customer, offer.ProgramId);

        Assert.True(_api.CheckoutSessions.Expired.ContainsKey(first.SessionId));
        Assert.False(_api.CheckoutSessions.Expired.ContainsKey(second.SessionId));
    }

    [Fact]
    public async Task An_older_page_already_paid_at_Stripe_counts_as_owned()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        var first = await _api.Checkout(customer, offer.ProgramId);
        _api.CheckoutSessions.PaidAtStripe[first.SessionId] = true; // paid, webhook not here yet

        var again = await _api.StartCheckout(customer, offer.ProgramId);

        Assert.Equal((HttpStatusCode.Conflict, "owned"), (again.StatusCode, await ReasonOf(again)));
    }

    // The safety net: if two pages were paid anyway, the second payment is refunded, the coach gets no
    // transfer for it, and the customer has one enrollment's worth of orders.
    [Fact]
    public async Task A_program_paid_twice_is_refunded_once()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        var first = await _api.Checkout(customer, offer.ProgramId);
        var second = await _api.Checkout(customer, offer.ProgramId);

        await _api.Pay(second);
        await _api.Pay(first); // paid before it could be closed

        var (paid, refunded) = (_api.OrdersOf(second).Single(), _api.OrdersOf(first).Single());
        Assert.Equal((offer.PriceAmount, "duplicate"), _api.CheckoutSessions.Refunds[refunded.Id]);
        Assert.False(_api.CheckoutSessions.Transfers.ContainsKey(refunded.Id));
        Assert.True(_api.CheckoutSessions.Transfers.ContainsKey(paid.Id));
        var orders = await MyOrders(customer);
        Assert.Equal(OrderStatus.Paid, orders.Single(o => o.Id == paid.Id).Status);
        Assert.Equal(OrderStatus.Refunded, orders.Single(o => o.Id == refunded.Id).Status);
        await _api.PublishOutbox();
        Assert.DoesNotContain(await _api.Outbox(), m => m.Queue == OrderPaid.Queue && m.Message.Payload.Contains(refunded.Id.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_coach_sees_the_sale()
    {
        var coach = NewCoach();
        var orderId = await _api.Buy(Guid.CreateVersion7(), coach);

        Assert.Equal(orderId, Assert.Single(await Sales(coach)).Id);
    }

    private async Task<List<MyOrder>> MyOrders(Guid customer)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/orders"), customer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MyOrdersPage>(Json, Cancel))!.Items;
    }

    private async Task<List<Sale>> Sales(Coach coach) =>
        (await (await _api.AsCoach(coach, "/sales")).Content.ReadFromJsonAsync<SalesPage>(Json, Cancel))!.Items;

    private static async Task<Guid[]> ProgramIdsIn(HttpResponseMessage problem) =>
        (await ProblemOf(problem)).GetProperty("programIds").EnumerateArray().Select(e => e.GetGuid()).ToArray();

    private static async Task<string?> ReasonOf(HttpResponseMessage problem) =>
        (await ProblemOf(problem)).GetProperty("reason").GetString();

    // Buffered, so a test can read the problem more than once.
    private static async Task<JsonElement> ProblemOf(HttpResponseMessage problem)
    {
        await problem.Content.LoadIntoBufferAsync(Cancel);
        return JsonDocument.Parse(await problem.Content.ReadAsStringAsync(Cancel)).RootElement;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    private sealed record MyOrder(Guid Id, Guid ProgramId, OrderStatus Status);

    private sealed record MyOrdersPage(List<MyOrder> Items);

    private sealed record Sale(Guid Id);

    private sealed record SalesPage(List<Sale> Items);
}
