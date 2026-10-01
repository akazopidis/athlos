using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Athlos.Messaging;

// Handles one message type from one queue, in a DI scope of its own per message. It must be
// idempotent: delivery is at least once (the outbox may resend, Service Bus may redeliver).
public interface IMessageHandler<in TMessage>
{
    Task HandleAsync(TMessage message, CancellationToken cancellationToken);
}

// What the consumer does with a message once it is handled: Service Bus settlement, a fake in tests.
public interface IMessageSettlement
{
    Task CompleteAsync(CancellationToken cancellationToken);

    Task AbandonAsync(CancellationToken cancellationToken);

    Task DeadLetterAsync(string reason, string description, CancellationToken cancellationToken);
}

// Receives TMessage from a Service Bus queue and hands each one to IMessageHandler<TMessage>
// (design §2.2). A message is completed only after the handler succeeded. If the handler throws,
// the message is abandoned and redelivered, up to the queue's maxDeliveryCount, then dead-lettered.
// A message that cannot be read is dead-lettered at once: retrying would not change it.
public sealed partial class QueueConsumer<TMessage>(
    IServiceProvider services, IServiceScopeFactory scopes, string queue, ILogger<QueueConsumer<TMessage>> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Resolved only when receiving starts: building the host (OpenAPI generation, tests) needs no Service Bus.
        var client = services.GetRequiredService<ServiceBusClient>();
        await using var processor = client.CreateProcessor(queue, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 4,
        });
        processor.ProcessMessageAsync += args =>
            ProcessAsync(args.Message.Body, args.Message.MessageId, args.Message.DeliveryCount, new Settlement(args), args.CancellationToken);
        processor.ProcessErrorAsync += args =>
        {
            LogReceiveFailed(logger, args.Exception, queue, args.ErrorSource.ToString());
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }

        await processor.StopProcessingAsync(CancellationToken.None);
    }

    // One delivery. Public so tests can deliver a message without Service Bus.
    public async Task ProcessAsync(BinaryData body, string messageId, int deliveryCount, IMessageSettlement settlement, CancellationToken cancellationToken)
    {
        TMessage? message;
        try
        {
            message = body.ToObjectFromJson<TMessage>(JsonSerializerOptions.Web);
        }
        catch (JsonException e)
        {
            LogUnreadable(logger, e, queue, messageId);
            await settlement.DeadLetterAsync("Unreadable", e.Message, cancellationToken);
            return;
        }

        if (message is null)
        {
            await settlement.DeadLetterAsync("Unreadable", "The body is null.", cancellationToken);
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IMessageHandler<TMessage>>().HandleAsync(message, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogHandlerFailed(logger, e, queue, messageId, deliveryCount);
            await settlement.AbandonAsync(cancellationToken);
            return;
        }

        await settlement.CompleteAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Receiving from {Queue} failed ({ErrorSource})")]
    private static partial void LogReceiveFailed(ILogger logger, Exception exception, string queue, string errorSource);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} on {Queue} is unreadable; dead-lettered")]
    private static partial void LogUnreadable(ILogger logger, Exception exception, string queue, string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling message {MessageId} from {Queue} failed (delivery {DeliveryCount}); it will be retried")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string queue, string messageId, int deliveryCount);

    private sealed class Settlement(ProcessMessageEventArgs args) : IMessageSettlement
    {
        public Task CompleteAsync(CancellationToken cancellationToken) =>
            args.CompleteMessageAsync(args.Message, cancellationToken);

        public Task AbandonAsync(CancellationToken cancellationToken) =>
            args.AbandonMessageAsync(args.Message, cancellationToken: cancellationToken);

        public Task DeadLetterAsync(string reason, string description, CancellationToken cancellationToken) =>
            args.DeadLetterMessageAsync(args.Message, reason, description, cancellationToken);
    }
}

public static class QueueConsumerExtensions
{
    // Receives TMessage from the queue and handles it with THandler (scoped).
    public static IHostApplicationBuilder AddQueueConsumer<TMessage, THandler>(this IHostApplicationBuilder builder, string queue)
        where THandler : class, IMessageHandler<TMessage>
    {
        builder.AddServiceBus();
        builder.Services.AddScoped<IMessageHandler<TMessage>, THandler>();
        builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<QueueConsumer<TMessage>>(sp, queue));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<QueueConsumer<TMessage>>());
        return builder;
    }

    // ConnectionStrings:messaging: the Service Bus emulator locally, the namespace endpoint in Azure
    // (managed identity). Registered once, whether a service sends, receives or both.
    internal static void AddServiceBus(this IHostApplicationBuilder builder)
    {
        if (!builder.Services.Any(s => s.ServiceType == typeof(ServiceBusClient)))
        {
            builder.AddAzureServiceBusClient("messaging");
        }
    }
}
