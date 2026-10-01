using System.Net;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Testing;
using static Athlos.Testing.CatalogArrange;

namespace Athlos.Catalog.Api.IntegrationTests;

public sealed class CatalogApiTests(AthlosDatabases databases, AthlosStorage storage) : IAsyncLifetime
{
    private readonly CatalogApiFactory _api = new(databases, storage);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_program_is_a_private_draft()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);

        var mine = await Read<List<ProgramRow>>(await _api.AsCoach(coach, HttpMethod.Get, "/programs"));
        var inMarketplace = await Search($"?coach={coach.Slug}");

        Assert.False(program.IsPublished);
        Assert.Contains(mine, p => p.Id == program.Id);
        Assert.Equal(0, inMarketplace.TotalCount);
    }

    [Fact]
    public async Task Publishing_needs_a_workout_and_puts_the_program_in_the_marketplace()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);

        var tooEarly = await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/publish");
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/workouts", new { week = 1, day = 1, title = "Day one" });
        var published = await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/publish");

        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var page = await Search($"?coach={coach.Slug}");
        Assert.Equal(program.Slug, Assert.Single(page.Items).Slug);
    }

    [Fact]
    public async Task A_workspace_without_finished_payouts_cannot_publish()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/workouts", new { week = 1, day = 1, title = "Day one" });
        _api.Payouts.NotReady(coach.TenantId);

        var published = await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/publish");

        Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        Assert.Equal(0, (await Search($"?coach={coach.Slug}")).TotalCount);
    }

    [Fact]
    public async Task Published_program_page_gives_the_id_checkout_needs()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach, publish: true);

        var page = await Read<ProgramRow>(
            await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{coach.Slug}/programs/{program.Slug}")));

        Assert.Equal(program.Id, page.Id);
    }

    [Fact]
    public async Task Unpublishing_removes_it_from_the_marketplace()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach, publish: true);

        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/unpublish");

        Assert.Equal(0, (await Search($"?coach={coach.Slug}")).TotalCount);
        var page = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{coach.Slug}/programs/{program.Slug}"));
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
    }

    [Fact]
    public async Task Search_matches_title_and_escapes_wildcards()
    {
        var coach = NewCoach();
        await _api.CreateProgram(coach, title: "Kettlebell 100% basics", publish: true);
        await _api.CreateProgram(coach, title: "Marathon plan", publish: true);

        var kettlebell = await Search($"?coach={coach.Slug}&q=kettlebell");
        var percent = await Search($"?coach={coach.Slug}&q=100%25");
        var wildcard = await Search($"?coach={coach.Slug}&q=%25");

        Assert.Equal("Kettlebell 100% basics", Assert.Single(kettlebell.Items).Title);
        Assert.Single(percent.Items);
        Assert.Single(wildcard.Items);
    }

    [Fact]
    public async Task Page_size_is_capped_at_50()
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/marketplace/programs?pageSize=500"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_published_program_cannot_be_deleted()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach, publish: true);

        var delete = await _api.AsCoach(coach, HttpMethod.Delete, $"/programs/{program.Id}");

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task A_draft_can_be_deleted_with_its_workouts()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/workouts", new { week = 1, day = 1, title = "Day one" });

        var delete = await _api.AsCoach(coach, HttpMethod.Delete, $"/programs/{program.Id}");
        var after = await _api.AsCoach(coach, HttpMethod.Get, $"/programs/{program.Id}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Program_slugs_are_unique_per_coach_not_globally()
    {
        var (a, b) = (NewCoach(), NewCoach());
        var body = new { slug = "twelve-week-strength", title = "Strength", level = "Beginner", durationWeeks = 12, priceAmount = 4900, currency = "EUR" };

        var first = await _api.AsCoach(a, HttpMethod.Post, "/programs", body);
        var sameCoach = await _api.AsCoach(a, HttpMethod.Post, "/programs", body);
        var otherCoach = await _api.AsCoach(b, HttpMethod.Post, "/programs", body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, sameCoach.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherCoach.StatusCode);
    }

    [Fact]
    public async Task Staff_manage_programs_too()
    {
        var coach = NewCoach();
        var response = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{coach.Slug}/programs"), Guid.CreateVersion7(), coach.TenantId, TenantRole.Staff);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<SearchPage> Search(string query)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs{query}"));
        return await Read<SearchPage>(response);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Json, Cancel))!;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    private sealed record ProgramRow(Guid Id, string Slug, bool IsPublished);

    private sealed record SearchItem(string CoachSlug, string Slug, string Title);

    private sealed record SearchPage(List<SearchItem> Items, int Page, int PageSize, int TotalCount);
}
