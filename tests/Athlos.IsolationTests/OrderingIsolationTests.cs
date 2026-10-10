using System.Net;
using System.Net.Http.Headers;
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
        { "POST", "/webhooks/stripe" },
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

    [Fact]
    public async Task B_asking_for_As_sales_by_slug_gets_nothing()
    {
        var response = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{_a.Slug}/sales"), _b.OwnerId, _b.TenantId, TenantRole.Owner);

        var body = await Body(response);
        Assert.DoesNotContain(_aliceBuysFromA.ToString(), body, StringComparison.Ordinal);
        Assert.DoesNotContain(_bobBuysFromA.ToString(), body, StringComparison.Ordinal);
    }

    // Even a genuinely signed event cannot move an order across tenants: the order is looked up
    // inside the tenant named in the metadata, where another tenant's order does not exist.
    [Fact]
    public async Task A_webhook_naming_the_wrong_tenant_cannot_touch_the_order()
    {
        var offer = _api.Sellers.Sell(_a.TenantId, _a.Slug);
        var checkout = await _api.Checkout(Guid.CreateVersion7(), offer.ProgramId);

        var response = await _api.SendWebhook(StripeEvents.CheckoutCompleted(
            $"evt_{Guid.NewGuid():N}", $"cs_test_{checkout.OrderId:N}", checkout.OrderId, tenantId: _b.TenantId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Pending", await Scalar<string>(null, null, $"SELECT status FROM orders WHERE id = '{checkout.OrderId}'", asOwner: true));
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
