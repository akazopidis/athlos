using System.Security.Cryptography;
using System.Text;
using Athlos.Catalog.Api.Data;
using Athlos.Enrollment.Api.Data;
using Athlos.Ordering.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Plans;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.DbMigrator;

// Demo data for dev and staging (design §15.2): three coaches on different plans, 20 published
// programs and two customers with purchases. The k6 scripts and demos use these fixed tenants, so
// results are repeatable.
//
// - Every id is derived from a name, so a re-run finds what it already wrote and adds only what is
//   missing (each database separately: there is no transaction across databases).
// - It writes through the same RLS path as the services: one transaction per coach, tagged with
//   that coach's tenant by TenantSessionInterceptor. The owner is subject to FORCE ROW LEVEL
//   SECURITY too, so the policies hold for the seed as well.
// - Demo coaches and customers are not Entra users (nobody can sign in as them), and the coaches
//   have no Stripe account: their programs can be browsed but not bought.
public static class DemoData
{
    public sealed record Sources(NpgsqlDataSource Tenancy, NpgsqlDataSource Catalog, NpgsqlDataSource Ordering, NpgsqlDataSource Enrollment);

    private sealed record Coach(string Slug, string Name, PlanName Plan, string Color, string[] Workouts, DemoProgram[] Programs);

    private sealed record DemoProgram(string Slug, string Title, ProgramLevel Level, int Weeks, long Price, string Description);

    private sealed record Purchase(int Customer, string Coach, string Program, DateTimeOffset PaidAt);

    private const int WorkoutsPerProgram = 3;
    private const int PlatformFeePercent = 10;

    private static readonly Coach[] Coaches =
    [
        new("demo-forge", "Forge Strength", PlanName.Pro, "#ea580c",
            ["Squat and press", "Deadlift and pull", "Full-body conditioning"],
            [
                new("strong-start", "Strong Start", ProgramLevel.Beginner, 8, 4900, "Learn the big lifts with simple progressions, three sessions a week."),
                new("barbell-basics", "Barbell Basics", ProgramLevel.Beginner, 6, 3900, "Technique first: squat, bench, deadlift and overhead press."),
                new("kettlebell-engine", "Kettlebell Engine", ProgramLevel.Beginner, 4, 2900, "Swings, carries and get-ups for strength and conditioning at home."),
                new("powerbuilding", "Powerbuilding 12", ProgramLevel.Intermediate, 12, 6900, "Heavy triples on the main lifts, volume work for size."),
                new("upper-lower", "Upper / Lower Split", ProgramLevel.Intermediate, 8, 4900, "Four days a week, alternating upper and lower body."),
                new("hybrid-athlete", "Hybrid Athlete", ProgramLevel.Intermediate, 10, 5900, "Lift heavy and run well in the same week."),
                new("deadlift-peak", "Deadlift Peak", ProgramLevel.Advanced, 6, 4400, "A focused block to set a new deadlift max."),
                new("strongman-intro", "Strongman Intro", ProgramLevel.Advanced, 8, 5400, "Yoke, stones and log press, with a gym-friendly variant for each."),
                new("olympic-lifting", "Olympic Lifting Fundamentals", ProgramLevel.Advanced, 10, 7900, "Snatch and clean and jerk, broken into teachable pieces."),
                new("minimalist", "Minimalist Strength", ProgramLevel.Beginner, 6, 1900, "Two 40-minute sessions a week for busy people."),
            ]),
        new("demo-stride", "Stride Running Club", PlanName.Free, "#2563eb",
            ["Easy run", "Intervals", "Long run"],
            [
                new("first-5k", "Your First 5K", ProgramLevel.Beginner, 8, 1900, "From walking breaks to running 5 km without stopping."),
                new("10k-faster", "Faster 10K", ProgramLevel.Intermediate, 10, 2900, "Tempo runs and intervals to take minutes off your 10K."),
                new("half-marathon", "Half Marathon Plan", ProgramLevel.Intermediate, 12, 3900, "Build up to 21.1 km with four runs a week."),
                new("marathon-sub4", "Marathon Sub-4", ProgramLevel.Advanced, 16, 5900, "A structured block for a marathon under four hours."),
                new("trail-ready", "Trail Ready", ProgramLevel.Intermediate, 8, 2900, "Hills, technical descents and strength for the trails."),
            ]),
        new("demo-flow", "Flow Mobility", PlanName.Free, "#0d9488",
            ["Hips and spine", "Shoulders and upper back", "Full-body flow"],
            [
                new("daily-mobility", "Daily Mobility", ProgramLevel.Beginner, 4, 1500, "Fifteen minutes a day to move and feel better."),
                new("desk-reset", "Desk Reset", ProgramLevel.Beginner, 3, 900, "Undo long days at a desk: neck, hips and back."),
                new("splits-progression", "Splits Progression", ProgramLevel.Intermediate, 8, 2400, "Front and middle splits, step by step."),
                new("handstand-path", "Handstand Path", ProgramLevel.Advanced, 10, 3400, "Wrist prep, wall drills and your first freestanding hold."),
                new("yoga-for-lifters", "Yoga for Lifters", ProgramLevel.Intermediate, 6, 1900, "Mobility that carries over to the squat and the overhead press."),
            ]),
    ];

