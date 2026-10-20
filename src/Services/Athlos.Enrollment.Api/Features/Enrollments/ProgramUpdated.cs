using System.ComponentModel.DataAnnotations;
using Athlos.Enrollment.Api.Data;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.Enrollments;

// For catalog-api (signed), after a coach adds a workout to a program that has been on sale (design
// §5.3): its customers are emailed that the program has something new (program-updated), at most once
// a day each, so a coach adding a week of workouts sends one email, not seven. The handler enters the
// named tenant, so the tenant filter and RLS confine it to that coach's enrollments. Enrollments
// without an address (from before v1.12.0) get nothing.
public static class ProgramUpdated
{
    public static readonly TimeSpan Quiet = TimeSpan.FromDays(1);

    public sealed record Request([property: Required, MaxLength(120)] string WorkoutTitle);

    public static void MapProgramUpdated(this IEndpointRouteBuilder app) =>
        app.MapPost("/internal/tenants/{tenantId:guid}/programs/{programId:guid}/updated", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(
        Guid tenantId, Guid programId, Request request, TenantContext tenant, EnrollmentDbContext db, TimeProvider time, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        var now = time.GetUtcNow();
        var publicUrl = new Uri(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var due = await db.Enrollments
            .Where(e => e.ProgramId == programId && e.CustomerEmail != null && (e.UpdateEmailedAt == null || e.UpdateEmailedAt < now - Quiet))
            .ToListAsync(cancellationToken);
        foreach (var enrollment in due)
        {
            enrollment.UpdateEmailedAt = now;
            db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, new EmailNotification(
                EmailTemplates.ProgramUpdated, To: [enrollment.CustomerEmail!], ToOwnersOfTenant: null, Locale: "en", new Dictionary<string, string>
                {
                    ["programTitle"] = enrollment.ProgramTitle,
                    ["workoutTitle"] = request.WorkoutTitle,
                    ["coach"] = enrollment.CoachSlug,
                    ["programUrl"] = new Uri(publicUrl, $"/me/programs/{programId}").ToString(),
                })));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
