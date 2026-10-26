using Athlos.Messaging;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Data;

public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant), IHasOutbox, IHasAdminActions
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Checkout> Checkouts => Set<Checkout>();

    public DbSet<CustomerCart> Carts => Set<CustomerCart>();

    public DbSet<DiscountCode> DiscountCodes => Set<DiscountCode>();

    public DbSet<PayoutRetry> PayoutRetries => Set<PayoutRetry>();

    public DbSet<RefundRequest> RefundRequests => Set<RefundRequest>();

    public DbSet<ProcessedStripeEvent> ProcessedStripeEvents => Set<ProcessedStripeEvent>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<AdminAction> AdminActions => Set<AdminAction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(order =>
        {
            order.HasIndex(o => new { o.TenantId, o.CreatedAt });
            order.HasIndex(o => o.CustomerId);
            order.HasIndex(o => o.StripeCheckoutSessionId);
            order.HasIndex(o => o.CheckoutId);
            order.HasIndex(o => new { o.CustomerId, o.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            order.Property(o => o.CustomerEmail).HasMaxLength(320);
            order.Property(o => o.ProgramTitle).HasMaxLength(120);
            order.Property(o => o.CoachSlug).HasMaxLength(40);
            order.Property(o => o.Currency).HasMaxLength(3).IsFixedLength();
            order.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            order.Property(o => o.IdempotencyKey).HasMaxLength(100);
            order.Property(o => o.StripeCheckoutSessionId).HasMaxLength(255);
            order.Property(o => o.CheckoutUrl).HasMaxLength(2000);
            order.Property(o => o.SellerAccountId).HasMaxLength(255);
            order.Property(o => o.StripeTransferId).HasMaxLength(255);
            order.Property(o => o.StripePaymentIntentId).HasMaxLength(255);
            order.HasIndex(o => o.StripePaymentIntentId);
            order.Property(o => o.DiscountCode).HasMaxLength(20);
            order.Property(o => o.RefundRequestNote).HasMaxLength(500);
            // A code's uses are counted inside its tenant.
            order.HasIndex(o => new { o.TenantId, o.DiscountCode }).HasFilter("discount_code IS NOT NULL");
        });

        modelBuilder.Entity<DiscountCode>(code =>
        {
            code.HasIndex(d => new { d.TenantId, d.Code }).IsUnique();
            code.Property(d => d.Code).HasMaxLength(20);
        });

        modelBuilder.Entity<CustomerCart>(cart =>
        {
            cart.ToTable("carts");
            cart.HasKey(c => c.CustomerId);
            cart.OwnsMany(c => c.Lines, lines => lines.ToJson());
        });

        modelBuilder.Entity<Checkout>(checkout =>
        {
            checkout.HasIndex(c => new { c.CustomerId, c.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            checkout.HasIndex(c => c.StripeCheckoutSessionId).IsUnique();
            checkout.Property(c => c.IdempotencyKey).HasMaxLength(100);
            checkout.Property(c => c.StripeCheckoutSessionId).HasMaxLength(255);
            checkout.Property(c => c.CheckoutUrl).HasMaxLength(2000);
            checkout.Property(c => c.TermsVersion).HasMaxLength(20);
        });

        modelBuilder.Entity<PayoutRetry>(retry =>
        {
            retry.HasKey(r => r.OrderId);
            retry.HasIndex(r => r.NextAttemptAt);
            retry.Property(r => r.CoachSlug).HasMaxLength(40);
            retry.Property(r => r.Currency).HasMaxLength(3).IsFixedLength();
            retry.Property(r => r.LastError).HasMaxLength(200);
        });

        modelBuilder.Entity<RefundRequest>(request =>
        {
            request.HasKey(r => r.OrderId);
            request.HasIndex(r => r.DueAt);
            request.Property(r => r.CoachSlug).HasMaxLength(40);
            request.Property(r => r.ProgramTitle).HasMaxLength(120);
            request.Property(r => r.Currency).HasMaxLength(3).IsFixedLength();
        });

        modelBuilder.Entity<ProcessedStripeEvent>(e =>
        {
            e.HasKey(p => p.StripeEventId);
            e.Property(p => p.StripeEventId).HasMaxLength(255);
        });

        modelBuilder.ConfigureOutbox();
        modelBuilder.ConfigureAdminActions();

        base.OnModelCreating(modelBuilder);
    }
}
