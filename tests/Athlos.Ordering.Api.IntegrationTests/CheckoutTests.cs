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
    public async Task Checkout_creates_a_pending_order_paid_to_the_coach_with_the_platform_fee()
    {
        var coach = NewCoach();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug, price: 4900);
        var customer = Guid.CreateVersion7();

        var checkout = await _api.Checkout(customer, offer.ProgramId);

        var (order, destination) = _api.CheckoutSessions.Sessions[$"cs_test_{checkout.OrderId:N}"];
        Assert.Equal($"acct_test_{coach.TenantId:N}", destination);
        Assert.Equal((4900, 490, "EUR"), (order.Amount, order.PlatformFee, order.Currency));
        Assert.Equal(coach.TenantId, order.TenantId);
        Assert.Equal(OrderStatus.Pending, (await MyOrders(customer)).Single().Status);
    }

    [Fact]
    public async Task Only_published_programs_of_coaches_who_can_take_payments_can_be_bought()
    {
        var coach = NewCoach();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        _api.Sellers.CannotSell(coach.TenantId);

        var notForSale = await _api.StartCheckout(Guid.CreateVersion7(), Guid.CreateVersion7());
        var notPayable = await _api.StartCheckout(Guid.CreateVersion7(), offer.ProgramId);

        Assert.Equal(HttpStatusCode.NotFound, notForSale.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, notPayable.StatusCode);
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
    public async Task The_paid_webhook_marks_the_order_paid_and_publishes_OrderPaid()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var orderId = await _api.Buy(customer, coach);

        var published = await _api.PublishOutbox();

        Assert.Equal(1, published);
        var (queue, message) = Assert.Single(_api.Messages.Sent, m => m.Message.Payload.Contains(orderId.ToString(), StringComparison.Ordinal));
        var orderPaid = JsonSerializer.Deserialize<OrderPaid>(message.Payload, Json)!;
        Assert.Equal(OrderPaid.Queue, queue);
        Assert.Equal((orderId, coach.TenantId, customer), (orderPaid.OrderId, orderPaid.TenantId, orderPaid.CustomerId));
        Assert.Equal(OrderStatus.Paid, (await MyOrders(customer)).Single().Status);
        Assert.Equal(0, await _api.PublishOutbox()); // already sent
    }

    [Fact]
    public async Task A_retried_webhook_changes_nothing()
    {
        var coach = NewCoach();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        var checkout = await _api.Checkout(Guid.CreateVersion7(), offer.ProgramId);
        var payload = StripeEvents.CheckoutCompleted("evt_retried", $"cs_test_{checkout.OrderId:N}", checkout.OrderId, coach.TenantId);

        var first = await _api.SendWebhook(payload);
        var retry = await _api.SendWebhook(payload);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, retry.StatusCode));
        Assert.Equal(1, await _api.PublishOutbox());
    }

    [Fact]
    public async Task Webhooks_with_a_bad_or_stale_signature_are_rejected()
    {
        var payload = StripeEvents.CheckoutCompleted("evt_forged", "cs_test_x", Guid.CreateVersion7(), Guid.CreateVersion7());

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

        await _api.SendWebhook(StripeEvents.CheckoutExpired("evt_expired", $"cs_test_{expired.OrderId:N}", expired.OrderId, coach.TenantId));
        await _api.SendWebhook(StripeEvents.CheckoutCompleted("evt_delayed", $"cs_test_{delayed.OrderId:N}", delayed.OrderId, coach.TenantId, paymentStatus: "unpaid"));

        var orders = await MyOrders(customer);
        Assert.Equal(delayed.OrderId, Assert.Single(orders).Id); // failed orders are not listed
        Assert.Equal(OrderStatus.Pending, orders[0].Status);
    }

    [Fact]
    public async Task A_program_is_bought_only_once()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var offer = _api.Sellers.Sell(coach.TenantId, coach.Slug);
        var first = await _api.Checkout(customer, offer.ProgramId);
        await _api.SendWebhook(StripeEvents.CheckoutCompleted("evt_once", $"cs_test_{first.OrderId:N}", first.OrderId, coach.TenantId));

        var again = await _api.StartCheckout(customer, offer.ProgramId);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task The_coach_sees_the_sale()
    {
        var coach = NewCoach();
        var orderId = await _api.Buy(Guid.CreateVersion7(), coach);

        var sales = await (await _api.AsCoach(coach, "/sales")).Content.ReadFromJsonAsync<SalesPage>(Json, Cancel);

        Assert.Equal(orderId, Assert.Single(sales!.Items).Id);
    }

    private async Task<List<MyOrder>> MyOrders(Guid customer)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/orders"), customer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MyOrdersPage>(Json, Cancel))!.Items;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    private sealed record MyOrder(Guid Id, Guid ProgramId, OrderStatus Status);

    private sealed record MyOrdersPage(List<MyOrder> Items);

    private sealed record Sale(Guid Id);

    private sealed record SalesPage(List<Sale> Items);
}
