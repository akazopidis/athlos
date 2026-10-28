using System.Collections.Concurrent;
using System.Text.Json;
using Athlos.Messaging;
using Athlos.Ordering.Api.Data;
using Athlos.Tenancy;
using Athlos.Testing;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: AssemblyFixture(typeof(AthlosDatabases))]
[assembly: AssemblyFixture(typeof(Athlos.Messaging.Tests.ServiceBusEmulator))]

namespace Athlos.Messaging.Tests;

// Messaging against a real broker (design §2.2), not fakes: the outbox publisher, the queue consumer's
// settlement, retries and dead-lettering, as Service Bus does them. Each test has a queue of its own
// (emulator-config.json, at most 3 deliveries).
public sealed class BrokerTests(AthlosDatabases databases, ServiceBusEmulator broker)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public sealed record Ping(string Id);

    [Fact]
    public async Task The_outbox_sends_a_committed_row_with_its_id_type_and_body_and_marks_it_sent()
    {
        var services = new ServiceCollection()
            .AddScoped<ITenantContext, TenantContext>()
            .AddDbContext<OrderingDbContext>(o => o.UseNpgsql(databases.AppConnectionString("ordering")).UseSnakeCaseNamingConvention())
            .AddSingleton<IMessageSender>(new ServiceBusMessageSender(broker.Client))
            .BuildServiceProvider();
        var row = OutboxMessage.For("outbox-test", new Ping("outbox-1"));
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            db.OutboxMessages.Add(row);
            await db.SaveChangesAsync(Cancel);
        }

        var published = await OutboxDelivery.PublishPendingAsync<OrderingDbContext>(services.GetRequiredService<IServiceScopeFactory>(), Cancel);

        await using var receiver = broker.Client.CreateReceiver("outbox-test");
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), Cancel);
        Assert.NotNull(message);
        await receiver.CompleteMessageAsync(message, Cancel);
        Assert.True(published >= 1);
        Assert.Equal((row.Id.ToString(), nameof(Ping), "application/json"), (message.MessageId, message.Subject, message.ContentType));
        Assert.Equal(new Ping("outbox-1"), message.Body.ToObjectFromJson<Ping>(JsonSerializerOptions.Web));
        await using var check = services.CreateAsyncScope();
        Assert.NotNull((await check.ServiceProvider.GetRequiredService<OrderingDbContext>().OutboxMessages.SingleAsync(m => m.Id == row.Id, Cancel)).SentAt);
    }

    [Fact]
    public async Task A_handled_message_is_completed_and_leaves_the_queue()
    {
        var handled = new Recorder();
        await using var consumer = await StartConsumer("consumer-ok", handled, fail: false);

        await Send("consumer-ok", BinaryData.FromObjectAsJson(new Ping("ok-1"), JsonSerializerOptions.Web));

        await handled.WaitFor(1);
        await using var receiver = broker.Client.CreateReceiver("consumer-ok");
        await WaitUntil(async () => await receiver.PeekMessageAsync(cancellationToken: Cancel) is null);
        Assert.Equal(["ok-1"], handled.Ids);
    }

    [Fact]
    public async Task A_failing_handler_is_retried_until_the_queue_dead_letters_the_message()
    {
        var attempts = new Recorder();
        await using var consumer = await StartConsumer("consumer-fails", attempts, fail: true);

        await Send("consumer-fails", BinaryData.FromObjectAsJson(new Ping("fails-1"), JsonSerializerOptions.Web));

        var dead = await DeadLetter("consumer-fails");
        Assert.Equal("MaxDeliveryCountExceeded", dead.DeadLetterReason);
        Assert.Equal(["fails-1", "fails-1", "fails-1"], attempts.Ids);
    }

    [Fact]
    public async Task An_unreadable_message_is_dead_lettered_at_once_without_reaching_the_handler()
    {
        var handled = new Recorder();
        await using var consumer = await StartConsumer("consumer-unreadable", handled, fail: false);

        await Send("consumer-unreadable", BinaryData.FromString("{"));

        var dead = await DeadLetter("consumer-unreadable");
        Assert.Equal("Unreadable", dead.DeadLetterReason);
        Assert.Equal(1, dead.DeliveryCount);
        Assert.Empty(handled.Ids);
    }

    private async Task<RunningConsumer> StartConsumer(string queue, Recorder recorder, bool fail)
    {
        var services = new ServiceCollection()
            .AddSingleton(broker.Client)
            .AddSingleton(recorder)
            .AddScoped<IMessageHandler<Ping>>(sp => new RecordingHandler(sp.GetRequiredService<Recorder>(), fail))
            .BuildServiceProvider();
        var consumer = new QueueConsumer<Ping>(services, services.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger<QueueConsumer<Ping>>.Instance);
        await consumer.StartAsync(Cancel);
        return new RunningConsumer(consumer);
    }

    private async Task Send(string queue, BinaryData body)
    {
        await using var sender = broker.Client.CreateSender(queue);
        await sender.SendMessageAsync(new ServiceBusMessage(body) { MessageId = Guid.NewGuid().ToString() }, Cancel);
    }

    private async Task<ServiceBusReceivedMessage> DeadLetter(string queue)
    {
        await using var receiver = broker.Client.CreateReceiver(queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(60), Cancel);
        Assert.NotNull(message);
        await receiver.CompleteMessageAsync(message, Cancel);
        return message;
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 60 && !await condition(); i++)
        {
            await Task.Delay(500, Cancel);
        }

        Assert.True(await condition());
    }

    public sealed class Recorder
    {
        private readonly ConcurrentQueue<string> _ids = new();

        public List<string> Ids => [.. _ids];

        public void Add(string id) => _ids.Enqueue(id);

        public async Task WaitFor(int count)
        {
            for (var i = 0; i < 120 && _ids.Count < count; i++)
            {
                await Task.Delay(500, Cancel);
            }

            Assert.True(_ids.Count >= count, $"Handled {_ids.Count} of {count}.");
        }
    }

    private sealed class RecordingHandler(Recorder recorder, bool fail) : IMessageHandler<Ping>
    {
        public Task HandleAsync(Ping message, CancellationToken cancellationToken)
        {
            recorder.Add(message.Id);
            return fail ? throw new InvalidOperationException("The handler fails on purpose.") : Task.CompletedTask;
        }
    }

    private sealed class RunningConsumer(QueueConsumer<Ping> consumer) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await consumer.StopAsync(CancellationToken.None);
            consumer.Dispose();
        }
    }
}
