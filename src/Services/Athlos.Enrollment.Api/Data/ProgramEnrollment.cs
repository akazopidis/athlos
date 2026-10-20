using Athlos.Tenancy.Data;

namespace Athlos.Enrollment.Api.Data;

// A customer's access to a program they bought (design §2.3). Like an order, it belongs to the
// coach's tenant and to the customer: RLS lets the tenant see its rows and the customer see theirs,
// across coaches. Created only from OrderPaid, once per order.
public sealed class ProgramEnrollment : ITenantOwned, IHasTimestamps
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    // Entra object id of the buyer.
    public required Guid CustomerId { get; init; }

    public required Guid ProgramId { get; init; }

    // Unique: a redelivered OrderPaid finds the enrollment it already created.
    public required Guid OrderId { get; init; }

    // Copied from the order, so "my programs" needs no call to catalog (design §2.3).
    public required string ProgramTitle { get; init; }

    public required string CoachSlug { get; init; }

    public required DateTimeOffset EnrolledAt { get; init; }

    // The buyer's address, from OrderPaid (design §5.3), for the emails about this program: a coach's
    // reply to their review, new workouts. Null on enrollments from before v1.12.0 (they get none).
    // Personal data: never logged; erased with the enrollment when the account is deleted.
    public string? CustomerEmail { get; init; }

    // When the customer was last emailed that the program has new workouts: at most once a day.
    public DateTimeOffset? UpdateEmailedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One workout the customer marked as done. Belongs to the enrollment's tenant; the customer reads it
// across tenants like the enrollment itself.
public sealed class WorkoutProgress : ITenantOwned, IHasTimestamps
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public required Guid EnrollmentId { get; init; }

    public required Guid CustomerId { get; init; }

    // A workout in catalog-api (by id only, design §2.3).
    public required Guid WorkoutId { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
