using System.Net;
using Athlos.Messaging.Contracts;
using Athlos.Ordering.Api.Data;
using Athlos.Testing;
using static Athlos.Testing.OrderingArrange;

namespace Athlos.Ordering.Api.IntegrationTests;

// What happens to sales and open checkouts when a workspace or an account goes (design §3.4, §5.2),
// and when Stripe cannot open a payment page.
public sealed class ClosingTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly OrderingApiFactory _api = new(databases);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // A sale inside the refund window keeps the workspace until it runs out (or is refunded).
    [Fact]
    public async Task A_workspace_with_a_refundable_sale_cannot_be_deleted_until_the_window_ends()
    {
        var coach = NewCoach();
        var orderId = await _api.Buy(Guid.CreateVersion7(), coach);

        var whileRefundable = await PrepareDeletion(coach);
        await PaidDaysAgo(orderId, 15);
        var afterwards = await PrepareDeletion(coach);

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.NoContent), (whileRefundable.StatusCode, afterwards.StatusCode));
    }

    // Nobody can pay for a program about to be erased: open payment pages are closed first, and one
    // already paid holds the deletion until its payment is recorded.
    [Fact]
    public async Task Deleting_a_workspace_closes_its_open_checkouts()
    {
        var (open, paying) = (NewCoach(), NewCoach());
        var openCheckout = await _api.Checkout(Guid.CreateVersion7(), _api.Sellers.Sell(open.TenantId, open.Slug).ProgramId);
        var payingCheckout = await _api.Checkout(Guid.CreateVersion7(), _api.Sellers.Sell(paying.TenantId, paying.Slug).ProgramId);
        _api.CheckoutSessions.PaidAtStripe[payingCheckout.SessionId] = true;

        var closed = await PrepareDeletion(open);
        var held = await PrepareDeletion(paying);

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.Conflict), (closed.StatusCode, held.StatusCode));
        Assert.True(_api.CheckoutSessions.Expired.ContainsKey(openCheckout.SessionId));
    }

    // A deleted account cannot be charged: its open pages close, and a payment that still arrives is
    // refunded without granting access or paying the coach.
    [Fact]
    public async Task A_deleted_customer_is_not_charged_for_an_open_checkout()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var checkout = await _api.Checkout(customer, _api.Sellers.Sell(coach.TenantId, coach.Slug).ProgramId);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{customer}"))).StatusCode);
        var expired = _api.CheckoutSessions.Expired.ContainsKey(checkout.SessionId);
        await _api.Pay(checkout); // paid in the moment before the page closed

        var orderId = _api.OrdersOf(checkout).Single().Id;
        Assert.True(expired);
        Assert.Equal("requested_by_customer", _api.CheckoutSessions.Refunds[orderId].Reason);
        Assert.False(_api.CheckoutSessions.Transfers.ContainsKey(orderId));
        Assert.DoesNotContain(await _api.Outbox(), m => m.Queue == OrderPaid.Queue && m.Message.Payload.Contains(orderId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Checkout_answers_502_when_Stripe_cannot_open_the_payment_page()
    {
        var coach = NewCoach();
        var programId = _api.Sellers.Sell(coach.TenantId, coach.Slug).ProgramId;
        _api.CheckoutSessions.FailCheckouts = true;

        var response = await _api.StartCheckout(Guid.CreateVersion7(), programId);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    private Task<HttpResponseMessage> PrepareDeletion(Coach coach) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/internal/tenants/{coach.TenantId}/prepare-deletion"));

    private async Task PaidDaysAgo(Guid orderId, int days)
    {
        await using var connection = new Npgsql.NpgsqlConnection(databases.OwnerConnectionString("ordering"));
        await connection.OpenAsync(Cancel);
        await using var command = new Npgsql.NpgsqlCommand("UPDATE orders SET paid_at = now() - make_interval(days => @days) WHERE id = @id", connection);
        command.Parameters.AddWithValue("days", days);
        command.Parameters.AddWithValue("id", orderId);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
