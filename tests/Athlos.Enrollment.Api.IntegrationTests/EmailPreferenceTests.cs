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

// Email preferences (design §5.3, v17.9): the customer turns off the new-workout and review-reply emails
// in their account or with the email's own unsubscribe link, and the senders skip them.
public sealed class EmailPreferenceTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly EnrollmentApiFactory _api = new(databases);
    private readonly Coach _coach = NewCoach();
    private readonly Guid _customer = Guid.CreateVersion7();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_email_is_on_until_the_customer_turns_one_off()
    {
        var other = Guid.CreateVersion7();
        var before = await Preferences(_customer);

        var saved = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/email-preferences") { Content = JsonContent.Create(new Prefs(false, true)) }, _customer);
        var again = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/email-preferences") { Content = JsonContent.Create(new Prefs(false, false)) }, _customer);

        Assert.Equal(new Prefs(true, true), before);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (saved.StatusCode, again.StatusCode));
        Assert.Equal(new Prefs(false, false), await Preferences(_customer));
        Assert.Equal(new Prefs(true, true), await Preferences(other));
    }

    // Turned off, the new-workout email skips the customer; the other buyer's carries a link that turns
    // it off for them, and the link names only that email.
    [Fact]
    public async Task New_workout_emails_skip_customers_who_turned_them_off_and_carry_a_link()
    {
        var program = await _api.Enroll(_customer, _coach);
        var other = Guid.CreateVersion7();
        await _api.Deliver(Paid(other, _coach.TenantId, _coach.Slug, program));
        await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/email-preferences") { Content = JsonContent.Create(new Prefs(false, true)) }, _customer);

        await Updated(program.ProgramId, "Week 2");
        var email = Assert.Single(Emails(), e => e.Template == EmailTemplates.ProgramUpdated && e.Data["programTitle"] == program.Title);
        var rendered = new EmailRenderer().Render(email.Template, email.Data);
        var unsubscribe = await Unsubscribe(Token(email), signedIn: null);

        Assert.Equal([$"{other}@test.local"], email.To);
        Assert.Contains($"Unsubscribe: {email.Data["unsubscribeUrl"]}", rendered.PlainText, StringComparison.Ordinal);
        Assert.Contains(">Unsubscribe</a>", rendered.Html, StringComparison.Ordinal);
        Assert.EndsWith("</html>\n", rendered.Html.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, unsubscribe.StatusCode);
        Assert.Equal(new Prefs(false, true), await Preferences(other));
    }

    [Fact]
    public async Task A_review_reply_email_skips_a_customer_who_turned_it_off()
    {
        var program = await _api.Enroll(_customer, _coach);
        var other = Guid.CreateVersion7();
        await _api.Deliver(Paid(other, _coach.TenantId, _coach.Slug, program));
        await WriteReview(_customer, program.ProgramId);
        await WriteReview(other, program.ProgramId);
        await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/email-preferences") { Content = JsonContent.Create(new Prefs(true, false)) }, _customer);

        var reviews = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{_coach.Slug}/reviews"), _coach.OwnerId, _coach.TenantId, TenantRole.Owner);
        foreach (var review in (await reviews.Content.ReadFromJsonAsync<StudioPage>(EnrollmentArrange.Json, Cancel))!.Items)
        {
            await _api.SendAsGateway(
                new HttpRequestMessage(HttpMethod.Put, $"/api/v1/studio/{_coach.Slug}/reviews/{review.Id}/reply") { Content = JsonContent.Create(new { text = "Thanks" }) },
                _coach.OwnerId, _coach.TenantId, TenantRole.Owner);
        }

        var email = Assert.Single(Emails(), e => e.Template == EmailTemplates.ReviewReplied && e.Data["programTitle"] == program.Title);
        Assert.Equal([$"{other}@test.local"], email.To);
        Assert.Equal(HttpStatusCode.OK, (await Unsubscribe(Token(email), signedIn: null)).StatusCode);
        Assert.Equal(new Prefs(true, false), await Preferences(other));
    }

    // The token decides: a link signed in as someone else still only unsubscribes its recipient, and a
    // changed token (another customer's id, or another email) is 404.
    [Fact]
    public async Task An_unsubscribe_link_only_ever_changes_its_recipient()
    {
        var program = await _api.Enroll(_customer, _coach);
        await Updated(program.ProgramId, "Week 3");
        var token = Token(Assert.Single(Emails(), e => e.Template == EmailTemplates.ProgramUpdated && e.Data["programTitle"] == program.Title));
        var someoneElse = Guid.CreateVersion7();
        var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(token);
        bytes[0] ^= 1;
        var forged = System.Buffers.Text.Base64Url.EncodeToString(bytes);
        bytes[0] ^= 1;
        bytes[16] = 2;
        var otherTopic = System.Buffers.Text.Base64Url.EncodeToString(bytes);

        var signedInAsAnother = await Unsubscribe(token, signedIn: someoneElse);
        var tampered = await Unsubscribe(forged, signedIn: null);
        var retargeted = await Unsubscribe(otherTopic, signedIn: null);
        var garbage = await Unsubscribe("not-a-token", signedIn: null);

        Assert.Equal(HttpStatusCode.OK, signedInAsAnother.StatusCode);
        Assert.Equal("programUpdates", (await signedInAsAnother.Content.ReadFromJsonAsync<Unsubscribed>(EnrollmentArrange.Json, Cancel))!.Topic);
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound), (tampered.StatusCode, retargeted.StatusCode, garbage.StatusCode));
        Assert.Equal(new Prefs(false, true), await Preferences(_customer));
        Assert.Equal(new Prefs(true, true), await Preferences(someoneElse));
    }

    // The export shows the choice; erasing the account removes the row.
    [Fact]
    public async Task The_export_shows_the_preferences_and_erasure_removes_them()
    {
        await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/email-preferences") { Content = JsonContent.Create(new Prefs(false, true)) }, _customer);

        var export = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/customers/{_customer}/export"));
        var json = JsonDocument.Parse(await export.Content.ReadAsStringAsync(Cancel)).RootElement;
        var erased = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{_customer}"));

        Assert.False(json.GetProperty("emailPreferences").GetProperty("programUpdates").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, erased.StatusCode);
        // No row reads as every email on.
        Assert.Equal(new Prefs(true, true), await Preferences(_customer));
    }

    private async Task<Prefs> Preferences(Guid customerId)
    {
        var response = await _api.AsCustomer(customerId, HttpMethod.Get, "/email-preferences");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Prefs>(EnrollmentArrange.Json, Cancel))!;
    }

    private Task<HttpResponseMessage> Unsubscribe(string token, Guid? signedIn) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, "/api/v1/email-preferences/unsubscribe") { Content = JsonContent.Create(new { token }) }, signedIn);

    private Task<HttpResponseMessage> Updated(Guid programId, string workoutTitle) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/internal/tenants/{_coach.TenantId}/programs/{programId}/updated")
        {
            Content = JsonContent.Create(new { workoutTitle }),
        });

    private Task<HttpResponseMessage> WriteReview(Guid customerId, Guid programId) =>
        _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/me/programs/{programId}/review") { Content = JsonContent.Create(new { rating = 5, comment = "Good" }) },
            customerId);

    private static string Token(EmailNotification email) => new Uri(email.Data["unsubscribeUrl"]).Fragment.TrimStart('#');

    // Every email queued in the outbox, from the table (see ReviewTests.Emails).
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

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    private sealed record Prefs(bool ProgramUpdates, bool ReviewReplies);

    private sealed record Unsubscribed(string Topic);

    private sealed record StudioItem(Guid Id);

    private sealed record StudioPage(List<StudioItem> Items);
}
