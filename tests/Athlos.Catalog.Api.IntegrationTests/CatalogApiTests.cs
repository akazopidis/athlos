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

    // Tenant-aware telemetry (design §6): a studio request's span names the coach and the role.
    [Fact]
    public async Task Studio_requests_are_tagged_with_the_tenant_and_role()
    {
        var spans = new System.Collections.Concurrent.ConcurrentBag<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var coach = NewCoach();

        await _api.AsCoach(coach, HttpMethod.Get, "/programs");

        // The server span stops just after the response is sent, so wait for it briefly.
        Assert.True(await SpanArrives(spans, s => s.GetTagItem("tenant.id") as string == coach.TenantId.ToString() && s.GetTagItem("user.role") as string == "Owner"));
    }

    [Fact]
    public async Task The_plan_quota_counts_drafts_and_refuses_one_more()
    {
        var coach = NewCoach();
        _api.Quota.Limit(coach.TenantId, 2);
        await _api.CreateProgram(coach);
        await _api.CreateProgram(coach);

        var third = await _api.AsCoach(coach, HttpMethod.Post, "/programs", new
        {
            slug = TenancyArrange.NewSlug("program"),
            title = "One too many",
            category = "Powerlifting",
            durationWeeks = 4,
            priceAmount = 1900,
            currency = "EUR",
        });

        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Contains("allows 2 programs", await third.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
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

    // Programs cost at least €10 (design §2.3): checked on create and edit, and on publishing a program
    // priced before the minimum existed.
    [Fact]
    public async Task Programs_cost_at_least_ten_euros()
    {
        var coach = NewCoach();
        var tooCheap = await _api.AsCoach(coach, HttpMethod.Post, "/programs", new
        {
            slug = TenancyArrange.NewSlug("program"), title = "Cheap", category = "Running", durationWeeks = 4, priceAmount = 999, currency = "EUR",
        });
        var program = await _api.CreateProgram(coach);
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/workouts", new { week = 1, day = 1, title = "Day one" });
        await using (var connection = new Npgsql.NpgsqlConnection(databases.OwnerConnectionString("catalog")))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new Npgsql.NpgsqlCommand("UPDATE programs SET price_amount = 500 WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", program.Id);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var published = await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/publish");

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.Conflict), (tooCheap.StatusCode, published.StatusCode));
    }

    // The public program page (design §8): the outline of every workout, and the one free preview in full;
    // no other workout's content. Marking another workout as the preview moves it.
    [Fact]
    public async Task The_program_page_shows_the_outline_and_one_free_preview()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/workouts",
            new { week = 1, day = 1, title = "Squat day", content = "Free: 5x5 squats", isSample = true });
        var second = (await (await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/workouts",
            new { week = 1, day = 2, title = "Bench day", content = "Paid: 5x5 bench" })).Content.ReadFromJsonAsync<WorkoutRow>(Json, TestContext.Current.CancellationToken))!;
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/publish");

        var page = await PublicPage(coach, program.Slug);
        await _api.AsCoach(coach, HttpMethod.Put, $"/programs/{program.Id}/workouts/{second.Id}",
            new { week = 1, day = 2, title = "Bench day", content = "Paid: 5x5 bench", isSample = true });
        var moved = await PublicPage(coach, program.Slug);

        Assert.Equal([("Squat day", true), ("Bench day", false)], page.Outline.Select(o => (o.Title, o.Sample)));
        Assert.Equal("Free: 5x5 squats", page.Sample!.Content);
        Assert.DoesNotContain("Paid: 5x5 bench", System.Text.Json.JsonSerializer.Serialize(page), StringComparison.Ordinal);
        Assert.Equal([("Squat day", false), ("Bench day", true)], moved.Outline.Select(o => (o.Title, o.Sample)));
        Assert.Equal("Bench day", moved.Sample!.Title);
    }

    private async Task<PublicProgram> PublicPage(Coach coach, string slug) =>
        await Read<PublicProgram>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{coach.Slug}/programs/{slug}")));

    private sealed record OutlineRow(int Week, int Day, string Title, bool Sample);

    private sealed record PreviewRow(string Title, string Content);

    private sealed record PublicProgram(string Title, List<OutlineRow> Outline, PreviewRow? Sample);

    // Athlos sells in euros only (design §2.3).
    [Fact]
    public async Task Programs_are_priced_in_euros()
    {
        var coach = NewCoach();

        var dollars = await _api.AsCoach(coach, HttpMethod.Post, "/programs", new
        {
            slug = TenancyArrange.NewSlug("program"), title = "Dollars", category = "Running", durationWeeks = 4, priceAmount = 1900, currency = "USD",
        });

        Assert.Equal(HttpStatusCode.BadRequest, dollars.StatusCode);
    }

    // A 12-week program's workouts are in weeks 1 to 12, and it cannot be shortened below its last one.
    [Fact]
    public async Task Workouts_stay_inside_the_programs_length()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);
        var week13 = await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/workouts", new { week = 13, day = 1, title = "Too late" });
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/workouts", new { week = 10, day = 1, title = "Week ten" });

        var shorter = await _api.AsCoach(coach, HttpMethod.Put, $"/programs/{program.Id}", new
        {
            title = "Shorter", category = "Powerlifting", durationWeeks = 8, priceAmount = 4900, currency = "EUR",
        });
        var stillLongEnough = await _api.AsCoach(coach, HttpMethod.Put, $"/programs/{program.Id}", new
        {
            title = "Ten weeks", category = "Powerlifting", durationWeeks = 10, priceAmount = 4900, currency = "EUR",
        });

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.OK), (week13.StatusCode, shorter.StatusCode, stillLongEnough.StatusCode));
    }

    // Link rules (backlog item 45): a workout's video is an https link on YouTube or Vimeo.
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", HttpStatusCode.Created)]
    [InlineData("https://youtu.be/abc", HttpStatusCode.Created)]
    [InlineData("https://vimeo.com/123", HttpStatusCode.Created)]
    [InlineData("http://www.youtube.com/watch?v=abc", HttpStatusCode.BadRequest)]
    [InlineData("https://videos.example/abc.mp4", HttpStatusCode.BadRequest)]
    [InlineData("https://youtube.com.evil.example/watch", HttpStatusCode.BadRequest)]
    public async Task A_workout_video_is_an_https_link_on_YouTube_or_Vimeo(string videoUrl, HttpStatusCode expected)
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach);

        var response = await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/workouts", new { week = 1, day = 1, title = "Day one", videoUrl });

        Assert.Equal(expected, response.StatusCode);
    }

    // Customers' progress names a sold program's workouts: they can be edited, not deleted. A draft's can.
    [Fact]
    public async Task Workouts_of_a_program_that_has_been_on_sale_cannot_be_deleted()
    {
        var coach = NewCoach();
        var draft = await _api.CreateProgram(coach);
        var draftWorkout = await (await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{draft.Id}/workouts", new { week = 1, day = 1, title = "Day one" }))
            .Content.ReadFromJsonAsync<WorkoutRow>(Json, TestContext.Current.CancellationToken);
        var sold = await _api.CreateProgram(coach, publish: true);
        var soldWorkout = (await (await _api.AsCoach(coach, HttpMethod.Get, $"/programs/{sold.Id}/workouts"))
            .Content.ReadFromJsonAsync<List<WorkoutRow>>(Json, TestContext.Current.CancellationToken))!.Single();
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{sold.Id}/unpublish");

        var draftDeleted = await _api.AsCoach(coach, HttpMethod.Delete, $"/programs/{draft.Id}/workouts/{draftWorkout!.Id}");
        var soldDeleted = await _api.AsCoach(coach, HttpMethod.Delete, $"/programs/{sold.Id}/workouts/{soldWorkout.Id}");

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.Conflict), (draftDeleted.StatusCode, soldDeleted.StatusCode));
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

    // Customers keep what they bought (design §3.4), so the content outlives publishing.
    [Fact]
    public async Task Program_content_includes_the_workouts_even_after_unpublishing()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach, publish: true);
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/unpublish");

        var content = await Read<ProgramContent>(
            await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{coach.TenantId}/programs/{program.Id}/content")));

        Assert.Equal((program.Id, coach.Slug), (content.ProgramId, content.CoachSlug));
        Assert.Equal("Squat day", Assert.Single(content.Workouts).Title);
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

    // The shop's catalog (design §8): price and length ranges, sorting, and a coach facet that counts
    // every coach among the results even when one coach is picked.
    [Fact]
    public async Task The_shop_filters_by_price_and_length_sorts_by_price_and_counts_coaches()
    {
        var (anna, ben) = (NewCoach(), NewCoach());
        var tag = $"shop{Guid.NewGuid():N}"[..16];
        foreach (var (coach, price, weeks) in new[] { (anna, 3000L, 4), (anna, 1000L, 8), (anna, 2000L, 12), (ben, 2500L, 6) })
        {
            var program = await _api.CreateProgram(coach, title: $"{tag} {price}", publish: true);
            var update = await _api.AsCoach(coach, HttpMethod.Put, $"/programs/{program.Id}",
                new { title = $"{tag} {price}", category = "Powerlifting", durationWeeks = weeks, priceAmount = price, currency = "EUR" });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        var cheapestFirst = await Search($"?q={tag}&coach={anna.Slug}&sort=PriceLowToHigh");
        var dearestFirst = await Search($"?q={tag}&sort=PriceHighToLow");
        var midRange = await Search($"?q={tag}&minPrice=1500&maxPrice=2600");
        var short_ = await Search($"?q={tag}&maxWeeks=8");

        Assert.Equal([$"{tag} 1000", $"{tag} 2000", $"{tag} 3000"], cheapestFirst.Items.Select(i => i.Title));
        Assert.Equal([$"{tag} 3000", $"{tag} 2500", $"{tag} 2000", $"{tag} 1000"], dearestFirst.Items.Select(i => i.Title));
        Assert.Equal([$"{tag} 2500", $"{tag} 2000"], midRange.Items.Select(i => i.Title).Order(StringComparer.Ordinal).Reverse());
        Assert.Equal(3, short_.TotalCount);
        Assert.Equal([(anna.Slug, 3), (ben.Slug, 1)], cheapestFirst.Coaches.Select(c => (c.CoachSlug, c.Count)));
    }

    // Searching a coach's address finds their programs, with spaces for its dashes ("demo forge").
    [Fact]
    public async Task The_search_finds_a_coachs_programs_by_their_address()
    {
        var coach = NewCoach();
        await _api.CreateProgram(coach, title: "Unrelated title", publish: true);

        var bySlug = await Search($"?q={coach.Slug}");
        var withSpaces = await Search($"?q={Uri.EscapeDataString(coach.Slug.Replace('-', ' '))}");

        Assert.Equal([coach.Slug], bySlug.Items.Select(i => i.CoachSlug));
        Assert.Equal([coach.Slug], withSpaces.Items.Select(i => i.CoachSlug));
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

    // Design §3.4: customers keep what they bought, so a program that was ever on sale stays, even
    // after it is unpublished; the coach can only unpublish it.
    [Fact]
    public async Task A_program_that_was_on_sale_cannot_be_deleted_after_unpublishing()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach, publish: true);
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/unpublish");

        var delete = await _api.AsCoach(coach, HttpMethod.Delete, $"/programs/{program.Id}");
        var details = await Read<PublishHistory>(await _api.AsCoach(coach, HttpMethod.Get, $"/programs/{program.Id}"));

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.True(details.EverPublished);
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
        var body = new { slug = "twelve-week-strength", title = "Strength", category = "Powerlifting", durationWeeks = 12, priceAmount = 4900, currency = "EUR" };

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

    // Saved programs (design §2.4): newest saved first, idempotent, a program that leaves the shop is not
    // listed (and returns when published again), and erasing the customer removes their rows only.
    [Fact]
    public async Task Saved_programs_list_what_is_on_sale_and_go_with_the_account()
    {
        var coach = NewCoach();
        var first = await _api.CreateProgram(coach, publish: true);
        var second = await _api.CreateProgram(coach, publish: true);
        var customer = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await AsCustomer(customer, HttpMethod.Put, $"/saved/{first.Id}");
        await AsCustomer(customer, HttpMethod.Put, $"/saved/{second.Id}");
        var again = await AsCustomer(customer, HttpMethod.Put, $"/saved/{second.Id}");
        await AsCustomer(other, HttpMethod.Put, $"/saved/{first.Id}");

        var both = await Read<List<SearchItem>>(await AsCustomer(customer, HttpMethod.Get, "/saved"));
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{first.Id}/unpublish");
        var onSale = await Read<List<SearchItem>>(await AsCustomer(customer, HttpMethod.Get, "/saved"));
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{first.Id}/publish");
        await AsCustomer(customer, HttpMethod.Delete, $"/saved/{second.Id}");
        var erased = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{customer}"));
        var afterErasure = await Read<List<SearchItem>>(await AsCustomer(customer, HttpMethod.Get, "/saved"));
        var othersAfter = await Read<List<SearchItem>>(await AsCustomer(other, HttpMethod.Get, "/saved"));

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal([second.Slug, first.Slug], both.Select(p => p.Slug));
        Assert.Equal([second.Slug], onSale.Select(p => p.Slug));
        Assert.Equal(HttpStatusCode.NoContent, erased.StatusCode);
        Assert.Empty(afterErasure);
        Assert.Equal([first.Slug], othersAfter.Select(p => p.Slug));
    }

    // A workout added to a program that has been on sale is reported to enrollment-api, which emails the
    // program's customers (design §5.3); a draft's workouts are not.
    [Fact]
    public async Task New_workouts_of_a_program_on_sale_are_reported_once_committed()
    {
        var coach = NewCoach();
        var draft = await _api.CreateProgram(coach);
        var sold = await _api.CreateProgram(coach, publish: true);

        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{draft.Id}/workouts", new { week = 1, day = 2, title = "Draft day" });
        await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{sold.Id}/workouts", new { week = 1, day = 2, title = "Bonus day" });

        for (var waited = 0; waited < 3000 && !_api.Updates.Told.Any(t => t.ProgramId == sold.Id); waited += 50)
        {
            await Task.Delay(50, Cancel);
        }

        Assert.Contains((coach.TenantId, sold.Id, "Bonus day"), _api.Updates.Told);
        Assert.DoesNotContain(_api.Updates.Told, t => t.ProgramId == draft.Id);
    }

    // A double tap saves twice at the same moment: every request succeeds and one row is kept.
    [Fact]
    public async Task Saving_the_same_program_at_once_keeps_one_row_and_never_fails()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach, publish: true);
        var customer = Guid.CreateVersion7();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => AsCustomer(customer, HttpMethod.Put, $"/saved/{program.Id}")));
        var saved = await Read<List<SearchItem>>(await AsCustomer(customer, HttpMethod.Get, "/saved"));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Single(saved);
    }

    // The marketplace's public reads are cached for a few seconds (design §4): a second read of the same
    // address within the window is the cached answer, a studio read never is, and other addresses are
    // their own entries.
    [Fact]
    public async Task Public_marketplace_reads_are_cached_briefly_and_studio_reads_never()
    {
        await using var cached = new CatalogApiFactory(databases, storage) { CacheSeconds = 30 };
        var coach = NewCoach();
        var program = await cached.CreateProgram(coach, title: "Before", publish: true);
        var page = $"/api/v1/marketplace/coaches/{coach.Slug}/programs/{program.Slug}";

        var first = await (await cached.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, page))).Content.ReadAsStringAsync(Cancel);
        await cached.AsCoach(coach, HttpMethod.Put, $"/programs/{program.Id}", new
        {
            slug = program.Slug, title = "After", description = "x", category = "Powerlifting", durationWeeks = 12, priceAmount = 4900, currency = "EUR",
        });
        var again = await (await cached.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, page), Guid.CreateVersion7())).Content.ReadAsStringAsync(Cancel);
        var otherAddress = await (await cached.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs?coach={coach.Slug}"))).Content.ReadAsStringAsync(Cancel);
        var studio = await (await cached.AsCoach(coach, HttpMethod.Get, $"/programs/{program.Id}")).Content.ReadAsStringAsync(Cancel);

        Assert.Contains("\"Before\"", first, StringComparison.Ordinal);
        Assert.Equal(first, again); // Cached, even for a signed-in visitor.
        Assert.Contains("\"After\"", otherAddress, StringComparison.Ordinal);
        Assert.Contains("\"After\"", studio, StringComparison.Ordinal);
    }

    // Ratings come from enrollment-api (design §2.4): cards carry them and "Top rated" sorts by the
    // average, then the number of reviews; programs without reviews come last.
    [Fact]
    public async Task Top_rated_sorts_by_average_then_number_of_reviews()
    {
        var coach = NewCoach();
        var unrated = await _api.CreateProgram(coach, publish: true);
        var fewer = await _api.CreateProgram(coach, publish: true);
        var more = await _api.CreateProgram(coach, publish: true);
        var best = await _api.CreateProgram(coach, publish: true);
        await SetRating(coach, fewer.Id, 1, 4.5m);
        await SetRating(coach, more.Id, 8, 4.5m);
        await SetRating(coach, best.Id, 2, 5m);

        var page = await Read<RatedPage>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs?coach={coach.Slug}&sort=TopRated")));

        Assert.Equal([best.Slug, more.Slug, fewer.Slug, unrated.Slug], page.Items.Select(p => p.Slug));
        Assert.Equal((5m, 2), (page.Items[0].RatingAverage, page.Items[0].RatingCount));
        Assert.Equal((0m, 0), (page.Items[3].RatingAverage, page.Items[3].RatingCount));
    }

    // The code a coach shows (design §2.4): on the shop's cards, saved programs and the program page, on
    // every program of the workspace or only the one it names, until it ends; removed when ordering-api
    // says none is shown. Another coach's programs never carry it.
    [Fact]
    public async Task A_shown_discount_is_on_the_coachs_programs_until_it_ends()
    {
        var coach = NewCoach();
        var other = NewCoach();
        var first = await _api.CreateProgram(coach, publish: true);
        var second = await _api.CreateProgram(coach, publish: true);
        var theirs = await _api.CreateProgram(other, publish: true);
        var customer = Guid.CreateVersion7();
        await AsCustomer(customer, HttpMethod.Put, $"/saved/{first.Id}");

        await ShowDiscount(coach, new { code = "SUMMER", percentOff = 20, expiresAt = DateTimeOffset.UtcNow.AddDays(3) });
        var everywhere = await Read<OfferPage>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs?coach={coach.Slug}")));
        var notTheirs = await Read<OfferPage>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs?coach={other.Slug}")));
        var saved = await Read<List<OfferItem>>(await AsCustomer(customer, HttpMethod.Get, "/saved"));
        var page = await Read<OfferItem>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{coach.Slug}/programs/{first.Slug}")));
        await ShowDiscount(coach, new { code = "ONLY-ONE", percentOff = 30, programId = second.Id });
        var onlyOne = await Read<OfferPage>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs?coach={coach.Slug}")));
        await ShowDiscount(coach, new { code = "ENDED", percentOff = 30, expiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        var ended = await Read<OfferPage>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs?coach={coach.Slug}")));
        await ShowDiscount(coach, new { code = "BACK", percentOff = 10 });
        var removed = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/tenants/{coach.TenantId}/public-discount"));
        var none = await Read<OfferPage>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs?coach={coach.Slug}")));

        Assert.Equal([("SUMMER", 20), ("SUMMER", 20)], everywhere.Items.Select(i => (i.Discount!.Code, i.Discount.PercentOff)));
        Assert.Null(Assert.Single(notTheirs.Items, i => i.Slug == theirs.Slug).Discount);
        Assert.Equal("SUMMER", Assert.Single(saved).Discount!.Code);
        Assert.Equal(("SUMMER", true), (page.Discount!.Code, page.Discount.ExpiresAt > DateTimeOffset.UtcNow));
        Assert.Equal("ONLY-ONE", onlyOne.Items.Single(i => i.Slug == second.Slug).Discount!.Code);
        Assert.Null(onlyOne.Items.Single(i => i.Slug == first.Slug).Discount);
        Assert.All(ended.Items, i => Assert.Null(i.Discount));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.All(none.Items, i => Assert.Null(i.Discount));
    }

    private async Task ShowDiscount(Coach coach, object body)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, $"/internal/tenants/{coach.TenantId}/public-discount")
        {
            Content = JsonContent.Create(body),
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private sealed record Offer(string Code, int PercentOff, DateTimeOffset? ExpiresAt);

    private sealed record OfferItem(string Slug, Offer? Discount);

    private sealed record OfferPage(List<OfferItem> Items);

    private async Task SetRating(Coach coach, Guid programId, int count, decimal average)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, $"/internal/tenants/{coach.TenantId}/programs/{programId}/rating")
        {
            Content = JsonContent.Create(new { count, average }),
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private sealed record RatedItem(string Slug, decimal RatingAverage, int RatingCount);

    private sealed record RatedPage(List<RatedItem> Items);

    private Task<HttpResponseMessage> AsCustomer(Guid customer, HttpMethod method, string path) =>
        _api.SendAsGateway(new HttpRequestMessage(method, $"/api/v1/me{path}"), customer);

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

    private sealed record PublishHistory(bool IsPublished, bool EverPublished);

    private sealed record ProgramContent(Guid ProgramId, string CoachSlug, List<WorkoutRow> Workouts);

    private sealed record WorkoutRow(Guid Id, string Title);

    private sealed record SearchItem(string CoachSlug, string Slug, string Title);

    private sealed record CoachFacet(string CoachSlug, int Count);

    private sealed record SearchPage(List<SearchItem> Items, int Page, int PageSize, int TotalCount, List<CoachFacet> Coaches);

    private static async Task<bool> SpanArrives(
        System.Collections.Concurrent.ConcurrentBag<System.Diagnostics.Activity> spans, Func<System.Diagnostics.Activity, bool> match)
    {
        for (var waited = 0; waited < 5000; waited += 50)
        {
            if (spans.Any(match))
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return false;
    }
}
