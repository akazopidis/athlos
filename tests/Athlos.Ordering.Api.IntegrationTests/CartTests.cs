using System.Net;
using System.Net.Http.Json;
using Athlos.Testing;
using static Athlos.Testing.OrderingArrange;

namespace Athlos.Ordering.Api.IntegrationTests;

// The cart kept with the account (design §2.2, §8): the browser sends the whole cart after each change,
// so it follows the customer to another device. At most one checkout's worth of programs.
public sealed class CartTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly OrderingApiFactory _api = new(databases);
    private readonly Guid _customer = Guid.CreateVersion7();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_cart_is_kept_as_sent_once_per_program_and_replaced_by_the_next_change()
    {
        var empty = await Read(await Get(_customer));
        var first = Line("Strong start");
        var second = Line("Engine");

        var saved = await Read(await Put(_customer, first, second, first));
        var onAnotherDevice = await Read(await Get(_customer));
        var replaced = await Read(await Put(_customer, second));

        Assert.Empty(empty.Items);
        Assert.Null(empty.UpdatedAt);
        Assert.Equal(["Strong start", "Engine"], onAnotherDevice.Items.Select(i => i.Title));
        Assert.Equal((first.ProgramId, 4900L, "EUR", "Powerlifting"), (onAnotherDevice.Items[0].ProgramId, onAnotherDevice.Items[0].PriceAmount, onAnotherDevice.Items[0].Currency, onAnotherDevice.Items[0].Category));
        Assert.NotNull(saved.UpdatedAt);
        Assert.Equal(["Engine"], replaced.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task More_than_one_checkouts_worth_is_refused_and_nobody_signed_out_has_a_cart()
    {
        var tooMany = await Put(_customer, [.. Enumerable.Range(1, 11).Select(n => Line($"Program {n}"))]);
        var badCurrency = await Put(_customer, Line("Strong start") with { Currency = "EURO" });
        var anonymous = await _api.CreateClient().GetAsync("/api/v1/me/cart", Cancel);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (tooMany.StatusCode, badCurrency.StatusCode));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // Account deletion (design §5.2) takes the cart; the data export (design §5.4) lists it.
    [Fact]
    public async Task The_export_lists_the_cart_and_account_deletion_takes_it()
    {
        await Put(_customer, Line("Strong start"));

        var export = await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/customers/{_customer}/export"))).Content.ReadAsStringAsync(Cancel);
        var erased = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{_customer}"));
        var after = await Read(await Get(_customer));

        Assert.Contains("\"cart\":[{", export, StringComparison.Ordinal);
        Assert.Contains("Strong start", export, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, erased.StatusCode);
        Assert.Empty(after.Items);
    }

    private static CartLine Line(string title) =>
        new(Guid.CreateVersion7(), "anna", title.ToLowerInvariant().Replace(' ', '-'), title, 4900, "EUR", "Powerlifting");

    private Task<HttpResponseMessage> Get(Guid customer) => _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/cart"), customer);

    private Task<HttpResponseMessage> Put(Guid customer, params CartLine[] items) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/cart") { Content = JsonContent.Create(new { items }) }, customer);

    private static async Task<CartRow> Read(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancel)}");
        return (await response.Content.ReadFromJsonAsync<CartRow>(Json, Cancel))!;
    }

    private sealed record CartLine(Guid ProgramId, string CoachSlug, string Slug, string Title, long PriceAmount, string Currency, string? Category);

    private sealed record CartRow(List<CartLine> Items, DateTimeOffset? UpdatedAt);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
