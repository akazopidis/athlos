using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Testing;
using Npgsql;
using static Athlos.Testing.OrderingArrange;

namespace Athlos.IsolationTests;

// Design §1 and §3.3 for ordering-api: an order belongs to the coach's tenant AND to the customer.
// The coach sees their tenant's sales, the customer sees their own orders across coaches, and
// nobody sees anything else: through the endpoints, and in PostgreSQL alone.
public sealed class OrderingIsolationTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly OrderingApiFactory _api = new(databases);
    private readonly Coach _a = NewCoach();
    private readonly Coach _b = NewCoach();
    private readonly Guid _alice = Guid.CreateVersion7();
    private readonly Guid _bob = Guid.CreateVersion7();
    private Guid _aliceBuysFromA;
    private Guid _aliceBuysFromB;
    private Guid _bobBuysFromA;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _aliceBuysFromA = await _api.Buy(_alice, _a);
        _aliceBuysFromB = await _api.Buy(_alice, _b);
        _bobBuysFromA = await _api.Buy(_bob, _a);
    }

    // Deleting Alice's account (design §5.2): her orders stay with both coaches for accounting, with
    // her address gone; Bob's order is untouched. Counted as the superuser, so RLS cannot hide a row.
    [Fact]
    public async Task Erasing_Alice_anonymizes_her_orders_with_every_coach_and_nothing_of_Bobs()
    {
        var erased = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{_alice}"));
        var again = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{_alice}"));

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (erased.StatusCode, again.StatusCode));
        Assert.Equal(Athlos.Ordering.Api.Features.Customers.EraseCustomer.ErasedEmail, await EmailOf(_aliceBuysFromA));
        Assert.Equal(Athlos.Ordering.Api.Features.Customers.EraseCustomer.ErasedEmail, await EmailOf(_aliceBuysFromB));
        Assert.NotEqual(Athlos.Ordering.Api.Features.Customers.EraseCustomer.ErasedEmail, await EmailOf(_bobBuysFromA));
    }

    [Fact]
    public async Task Unsigned_erase_requests_are_rejected()
    {
        var response = await _api.CreateClient().DeleteAsync($"/internal/customers/{_alice}", Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<string> EmailOf(Guid orderId)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("ordering"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand("SELECT customer_email FROM orders WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", orderId);
        return (string)(await command.ExecuteScalarAsync(Cancel))!;
    }

    public static TheoryData<string, string> AllEndpoints => new()
    {
        { "POST", "/api/v1/orders/checkout" },
        { "GET", "/api/v1/me/orders" },
        { "GET", "/api/v1/studio/some-coach/sales" },
        { "GET", "/api/v1/studio/some-coach/sales/summary" },
        { "POST", $"/api/v1/studio/some-coach/sales/{Guid.Empty}/refund" },
        { "POST", "/webhooks/stripe" },
        { "GET", "/api/v1/admin/payouts" },
        { "POST", $"/api/v1/admin/payouts/{Guid.Empty}/retry" },
        { "POST", "/api/v1/orders/discount" },
        { "GET", $"/api/v1/me/orders/receipts/{Guid.Empty}" },
        { "GET", "/api/v1/studio/some-coach/discounts" },
        { "POST", "/api/v1/studio/some-coach/discounts" },
        { "PUT", $"/api/v1/studio/some-coach/discounts/{Guid.Empty}" },
        { "DELETE", $"/api/v1/studio/some-coach/discounts/{Guid.Empty}" },
        { "GET", "/api/v1/me/cart" },
        { "PUT", "/api/v1/me/cart" },
    };

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task Unsigned_requests_are_rejected(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TrustedHeaders.TenantId, _a.TenantId.ToString());
        request.Headers.Add(TrustedHeaders.UserRole, "Owner");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(_alice));

        var response = await _api.CreateClient().SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_customer_sees_their_orders_from_every_coach_and_nobody_elses()
    {
        var alices = await Body(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/orders"), _alice));

        Assert.Contains(_aliceBuysFromA.ToString(), alices, StringComparison.Ordinal);
        Assert.Contains(_aliceBuysFromB.ToString(), alices, StringComparison.Ordinal);
        Assert.DoesNotContain(_bobBuysFromA.ToString(), alices, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_coach_sees_their_sales_only()
    {
        var bs = await Body(await _api.AsCoach(_b, "/sales"));

        Assert.Contains(_aliceBuysFromB.ToString(), bs, StringComparison.Ordinal);
        Assert.DoesNotContain(_aliceBuysFromA.ToString(), bs, StringComparison.Ordinal);
        Assert.DoesNotContain(_bobBuysFromA.ToString(), bs, StringComparison.Ordinal);
    }

    // The studio's numbers (design §8): B asking for A's by A's address gets zeros, not A's totals.
    [Fact]
    public async Task B_asking_for_As_sales_summary_gets_zeros()
    {
        var response = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{_a.Slug}/sales/summary"), _b.OwnerId, _b.TenantId, TenantRole.Owner);
        var mine = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{_a.Slug}/sales/summary"), _a.OwnerId, _a.TenantId, TenantRole.Owner);

        var theirs = System.Text.Json.JsonDocument.Parse(await Body(response)).RootElement;
        var own = System.Text.Json.JsonDocument.Parse(await Body(mine)).RootElement;
        Assert.Equal((0, 0L), (theirs.GetProperty("sales").GetInt32(), theirs.GetProperty("earnings").GetInt64()));
        Assert.True(own.GetProperty("sales").GetInt32() > 0);
    }

    [Fact]
    public async Task B_asking_for_As_sales_by_slug_gets_nothing()
    {
        var response = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{_a.Slug}/sales"), _b.OwnerId, _b.TenantId, TenantRole.Owner);

        var body = await Body(response);
        Assert.DoesNotContain(_aliceBuysFromA.ToString(), body, StringComparison.Ordinal);
        Assert.DoesNotContain(_bobBuysFromA.ToString(), body, StringComparison.Ordinal);
    }

    // Even a genuinely signed event cannot move an order across tenants: each order is looked up
    // inside the tenant named next to it in the metadata, where another tenant's order does not exist.
    [Fact]
    public async Task A_webhook_naming_the_wrong_tenant_cannot_touch_the_order()
    {
        var offer = _api.Sellers.Sell(_a.TenantId, _a.Slug);
        var checkout = await _api.Checkout(Guid.CreateVersion7(), offer.ProgramId);
        var order = _api.OrdersOf(checkout).Single();

        var response = await _api.SendWebhook(StripeEvents.CheckoutCompleted(
            $"evt_{Guid.NewGuid():N}", checkout.SessionId, [(_b.TenantId, order.Id)]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Pending", await Scalar<string>(null, null, $"SELECT status FROM orders WHERE id = '{order.Id}'", asOwner: true));
        Assert.False(_api.CheckoutSessions.Transfers.ContainsKey(order.Id));
    }

    // The cart (design §2.2): one checkout across coaches writes each order into its own coach's
    // tenant, and each coach sees only their part of it. Counted as the superuser.
    [Fact]
    public async Task A_cart_across_A_and_B_puts_each_order_in_its_coachs_tenant()
    {
        var carol = Guid.CreateVersion7();
        var fromA = _api.Sellers.Sell(_a.TenantId, _a.Slug);
        var fromB = _api.Sellers.Sell(_b.TenantId, _b.Slug);
        var checkout = await _api.Checkout(carol, [fromA.ProgramId, fromB.ProgramId]);
        await _api.Pay(checkout);
        var orders = _api.OrdersOf(checkout).ToDictionary(o => o.ProgramId, o => o.Id);

        var tenantOfA = await Scalar<Guid>(null, null, $"SELECT tenant_id FROM orders WHERE id = '{orders[fromA.ProgramId]}'", asOwner: true);
        var tenantOfB = await Scalar<Guid>(null, null, $"SELECT tenant_id FROM orders WHERE id = '{orders[fromB.ProgramId]}'", asOwner: true);
        var as_ = await Body(await _api.AsCoach(_a, "/sales"));
        var bs = await Body(await _api.AsCoach(_b, "/sales"));

        Assert.Equal((_a.TenantId, _b.TenantId), (tenantOfA, tenantOfB));
        Assert.Contains(orders[fromA.ProgramId].ToString(), as_, StringComparison.Ordinal);
        Assert.DoesNotContain(orders[fromB.ProgramId].ToString(), as_, StringComparison.Ordinal);
        Assert.Contains(orders[fromB.ProgramId].ToString(), bs, StringComparison.Ordinal);
        Assert.DoesNotContain(orders[fromA.ProgramId].ToString(), bs, StringComparison.Ordinal);
    }

    // Layer 4 alone: a checkout is its customer's. Another user, or a coach's tenant session, sees none.
    [Fact]
    public async Task RLS_a_checkout_is_visible_to_its_customer_only()
    {
        var offer = _api.Sellers.Sell(_a.TenantId, _a.Slug);
        var checkout = await _api.Checkout(_alice, offer.ProgramId);
        var sql = $"SELECT count(*) FROM checkouts WHERE id = '{checkout.CheckoutId}'";

        var asAlice = await Scalar<long>(null, _alice, sql);
        var asBob = await Scalar<long>(null, _bob, sql);
        var asCoachA = await Scalar<long>(_a.TenantId, null, sql);
        var bobDeletes = await Execute(null, _bob, $"DELETE FROM checkouts WHERE id = '{checkout.CheckoutId}'");

        Assert.Equal((1L, 0L, 0L, 0), (asAlice, asBob, asCoachA, bobDeletes));
    }

    // A cart is its customer's (design §2.2): Bob reads and writes his own cart only, through the API and
    // in PostgreSQL (he cannot delete Alice's or write one for anybody else); a coach's tenant session
    // sees no carts at all.
    [Fact]
    public async Task A_cart_is_its_customers_alone()
    {
        var program = Guid.CreateVersion7();
        var line = new { programId = program, coachSlug = _a.Slug, slug = "strong-start", title = "Strong start", priceAmount = 4900, currency = "EUR" };
        var put = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/cart") { Content = JsonContent.Create(new { items = new[] { line } }) }, _alice);
        var bobsCart = await Body(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/cart"), _bob));
        var sql = $"SELECT count(*) FROM carts WHERE customer_id = '{_alice}'";

        var asAlice = await Scalar<long>(null, _alice, sql);
        var asBob = await Scalar<long>(null, _bob, sql);
        var asCoachA = await Scalar<long>(_a.TenantId, null, sql);
        var bobDeletes = await Execute(null, _bob, $"DELETE FROM carts WHERE customer_id = '{_alice}'");
        var bobPlants = await Assert.ThrowsAsync<PostgresException>(() => Execute(null, _bob, $"INSERT INTO carts (customer_id, created_at, updated_at) VALUES ('{Guid.CreateVersion7()}', now(), now())"));
        var aliceCart = await Body(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/cart"), _alice));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.DoesNotContain(program.ToString(), bobsCart, StringComparison.Ordinal);
        Assert.Equal((1L, 0L, 0L, 0), (asAlice, asBob, asCoachA, bobDeletes));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, bobPlants.SqlState);
        Assert.Contains(program.ToString(), aliceCart, StringComparison.Ordinal);
    }

    // A refund is a tenant's own action: B's owner, naming A's sale (under either address), finds
    // nothing, and A's sale is untouched.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task B_cannot_refund_As_sale(bool underAsAddress)
    {
        var slug = underAsAddress ? _a.Slug : _b.Slug;
        var response = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/studio/{slug}/sales/{_aliceBuysFromA}/refund"), _b.OwnerId, _b.TenantId, TenantRole.Owner);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(_api.CheckoutSessions.Refunds.ContainsKey(_aliceBuysFromA));
        Assert.Equal("Paid", await Scalar<string>(null, null, $"SELECT status FROM orders WHERE id = '{_aliceBuysFromA}'", asOwner: true));
    }

    // Layer 4 alone: raw SQL as ordering-api's role.
    [Fact]
    public async Task RLS_tenant_B_sees_none_of_As_orders()
    {
        var visible = await Scalar<long>(_b.TenantId, null, $"SELECT count(*) FROM orders WHERE tenant_id = '{_a.TenantId}'");

        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task RLS_a_customer_reads_their_orders_across_tenants_but_cannot_change_them()
    {
        var visible = await Scalar<long>(null, _alice, $"SELECT count(*) FROM orders WHERE customer_id = '{_alice}'");
        var others = await Scalar<long>(null, _alice, $"SELECT count(*) FROM orders WHERE customer_id = '{_bob}'");
        var updated = await Execute(null, _alice, $"UPDATE orders SET amount = 1 WHERE customer_id = '{_alice}'");
        var deleted = await Execute(null, _alice, $"DELETE FROM orders WHERE customer_id = '{_alice}'");

        Assert.Equal((2L, 0L), (visible, others));
        Assert.Equal((0, 0), (updated, deleted));
    }

    // Discount codes (design §2.4): B neither sees, changes nor deletes A's code, and A's code takes
    // nothing off B's programs, even when B has no code of that name.
    [Fact]
    public async Task B_cannot_see_change_delete_or_use_As_discount_code()
    {
        var created = await AsCoach(_a, HttpMethod.Post, "/discounts", new { code = "A-ONLY", percentOff = 50 });
        var id = System.Text.Json.JsonDocument.Parse(await created.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("id").GetGuid();
        var bsProgram = _api.Sellers.Sell(_b.TenantId, _b.Slug);

        var list = await Body(await AsCoach(_b, HttpMethod.Get, "/discounts"));
        var updated = await AsCoach(_b, HttpMethod.Put, $"/discounts/{id}", new { percentOff = 90, isActive = true });
        var deleted = await AsCoach(_b, HttpMethod.Delete, $"/discounts/{id}");
        var used = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders/discount") { Content = System.Net.Http.Json.JsonContent.Create(new { code = "A-ONLY", programIds = new[] { bsProgram.ProgramId } }) },
            _bob);
        var percent = await Scalar<int>(_a.TenantId, null, $"SELECT percent_off FROM discount_codes WHERE id = '{id}'");

        Assert.DoesNotContain("A-ONLY", list, StringComparison.Ordinal);
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound), (updated.StatusCode, deleted.StatusCode, used.StatusCode));
        Assert.Equal(50, percent);
    }

    // A data export (design §5.4) holds the customer's own orders, with every coach, and nobody else's;
    // like every internal endpoint it needs the gateway signature.
    [Fact]
    public async Task Alices_export_holds_her_orders_only()
    {
        var export = await Body(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/customers/{_alice}/export")));
        var unsigned = await _api.CreateClient().GetAsync($"/internal/customers/{_alice}/export", Cancel);

        Assert.Contains(_aliceBuysFromA.ToString(), export, StringComparison.Ordinal);
        Assert.Contains(_aliceBuysFromB.ToString(), export, StringComparison.Ordinal);
        Assert.DoesNotContain(_bobBuysFromA.ToString(), export, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    // A receipt is the customer's own: Bob gets 404 for Alice's checkout (design §5.4).
    [Fact]
    public async Task Bob_gets_404_for_Alices_receipt()
    {
        var offer = _api.Sellers.Sell(_a.TenantId, _a.Slug);
        var checkout = await _api.Checkout(_alice, offer.ProgramId);
        await _api.Pay(checkout);

        var alices = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/orders/receipts/{checkout.CheckoutId}"), _alice);
        var bobs = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/orders/receipts/{checkout.CheckoutId}"), _bob);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.NotFound), (alices.StatusCode, bobs.StatusCode));
    }

    // Layer 4 alone: tenant B reads none of A's codes and cannot plant one in A's name.
    [Fact]
    public async Task RLS_tenant_B_sees_none_of_As_discount_codes()
    {
        await AsCoach(_a, HttpMethod.Post, "/discounts", new { code = "RLS-A", percentOff = 20 });

        var seen = await Scalar<long>(_b.TenantId, null, $"SELECT count(*) FROM discount_codes WHERE tenant_id = '{_a.TenantId}'");
        var planted = await Assert.ThrowsAsync<PostgresException>(() => Execute(_b.TenantId, null,
            $"INSERT INTO discount_codes (id, tenant_id, code, percent_off, is_active, created_at, updated_at) VALUES ('{Guid.CreateVersion7()}', '{_a.TenantId}', 'PLANTED', 90, true, now(), now())"));

        Assert.Equal(0L, seen);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, planted.SqlState);
    }

    private Task<HttpResponseMessage> AsCoach(Coach coach, HttpMethod method, string path, object? body = null) =>
        _api.SendAsGateway(
            new HttpRequestMessage(method, $"/api/v1/studio/{coach.Slug}{path}") { Content = body is null ? null : System.Net.Http.Json.JsonContent.Create(body) },
            coach.OwnerId, coach.TenantId, TenantRole.Owner);

    private static async Task<string> Body(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    private async Task<T> Scalar<T>(Guid? tenantId, Guid? userId, string sql, bool asOwner = false)
    {
        await using var connection = await Open(tenantId, userId, asOwner);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Cancel))!;
    }

    private async Task<int> Execute(Guid? tenantId, Guid? userId, string sql)
    {
        await using var connection = await Open(tenantId, userId, asOwner: false);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(Cancel);
    }

    // A connection inside a transaction with the given settings (never committed).
    private async Task<NpgsqlConnection> Open(Guid? tenantId, Guid? userId, bool asOwner)
    {
        var connection = new NpgsqlConnection(asOwner ? databases.OwnerConnectionString("ordering") : databases.AppConnectionString("ordering"));
        await connection.OpenAsync(Cancel);
        await using (var begin = new NpgsqlCommand(
            $"BEGIN; SELECT set_config('app.tenant_id', '{tenantId}', true), set_config('app.user_id', '{userId}', true)", connection))
        {
            await begin.ExecuteNonQueryAsync(Cancel);
        }

        return connection;
    }

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
