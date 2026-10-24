using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Notifications.Worker;
using Athlos.Tenancy;
using Athlos.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Athlos.Testing.EnrollmentArrange;
using static Athlos.Testing.OrderingArrange;

namespace Athlos.Enrollment.Api.IntegrationTests;

// Reviews and ratings (design §2.4): only buyers review, once per purchase; the program page lists them
// with the average; the coach replies; catalog's copy of the rating follows every change.
public sealed class ReviewTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly EnrollmentApiFactory _api = new(databases);
    private readonly Coach _coach = NewCoach();
    private readonly Guid _customer = Guid.CreateVersion7();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_a_buyer_can_review_and_writing_again_edits_the_review()
    {
        var program = await _api.Enroll(_customer, _coach);
        var stranger = await WriteReview(Guid.CreateVersion7(), program.ProgramId, 1, "Never bought it");
        var before = Assert.Single((await _api.MyPrograms(_customer)).Items);

        var first = await WriteReview(_customer, program.ProgramId, 4, "  Solid plan.  ");
        var edited = await WriteReview(_customer, program.ProgramId, 5, "Even better in week 3.");
        var mine = await Read<MyProgramWithReview>(await _api.AsCustomer(_customer, HttpMethod.Get, $"/programs/{program.ProgramId}"));

        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.False(before.Reviewed);
        Assert.True(Assert.Single((await _api.MyPrograms(_customer)).Items).Reviewed);
        Assert.Equal("Solid plan.", (await Read<ReviewRow>(first)).Comment);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal((5, "Even better in week 3.", "Test U."), (mine.Review!.Rating, mine.Review.Comment, mine.Review.AuthorName));
        Assert.Equal((1, 5m), _api.Ratings.For(_coach.TenantId, program.ProgramId));
    }

    [Fact]
    public async Task A_rating_outside_one_to_five_is_refused()
    {
        var program = await _api.Enroll(_customer, _coach);

        var zero = await WriteReview(_customer, program.ProgramId, 0, null);
        var six = await WriteReview(_customer, program.ProgramId, 6, null);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (zero.StatusCode, six.StatusCode));
    }

    // The program page: newest first, the average, the count and the stars; no customer id leaves.
    [Fact]
    public async Task The_program_page_lists_reviews_with_the_average()
    {
        var program = _api.Programs.Add(_coach.TenantId, _coach.Slug);
        var buyers = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        var ratings = new[] { 5, 4, 2 };
        for (var i = 0; i < buyers.Length; i++)
        {
            await _api.Deliver(Paid(buyers[i], _coach.TenantId, _coach.Slug, program));
            await WriteReview(buyers[i], program.ProgramId, ratings[i], $"Review {i}");
        }

        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs/{program.ProgramId}/reviews"));
        var json = await response.Content.ReadAsStringAsync(Cancel);
        var page = JsonSerializer.Deserialize<PublicReviews>(json, EnrollmentArrange.Json)!;

        Assert.Equal((3, 3.67m), (page.Count, page.Average));
        Assert.Equal([0, 1, 0, 1, 1], page.Stars);
        Assert.Equal(["Review 2", "Review 1", "Review 0"], page.Items.Select(r => r.Comment));
        Assert.DoesNotContain(buyers[0].ToString(), json, StringComparison.Ordinal);
        Assert.Equal((3, 3.67m), _api.Ratings.For(_coach.TenantId, program.ProgramId));
    }

    // The first review emails the owners (the worker resolves their addresses); an edit does not.
    [Fact]
    public async Task A_first_review_emails_the_coach_once()
    {
        var program = await _api.Enroll(_customer, _coach);
        await WriteReview(_customer, program.ProgramId, 3, "Good <b>start</b>");
        await WriteReview(_customer, program.ProgramId, 4, "Edited");

        await _api.PublishOutbox();

        var email = Assert.Single(Emails(), e => e.Template == EmailTemplates.ReviewReceived && e.Data["programTitle"] == program.Title);
        Assert.Equal(_coach.TenantId, email.ToOwnersOfTenant);
        var rendered = new EmailRenderer().Render(email.Template, email.Data);
        Assert.Contains("3 out of 5", rendered.Subject, StringComparison.Ordinal);
        Assert.Contains("Good &lt;b&gt;start&lt;/b&gt;", rendered.Html, StringComparison.Ordinal);
        Assert.Contains($"/studio/{_coach.Slug}/reviews", rendered.PlainText, StringComparison.Ordinal);
    }

    // If catalog cannot take the new rating, the review is not saved, so the two never disagree.
    [Fact]
    public async Task A_review_is_not_saved_when_the_rating_cannot_follow()
    {
        var program = await _api.Enroll(_customer, _coach);
        _api.Ratings.Unreachable = true;

        var failed = await WriteReview(_customer, program.ProgramId, 5, "Lost?");
        _api.Ratings.Unreachable = false;
        var mine = await Read<MyProgramWithReview>(await _api.AsCustomer(_customer, HttpMethod.Get, $"/programs/{program.ProgramId}"));

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Null(mine.Review);
        Assert.Null(_api.Ratings.For(_coach.TenantId, program.ProgramId));
    }

    // Deleting the review, a refund and an erased account each take the review away, and the rating follows.
    [Fact]
    public async Task Deleting_refunding_or_erasing_removes_the_review_and_updates_the_rating()
    {
        var program = _api.Programs.Add(_coach.TenantId, _coach.Slug);
        var refunded = Paid(Guid.CreateVersion7(), _coach.TenantId, _coach.Slug, program);
        var erased = Guid.CreateVersion7();
        await _api.Deliver(Paid(_customer, _coach.TenantId, _coach.Slug, program));
        await _api.Deliver(refunded);
        await _api.Deliver(Paid(erased, _coach.TenantId, _coach.Slug, program));
        await WriteReview(_customer, program.ProgramId, 5, null);
        await WriteReview(refunded.CustomerId, program.ProgramId, 1, null);
        await WriteReview(erased, program.ProgramId, 3, null);

        await _api.AsCustomer(_customer, HttpMethod.Delete, $"/programs/{program.ProgramId}/review");
        var afterDelete = _api.Ratings.For(_coach.TenantId, program.ProgramId);
        await _api.Deliver(new OrderRefunded(refunded.OrderId, _coach.TenantId, refunded.CustomerId, program.ProgramId, DateTimeOffset.UtcNow));
        var afterRefund = _api.Ratings.For(_coach.TenantId, program.ProgramId);
        var erasure = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{erased}"));

        Assert.Equal((2, 2m), afterDelete);
        Assert.Equal((1, 3m), afterRefund);
        Assert.Equal(HttpStatusCode.NoContent, erasure.StatusCode);
        Assert.Equal((0, 0m), _api.Ratings.For(_coach.TenantId, program.ProgramId));
    }

    // The studio lists the workspace's reviews and replies once; the reply is public; it can be removed.
    [Fact]
    public async Task The_coach_sees_reviews_and_replies_publicly()
    {
        var program = await _api.Enroll(_customer, _coach);
        await WriteReview(_customer, program.ProgramId, 4, "Great cues.");

        var list = await Read<StudioPage>(await AsCoach(_coach, HttpMethod.Get, "/reviews"));
        var review = Assert.Single(list.Items);
        var blank = await AsCoach(_coach, HttpMethod.Put, $"/reviews/{review.Id}/reply", new { text = "   " });
        var replied = await AsCoach(_coach, HttpMethod.Put, $"/reviews/{review.Id}/reply", new { text = "Thank you!" }, TenantRole.Staff);
        var page = await Read<PublicReviews>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs/{program.ProgramId}/reviews")));
        var removed = await AsCoach(_coach, HttpMethod.Delete, $"/reviews/{review.Id}/reply");
        var after = Assert.Single((await Read<StudioPage>(await AsCoach(_coach, HttpMethod.Get, $"/reviews?programId={program.ProgramId}"))).Items);

        Assert.Equal((program.Title, 4), (review.ProgramTitle, review.Rating));
        Assert.Equal((4m, 1), (list.Average, list.ReviewCount));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (replied.StatusCode, removed.StatusCode));
        Assert.Equal("Thank you!", Assert.Single(page.Items).Reply);
        Assert.Null(after.Reply);
    }

    // Many buyers review one program at the same moment: the advisory lock orders the rating updates, so
    // the copy in catalog ends with every review counted (design §2.4).
    [Fact]
    public async Task Concurrent_reviews_leave_an_exact_rating()
    {
        var program = _api.Programs.Add(_coach.TenantId, _coach.Slug);
        var buyers = Enumerable.Range(0, 20).Select(_ => Guid.CreateVersion7()).ToList();
        foreach (var buyer in buyers)
        {
            await _api.Deliver(Paid(buyer, _coach.TenantId, _coach.Slug, program));
        }

        var responses = await Task.WhenAll(buyers.Select((buyer, i) => WriteReview(buyer, program.ProgramId, i % 5 + 1, null)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal((20, 3m), _api.Ratings.For(_coach.TenantId, program.ProgramId));
    }

    // The public list tells a signed-in reviewer which review is theirs (no "Report" on it); nobody else.
    [Fact]
    public async Task The_public_list_marks_the_callers_own_review()
    {
        var program = await _api.Enroll(_customer, _coach);
        await WriteReview(_customer, program.ProgramId, 5, "Mine");
        var path = $"/api/v1/marketplace/programs/{program.ProgramId}/reviews";

        var asAuthor = await Read<MineList>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, path), _customer));
        var asOther = await Read<MineList>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, path), Guid.CreateVersion7()));
        var anonymous = await Read<MineList>(await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, path)));

        Assert.Equal((true, false, false), (Assert.Single(asAuthor.Items).Mine, Assert.Single(asOther.Items).Mine, Assert.Single(anonymous.Items).Mine));
    }

    private sealed record MineRow(bool Mine);

    private sealed record MineList(List<MineRow> Items);

    // Reviews are public only while the program is on sale (design §2.4).
    [Fact]
    public async Task Reviews_of_a_program_off_sale_are_not_public()
    {
        var program = await _api.Enroll(_customer, _coach);
        await WriteReview(_customer, program.ProgramId, 4, "Good");

        _api.Programs.TakeOffSale(program.ProgramId);
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs/{program.ProgramId}/reviews"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Moderation (design §2.4): reports collect per review (one per reporter); the admin hides the review,
    // which leaves the page and the rating, stays visible to its author and the coach, marked, and closes
    // the reports; "keep" shows it again.
    [Fact]
    public async Task Reported_reviews_are_hidden_or_kept_by_the_platform_admin()
    {
        var program = _api.Programs.Add(_coach.TenantId, _coach.Slug);
        var other = Guid.CreateVersion7();
        await _api.Deliver(Paid(_customer, _coach.TenantId, _coach.Slug, program));
        await _api.Deliver(Paid(other, _coach.TenantId, _coach.Slug, program));
        await WriteReview(_customer, program.ProgramId, 1, "Rude words");
        await WriteReview(other, program.ProgramId, 5, "Great");
        var reviewId = (await Read<PublicReviews>(await PublicList(program.ProgramId))).Items.Single(r => r.Rating == 1).Id;

        var first = await ReportReview(other, program.ProgramId, reviewId, "Abusive");
        await ReportReview(other, program.ProgramId, reviewId, "Spam");
        await ReportReview(_coach.OwnerId, program.ProgramId, reviewId, "OffTopic");
        var notAdmin = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/reviews/reports"), other);
        var reports = await Read<List<OpenReportRow>>(await AsAdmin(HttpMethod.Get, "/api/v1/admin/reviews/reports"));
        var hidden = await AsAdmin(HttpMethod.Post, $"/api/v1/admin/reviews/{reviewId}/hide");
        var page = await Read<PublicReviews>(await PublicList(program.ProgramId));
        var mine = await Read<MyProgramWithReview>(await _api.AsCustomer(_customer, HttpMethod.Get, $"/programs/{program.ProgramId}"));
        var afterHide = await Read<List<OpenReportRow>>(await AsAdmin(HttpMethod.Get, "/api/v1/admin/reviews/reports"));
        var ratingHidden = _api.Ratings.For(_coach.TenantId, program.ProgramId);
        await ReportReview(other, program.ProgramId, Guid.CreateVersion7(), "Spam");
        await AsAdmin(HttpMethod.Post, $"/api/v1/admin/reviews/{reviewId}/keep");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
        // The queue is shared with other tests' reviews: this one's entry only.
        var open = Assert.Single(reports, r => r.ReviewId == reviewId);
        Assert.Equal((reviewId, 2, "Rude words", program.Title), (open.ReviewId, open.Reports, open.Comment, open.ProgramTitle));
        Assert.Equal(HttpStatusCode.NoContent, hidden.StatusCode);
        Assert.Equal(["Great"], page.Items.Select(r => r.Comment));
        Assert.Equal((1, 5m), ratingHidden);
        Assert.NotNull(mine.Review!.HiddenAt);
        Assert.DoesNotContain(afterHide, r => r.ReviewId == reviewId);
        Assert.Equal((2, 3m), _api.Ratings.For(_coach.TenantId, program.ProgramId));
    }

    // The automatic check (design §2.4): a comment with contact details is hidden at once, waits in the
    // admin's queue as a report from Athlos, and emails the coach nothing; "keep" shows it again, and a
    // later edit the check catches holds it back again.
    [Fact]
    public async Task A_comment_the_automatic_check_catches_waits_hidden_for_the_admin()
    {
        var program = await _api.Enroll(_customer, _coach);

        var written = await Read<ReviewRow>(await WriteReview(_customer, program.ProgramId, 2, "Write to me at maria@example.com"));
        var page = await Read<PublicReviews>(await PublicList(program.ProgramId));
        var queue = await Read<List<OpenReportRow>>(await AsAdmin(HttpMethod.Get, "/api/v1/admin/reviews/reports"));
        await _api.PublishOutbox();
        var emails = Emails().Where(e => e.Template == EmailTemplates.ReviewReceived && e.Data["programTitle"] == program.Title).ToList();
        var reviewId = Assert.Single(queue, r => r.ProgramTitle == program.Title).ReviewId;
        await AsAdmin(HttpMethod.Post, $"/api/v1/admin/reviews/{reviewId}/keep");
        var kept = await Read<PublicReviews>(await PublicList(program.ProgramId));
        var again = await Read<ReviewRow>(await WriteReview(_customer, program.ProgramId, 2, "Better on www.example.gr"));
        var queueAgain = await Read<List<OpenReportRow>>(await AsAdmin(HttpMethod.Get, "/api/v1/admin/reviews/reports"));

        Assert.NotNull(written.HiddenAt);
        Assert.Empty(page.Items);
        Assert.Empty(emails);
        Assert.Single(kept.Items);
        Assert.NotNull(again.HiddenAt);
        Assert.Equal("Better on www.example.gr", Assert.Single(queueAgain, r => r.ReviewId == reviewId).Comment);
        // Leave the shared queue as it was.
        await AsAdmin(HttpMethod.Post, $"/api/v1/admin/reviews/{reviewId}/hide");
    }

    [Theory]
    [InlineData("Email me at anna.k@example.com", "PersonalData")]
    [InlineData("Call me on +30 691 234 5678", "PersonalData")]
    [InlineData("Started 01.09.2026: 12 weeks of 3x5 and 2026-10-24 was the last day", null)]
    [InlineData("All my programs are on www.mycoaching.gr", "Spam")]
    [InlineData("https://bit.ly/abc", "Spam")]
    [InlineData("Great for beginners, built on Node.js knowledge", null)]
    [InlineData("This program is shit", "Abusive")]
    [InlineData("Shiitake mushrooms after training", null)]
    [InlineData("Τι μαλακια ήταν αυτή", "Abusive")]
    [InlineData("Πολύ καλό πρόγραμμα, ευχαριστώ!", null)]
    public void The_automatic_check_catches_contact_details_links_and_listed_words(string comment, string? reason)
    {
        var blocked = Athlos.Enrollment.Api.Features.Reviews.ReviewFilter.BlockedWords(_api.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>());

        Assert.Equal(reason, Athlos.Enrollment.Api.Features.Reviews.ReviewFilter.Check(comment, blocked)?.ToString());
    }

    // The coach's first reply emails the customer (design §5.3); changing the reply sends nothing more.
    [Fact]
    public async Task The_first_reply_emails_the_customer()
    {
        var program = await _api.Enroll(_customer, _coach);
        await WriteReview(_customer, program.ProgramId, 4, "Nice");
        var review = Assert.Single((await Read<StudioPage>(await AsCoach(_coach, HttpMethod.Get, "/reviews"))).Items);

        await AsCoach(_coach, HttpMethod.Put, $"/reviews/{review.Id}/reply", new { text = "Thanks <3" });
        await AsCoach(_coach, HttpMethod.Put, $"/reviews/{review.Id}/reply", new { text = "Thanks again" });
        await _api.PublishOutbox();

        var email = Assert.Single(Emails(), e => e.Template == EmailTemplates.ReviewReplied && e.Data["programTitle"] == program.Title);
        Assert.Equal([$"{_customer}@test.local"], email.To);
        var rendered = new EmailRenderer().Render(email.Template, email.Data);
        Assert.Contains("Thanks &lt;3", rendered.Html, StringComparison.Ordinal);
        Assert.Contains($"/me/programs/{program.ProgramId}", rendered.PlainText, StringComparison.Ordinal);
    }

    // A new workout in a program on sale emails each of its customers, at most once a day (design §5.3).
    [Fact]
    public async Task New_workouts_email_the_programs_customers_once_a_day()
    {
        var program = await _api.Enroll(_customer, _coach);
        var other = Guid.CreateVersion7();
        await _api.Deliver(Paid(other, _coach.TenantId, _coach.Slug, program));

        var first = await Updated(_coach.TenantId, program.ProgramId, "Week 5, day 1");
        await Updated(_coach.TenantId, program.ProgramId, "Week 5, day 2");
        var wrongTenant = await Updated(Guid.CreateVersion7(), program.ProgramId, "Planted");
        await _api.PublishOutbox();

        var emails = Emails().Where(e => e.Template == EmailTemplates.ProgramUpdated && e.Data["programTitle"] == program.Title).ToList();
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (first.StatusCode, wrongTenant.StatusCode));
        Assert.Equal(2, emails.Count);
        Assert.All(emails, e => Assert.Equal("Week 5, day 1", e.Data["workoutTitle"]));
        Assert.Equal([$"{_customer}@test.local", $"{other}@test.local"], emails.SelectMany(e => e.To).Order());
        Assert.Contains("New in", new EmailRenderer().Render(emails[0].Template, emails[0].Data).Subject, StringComparison.Ordinal);
    }

    private Task<HttpResponseMessage> Updated(Guid tenantId, Guid programId, string workoutTitle) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/internal/tenants/{tenantId}/programs/{programId}/updated")
        {
            Content = JsonContent.Create(new { workoutTitle }),
        });

    private Task<HttpResponseMessage> PublicList(Guid programId) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs/{programId}/reviews"));

    private Task<HttpResponseMessage> ReportReview(Guid reporter, Guid programId, Guid reviewId, string reason) =>
        _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/marketplace/programs/{programId}/reviews/{reviewId}/report") { Content = JsonContent.Create(new { reason }) },
            reporter);

    private Task<HttpResponseMessage> AsAdmin(HttpMethod method, string path) =>
        _api.SendAsGateway(new HttpRequestMessage(method, path), Guid.CreateVersion7(), appRoles: ["Platform.Admin"]);

    private Task<HttpResponseMessage> WriteReview(Guid customerId, Guid programId, int rating, string? comment) =>
        _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/me/programs/{programId}/review") { Content = JsonContent.Create(new { rating, comment }) },
            customerId);

    private Task<HttpResponseMessage> AsCoach(Coach coach, HttpMethod method, string path, object? body = null, TenantRole role = TenantRole.Owner) =>
        _api.SendAsGateway(
            new HttpRequestMessage(method, $"/api/v1/studio/{coach.Slug}{path}") { Content = body is null ? null : JsonContent.Create(body) },
            coach.OwnerId, coach.TenantId, role);

    // Every email queued in the outbox, sent or not. Read from the table: test apps share it and each runs
    // a publisher, so which app's sender sends a row is a race, but the row is there.
    private List<EmailNotification> Emails()
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Athlos.Enrollment.Api.Data.EnrollmentDbContext>();
        return db.OutboxMessages.AsNoTracking()
            .Where(m => m.Queue == EmailNotification.Queue)
            .AsEnumerable()
            .Select(m => JsonSerializer.Deserialize<EmailNotification>(m.Payload, JsonSerializerOptions.Web)!)
            .ToList();
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(EnrollmentArrange.Json, Cancel))!;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    private sealed record ReviewRow(Guid Id, int Rating, string? Comment, string AuthorName, string? Reply, DateTimeOffset? HiddenAt);

    private sealed record OpenReportRow(Guid ReviewId, string ProgramTitle, string? Comment, int Reports);

    private sealed record MyProgramWithReview(Guid ProgramId, ReviewRow? Review);

    private sealed record PublicReviews(decimal Average, int Count, List<int> Stars, List<ReviewRow> Items);

    private sealed record StudioItem(Guid Id, Guid ProgramId, string ProgramTitle, int Rating, string? Reply);

    private sealed record StudioPage(List<StudioItem> Items, int TotalCount, decimal Average, int ReviewCount);
}
