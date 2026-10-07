using System.Net;
using System.Net.Http.Headers;
using Athlos.Enrollment.Api.Features.MyPrograms;
using Athlos.Tenancy;
using Athlos.Testing;
using Npgsql;
using static Athlos.Testing.EnrollmentArrange;
using static Athlos.Testing.OrderingArrange;

namespace Athlos.IsolationTests;

// Design §1 and §3.3 for enrollment-api: an enrollment belongs to the coach's tenant AND to the
// customer, like the order it came from. A customer sees and changes only their own enrollments and
// progress; another customer's program is 404. In PostgreSQL alone, a customer can read their rows
// but never write outside a tenant, and a progress row cannot point at someone else's enrollment.
public sealed class EnrollmentIsolationTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly EnrollmentApiFactory _api = new(databases);
    private readonly Coach _a = NewCoach();
    private readonly Coach _b = NewCoach();
    private readonly Guid _alice = Guid.CreateVersion7();
    private readonly Guid _bob = Guid.CreateVersion7();
    private ProgramContentResponse _alicesProgram = null!;
    private ProgramContentResponse _bobsProgram = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _alicesProgram = await _api.Enroll(_alice, _a);
        await _api.Complete(_alice, _alicesProgram.ProgramId, _alicesProgram.Workouts[0].Id);
        _bobsProgram = await _api.Enroll(_bob, _b);
    }

    public static TheoryData<string, string> AllEndpoints => new()
    {
        { "GET", "/api/v1/me/programs" },
        { "GET", $"/api/v1/me/programs/{Guid.Empty}" },
        { "PUT", $"/api/v1/me/programs/{Guid.Empty}/workouts/{Guid.Empty}/completion" },
        { "DELETE", $"/api/v1/me/programs/{Guid.Empty}/workouts/{Guid.Empty}/completion" },
    };

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task Unsigned_requests_are_rejected(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TrustedHeaders.TenantId, _a.TenantId.ToString());
        request.Headers.Add(TrustedHeaders.UserRole, "Owner");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(_alice));

        var response = await _api.CreateClient().SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task Anonymous_requests_are_rejected(string method, string path)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Called by tenancy-api only, so it needs the gateway signature like every route.
    [Fact]
    public async Task Unsigned_erase_requests_are_rejected()
    {
        var response = await _api.CreateClient().DeleteAsync($"/internal/tenants/{_a.TenantId}", Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Deleting a workspace (design §3.4): one tenant id erases that coach's enrollments and progress,
    // and nothing of another coach's. Counted as the superuser, so RLS cannot hide a leftover.
    [Fact]
    public async Task Erasing_A_removes_its_enrollments_and_progress_and_nothing_of_Bs()
    {
        var erased = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/tenants/{_a.TenantId}"));
        var again = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/tenants/{_a.TenantId}"));

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (erased.StatusCode, again.StatusCode));
        Assert.Equal((0L, 0L), (await RowsOf("enrollments", _a.TenantId), await RowsOf("workout_progress", _a.TenantId)));
        Assert.Equal(1L, await RowsOf("enrollments", _b.TenantId));
        Assert.Empty((await _api.MyPrograms(_alice)).Items);
        Assert.Single((await _api.MyPrograms(_bob)).Items);
    }

    private async Task<long> RowsOf(string table, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("enrollment"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE tenant_id = @tenant", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }

    [Fact]
    public async Task A_customer_lists_only_their_own_programs()
    {
        var bobs = await _api.MyPrograms(_bob);

        Assert.Equal(_bobsProgram.ProgramId, Assert.Single(bobs.Items).ProgramId);
    }

    [Fact]
    public async Task Bob_gets_404_for_Alices_program_and_cannot_touch_her_progress()
    {
        var page = await _api.AsCustomer(_bob, HttpMethod.Get, $"/programs/{_alicesProgram.ProgramId}");
        var complete = await _api.Complete(_bob, _alicesProgram.ProgramId, _alicesProgram.Workouts[1].Id);
        var undo = await _api.AsCustomer(_bob, HttpMethod.Delete, $"/programs/{_alicesProgram.ProgramId}/workouts/{_alicesProgram.Workouts[0].Id}/completion");

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound), (page.StatusCode, complete.StatusCode, undo.StatusCode));
        var alices = await _api.MyProgram(_alice, _alicesProgram.ProgramId);
        Assert.NotNull(alices.Workouts[0].CompletedAt);
        Assert.Null(alices.Workouts[1].CompletedAt);
    }

    // Layer 4 alone: raw SQL as enrollment-api's role.
    [Fact]
    public async Task RLS_tenant_B_sees_none_of_As_enrollments_or_progress()
    {
        var enrollments = await Scalar<long>(_b.TenantId, null, $"SELECT count(*) FROM enrollments WHERE tenant_id = '{_a.TenantId}'");
        var progress = await Scalar<long>(_b.TenantId, null, $"SELECT count(*) FROM workout_progress WHERE tenant_id = '{_a.TenantId}'");

        Assert.Equal((0L, 0L), (enrollments, progress));
    }

    [Fact]
    public async Task RLS_a_customer_reads_their_rows_across_tenants_but_cannot_write_without_the_tenant()
    {
        var own = await Scalar<long>(null, _alice, $"SELECT count(*) FROM enrollments WHERE customer_id = '{_alice}'");
        var others = await Scalar<long>(null, _alice, $"SELECT count(*) FROM enrollments WHERE customer_id = '{_bob}'");
        var progress = await Scalar<long>(null, _alice, $"SELECT count(*) FROM workout_progress WHERE customer_id = '{_alice}'");
        var updated = await Execute(null, _alice, $"UPDATE enrollments SET program_title = 'x' WHERE customer_id = '{_alice}'");
        var deleted = await Execute(null, _alice, $"DELETE FROM workout_progress WHERE customer_id = '{_alice}'");

        Assert.Equal((1L, 0L, 1L), (own, others, progress));
        Assert.Equal((0, 0), (updated, deleted));
    }

    [Fact]
    public async Task RLS_a_customer_cannot_insert_progress_without_entering_the_tenant()
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(null, _alice, InsertProgress(_a.TenantId, _alice)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    // Even inside tenant A, a progress row must match its enrollment's customer (composite foreign key).
    [Fact]
    public async Task A_progress_row_cannot_point_at_another_customers_enrollment()
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(_a.TenantId, _bob, InsertProgress(_a.TenantId, _bob)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    // Alice's enrollment in tenant A, with the given tenant and customer on the progress row.
    private string InsertProgress(Guid tenantId, Guid customerId) => $"""
        INSERT INTO workout_progress (id, tenant_id, enrollment_id, customer_id, workout_id, completed_at, created_at, updated_at)
        SELECT '{Guid.CreateVersion7()}', '{tenantId}', id, '{customerId}', '{Guid.CreateVersion7()}', now(), now(), now()
        FROM enrollments WHERE customer_id = '{_alice}' AND program_id = '{_alicesProgram.ProgramId}'
        """;

    private async Task<T> Scalar<T>(Guid? tenantId, Guid? userId, string sql)
    {
        await using var connection = await Open(tenantId, userId);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Cancel))!;
    }

    private async Task<int> Execute(Guid? tenantId, Guid? userId, string sql)
    {
        await using var connection = await Open(tenantId, userId);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(Cancel);
    }

    // A connection inside a transaction with the given settings (never committed).
    private async Task<NpgsqlConnection> Open(Guid? tenantId, Guid? userId)
    {
        var connection = new NpgsqlConnection(databases.AppConnectionString("enrollment"));
        await connection.OpenAsync(Cancel);
        await using (var begin = new NpgsqlCommand(
            $"BEGIN; SELECT set_config('app.tenant_id', '{tenantId}', true), set_config('app.user_id', '{userId}', true)", connection))
        {
            await begin.ExecuteNonQueryAsync(Cancel);
        }

        return connection;
    }

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
