using System.ComponentModel.DataAnnotations;
using Athlos.Tenancy.Api.Data;
using Athlos.Messaging.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.Tenancy.Api.Features.Tenants;

// Becoming a coach (design §5): a signed-in user creates a workspace and becomes its Owner, accepting
// the Terms for coaches (design §5.4): TermsVersion must be the current one (Legal:CoachTermsVersion),
// or nothing is created (409), and the workspace keeps the version, the time and who accepted it.
// The tenant does not exist yet, so the slice enters it itself instead of getting it from the gateway.
public static class CreateTenant
{
    // The Terms for coaches in force (the SPA's lib/legal.ts shows the same version); Legal:CoachTermsVersion overrides it.
    public const string CurrentCoachTermsVersion = "2026-10-26";

    public static string CoachTermsInForce(IConfiguration configuration) => configuration["Legal:CoachTermsVersion"] ?? CurrentCoachTermsVersion;

    public sealed record Request(
        [property: Required, RegularExpression(Slugs.Pattern), StringLength(40, MinimumLength = 3)] string Slug,
        [property: Required, StringLength(100, MinimumLength = 1)] string DisplayName,
        [property: MaxLength(20)] string? TermsVersion = null);

    public sealed record Response(Guid Id, string Slug, string DisplayName);

    public static void MapCreateTenant(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/tenants", Handle)
            .RequireAuthorization()
            .WithName("CreateTenant")
            .WithTags("Tenants");

    private static async Task<Results<Created<Response>, ProblemHttpResult>> Handle(
        Request request, TenantContext tenant, TenancyDbContext db, IConfiguration configuration, TimeProvider time, CancellationToken cancellationToken)
    {
        if (tenant.UserId is not { } userId)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        var terms = CoachTermsInForce(configuration);
        if (request.TermsVersion != terms)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Read and accept the current Terms for coaches to open a workspace.",
                extensions: new Dictionary<string, object?> { ["reason"] = "terms" });
        }

        if (Slugs.IsReserved(request.Slug))
        {
            return SlugTaken();
        }

        var created = new Tenant
        {
            Slug = request.Slug,
            DisplayName = request.DisplayName.Trim(),
            CoachTermsVersion = terms,
            CoachTermsAcceptedAt = time.GetUtcNow(),
            CoachTermsAcceptedBy = userId,
        };
        tenant.EnterTenant(created.Id, TenantRole.Owner);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Tenants.Add(created);
        db.Memberships.Add(new Membership { UserId = userId, Role = TenantRole.Owner });
        WorkspaceEmails.ToOwners(db, EmailTemplates.WorkspaceWelcome, created, configuration);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return SlugTaken();
        }

        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Created(
            $"/api/v1/studio/{created.Slug}/workspace",
            new Response(created.Id, created.Slug, created.DisplayName));
    }

    private static ProblemHttpResult SlugTaken() =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This workspace address is taken.");
}
