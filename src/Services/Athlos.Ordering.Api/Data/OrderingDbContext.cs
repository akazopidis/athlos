using Athlos.Messaging;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Data;

public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant), IHasOutbox
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Checkout> Checkouts => Set<Checkout>();

    public DbSet<PayoutRetry> PayoutRetries => Set<PayoutRetry>();

    public DbSet<ProcessedStripeEvent> ProcessedStripeEvents => Set<ProcessedStripeEvent>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

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
        });

        modelBuilder.Entity<Checkout>(checkout =>
        {
            checkout.HasIndex(c => new { c.CustomerId, c.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            checkout.HasIndex(c => c.StripeCheckoutSessionId).IsUnique();
            checkout.Property(c => c.IdempotencyKey).HasMaxLength(100);
            checkout.Property(c => c.StripeCheckoutSessionId).HasMaxLength(255);
            checkout.Property(c => c.CheckoutUrl).HasMaxLength(2000);
        });

        modelBuilder.Entity<PayoutRetry>(retry =>
        {
            retry.HasKey(r => r.OrderId);
            retry.HasIndex(r => r.NextAttemptAt);
            retry.Property(r => r.CoachSlug).HasMaxLength(40);
            retry.Property(r => r.Currency).HasMaxLength(3).IsFixedLength();
            retry.Property(r => r.LastError).HasMaxLength(200);
        });

        modelBuilder.Entity<ProcessedStripeEvent>(e =>
        {
            e.HasKey(p => p.StripeEventId);
            e.Property(p => p.StripeEventId).HasMaxLength(255);
        });

        modelBuilder.ConfigureOutbox();

        base.OnModelCreating(modelBuilder);
    }
}