    private static readonly Purchase[] Purchases =
    [
        new(1, "demo-forge", "strong-start", new DateTimeOffset(2026, 9, 20, 9, 30, 0, TimeSpan.Zero)),
        new(1, "demo-stride", "first-5k", new DateTimeOffset(2026, 9, 22, 18, 5, 0, TimeSpan.Zero)),
        new(2, "demo-forge", "hybrid-athlete", new DateTimeOffset(2026, 9, 24, 7, 45, 0, TimeSpan.Zero)),
        new(2, "demo-flow", "daily-mobility", new DateTimeOffset(2026, 9, 25, 20, 10, 0, TimeSpan.Zero)),
    ];

    // Returns the demo slugs it skipped because another workspace already has them (created before
    // demo-* was reserved), or because the demo workspace was deleted: the seed never touches a real
    // tenant and never brings a deleted one back.
    public static async Task<IReadOnlyList<string>> SeedAsync(Sources sources, CancellationToken cancellationToken)
    {
        var skipped = new List<string>();
        foreach (var coach in Coaches)
        {
            var tenantId = Id($"tenant:{coach.Slug}");
            var tenant = new TenantContext();
            tenant.EnterTenant(tenantId, TenantRole.Owner);

            if (!await SeedTenancyAsync(sources.Tenancy, tenant, coach, cancellationToken))
            {
                skipped.Add(coach.Slug);
                continue;
            }

            await SeedCatalogAsync(sources.Catalog, tenant, coach, cancellationToken);
            await SeedOrderingAsync(sources.Ordering, tenant, coach, cancellationToken);
            await SeedEnrollmentAsync(sources.Enrollment, tenant, coach, cancellationToken);
        }

        return skipped;
    }

