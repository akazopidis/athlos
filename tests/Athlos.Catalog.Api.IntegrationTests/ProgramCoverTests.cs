using System.Net;
using System.Net.Http.Json;
using Athlos.Testing;
using static Athlos.Testing.CatalogArrange;

namespace Athlos.Catalog.Api.IntegrationTests;

public sealed class ProgramCoverTests(AthlosDatabases databases, AthlosStorage storage) : IAsyncLifetime
{
    private readonly CatalogApiFactory _api = new(databases, storage);
    private readonly HttpClient _web = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_uploaded_cover_is_served_through_a_short_lived_link()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);
        var image = Png(1024);

        var details = await Read(await _api.UploadCover(coach, program.Id, image));
        var served = await _web.GetAsync(details.CoverImageUrl, Cancel);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(image, await served.Content.ReadAsByteArrayAsync(Cancel));
        Assert.Contains($"/tenants/{coach.TenantId}/programs/{program.Id}/cover-", details.CoverImageUrl, StringComparison.Ordinal);
        Assert.Contains("se=", details.CoverImageUrl, StringComparison.Ordinal); // expiry in the SAS
    }

    // The same link for everyone within a 10-minute window (the expiry is rounded), so browsers cache the
    // image instead of downloading it under a new address on every page; it stays valid 10 to 20 minutes.
    [Fact]
    public async Task Reading_a_cover_twice_gives_the_same_link()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);
        await _api.UploadCover(coach, program.Id, Png(256));

        var first = await Read(await _api.AsCoach(coach, HttpMethod.Get, $"/programs/{program.Id}"));
        var second = await Read(await _api.AsCoach(coach, HttpMethod.Get, $"/programs/{program.Id}"));
        if (first.CoverImageUrl != second.CoverImageUrl)
        {
            // The two reads straddled a window boundary; the next one is in the same window as the second.
            first = second;
            second = await Read(await _api.AsCoach(coach, HttpMethod.Get, $"/programs/{program.Id}"));
        }

        var expiry = DateTimeOffset.Parse(Uri.UnescapeDataString(System.Web.HttpUtility.ParseQueryString(new Uri(second.CoverImageUrl!).Query)["se"]!),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(first.CoverImageUrl, second.CoverImageUrl);
        Assert.InRange(expiry - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9.5), TimeSpan.FromMinutes(20));
    }

    [Fact]
    public async Task Anything_but_JPEG_PNG_or_WebP_is_rejected_whatever_the_content_type_says()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);

        var html = await _api.UploadCover(coach, program.Id, "<html><script>alert(1)</script></html>"u8.ToArray(), "image/png");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, html.StatusCode);
    }

    [Fact]
    public async Task Covers_over_2_MB_are_rejected()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);

        var response = await _api.UploadCover(coach, program.Id, Png((2 * 1024 * 1024) + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Replacing_a_cover_removes_the_old_image()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);
        var first = await Read(await _api.UploadCover(coach, program.Id, Png()));

        var second = await Read(await _api.UploadCover(coach, program.Id, Png(128)));

        Assert.NotEqual(first.CoverImageUrl, second.CoverImageUrl);
        Assert.Equal(HttpStatusCode.NotFound, (await _web.GetAsync(first.CoverImageUrl, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _web.GetAsync(second.CoverImageUrl, Cancel)).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_cover_clears_it()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);
        var uploaded = await Read(await _api.UploadCover(coach, program.Id, Png()));

        var delete = await _api.AsCoach(coach, HttpMethod.Delete, $"/programs/{program.Id}/cover");
        var after = await Read(await _api.AsCoach(coach, HttpMethod.Get, $"/programs/{program.Id}"));

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Null(after.CoverImageUrl);
        Assert.Equal(HttpStatusCode.NotFound, (await _web.GetAsync(uploaded.CoverImageUrl, Cancel)).StatusCode);
    }

    [Fact]
    public async Task The_marketplace_shows_the_cover_of_a_published_program()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach, publish: true);
        await _api.UploadCover(coach, program.Id, Png());

        var page = await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs?coach={coach.Slug}")))
            .Content.ReadFromJsonAsync<SearchPage>(Json, Cancel);

        var item = Assert.Single(page!.Items);
        Assert.Equal(HttpStatusCode.OK, (await _web.GetAsync(item.CoverImageUrl, Cancel)).StatusCode);
    }

    // The card-size cover (design §8): cards and saved programs show it, the program page the full one;
    // a new full cover takes the old card version away, and deleting the cover removes both.
    [Fact]
    public async Task Cards_show_the_card_size_cover_and_a_new_full_cover_replaces_it()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach, publish: true);
        var full = Png(512);
        var card = Png(128);
        await _api.UploadCover(coach, program.Id, full);
        Assert.Equal(HttpStatusCode.OK, (await _api.UploadCover(coach, program.Id, card, size: "card")).StatusCode);

        var cardLink = await CardCover(coach);
        var pageLink = await PageCover(coach, program.Slug);
        var cardBytes = await _web.GetByteArrayAsync(cardLink, Cancel);
        var pageBytes = await _web.GetByteArrayAsync(pageLink, Cancel);
        await _api.UploadCover(coach, program.Id, Png(256));
        var afterNewFull = await CardCover(coach);
        var oldCard = await _web.GetAsync(cardLink, Cancel);

        Assert.Contains("/cover-card-", cardLink, StringComparison.Ordinal);
        Assert.Equal((card.Length, full.Length), (cardBytes.Length, pageBytes.Length));
        Assert.DoesNotContain("/cover-card-", afterNewFull, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, oldCard.StatusCode);
    }

    private async Task<string> CardCover(Coach coach) =>
        Assert.Single((await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs?coach={coach.Slug}")))
            .Content.ReadFromJsonAsync<SearchPage>(Json, Cancel))!.Items).CoverImageUrl!;

    private async Task<string> PageCover(Coach coach, string slug) =>
        (await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{coach.Slug}/programs/{slug}")))
            .Content.ReadFromJsonAsync<Details>(Json, Cancel))!.CoverImageUrl!;

    private static async Task<Details> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Details>(Json, Cancel))!;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _web.Dispose();
        await _api.DisposeAsync();
    }

    private sealed record Details(Guid Id, string? CoverImageUrl);

    private sealed record SearchItem(string Slug, string? CoverImageUrl);

    private sealed record SearchPage(List<SearchItem> Items);
}
