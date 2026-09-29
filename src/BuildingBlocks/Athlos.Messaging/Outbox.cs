using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Athlos.Messaging;

// The transactional outbox (design §2.2, §14: "a 100-line outbox + SDK is enough").
// A service writes an OutboxMessage in the same database transaction as the change it announces,
// so an event is sent if and only if the change committed. OutboxPublisher then sends it to its
// Service Bus queue. Delivery is at least once: consumers dedupe on ids.
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    // The queue it goes to, for example "order-paid".
    public required string Queue { get; init; }

    public required string Type { get; init; }

    public required string Payload { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? SentAt { get; set; }

    public static OutboxMessage For<T>(string queue, T message) => new()
    {
        Queue = queue,
        Type = typeof(T).Name,
        Payload = JsonSerializer.Serialize(message, JsonSerializerOptions.Web),
    };
}

public interface IHasOutbox
{
    DbSet<OutboxMessage> OutboxMessages { get; }
}

public static class OutboxModel
{
    public static void ConfigureOutbox(this ModelBuilder modelBuilder) =>
        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_messages");
            outbox.Property(m => m.Queue).HasMaxLength(100);
            outbox.Property(m => m.Type).HasMaxLength(100);
            outbox.Property(m => m.Payload).HasColumnType("jsonb");
            // The publisher's query: unsent messages, oldest first.
            outbox.HasIndex(m => m.OccurredAt).HasFilter("sent_at IS NULL");
        });
}

// Sends one message; the Service Bus implementation below, a fake in tests.
public interface IMessageSender
{
    Task SendAsync(string queue, OutboxMessage message, CancellationToken cancellationToken);
}

public sealed class ServiceBusMessageSender(ServiceBusClient client) : IMessageSender, IAsyncDisposable
{
    private readonly Dictionary<string, ServiceBusSender> _senders = [];

    public async Task SendAsync(string queue, OutboxMessage message, CancellationToken cancellationToken)
    {
        if (!_senders.TryGetValue(queue, out var sender))
        {
            _senders[queue] = sender = client.CreateSender(queue);
        }

        await sender.SendMessageAsync(new ServiceBusMessage(message.Payload)
        {
            // Service Bus Basic has no duplicate detection; consumers dedupe on the ids in the payload.
            MessageId = message.Id.ToString(),
            Subject = message.Type,
            ContentType = "application/json",
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
        {
            await sender.DisposeAsync();
        }
    }
}

// Polls the outbox and sends what is waiting. Rows are claimed with FOR UPDATE SKIP LOCKED inside a
// transaction, so several replicas never send the same row at the same time; a row is marked sent
// only after Service Bus accepted it.
public sealed partial class OutboxPublisher<TContext>(IServiceScopeFactory scopes, ILogger<OutboxPublisher<TContext>> logger) : BackgroundService
    where TContext : DbContext, IHasOutbox
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await OutboxDelivery.PublishPendingAsync<TContext>(scopes, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogPublishFailed(logger, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing the outbox failed; retrying on the next tick")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);
}

// One pass over the outbox; the publisher calls it on a timer, tests call it directly.
public static class OutboxDelivery
{
    private const int BatchSize = 20;

    public static async Task<int> PublishPendingAsync<TContext>(IServiceScopeFactory scopes, CancellationToken cancellationToken)
        where TContext : DbContext, IHasOutbox
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IMessageSender>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var pending = await db.OutboxMessages
            .FromSql($"SELECT * FROM outbox_messages WHERE sent_at IS NULL ORDER BY occurred_at LIMIT {BatchSize} FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            await sender.SendAsync(message.Queue, message, cancellationToken);
            message.SentAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return pending.Count;
    }
}

public static class OutboxExtensions
{
    // ConnectionStrings:messaging: the Service Bus emulator locally, the namespace endpoint in Azure
    // (managed identity).
    public static IHostApplicationBuilder AddOutbox<TContext>(this IHostApplicationBuilder builder)
        where TContext : DbContext, IHasOutbox
    {
        builder.AddAzureServiceBusClient("messaging");
        builder.Services.AddSingleton<IMessageSender, ServiceBusMessageSender>();
        builder.Services.AddHostedService<OutboxPublisher<TContext>>();
        return builder;
    }
}
