using System.ComponentModel.DataAnnotations;
using Athlos.Enrollment.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.EmailPreferences;

// The customer's choice of the emails enrollment-api sends them (design §5.3, v17.9): new workouts in
// their programs, coaches' replies to their reviews, and price drops on programs they saved. Purchase, refund and account emails are not
// optional. Set in the account (signed in) or by an email's unsubscribe link (no sign-in: the link's
// signed token names the customer and the email).
public static class EmailPreferenceEndpoints
{
    // PriceDrops defaults to on when a client leaves it out (one from before it existed).
    public sealed record Preferences(bool ProgramUpdates, bool ReviewReplies, bool PriceDrops = true);

    public sealed record UnsubscribeRequest([property: Required, MaxLength(64)] string Token);

    // Which email the link turned off: programUpdates, reviewReplies or priceDrops.
    public sealed record Unsubscribed(string Topic);

    // GET and PUT /api/v1/me/email-preferences, in the customer's own transaction (their row, RLS).
    public static void MapMyEmailPreferences(this IEndpointRouteBuilder me)
    {
        me.MapGet("/email-preferences", async (ITenantContext tenant, EnrollmentDbContext db, CancellationToken cancellationToken) =>
            {
                var customerId = tenant.UserId!.Value;
                var row = await db.EmailPreferences.AsNoTracking().SingleOrDefaultAsync(p => p.CustomerId == customerId, cancellationToken);
                return TypedResults.Ok(row is null ? new Preferences(true, true, true) : new Preferences(row.ProgramUpdates, row.ReviewReplies, row.PriceDrops));
            })
            .WithName("GetMyEmailPreferences")
            .WithTags("Email preferences");

        me.MapPut("/email-preferences", async (Preferences request, ITenantContext tenant, EnrollmentDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
            {
                var customerId = tenant.UserId!.Value;
                var now = time.GetUtcNow();
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO email_preferences (customer_id, program_updates, review_replies, price_drops, updated_at)
                    VALUES ({customerId}, {request.ProgramUpdates}, {request.ReviewReplies}, {request.PriceDrops}, {now})
                    ON CONFLICT (customer_id) DO UPDATE
                    SET program_updates = excluded.program_updates, review_replies = excluded.review_replies, price_drops = excluded.price_drops,
                        updated_at = excluded.updated_at
                    """, cancellationToken);
                return TypedResults.Ok(request);
            })
            .WithName("UpdateMyEmailPreferences")
            .WithTags("Email preferences");
    }

    // POST /api/v1/email-preferences/unsubscribe: no sign-in, the token decides. It turns off one email
    // for the customer it names, whoever is signed in, so a forwarded link only ever unsubscribes its
    // recipient. A token that does not verify is 404.
    public static void MapUnsubscribe(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/email-preferences/unsubscribe", Unsubscribe)
            .WithName("Unsubscribe")
            .WithTags("Email preferences");

    private static async Task<Results<Ok<Unsubscribed>, NotFound>> Unsubscribe(
        UnsubscribeRequest request, UnsubscribeTokens tokens, IServiceScopeFactory scopes, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!tokens.TryRead(request.Token, out var customerId, out var topic))
        {
            return TypedResults.NotFound();
        }

        // Only the named email goes off: each column keeps its value AND the new one, which is false for
        // that email alone.
        var (programUpdates, reviewReplies, priceDrops) = (topic != EmailTopic.ProgramUpdates, topic != EmailTopic.ReviewReplies, topic != EmailTopic.PriceDrops);
        var now = time.GetUtcNow();
        await scopes.AsUserAsync<EnrollmentDbContext, int>(customerId, db => db.Database.ExecuteSqlAsync($"""
            INSERT INTO email_preferences (customer_id, program_updates, review_replies, price_drops, updated_at)
            VALUES ({customerId}, {programUpdates}, {reviewReplies}, {priceDrops}, {now})
            ON CONFLICT (customer_id) DO UPDATE
            SET program_updates = email_preferences.program_updates AND excluded.program_updates,
                review_replies = email_preferences.review_replies AND excluded.review_replies,
                price_drops = email_preferences.price_drops AND excluded.price_drops,
                updated_at = excluded.updated_at
            """, cancellationToken), cancellationToken);
        return TypedResults.Ok(new Unsubscribed(topic switch
        {
            EmailTopic.ProgramUpdates => "programUpdates",
            EmailTopic.ReviewReplies => "reviewReplies",
            _ => "priceDrops",
        }));
    }

    // For the senders, inside the coach's tenant: the customers who turned this email off. RLS shows
    // the preferences of the tenant's own customers only (tenant_customers_read).
    public static IQueryable<Guid> TurnedOff(EnrollmentDbContext db, EmailTopic topic) =>
        (topic switch
        {
            EmailTopic.ProgramUpdates => db.EmailPreferences.Where(p => !p.ProgramUpdates),
            EmailTopic.ReviewReplies => db.EmailPreferences.Where(p => !p.ReviewReplies),
            _ => db.EmailPreferences.Where(p => !p.PriceDrops),
        })
        .Select(p => p.CustomerId);

    // The email's unsubscribe link. The token is in the fragment, which browsers never send to a
    // server, so it stays out of gateway logs; the SPA's /unsubscribe page posts it.
    public static string UnsubscribeUrl(Uri publicUrl, UnsubscribeTokens tokens, Guid customerId, EmailTopic topic) =>
        new Uri(publicUrl, $"/unsubscribe#{tokens.Create(customerId, topic)}").ToString();
}
