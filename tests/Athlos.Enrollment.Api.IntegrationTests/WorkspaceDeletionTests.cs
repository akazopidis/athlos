using System.Net;
using Athlos.Testing;
using Npgsql;
using static Athlos.Testing.EnrollmentArrange;
using static Athlos.Testing.OrderingArrange;

namespace Athlos.Enrollment.Api.IntegrationTests;

// Buyers keep what they paid for when a coach deletes their workspace (design §3.4): enrollment-api
// copies the program before catalog-api erases it, and the customer goes on reading it and ticking
// off workouts. The copy goes once nobody owns the program any more.
public sealed class WorkspaceDeletionTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly EnrollmentApiFactory _api = new(databases);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_buyer_keeps_reading_and_completing_a_deleted_coachs_program()
    {
        var coach = NewCoach();
        var customer = Guid.CreateVersion7();
        var program = await _api.Enroll(customer, coach, workouts: 3);
        await _api.Complete(customer, program.ProgramId, program.Workouts[0].Id);

        await DeleteWorkspace(coach);
        var page = await _api.MyProgram(customer, program.ProgramId);
        var completed = await _api.Complete(customer, program.ProgramId, program.Workouts[1].Id);
        var unknownWorkout = await _api.Complete(customer, program.ProgramId, Guid.CreateVersion7());

        Assert.True(page.CoachLeft);
        Assert.Equal((program.Title, 3), (page.Title, page.Workouts.Count));
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (completed.StatusCode, unknownWorkout.StatusCode));
        Assert.Single((await _api.MyPrograms(customer)).Items);
    }

    // The copy serves every buyer, and goes with the last one's account.
    [Fact]
    public async Task The_copy_stays_while_someone_owns_the_program()
    {
        var coach = NewCoach();
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var program = await _api.Enroll(first, coach);
        await _api.Deliver(Paid(second, coach.TenantId, coach.Slug, program));
        await DeleteWorkspace(coach);

        await Erase(first);
        var whileOwned = await Copies(coach.TenantId);
        await Erase(second);

        Assert.Equal((1L, 0L), (whileOwned, await Copies(coach.TenantId)));
    }

    // catalog-api's part of the deletion, after enrollment-api's: the program is gone from catalog.
    private async Task DeleteWorkspace(Coach coach)
    {
        var copied = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/tenants/{coach.TenantId}"));
        Assert.Equal(HttpStatusCode.NoContent, copied.StatusCode);
        foreach (var programId in await ProgramsOf(coach.TenantId))
        {
            _api.Programs.Remove(coach.TenantId, programId);
        }
    }

    private async Task Erase(Guid customer) =>
        Assert.Equal(HttpStatusCode.NoContent, (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{customer}"))).StatusCode);

    private Task<long> Copies(Guid tenantId) => Owner<long>("SELECT count(*) FROM program_copies WHERE tenant_id = @tenant", tenantId);

    private async Task<List<Guid>> ProgramsOf(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("enrollment"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand("SELECT program_id FROM program_copies WHERE tenant_id = @tenant", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        List<Guid> programs = [];
        while (await reader.ReadAsync(Cancel))
        {
            programs.Add(reader.GetGuid(0));
        }

        return programs;
    }

    private async Task<T> Owner<T>(string sql, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("enrollment"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        return (T)(await command.ExecuteScalarAsync(Cancel))!;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
