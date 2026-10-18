using System.Net;
using System.Text.Json;
using Athlos.Messaging.Contracts;
using Athlos.Notifications.Worker;
using Athlos.Testing;
using static Athlos.Testing.EnrollmentArrange;
using static Athlos.Testing.OrderingArrange;

namespace Athlos.Enrollment.Api.IntegrationTests;

public sealed class EnrollmentTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly EnrollmentApiFactory _api = new(databases);
    private readonly Coach _coach = NewCoach();
    private readonly Guid _customer = Guid.CreateVersion7();

    [Fact]
    public async Task OrderPaid_enrolls_the_customer_once_even_when_delivered_again()
    {
        var program = _api.Programs.Add(_coach.TenantId, _coach.Slug);
        var paid = Paid(_customer, _coach.TenantId, _coach.Slug, program);

        var first = await _api.Deliver(paid);
        var again = await _api.Deliver(paid);

        Assert.Equal(("completed", "completed"), (first, again));
        var enrolled = Assert.Single((await _api.MyPrograms(_customer)).Items);
        Assert.Equal((program.ProgramId, program.Title, _coach.Slug, 0), (enrolled.ProgramId, enrolled.ProgramTitle, enrolled.CoachSlug, enrolled.CompletedWorkouts));
    }

    // Design §5.3: the purchase confirmation is ordering-api's, one per checkout; enrollment sends none.
    [Fact]
    public async Task Enrolling_queues_no_email()
    {
        var program = _api.Programs.Add(_coach.TenantId, _coach.Slug);
        await _api.Deliver(Paid(_customer, _coach.TenantId, _coach.Slug, program));

        await _api.PublishOutbox();

        Assert.DoesNotContain(_api.Messages.Sent, m => m.Queue == EmailNotification.Queue && m.Message.Payload.Contains(program.ProgramId.ToString(), StringComparison.Ordinal));
        Assert.Single((await _api.MyPrograms(_customer)).Items, p => p.ProgramId == program.ProgramId);
    }

    // A refund (design §2.2) removes the access that order gave, with its progress; delivered again,
    // nothing more happens.
    [Fact]
    public async Task OrderRefunded_removes_the_access_the_order_gave()
    {
        var program = _api.Programs.Add(_coach.TenantId, _coach.Slug);
        var paid = Paid(_customer, _coach.TenantId, _coach.Slug, program);
        await _api.Deliver(paid);
        await _api.Complete(_customer, program.ProgramId, program.Workouts[0].Id);
        var refunded = new OrderRefunded(paid.OrderId, _coach.TenantId, _customer, program.ProgramId, DateTimeOffset.UtcNow);

        var first = await _api.Deliver(refunded);
        var again = await _api.Deliver(refunded);

        Assert.Equal(("completed", "completed"), (first, again));
        Assert.Empty((await _api.MyPrograms(_customer)).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.AsCustomer(_customer, HttpMethod.Get, $"/api/v1/me/programs/{program.ProgramId}")).StatusCode);
    }

    // Only the refunded order's enrollment goes: a refund of another order changes nothing.
    [Fact]
    public async Task A_refund_of_another_order_keeps_the_access()
    {
        var program = await _api.Enroll(_customer, _coach);

        await _api.Deliver(new OrderRefunded(Guid.CreateVersion7(), _coach.TenantId, _customer, program.ProgramId, DateTimeOffset.UtcNow));

        Assert.Single((await _api.MyPrograms(_customer)).Items);
    }

    [Fact]
    public async Task A_second_paid_order_for_the_same_program_keeps_one_enrollment()
    {
        var program = await _api.Enroll(_customer, _coach);

        var second = await _api.Deliver(Paid(_customer, _coach.TenantId, _coach.Slug, program));

        Assert.Equal("completed", second);
        Assert.Single((await _api.MyPrograms(_customer)).Items);
    }

    [Fact]
    public async Task An_unreadable_message_is_dead_lettered_and_a_failing_one_is_retried()
    {
        var unreadable = await _api.DeliverRaw(BinaryData.FromString("not json"));
        // Longer than the column: the insert fails, so the handler throws.
        var failing = await _api.Deliver(Paid(_customer, _coach.TenantId, new string('x', 41), _api.Programs.Add(_coach.TenantId, _coach.Slug)));

        Assert.Equal(("dead-lettered", "abandoned"), (unreadable, failing));
        Assert.Empty((await _api.MyPrograms(_customer)).Items);
    }

    [Fact]
    public async Task The_program_page_shows_the_workouts_and_which_are_done()
    {
        var program = await _api.Enroll(_customer, _coach, workouts: 3);
        var first = program.Workouts[0].Id;

        Assert.Equal(HttpStatusCode.NoContent, (await _api.Complete(_customer, program.ProgramId, first)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _api.Complete(_customer, program.ProgramId, first)).StatusCode);
        var page = await _api.MyProgram(_customer, program.ProgramId);

        Assert.Equal(program.Title, page.Title);
        Assert.Equal(3, page.Workouts.Count);
        Assert.NotNull(page.Workouts.Single(w => w.Id == first).CompletedAt);
        Assert.Equal(2, page.Workouts.Count(w => w.CompletedAt is null));
        Assert.Equal(1, Assert.Single((await _api.MyPrograms(_customer)).Items).CompletedWorkouts);
    }

    [Fact]
    public async Task Undoing_a_completion_clears_it()
    {
        var program = await _api.Enroll(_customer, _coach);
        var workout = program.Workouts[1].Id;
        await _api.Complete(_customer, program.ProgramId, workout);

        var undo = await _api.AsCustomer(_customer, HttpMethod.Delete, $"/programs/{program.ProgramId}/workouts/{workout}/completion");

        Assert.Equal(HttpStatusCode.NoContent, undo.StatusCode);
        Assert.All((await _api.MyProgram(_customer, program.ProgramId)).Workouts, w => Assert.Null(w.CompletedAt));
    }

    [Fact]
    public async Task Only_workouts_of_the_program_can_be_completed()
    {
        var program = await _api.Enroll(_customer, _coach);
        var other = _api.Programs.Add(_coach.TenantId, _coach.Slug);

        var response = await _api.Complete(_customer, program.ProgramId, other.Workouts[0].Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_program_the_customer_did_not_buy_is_not_found()
    {
        var notBought = _api.Programs.Add(_coach.TenantId, _coach.Slug);

        var page = await _api.AsCustomer(_customer, HttpMethod.Get, $"/programs/{notBought.ProgramId}");
        var complete = await _api.Complete(_customer, notBought.ProgramId, notBought.Workouts[0].Id);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (page.StatusCode, complete.StatusCode));
    }

    [Fact]
    public async Task A_program_the_coach_deleted_is_not_found_but_stays_in_the_list()
    {
        var program = await _api.Enroll(_customer, _coach);
        _api.Programs.Remove(_coach.TenantId, program.ProgramId);

        var page = await _api.AsCustomer(_customer, HttpMethod.Get, $"/programs/{program.ProgramId}");

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Single((await _api.MyPrograms(_customer)).Items);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
