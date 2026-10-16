using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Athlos.Enrollment.Api.Data;
using Athlos.Enrollment.Api.Features.MyPrograms;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Athlos.Testing;

// enrollment-api against its test database. Messages are delivered by calling the consumer directly;
// catalog-api's content is a fake.
public sealed class EnrollmentApiFactory(AthlosDatabases databases) : ServiceApiFactory<EnrollmentDbContext>(databases, "enrollment")
{
    public FakeProgramContent Programs { get; } = new();

    public CapturingMessageSender Messages { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Never connected: the consumer's background loop is removed below. Aspire needs a well-formed value.
        builder.UseSetting("ConnectionStrings:messaging", "Endpoint=sb://localhost;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==");
        builder.UseSetting("App:PublicUrl", TenancyApiFactory.PublicUrl);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.AddSingleton<IProgramContent>(Programs);
            services.AddSingleton<IMessageSender>(Messages);
        });
    }

    // One pass of the outbox publisher (the background loop is removed above).
    public Task<int> PublishOutbox() =>
        OutboxDelivery.PublishPendingAsync<EnrollmentDbContext>(Services.GetRequiredService<IServiceScopeFactory>(), TestContext.Current.CancellationToken);

    // One delivery of an OrderPaid, as Service Bus would make it; returns how it was settled.
    public async Task<string> Deliver(OrderPaid message) => await DeliverRaw(BinaryData.FromObjectAsJson(message, JsonSerializerOptions.Web));

    // One delivery of an OrderRefunded (design §2.2).
    public async Task<string> Deliver(OrderRefunded message)
    {
        var settlement = new RecordingSettlement();
        await Services.GetRequiredService<QueueConsumer<OrderRefunded>>()
            .ProcessAsync(BinaryData.FromObjectAsJson(message, JsonSerializerOptions.Web), Guid.NewGuid().ToString(), deliveryCount: 1, settlement,
                TestContext.Current.CancellationToken);
        return settlement.Outcome ?? "none";
    }

    public async Task<string> DeliverRaw(BinaryData body)
    {
        var settlement = new RecordingSettlement();
        await Services.GetRequiredService<QueueConsumer<OrderPaid>>()
            .ProcessAsync(body, Guid.NewGuid().ToString(), deliveryCount: 1, settlement, TestContext.Current.CancellationToken);
        return settlement.Outcome ?? "none";
    }

    private sealed class RecordingSettlement : IMessageSettlement
    {
        public string? Outcome { get; private set; }

        public Task CompleteAsync(CancellationToken cancellationToken) => Set("completed");

        public Task AbandonAsync(CancellationToken cancellationToken) => Set("abandoned");

        public Task DeadLetterAsync(string reason, string description, CancellationToken cancellationToken) => Set("dead-lettered");

        private Task Set(string outcome)
        {
            Outcome = outcome;
            return Task.CompletedTask;
        }
    }
}

// catalog-api's internal content endpoint, as enrollment sees it: content per (tenant, program).
public sealed class FakeProgramContent : IProgramContent
{
    private readonly ConcurrentDictionary<(Guid TenantId, Guid ProgramId), ProgramContentResponse> _programs = new();

    public ProgramContentResponse Add(Guid tenantId, string coachSlug, int workouts = 3)
    {
        var content = new ProgramContentResponse(Guid.CreateVersion7(), coachSlug, $"Program {Guid.NewGuid():N}"[..20], "Strength basics.", "Powerlifting", 4, null,
            [.. Enumerable.Range(1, workouts).Select(day => new ProgramContentWorkout(Guid.CreateVersion7(), 1, day, $"Day {day}", "5x5", null))]);
        _programs[(tenantId, content.ProgramId)] = content;
        return content;
    }

    public void Remove(Guid tenantId, Guid programId) => _programs.TryRemove((tenantId, programId), out _);

    public Task<ProgramContentResponse?> GetAsync(Guid tenantId, Guid programId, CancellationToken cancellationToken) =>
        Task.FromResult(_programs.GetValueOrDefault((tenantId, programId)));
}

public static class EnrollmentArrange
{
    public static readonly JsonSerializerOptions Json = OrderingArrange.Json;

    public sealed record MyProgramItem(Guid ProgramId, string ProgramTitle, string CoachSlug, DateTimeOffset EnrolledAt, int CompletedWorkouts);

    public sealed record MyProgramsPage(List<MyProgramItem> Items, int Page, int PageSize, int TotalCount);

    public sealed record Workout(Guid Id, int Week, int Day, string Title, DateTimeOffset? CompletedAt);

    public sealed record ProgramPage(Guid ProgramId, string Title, List<Workout> Workouts);

    public static OrderPaid Paid(Guid customerId, Guid tenantId, string coachSlug, ProgramContentResponse program, Guid? orderId = null) =>
        new(orderId ?? Guid.CreateVersion7(), tenantId, customerId, $"{customerId}@test.local", program.ProgramId, program.Title, coachSlug, 4900, "EUR", DateTimeOffset.UtcNow);

    // A program in catalog (fake) and the customer's paid order for it, delivered.
    public static async Task<ProgramContentResponse> Enroll(this EnrollmentApiFactory api, Guid customerId, OrderingArrange.Coach coach, int workouts = 3)
    {
        var program = api.Programs.Add(coach.TenantId, coach.Slug, workouts);
        Assert.Equal("completed", await api.Deliver(Paid(customerId, coach.TenantId, coach.Slug, program)));
        return program;
    }

    public static Task<HttpResponseMessage> AsCustomer(this EnrollmentApiFactory api, Guid customerId, HttpMethod method, string path) =>
        api.SendAsGateway(new HttpRequestMessage(method, $"/api/v1/me{path}"), customerId);

    public static async Task<MyProgramsPage> MyPrograms(this EnrollmentApiFactory api, Guid customerId) =>
        await Read<MyProgramsPage>(await api.AsCustomer(customerId, HttpMethod.Get, "/programs"));

    public static async Task<ProgramPage> MyProgram(this EnrollmentApiFactory api, Guid customerId, Guid programId) =>
        await Read<ProgramPage>(await api.AsCustomer(customerId, HttpMethod.Get, $"/programs/{programId}"));

    public static Task<HttpResponseMessage> Complete(this EnrollmentApiFactory api, Guid customerId, Guid programId, Guid workoutId) =>
        api.AsCustomer(customerId, HttpMethod.Put, $"/programs/{programId}/workouts/{workoutId}/completion");

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Json, TestContext.Current.CancellationToken))!;
    }
}
