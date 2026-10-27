using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Athlos.Messaging.Contracts;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Refunds;
using Athlos.Tenancy;
using Athlos.Testing;
using Npgsql;
using static Athlos.Testing.OrderingArrange;

namespace Athlos.Ordering.Api.IntegrationTests;

// Refunds Athlos stands behind (design §2.2): the customer asks within the refund window, the coach is
// told and refunds it, and after three working days the platform admin refunds it instead.
public sealed class RefundRequestTests(AthlosDatabases databases) : IAsyncLifetime
{
    private static readonly string[] AdminRole = ["Platform.Admin"];

    private readonly OrderingApiFactory _api = new(databases);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_is_shown_to_the_coach_and_the_customer_and_emailed_once()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var orderId = await _api.Buy(customer, coach);

        var first = await RequestRefund(customer, orderId, "  The plan does not suit my knees  ");
        var again = await RequestRefund(customer, orderId, "Second try");

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (first.StatusCode, again.StatusCode));
        var mine = Assert.Single((await MyOrders(customer)).EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, mine.GetProperty("refundRequestedAt").ValueKind);
        var sale = Assert.Single((await (await _api.AsCoach(coach, "/sales")).Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("items").EnumerateArray());
        Assert.Equal("The plan does not suit my knees", sale.GetProperty("refundRequestNote").GetString());
        var emails = (await _api.Outbox())
            .Where(m => m.Queue == EmailNotification.Queue)
            .Select(m => JsonSerializer.Deserialize<EmailNotification>(m.Message.Payload, Json)!)
            .Where(e => e.Template == EmailTemplates.RefundRequested && e.ToOwnersOfTenant == coach.TenantId)
            .ToList();
        var email = Assert.Single(emails);
        Assert.Equal("The plan does not suit my knees", email.Data["note"]);
        var open = Assert.Single(await OpenRequests(), r => r.GetProperty("orderId").GetGuid() == orderId);
        Assert.True(open.GetProperty("dueAt").GetDateTimeOffset() > open.GetProperty("requestedAt").GetDateTimeOffset().AddDays(2.9));
    }

    // Only the customer's own paid order, within the window, and not one refunded already.
    [Fact]
    public async Task Requests_are_for_the_customers_own_paid_orders_within_the_window()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var (late, refunded, mine) = (await _api.Buy(customer, coach), await _api.Buy(customer, coach), await _api.Buy(customer, coach));
        await PaidDaysAgo(late, 15);
        Assert.Equal(HttpStatusCode.NoContent, (await Refund(coach, refunded)).StatusCode);

        var someoneElse = await RequestRefund(Guid.CreateVersion7(), mine);
        var tooLate = await RequestRefund(customer, late);
        var alreadyRefunded = await RequestRefund(customer, refunded);
        var unknown = await RequestRefund(customer, Guid.CreateVersion7());

        Assert.Equal(
            (HttpStatusCode.NotFound, HttpStatusCode.Conflict, HttpStatusCode.Conflict, HttpStatusCode.NotFound),
            (someoneElse.StatusCode, tooLate.StatusCode, alreadyRefunded.StatusCode, unknown.StatusCode));
    }

    // A sale the customer asked about stays refundable for the coach after the window, and the coach's
    // refund answers the request.
    [Fact]
    public async Task The_coach_can_refund_a_requested_sale_after_the_window_and_that_closes_the_request()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var orderId = await _api.Buy(customer, coach);
        await RequestRefund(customer, orderId);
        await PaidDaysAgo(orderId, 20);

        var refunded = await Refund(coach, orderId);

        Assert.Equal(HttpStatusCode.NoContent, refunded.StatusCode);
        Assert.DoesNotContain(await OpenRequests(), r => r.GetProperty("orderId").GetGuid() == orderId);
    }

    // The admin waits for the due date, then refunds with a reason in the audit trail; the coach's share
    // is taken back, and the coach is told why.
    [Fact]
    public async Task The_admin_refunds_a_request_the_coach_left_open_once_it_is_due()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var orderId = await _api.Buy(customer, coach);
        await RequestRefund(customer, orderId);
        var admin = Guid.CreateVersion7();

        var early = await AdminRefund(admin, orderId, "Coach did not answer");
        await DueDaysAgo(orderId, 1);
        var withoutReason = await AdminRefund(admin, orderId, "");
        var coachTries = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/refund-requests/{orderId}/refund") { Content = JsonContent.Create(new { reason = "Mine" }) },
            coach.OwnerId, coach.TenantId, TenantRole.Owner);
        var refunded = await AdminRefund(admin, orderId, "Coach did not answer");
        var again = await AdminRefund(admin, orderId, "Coach did not answer");

        Assert.Equal(
            (HttpStatusCode.Conflict, HttpStatusCode.BadRequest, HttpStatusCode.Forbidden, HttpStatusCode.NoContent, HttpStatusCode.NotFound),
            (early.StatusCode, withoutReason.StatusCode, coachTries.StatusCode, refunded.StatusCode, again.StatusCode));
        Assert.Equal(OrderStatus.Refunded.ToString(), Assert.Single((await MyOrders(customer)).EnumerateArray()).GetProperty("status").GetString());
        Assert.True(_api.CheckoutSessions.Reversals.ContainsKey(orderId));
        Assert.DoesNotContain(await OpenRequests(), r => r.GetProperty("orderId").GetGuid() == orderId);
        var log = await (await AsAdmin(admin, HttpMethod.Get, "/api/v1/admin/payouts/actions")).Content.ReadFromJsonAsync<List<JsonElement>>(Cancel);
        var action = Assert.Single(log!, a => a.GetProperty("detail").GetString()!.Contains(orderId.ToString(), StringComparison.Ordinal));
        Assert.Equal(("RefundIssued", admin, "Coach did not answer"), (action.GetProperty("action").GetString(), action.GetProperty("adminId").GetGuid(), action.GetProperty("reason").GetString()));
        var coachEmail = (await _api.Outbox())
            .Where(m => m.Queue == EmailNotification.Queue)
            .Select(m => JsonSerializer.Deserialize<EmailNotification>(m.Message.Payload, Json)!)
            .Single(e => e.Template == EmailTemplates.SaleRefunded && e.ToOwnersOfTenant == coach.TenantId);
        Assert.Contains("three working days", coachEmail.Data["reason"], StringComparison.Ordinal);
    }

    // The refund promise holds even when Stripe will not take the coach's share back.
    [Fact]
    public async Task The_admins_refund_goes_through_when_the_share_cannot_be_taken_back()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var orderId = await _api.Buy(customer, coach);
        await RequestRefund(customer, orderId);
        await DueDaysAgo(orderId, 1);
        _api.CheckoutSessions.FailReversalsFrom(coach.TenantId);

        var refunded = await AdminRefund(Guid.CreateVersion7(), orderId, "Promise to the customer");

        Assert.Equal(HttpStatusCode.NoContent, refunded.StatusCode);
        Assert.True(_api.CheckoutSessions.Refunds.ContainsKey(orderId));
        Assert.Equal(OrderStatus.Refunded.ToString(), Assert.Single((await MyOrders(customer)).EnumerateArray()).GetProperty("status").GetString());
    }

    // Deleting the account (design §5.2) clears the customer's note with their address.
    [Fact]
    public async Task Erasing_the_customer_clears_their_note()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var orderId = await _api.Buy(customer, coach);
        await RequestRefund(customer, orderId, "Call me on my phone");

        Assert.Equal(HttpStatusCode.NoContent, (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{customer}"))).StatusCode);

        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("ordering"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand("SELECT refund_request_note FROM orders WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", orderId);
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync(Cancel));
    }

    [Theory]
    [InlineData("2026-10-23T10:00:00Z", "2026-10-28T10:00:00Z")] // Friday: Monday, Tuesday, Wednesday
    [InlineData("2026-10-26T10:00:00Z", "2026-10-29T10:00:00Z")] // Monday: Thursday
    [InlineData("2026-10-25T10:00:00Z", "2026-10-28T10:00:00Z")] // Sunday: Wednesday
    public void Three_working_days_skip_the_weekend(string start, string due) =>
        Assert.Equal(DateTimeOffset.Parse(due, System.Globalization.CultureInfo.InvariantCulture),
            RefundRequests.AddWorkingDays(DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture), RefundRequests.WorkingDaysForTheCoach));

    [Theory]
    [InlineData("GET", "/api/v1/admin/refund-requests")]
    [InlineData("POST", "/api/v1/admin/refund-requests/0199a000-0000-7000-8000-000000000001/refund")]
    public async Task Refund_requests_are_for_the_platform_admin_only(string method, string path)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(new HttpMethod(method), path), Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private Task<HttpResponseMessage> RequestRefund(Guid customer, Guid orderId, string? note = null) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/orders/{orderId}/refund-request") { Content = JsonContent.Create(new { note }) }, customer);

    private Task<HttpResponseMessage> Refund(Coach coach, Guid orderId) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/studio/{coach.Slug}/sales/{orderId}/refund"), coach.OwnerId, coach.TenantId, TenantRole.Owner);

    private Task<HttpResponseMessage> AdminRefund(Guid admin, Guid orderId, string reason) =>
        _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/refund-requests/{orderId}/refund") { Content = JsonContent.Create(new { reason }) },
            admin, appRoles: AdminRole);

    private Task<HttpResponseMessage> AsAdmin(Guid admin, HttpMethod method, string path) =>
        _api.SendAsGateway(new HttpRequestMessage(method, path), admin, appRoles: AdminRole);

    private async Task<List<JsonElement>> OpenRequests()
    {
        var response = await AsAdmin(Guid.CreateVersion7(), HttpMethod.Get, "/api/v1/admin/refund-requests");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<JsonElement>>(Cancel))!;
    }

    private async Task<JsonElement> MyOrders(Guid customer) =>
        (await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/orders"), customer)).Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("items");

    private Task PaidDaysAgo(Guid orderId, int days) =>
        Owner("UPDATE orders SET paid_at = now() - make_interval(days => @days) WHERE id = @id", orderId, days);

    private Task DueDaysAgo(Guid orderId, int days) =>
        Owner("UPDATE refund_requests SET due_at = now() - make_interval(days => @days) WHERE order_id = @id", orderId, days);

    private async Task Owner(string sql, Guid orderId, int days)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("ordering"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("days", days);
        command.Parameters.AddWithValue("id", orderId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Cancel));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
