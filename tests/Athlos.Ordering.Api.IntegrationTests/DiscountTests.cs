using System.Net;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Testing;
using static Athlos.Testing.OrderingArrange;

namespace Athlos.Ordering.Api.IntegrationTests;

// Discount codes (design §2.4): a coach's code takes a percentage off their own programs in the cart;
// orders keep the code and charge, transfer and refund the discounted price.
public sealed class DiscountTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly OrderingApiFactory _api = new(databases);
    private readonly Coach _coach = NewCoach();
    private readonly Coach _other = NewCoach();
    private readonly Guid _customer = Guid.CreateVersion7();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_code_takes_its_percentage_off_and_the_order_keeps_it()
    {
        var program = _api.Sellers.Sell(_coach.TenantId, _coach.Slug, price: 4900);
        await CreateCode(_coach, new { code = "summer-20", percentOff = 20 });

        var check = await Read<CheckResult>(await CheckDiscount(_customer, "Summer-20", program.ProgramId));
        var checkout = await Checkout(_customer, "SUMMER-20", program.ProgramId);
        var order = Assert.Single(_api.OrdersOf(checkout));
        await _api.Pay(checkout);
        var codes = await Read<List<CodeRow>>(await AsCoach(_coach, HttpMethod.Get, "/discounts"));
        var sale = Assert.Single((await Read<SalesPage>(await _api.AsCoach(_coach, "/sales"))).Items);
        var mine = Assert.Single((await Read<MyOrdersPage>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/orders"), _customer))).Items);

        Assert.Equal(("SUMMER-20", 20, 980L), (check.Code, Assert.Single(check.Lines).PercentOff, check.Lines[0].DiscountAmount));
        Assert.Equal((3920L, 392L, "SUMMER-20", 980L), (order.Amount, order.PlatformFee, order.DiscountCode, order.DiscountAmount));
        Assert.Equal(3920L - 392L, _api.CheckoutSessions.Transfers[order.Id].Amount);
        Assert.Equal(1, Assert.Single(codes).Redemptions);
        Assert.Equal(("SUMMER-20", 3920L), (sale.DiscountCode, sale.Amount));
        Assert.Equal(("SUMMER-20", 980L, 3920L), (mine.DiscountCode, mine.DiscountAmount, mine.Amount));
    }

    // The receipt (design §5.4): one per checkout, across coaches, with each line's price, discount and
    // amount paid; a refunded line stays, marked. Not paid yet: 404.
    [Fact]
    public async Task The_receipt_lists_prices_discounts_and_refunds()
    {
        var mine = _api.Sellers.Sell(_coach.TenantId, _coach.Slug, price: 2000);
        var theirs = _api.Sellers.Sell(_other.TenantId, _other.Slug, price: 3000);
        await CreateCode(_coach, new { code = "HALF", percentOff = 50 });
        var checkout = await Checkout(_customer, "HALF", mine.ProgramId, theirs.ProgramId);
        var unpaid = await Receipt(checkout.CheckoutId);
        await _api.Pay(checkout);
        var theirOrder = _api.OrdersOf(checkout).Single(o => o.ProgramId == theirs.ProgramId);
        await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/studio/{_other.Slug}/sales/{theirOrder.Id}/refund"), _other.OwnerId, _other.TenantId, TenantRole.Owner);

        var receipt = await Read<ReceiptRow>(await Receipt(checkout.CheckoutId));

        Assert.Equal(HttpStatusCode.NotFound, unpaid.StatusCode);
        Assert.Equal((5000L, 1000L, 4000L, 3000L), (receipt.Subtotal, receipt.Discount, receipt.Total, receipt.Refunded));
        var half = receipt.Lines.Single(l => l.DiscountCode == "HALF");
        Assert.Equal((2000L, 1000L, 1000L), (half.Price, half.DiscountAmount, half.Amount));
        Assert.Equal("Refunded", receipt.Lines.Single(l => l.DiscountCode is null).Status);
    }

    private Task<HttpResponseMessage> Receipt(Guid checkoutId) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/orders/receipts/{checkoutId}"), _customer);

    // A code is looked up inside each coach's tenant: another coach's programs in the same cart keep
    // their price, and a code no coach in the cart has is refused, by the check and by checkout.
    [Fact]
    public async Task A_code_applies_only_to_the_programs_of_its_coach()
    {
        var mine = _api.Sellers.Sell(_coach.TenantId, _coach.Slug, price: 2000);
        var theirs = _api.Sellers.Sell(_other.TenantId, _other.Slug, price: 3000);
        await CreateCode(_coach, new { code = "FORGE10", percentOff = 10 });

        var check = await Read<CheckResult>(await CheckDiscount(_customer, "FORGE10", mine.ProgramId, theirs.ProgramId));
        var checkout = await Checkout(_customer, "FORGE10", mine.ProgramId, theirs.ProgramId);
        var orders = _api.OrdersOf(checkout);
        var onlyTheirs = await CheckDiscount(Guid.CreateVersion7(), "FORGE10", theirs.ProgramId);
        var refused = await StartCheckout(Guid.CreateVersion7(), "FORGE10", theirs.ProgramId);

        Assert.Equal(mine.ProgramId, Assert.Single(check.Lines).ProgramId);
        Assert.Equal(1800L, orders.Single(o => o.ProgramId == mine.ProgramId).Amount);
        Assert.Equal((3000L, null), (orders.Single(o => o.ProgramId == theirs.ProgramId).Amount, orders.Single(o => o.ProgramId == theirs.ProgramId).DiscountCode));
        Assert.Equal(HttpStatusCode.NotFound, onlyTheirs.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("discountCode", await refused.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    // One program only, switched off, expired, or used up: each stops applying. An open payment page
    // counts as a use, so a limit cannot be passed by opening many pages.
    [Fact]
    public async Task Program_expiry_switch_and_use_limits_are_respected()
    {
        var first = _api.Sellers.Sell(_coach.TenantId, _coach.Slug);
        var second = _api.Sellers.Sell(_coach.TenantId, _coach.Slug);
        var oneProgram = await Read<CodeRow>(await CreateCode(_coach, new { code = "FIRST", percentOff = 50, programId = first.ProgramId }));
        var once = await Read<CodeRow>(await CreateCode(_coach, new { code = "ONCE", percentOff = 30, maxRedemptions = 1 }));
        var expiresSoon = await CreateCode(_coach, new { code = "PAST", percentOff = 10, expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });

        var forFirst = await Read<CheckResult>(await CheckDiscount(_customer, "FIRST", first.ProgramId, second.ProgramId));
        await AsCoach(_coach, HttpMethod.Put, $"/discounts/{oneProgram.Id}", new { percentOff = 50, programId = first.ProgramId, isActive = false });
        var switchedOff = await CheckDiscount(_customer, "FIRST", first.ProgramId);
        var onceForTwo = await Read<CheckResult>(await CheckDiscount(_customer, "ONCE", first.ProgramId, second.ProgramId));
        await Checkout(_customer, "ONCE", first.ProgramId);
        var usedUp = await CheckDiscount(Guid.CreateVersion7(), "ONCE", second.ProgramId);

        Assert.Equal(first.ProgramId, Assert.Single(forFirst.Lines).ProgramId);
        Assert.Equal(HttpStatusCode.NotFound, switchedOff.StatusCode);
        Assert.Single(onceForTwo.Lines);
        Assert.Equal(HttpStatusCode.NotFound, usedUp.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, expiresSoon.StatusCode);
        Assert.Equal(1, once.MaxRedemptions);
    }

    [Fact]
    public async Task Codes_are_unique_per_workspace_and_can_be_deleted()
    {
        var created = await CreateCode(_coach, new { code = "WELCOME", percentOff = 15 });
        var again = await CreateCode(_coach, new { code = "welcome", percentOff = 25 });
        var elsewhere = await CreateCode(_other, new { code = "WELCOME", percentOff = 25 });
        var invalid = await CreateCode(_coach, new { code = "no spaces", percentOff = 15 });
        var tooMuch = await CreateCode(_coach, new { code = "ALL", percentOff = 100 });
        var id = (await Read<CodeRow>(created)).Id;

        var deleted = await AsCoach(_coach, HttpMethod.Delete, $"/discounts/{id}");
        var list = await Read<List<CodeRow>>(await AsCoach(_coach, HttpMethod.Get, "/discounts"));

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.Created), (created.StatusCode, again.StatusCode, elsewhere.StatusCode));
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (invalid.StatusCode, tooMuch.StatusCode));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(list);
    }

    private Task<HttpResponseMessage> CreateCode(Coach coach, object body) => AsCoach(coach, HttpMethod.Post, "/discounts", body);

    private Task<HttpResponseMessage> AsCoach(Coach coach, HttpMethod method, string path, object? body = null) =>
        _api.SendAsGateway(
            new HttpRequestMessage(method, $"/api/v1/studio/{coach.Slug}{path}") { Content = body is null ? null : JsonContent.Create(body) },
            coach.OwnerId, coach.TenantId, TenantRole.Owner);

    private Task<HttpResponseMessage> CheckDiscount(Guid customer, string code, params Guid[] programIds) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders/discount") { Content = JsonContent.Create(new { code, programIds }) }, customer);

    private Task<HttpResponseMessage> StartCheckout(Guid customer, string code, params Guid[] programIds) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders/checkout") { Content = JsonContent.Create(new { programIds, discountCode = code, termsVersion = TermsVersion }) }, customer);

    private async Task<CheckoutStarted> Checkout(Guid customer, string code, params Guid[] programIds) =>
        await Read<CheckoutStarted>(await StartCheckout(customer, code, programIds));

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancel)}");
        return (await response.Content.ReadFromJsonAsync<T>(Json, Cancel))!;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    private sealed record CheckLine(Guid ProgramId, int PercentOff, long DiscountAmount);

    private sealed record CheckResult(string Code, List<CheckLine> Lines);

    private sealed record CodeRow(Guid Id, string Code, int PercentOff, Guid? ProgramId, int? MaxRedemptions, bool IsActive, int Redemptions);

    private sealed record SaleRow(long Amount, string? DiscountCode);

    private sealed record SalesPage(List<SaleRow> Items);

    private sealed record MyOrderRow(long Amount, string? DiscountCode, long DiscountAmount);

    private sealed record MyOrdersPage(List<MyOrderRow> Items);

    private sealed record ReceiptLineRow(long Price, string? DiscountCode, long DiscountAmount, long Amount, string Status);

    private sealed record ReceiptRow(long Subtotal, long Discount, long Total, long Refunded, List<ReceiptLineRow> Lines);
}
