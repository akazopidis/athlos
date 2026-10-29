using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Data;

// The platform admin's audit trail (design §1, §3.4): who changed what, when, and why. Every admin
// action states a reason, and the service that makes the change records it in its own admin_actions
// table, in the same transaction as the change: tenancy for workspaces and plans, enrollment for
// review decisions, ordering for payout retries. Like payout_retries, the table is the service's own
// log, not a tenant's data (policy service_only); only admin routes read it. It holds no customer's
// personal data, so it outlives account deletions.
public sealed class AdminAction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid AdminId { get; set; }

    public AdminActionKind Action { get; set; }

    // The workspace the action concerns, if any.
    public Guid? TenantId { get; set; }

    // What it was done to, as the admin saw it: a workspace, a program, a coach's payout.
    public required string Subject { get; set; }

    public string? Detail { get; set; }

    public required string Reason { get; set; }

    public DateTimeOffset At { get; set; }
}

public enum AdminActionKind
{
    WorkspaceSuspended,
    WorkspaceReactivated,
    PlanChanged,
    ReviewHidden,
    ReviewKept,
    PayoutRetried,
    RefundIssued,
    ProgramUnpublished,
    ProgramAllowed,
    ReportsKept,
    WorkspaceDeleted,
}

public interface IHasAdminActions
{
    DbSet<AdminAction> AdminActions { get; }
}

public static class AdminActions
{
    public const int MinReason = 3;
    public const int MaxReason = 500;

    public sealed record Item(Guid Id, Guid AdminId, AdminActionKind Action, string Subject, string? Detail, string Reason, DateTimeOffset At);

    public static void ConfigureAdminActions(this ModelBuilder modelBuilder) =>
        modelBuilder.Entity<AdminAction>(action =>
        {
            action.ToTable("admin_actions");
            action.Property(a => a.Action).HasConversion<string>().HasMaxLength(40);
            action.Property(a => a.Subject).HasMaxLength(200);
            action.Property(a => a.Detail).HasMaxLength(200);
            action.Property(a => a.Reason).HasMaxLength(MaxReason);
            action.HasIndex(a => a.At);
        });

    // The reason, trimmed, or null when it is missing, too short or too long: the caller answers 400.
    public static string? Valid(string? reason) =>
        reason?.Trim() is { Length: >= MinReason and <= MaxReason } trimmed ? trimmed : null;

    public static ProblemHttpResult MissingReason() =>
        TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Give a reason of {MinReason} to {MaxReason} characters.");

    // GET {pattern}: the newest actions first, at most 100. Mapped inside a group that requires the
    // platform admin.
    public static RouteHandlerBuilder MapAdminActions<TContext>(this IEndpointRouteBuilder admin, string pattern)
        where TContext : DbContext, IHasAdminActions =>
        admin.MapGet(pattern, async (TContext db, CancellationToken cancellationToken) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            return TypedResults.Ok(await db.AdminActions.AsNoTracking()
                .OrderByDescending(a => a.At)
                .Take(100)
                .Select(a => new Item(a.Id, a.AdminId, a.Action, a.Subject, a.Detail, a.Reason, a.At))
                .ToListAsync(cancellationToken));
        });
}
