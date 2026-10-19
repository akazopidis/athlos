using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Notifications.Worker;
using Athlos.Tenancy;
using Athlos.Testing;
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

        var first = await WriteReview(_customer, program.ProgramId, 4, "  Solid plan.  ");
        var edited = await WriteReview(_customer, program.ProgramId, 5, "Even better in week 3.");
        var mine = await Read<MyProgramWithReview>(await _api.AsCustomer(_customer, HttpMethod.Get, $"/programs/{program.ProgramId}"));

        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
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
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (replied.StatusCode, removed.StatusCode));
        Assert.Equal("Thank you!", Assert.Single(page.Items).Reply);
        Assert.Null(after.Reply);
    }

    private Task<HttpResponseMessage> WriteReview(Guid customerId, Guid programId, int rating, string? comment) =>
        _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/me/programs/{programId}/review") { Content = JsonContent.Create(new { rating, comment }) },
            customerId);

    private Task<HttpResponseMessage> AsCoach(Coach coach, HttpMethod method, string path, object? body = null, TenantRole role = TenantRole.Owner) =>
        _api.SendAsGateway(
            new HttpRequestMessage(method, $"/api/v1/studio/{coach.Slug}{path}") { Content = body is null ? null : JsonContent.Create(body) },
            coach.OwnerId, coach.TenantId, role);

    private IEnumerable<EmailNotification> Emails() =>
        _api.Messages.Sent
            .Where(m => m.Queue == EmailNotification.Queue)
            .Select(m => JsonSerializer.Deserialize<EmailNotification>(m.Message.Payload, JsonSerializerOptions.Web)!);

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(EnrollmentArrange.Json, Cancel))!;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    private sealed record ReviewRow(int Rating, string? Comment, string AuthorName, string? Reply);

    private sealed record MyProgramWithReview(Guid ProgramId, ReviewRow? Review);

    private sealed record PublicReviews(decimal Average, int Count, List<int> Stars, List<ReviewRow> Items);

    private sealed record StudioItem(Guid Id, Guid ProgramId, string ProgramTitle, int Rating, string? Reply);

    private sealed record StudioPage(List<StudioItem> Items, int TotalCount);
}