    // False if the slug belongs to a workspace that is not this demo coach.
    private static async Task<bool> SeedTenancyAsync(NpgsqlDataSource owner, TenantContext tenant, Coach coach, CancellationToken cancellationToken)
    {
        var ours = true;
        await InTenantAsync<TenancyDbContext>(owner, tenant, (o, t) => new TenancyDbContext(o, t), async db =>
        {
            var tenantId = tenant.TenantId!.Value;
            // Tenants are readable by every session (the storefront is public), so this sees them all.
            var holder = await db.Tenants.Where(t => t.Slug == coach.Slug).Select(t => new { t.Id, t.Status }).SingleOrDefaultAsync(cancellationToken);
            if (holder is not null)
            {
                // A demo coach someone deleted (design §3.4) stays deleted: its data is not seeded again.
                ours = holder.Id == tenantId && holder.Status != TenantStatus.Deleted;
                return;
            }

            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Slug = coach.Slug,
                DisplayName = coach.Name,
                Plan = coach.Plan,
                Branding = new Branding { PrimaryColor = coach.Color },
            });
            db.Memberships.Add(new Membership { Id = Id($"membership:{coach.Slug}"), UserId = Id($"owner:{coach.Slug}"), Role = TenantRole.Owner });
        }, cancellationToken);
        return ours;
    }

    private static Task SeedCatalogAsync(NpgsqlDataSource owner, TenantContext tenant, Coach coach, CancellationToken cancellationToken) =>
        InTenantAsync<CatalogDbContext>(owner, tenant, (o, t) => new CatalogDbContext(o, t), async db =>
        {
            var existing = await db.Programs.Select(p => p.Id).ToListAsync(cancellationToken);
            foreach (var program in coach.Programs)
            {
                var programId = ProgramId(coach, program);
                if (existing.Contains(programId))
                {
                    continue;
                }

                var published = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
                db.Programs.Add(new TrainingProgram
                {
                    Id = programId,
                    CoachSlug = coach.Slug,
                    Slug = program.Slug,
                    Title = program.Title,
                    Description = program.Description,
                    Level = program.Level,
                    DurationWeeks = program.Weeks,
                    PriceAmount = program.Price,
                    Currency = "EUR",
                    IsPublished = true,
                    PublishedAt = published,
                    FirstPublishedAt = published,
                });

                for (var day = 1; day <= WorkoutsPerProgram; day++)
                {
                    db.Workouts.Add(new Workout
                    {
                        Id = WorkoutId(coach, program, day),
                        ProgramId = programId,
                        Week = 1,
                        Day = day,
                        Title = coach.Workouts[day - 1],
                        Content = $"Warm up for 10 minutes, then the main work for {program.Title}. Demo content.",
                    });
                }
            }
        }, cancellationToken);

    private static Task SeedOrderingAsync(NpgsqlDataSource owner, TenantContext tenant, Coach coach, CancellationToken cancellationToken) =>
        InTenantAsync<OrderingDbContext>(owner, tenant, (o, t) => new OrderingDbContext(o, t), async db =>
        {
            var existing = await db.Orders.Select(o => o.Id).ToListAsync(cancellationToken);
            foreach (var (purchase, program) in PurchasesOf(coach))
            {
                var orderId = OrderId(purchase);
                if (existing.Contains(orderId))
                {
                    continue;
                }

                db.Orders.Add(new Order
                {
                    Id = orderId,
                    CustomerId = CustomerId(purchase.Customer),
                    // A reserved domain (RFC 2606): nothing can be delivered there.
                    CustomerEmail = $"demo-customer-{purchase.Customer}@example.com",
                    ProgramId = ProgramId(coach, program),
                    ProgramTitle = program.Title,
                    CoachSlug = coach.Slug,
                    Amount = program.Price,
                    Currency = "EUR",
                    PlatformFee = program.Price * PlatformFeePercent / 100,
                    Status = OrderStatus.Paid,
                    PaidAt = purchase.PaidAt,
                });
            }
        }, cancellationToken);

    private static Task SeedEnrollmentAsync(NpgsqlDataSource owner, TenantContext tenant, Coach coach, CancellationToken cancellationToken) =>
        InTenantAsync<EnrollmentDbContext>(owner, tenant, (o, t) => new EnrollmentDbContext(o, t), async db =>
        {
            var existing = await db.Enrollments.Select(e => e.Id).ToListAsync(cancellationToken);
            foreach (var (purchase, program) in PurchasesOf(coach))
            {
                var enrollmentId = Id($"enrollment:{coach.Slug}:{program.Slug}:{purchase.Customer}");
                if (existing.Contains(enrollmentId))
                {
                    continue;
                }

                db.Enrollments.Add(new ProgramEnrollment
                {
                    Id = enrollmentId,
                    CustomerId = CustomerId(purchase.Customer),
                    ProgramId = ProgramId(coach, program),
                    OrderId = OrderId(purchase),
                    ProgramTitle = program.Title,
                    CoachSlug = coach.Slug,
                    EnrolledAt = purchase.PaidAt,
                });

                // The first workout done, so "my programs" shows some progress.
                db.WorkoutProgress.Add(new WorkoutProgress
                {
                    Id = Id($"progress:{enrollmentId}"),
                    EnrollmentId = enrollmentId,
                    CustomerId = CustomerId(purchase.Customer),
                    WorkoutId = WorkoutId(coach, program, 1),
                    CompletedAt = purchase.PaidAt.AddDays(1),
                });
            }
        }, cancellationToken);

    // One transaction per coach and database, tagged with the coach's tenant (design §3.2, layer 4).
    private static async Task InTenantAsync<TContext>(
        NpgsqlDataSource owner,
        TenantContext tenant,
        Func<DbContextOptions<TContext>, ITenantContext, TContext> create,
        Func<TContext, Task> seed,
        CancellationToken cancellationToken)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(owner)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor(tenant))
            .Options;

        await using var db = create(options, tenant);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await seed(db);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static IEnumerable<(Purchase Purchase, DemoProgram Program)> PurchasesOf(Coach coach) =>
        Purchases.Where(p => p.Coach == coach.Slug).Select(p => (p, coach.Programs.Single(program => program.Slug == p.Program)));

    private static Guid ProgramId(Coach coach, DemoProgram program) => Id($"program:{coach.Slug}:{program.Slug}");

    private static Guid WorkoutId(Coach coach, DemoProgram program, int day) => Id($"workout:{coach.Slug}:{program.Slug}:{day}");

    private static Guid OrderId(Purchase purchase) => Id($"order:{purchase.Coach}:{purchase.Program}:{purchase.Customer}");

    private static Guid CustomerId(int customer) => Id($"customer:{customer}");

    // The same name gives the same id on every run and in every environment.
    public static Guid Id(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"athlos-demo:{name}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
