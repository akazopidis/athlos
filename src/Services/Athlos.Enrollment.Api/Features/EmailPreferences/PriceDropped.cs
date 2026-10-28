using System.ComponentModel.DataAnnotations;
using Athlos.Enrollment.Api.Data;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.EmailPreferences;

// For catalog-api (signed), after a coach lowers the price of a program on sale (design §2.4, v17.9):
// the customers who saved it are emailed (price-drop), at most once a week each and only if they keep
// these emails on. Catalog owns the saved programs and names the customers; enrollment owns the
// preferences and has an outbox, and the worker finds each address with tenancy-api (ToUser), so no
// service here handles the customers' addresses. Each customer is handled in their own transaction,
// acting for them (their preferences row, RLS), with the email in the same transaction.
public static class PriceDropped
{
    public const int MaxCustomers = 500;

    public static readonly TimeSpan Quiet = TimeSpan.FromDays(7);

    public sealed record Request(
        [property: Required, MaxLength(120)] string ProgramTitle,
        [property: Required, MaxLength(40)] string CoachSlug,
        [property: Required, MaxLength(60)] string ProgramSlug,
        [property: Range(0, long.MaxValue)] long OldPriceAmount,
        [property: Range(0, long.MaxValue)] long NewPriceAmount,
        [property: Required, StringLength(3, MinimumLength = 3)] string Currency,
        [property: Required, MaxLength(MaxCustomers)] List<Guid> CustomerIds);

    public static void MapPriceDropped(this IEndpointRouteBuilder app) =>
        app.MapPost("/internal/price-drops", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(
        Request request, UnsubscribeTokens tokens, IServiceScopeFactory scopes, TimeProvider time, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (request.NewPriceAmount >= request.OldPriceAmount)
        {
            return TypedResults.NoContent();
        }

        var now = time.GetUtcNow();
        var publicUrl = new Uri(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
        var programUrl = new Uri(publicUrl, $"/c/{request.CoachSlug}/programs/{request.ProgramSlug}").ToString();
        foreach (var customerId in request.CustomerIds.Distinct())
        {
            await scopes.AsUserAsync<EnrollmentDbContext, int>(customerId, async db =>
            {
                // Claims this week's email in one statement: a new row, or a row with price drops on and
                // no email in the last week. Two drops at the same moment cannot both email the customer.
                var claimed = await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO email_preferences (customer_id, program_updates, review_replies, price_drops, price_drop_emailed_at, updated_at)
                    VALUES ({customerId}, true, true, true, {now}, {now})
                    ON CONFLICT (customer_id) DO UPDATE SET price_drop_emailed_at = excluded.price_drop_emailed_at
                    WHERE email_preferences.price_drops
                        AND (email_preferences.price_drop_emailed_at IS NULL OR email_preferences.price_drop_emailed_at < {now - Quiet})
                    """, cancellationToken);
                if (claimed == 0)
                {
                    return 0;
                }

                db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, new EmailNotification(
                    EmailTemplates.PriceDrop, To: [], ToOwnersOfTenant: null, Locale: "en", new Dictionary<string, string>
                    {
                        ["programTitle"] = request.ProgramTitle,
                        ["coach"] = request.CoachSlug,
                        ["oldPrice"] = MoneyText.Format(request.OldPriceAmount, request.Currency),
                        ["newPrice"] = MoneyText.Format(request.NewPriceAmount, request.Currency),
                        ["programUrl"] = programUrl,
                        ["unsubscribeUrl"] = EmailPreferenceEndpoints.UnsubscribeUrl(publicUrl, tokens, customerId, EmailTopic.PriceDrops),
                    },
                    ToUser: customerId)));
                return await db.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }

        return TypedResults.NoContent();
    }
}
