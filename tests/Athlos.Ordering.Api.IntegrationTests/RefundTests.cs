using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Athlos.Messaging.Contracts;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Payouts;
using Athlos.Tenancy;
using Athlos.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Athlos.Testing.OrderingArrange;

namespace Athlos.Ordering.Api.IntegrationTests;

// Refunds, disputes and coach payouts (design §2.2): with the cart the platform takes the payment, so
// it gives money back, takes the coach's share back, and pays shares whose transfer failed.
public sealed class RefundTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly OrderingApiFactory _api = new(databases);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_owner_refunds_a_sale_the_customer_is_paid_back_and_the_coachs_share_is_taken_back()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var orderId = await _api.Buy(customer, coach);

        var refunded = await Refund(coach, orderId, TenantRole.Owner);
        var again = await Refund(coach, orderId, TenantRole.Owner);

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (refunded.StatusCode, again.StatusCode));
        Assert.Equal((4900L, "requested_by_customer"), _api.CheckoutSessions.Refunds[orderId]);
        Assert.True(_api.CheckoutSessions.Reversals.ContainsKey(orderId));
        Assert.Equal(OrderStatus.Refunded, (await MyOrders(customer)).Single().Status);
        var sale = Assert.Single(await Sales(coach));
        Assert.Equal("Refunded", sale.GetProperty("status").GetString());
        await _api.PublishOutbox();
        var revoked = Assert.Single(await _api.Outbox(), m => m.Queue == OrderRefunded.Queue && m.Message.Payload.Contains(orderId.ToString(), StringComparison.Ordinal));
        var message = JsonSerializer.Deserialize<OrderRefunded>(revoked.Message.Payload, Json)!;
        Assert.Equal((orderId, coach.TenantId, customer), (message.OrderId, message.TenantId, message.CustomerId));
        var email = Assert.Single((await _api.Outbox())
            .Where(m => m.Queue == EmailNotification.Queue)
            .Select(m => JsonSerializer.Deserialize<EmailNotification>(m.Message.Payload, Json)!),
            e => e.Template == EmailTemplates.RefundConfirmation && e.Data["coachSlug"] == coach.Slug);
        Assert.Equal((1, "49.00 EUR"), (email.To.Count, email.Data["price"]));
    }

    // The coach's refund window (14 days after payment by default, the EU withdrawal period).
    [Fact]
    public async Task A_sale_older_than_the_refund_window_cannot_be_refunded_by_the_coach()
    {
        var coach = NewCoach();
        var orderId = await _api.Buy(Guid.CreateVersion7(), coach);
        await using (var connection = new Npgsql.NpgsqlConnection(databases.OwnerConnectionString("ordering")))
        {
            await connection.OpenAsync(Cancel);
            await using var command = new Npgsql.NpgsqlCommand("UPDATE orders SET paid_at = now() - interval '15 days' WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", orderId);
            await command.ExecuteNonQueryAsync(Cancel);
        }

        var response = await Refund(coach, orderId, TenantRole.Owner);
        var sale = Assert.Single(await Sales(coach));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(_api.CheckoutSessions.Refunds.ContainsKey(orderId));
        Assert.True(sale.GetProperty("refundableUntil").GetDateTimeOffset() < DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Staff_cannot_refund_and_nothing_changes()
    {
        var coach = NewCoach();
        var orderId = await _api.Buy(Guid.CreateVersion7(), coach);

        var response = await Refund(coach, orderId, TenantRole.Staff);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(_api.CheckoutSessions.Refunds.ContainsKey(orderId));
    }

    // A full refund made in the Stripe dashboard refunds every order of that payment in Athlos too.
    [Fact]
    public async Task A_full_refund_at_Stripe_refunds_every_order_of_the_payment()
    {
        var (anna, ben) = (NewCoach(), NewCoach());
        var customer = Guid.CreateVersion7();
        var checkout = await _api.Checkout(customer, [_api.Sellers.Sell(anna.TenantId, anna.Slug).ProgramId, _api.Sellers.Sell(ben.TenantId, ben.Slug).ProgramId]);
        await _api.Pay(checkout);
        var paymentIntent = FakeCheckoutSessions.PaymentIntentOf(checkout.SessionId);

        var webhook = await _api.SendWebhook(StripeEvents.ChargeRefunded($"evt_{Guid.NewGuid():N}", paymentIntent, amount: 9800, amountRefunded: 9800));

        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        Assert.All(await MyOrders(customer), o => Assert.Equal(OrderStatus.Refunded, o.Status));
        Assert.All(_api.OrdersOf(checkout), o => Assert.True(_api.CheckoutSessions.Reversals.ContainsKey(o.Id)));
        Assert.Empty(_api.CheckoutSessions.Refunds.Keys.Intersect(_api.OrdersOf(checkout).Select(o => o.Id))); // Stripe already paid it back
    }

    [Fact]
    public async Task A_partial_refund_at_Stripe_and_a_won_dispute_change_nothing_and_a_lost_dispute_refunds()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var checkout = await _api.Checkout(customer, _api.Sellers.Sell(coach.TenantId, coach.Slug).ProgramId);
        await _api.Pay(checkout);
        var paymentIntent = FakeCheckoutSessions.PaymentIntentOf(checkout.SessionId);

        await _api.SendWebhook(StripeEvents.ChargeRefunded($"evt_{Guid.NewGuid():N}", paymentIntent, amount: 4900, amountRefunded: 1000));
        await _api.SendWebhook(StripeEvents.DisputeClosed($"evt_{Guid.NewGuid():N}", paymentIntent, status: "won"));
        var stillPaid = (await MyOrders(customer)).Single().Status;
        await _api.SendWebhook(StripeEvents.DisputeClosed($"evt_{Guid.NewGuid():N}", paymentIntent, status: "lost"));

        Assert.Equal((OrderStatus.Paid, OrderStatus.Refunded), (stillPaid, (await MyOrders(customer)).Single().Status));
    }

    // The payment's own event failed (a cold start, say) and Stripe is retrying it when a refund made in
    // the dashboard arrives: the refund waits, so the late payment event cannot hand out access and the
    // coach's share for money already returned.
    [Fact]
    public async Task A_refund_at_Stripe_that_arrives_before_the_payment_waits_for_it()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var checkout = await _api.Checkout(customer, _api.Sellers.Sell(coach.TenantId, coach.Slug).ProgramId);
        var refundEvent = StripeEvents.ChargeRefunded($"evt_{Guid.NewGuid():N}", FakeCheckoutSessions.PaymentIntentOf(checkout.SessionId), amount: 4900, amountRefunded: 4900);

        var early = await _api.SendWebhook(refundEvent);
        var pendingMeanwhile = (await MyOrders(customer)).Single().Status;
        await _api.Pay(checkout);
        var retried = await _api.SendWebhook(refundEvent);

        Assert.Equal((HttpStatusCode.InternalServerError, OrderStatus.Pending), (early.StatusCode, pendingMeanwhile));
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(OrderStatus.Refunded, (await MyOrders(customer)).Single().Status);
        Assert.True(_api.CheckoutSessions.Reversals.ContainsKey(_api.OrdersOf(checkout).Single().Id));
    }

    // The coach's share is taken back first: if Stripe refuses, the customer is not refunded either, and
    // the coach can try again.
    [Fact]
    public async Task A_coach_refund_changes_nothing_when_Stripe_cannot_take_the_share_back()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var orderId = await _api.Buy(customer, coach);
        _api.CheckoutSessions.FailReversalsFrom(coach.TenantId);

        var response = await Refund(coach, orderId, TenantRole.Owner);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(_api.CheckoutSessions.Refunds.ContainsKey(orderId));
        Assert.Equal(OrderStatus.Paid, (await MyOrders(customer)).Single().Status);
    }

    // Money already returned at Stripe: the access goes even if the share cannot be taken back.
    [Fact]
    public async Task A_refund_at_Stripe_completes_when_the_share_cannot_be_taken_back()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var checkout = await _api.Checkout(customer, _api.Sellers.Sell(coach.TenantId, coach.Slug).ProgramId);
        await _api.Pay(checkout);
        _api.CheckoutSessions.FailReversalsFrom(coach.TenantId);

        var webhook = await _api.SendWebhook(StripeEvents.ChargeRefunded(
            $"evt_{Guid.NewGuid():N}", FakeCheckoutSessions.PaymentIntentOf(checkout.SessionId), amount: 4900, amountRefunded: 4900));

        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        Assert.Equal(OrderStatus.Refunded, (await MyOrders(customer)).Single().Status);
    }

    // A partial refund in the dashboard refunds the one program its metadata names; an unnamed one is
    // left for a person to settle.
    [Fact]
    public async Task A_dashboard_refund_naming_an_order_refunds_only_that_program()
    {
        var (anna, ben) = (NewCoach(), NewCoach());
        var customer = Guid.CreateVersion7();
        var checkout = await _api.Checkout(customer, [_api.Sellers.Sell(anna.TenantId, anna.Slug).ProgramId, _api.Sellers.Sell(ben.TenantId, ben.Slug).ProgramId]);
        await _api.Pay(checkout);
        var paymentIntent = FakeCheckoutSessions.PaymentIntentOf(checkout.SessionId);
        var annas = _api.OrdersOf(checkout).Single(o => o.TenantId == anna.TenantId).Id;
        _api.CheckoutSessions.RefundInDashboard(paymentIntent, orderId: null);
        _api.CheckoutSessions.RefundInDashboard(paymentIntent, annas);

        var webhook = await _api.SendWebhook(StripeEvents.ChargeRefunded($"evt_{Guid.NewGuid():N}", paymentIntent, amount: 9800, amountRefunded: 5900));

        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        var statuses = (await MyOrders(customer)).ToDictionary(o => o.Id, o => o.Status);
        Assert.Equal(OrderStatus.Refunded, statuses[annas]);
        Assert.Equal(OrderStatus.Paid, Assert.Single(statuses, s => s.Key != annas).Value);
    }

    // A customer who deleted their account has no address left: the refund email is not queued.
    [Fact]
    public async Task No_refund_email_is_queued_for_a_customer_who_deleted_their_account()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var orderId = await _api.Buy(customer, coach);
        Assert.Equal(HttpStatusCode.NoContent, (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{customer}"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Refund(coach, orderId, TenantRole.Owner)).StatusCode);

        var outbox = await _api.Outbox();
        Assert.Contains(outbox, m => m.Queue == OrderRefunded.Queue && m.Message.Payload.Contains(orderId.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(outbox, m => m.Queue == EmailNotification.Queue
            && JsonSerializer.Deserialize<EmailNotification>(m.Message.Payload, Json)!.Template == EmailTemplates.RefundConfirmation
            && m.Message.Payload.Contains(coach.Slug, StringComparison.Ordinal));
    }

    // A failed transfer waits in payout_retries; a later pass pays the coach once their account works.
    [Fact]
    public async Task A_failed_payout_is_retried_until_the_coach_is_paid()
    {
        var coach = NewCoach();
        _api.CheckoutSessions.FailTransfersTo(coach.TenantId);
        var orderId = await _api.Buy(Guid.CreateVersion7(), coach);
        var waiting = await AdminPayouts();
        Assert.Contains(waiting, p => p.GetProperty("orderId").GetGuid() == orderId);

        var stillFailing = await RetryNow(orderId);
        _api.CheckoutSessions.ResumeTransfersTo(coach.TenantId);
        var paid = await RetryNow(orderId);

        Assert.False(stillFailing);
        Assert.True(paid);
        Assert.True(_api.CheckoutSessions.Transfers.ContainsKey(orderId));
        Assert.DoesNotContain(await AdminPayouts(), p => p.GetProperty("orderId").GetGuid() == orderId);
    }

    [Fact]
    public async Task A_refund_drops_a_share_still_waiting_for_its_transfer()
    {
        var coach = NewCoach();
        _api.CheckoutSessions.FailTransfersTo(coach.TenantId);
        var orderId = await _api.Buy(Guid.CreateVersion7(), coach);

        Assert.Equal(HttpStatusCode.NoContent, (await Refund(coach, orderId, TenantRole.Owner)).StatusCode);

        Assert.False(_api.CheckoutSessions.Reversals.ContainsKey(orderId)); // nothing was transferred
        Assert.DoesNotContain(await AdminPayouts(), p => p.GetProperty("orderId").GetGuid() == orderId);
        Assert.Equal(0, await PayoutRetries.RetryDueAsync(_api.Services.GetRequiredService<IServiceScopeFactory>(), Cancel));
    }

    [Theory]
    [InlineData("GET", "/api/v1/admin/payouts")]
    [InlineData("POST", "/api/v1/admin/payouts/0199a000-0000-7000-8000-000000000001/retry")]
    public async Task Payouts_are_for_the_platform_admin_only(string method, string path)
    {
        var coach = await _api.SendAsGateway(new HttpRequestMessage(new HttpMethod(method), path), Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.Forbidden, coach.StatusCode);
    }

    private Task<HttpResponseMessage> Refund(Coach coach, Guid orderId, TenantRole role) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/studio/{coach.Slug}/sales/{orderId}/refund"), coach.OwnerId, coach.TenantId, role);

    private async Task<List<JsonElement>> AdminPayouts()
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/payouts"), Guid.CreateVersion7(), appRoles: ["Platform.Admin"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<JsonElement>>(Cancel))!;
    }

    private async Task<bool> RetryNow(Guid orderId)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/payouts/{orderId}/retry"), Guid.CreateVersion7(), appRoles: ["Platform.Admin"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("paid").GetBoolean();
    }

    private async Task<List<MyOrder>> MyOrders(Guid customer) =>
        (await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/orders"), customer)).Content.ReadFromJsonAsync<MyOrdersPage>(Json, Cancel))!.Items;

    private async Task<List<JsonElement>> Sales(Coach coach) =>
        (await (await _api.AsCoach(coach, "/sales")).Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("items").EnumerateArray().ToList();

    private sealed record MyOrder(Guid Id, OrderStatus Status);

    private sealed record MyOrdersPage(List<MyOrder> Items);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
