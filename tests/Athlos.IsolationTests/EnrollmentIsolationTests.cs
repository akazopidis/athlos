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
        { "PUT", $"/api/v1/me/programs/{Guid.Empty}/review" },
        { "DELETE", $"/api/v1/me/programs/{Guid.Empty}/review" },
        { "GET", "/api/v1/studio/any/reviews" },
        { "PUT", $"/api/v1/studio/any/reviews/{Guid.Empty}/reply" },
        { "DELETE", $"/api/v1/studio/any/reviews/{Guid.Empty}/reply" },
        { "POST", $"/api/v1/marketplace/programs/{Guid.Empty}/reviews/{Guid.Empty}/report" },
        { "GET", "/api/v1/admin/reviews/reports" },
        { "POST", $"/api/v1/admin/reviews/{Guid.Empty}/hide" },
        { "POST", $"/api/v1/admin/reviews/{Guid.Empty}/keep" },
    };

    // A workspace export (design §5.4) holds that coach's programs and reviews, and none of another's;
    // like every internal endpoint it needs the gateway signature.
    [Fact]
    public async Task As_workspace_export_holds_As_programs_only()
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{_a.TenantId}/export"));
        var export = await response.Content.ReadAsStringAsync(Cancel);
        var unsigned = await _api.CreateClient().GetAsync($"/internal/tenants/{_a.TenantId}/export", Cancel);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Unauthorized), (response.StatusCode, unsigned.StatusCode));
        Assert.Contains(_alicesProgram.ProgramId.ToString(), export, StringComparison.Ordinal);
        Assert.DoesNotContain(_bobsProgram.ProgramId.ToString(), export, StringComparison.Ordinal);
    }

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

    // Deleting Alice's account (design §5.2): her enrollments and progress go with every coach; Bob's
    // stay. Counted as the superuser, so RLS cannot hide a leftover.
    [Fact]
    public async Task Erasing_Alice_removes_her_enrollments_with_every_coach_and_nothing_of_Bobs()
    {
        await _api.Enroll(_alice, _b);

        var erased = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{_alice}"));
        var again = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{_alice}"));

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (erased.StatusCode, again.StatusCode));
        Assert.Equal((0L, 0L), (await RowsOfCustomer("enrollments", _alice), await RowsOfCustomer("workout_progress", _alice)));
        Assert.Equal(1L, await RowsOfCustomer("enrollments", _bob));
    }

    [Fact]
    public async Task Unsigned_customer_erase_requests_are_rejected()
    {
        var response = await _api.CreateClient().DeleteAsync($"/internal/customers/{_alice}", Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<long> RowsOfCustomer(string table, Guid customerId)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("enrollment"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE customer_id = @customer", connection);
        command.Parameters.AddWithValue("customer", customerId);
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

    // A refund names its tenant; naming another tenant removes nothing, because the delete runs inside
    // the tenant it names, where the other tenant's enrollment does not exist.
    [Fact]
    public async Task An_OrderRefunded_naming_the_wrong_tenant_removes_nothing()
    {
        var program = await _api.Enroll(_alice, _a);
        var enrollment = await Scalar<Guid>(null, _alice, $"SELECT order_id FROM enrollments WHERE customer_id = '{_alice}' AND program_id = '{program.ProgramId}'");

        await _api.Deliver(new Athlos.Messaging.Contracts.OrderRefunded(enrollment, _b.TenantId, _alice, program.ProgramId, DateTimeOffset.UtcNow));

        Assert.Contains((await _api.MyPrograms(_alice)).Items, p => p.ProgramId == program.ProgramId);
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

    // A data export (design §5.4) holds the customer's own programs and reviews, and nobody else's.
    [Fact]
    public async Task Alices_export_holds_her_programs_only()
    {
        await Review(_bob, _bobsProgram.ProgramId, 2);

        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/customers/{_alice}/export"));
        var export = await response.Content.ReadAsStringAsync(Cancel);
        var unsigned = await _api.CreateClient().GetAsync($"/internal/customers/{_alice}/export", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_alicesProgram.ProgramId.ToString(), export, StringComparison.Ordinal);
        Assert.Contains(_alicesProgram.Workouts[0].Id.ToString(), export, StringComparison.Ordinal);
        Assert.DoesNotContain(_bobsProgram.ProgramId.ToString(), export, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    // Reviews (design §2.4): only the buyer reviews their program; another customer's attempt is 404.
    [Fact]
    public async Task Bob_cannot_review_or_remove_Alices_review()
    {
        await Review(_alice, _alicesProgram.ProgramId, 5);

        var written = await Review(_bob, _alicesProgram.ProgramId, 1);
        var deleted = await _api.AsCustomer(_bob, HttpMethod.Delete, $"/programs/{_alicesProgram.ProgramId}/review");
        var reviews = await Scalar<long>(_a.TenantId, null, $"SELECT count(*) FROM reviews WHERE program_id = '{_alicesProgram.ProgramId}'");

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (written.StatusCode, deleted.StatusCode));
        Assert.Equal(1L, reviews);
    }

    // Coach B sees none of A's reviews and cannot reply to one, even naming A's address in the route.
    [Fact]
    public async Task Coach_B_cannot_list_or_reply_to_As_reviews()
    {
        await Review(_alice, _alicesProgram.ProgramId, 5);
        var review = await Scalar<Guid>(_a.TenantId, null, $"SELECT id FROM reviews WHERE program_id = '{_alicesProgram.ProgramId}'");

        var listed = await (await AsCoach(_b, _b.Slug, HttpMethod.Get, "/reviews")).Content.ReadAsStringAsync(Cancel);
        var listedUnderA = await (await AsCoach(_b, _a.Slug, HttpMethod.Get, "/reviews")).Content.ReadAsStringAsync(Cancel);
        var replied = await AsCoach(_b, _b.Slug, HttpMethod.Put, $"/reviews/{review}/reply", new { text = "Not yours" });
        var removed = await AsCoach(_b, _a.Slug, HttpMethod.Delete, $"/reviews/{review}/reply");

        Assert.DoesNotContain(review.ToString(), listed, StringComparison.Ordinal);
        Assert.DoesNotContain(review.ToString(), listedUnderA, StringComparison.Ordinal);
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (replied.StatusCode, removed.StatusCode));
        Assert.Null(await Scalar<object>(_a.TenantId, null, $"SELECT reply FROM reviews WHERE id = '{review}'") as string);
    }

    // "A program got a new workout" (design §5.3) enters the tenant it names: naming B for A's program
    // emails none of A's customers, and like every internal endpoint it needs the signature.
    [Fact]
    public async Task A_program_update_under_Bs_tenant_emails_none_of_As_customers()
    {
        var path = $"/internal/tenants/{_b.TenantId}/programs/{_alicesProgram.ProgramId}/updated";
        var wrong = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, path) { Content = System.Net.Http.Json.JsonContent.Create(new { workoutTitle = "Planted" }) });
        var unsigned = await _api.CreateClient().PostAsync(path, System.Net.Http.Json.JsonContent.Create(new { workoutTitle = "Planted" }), Cancel);
        var stamped = await Scalar<long>(_a.TenantId, null, $"SELECT count(*) FROM enrollments WHERE program_id = '{_alicesProgram.ProgramId}' AND update_emailed_at IS NOT NULL");

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.Unauthorized), (wrong.StatusCode, unsigned.StatusCode));
        Assert.Equal(0L, stamped);
    }

    // Moderation (design §2.4): a report must name the review's own program (another program's id finds
    // nothing), and only the platform admin hides; a coach or customer gets 403 whatever the review.
    [Fact]
    public async Task Reports_name_the_reviews_own_program_and_only_the_admin_hides()
    {
        await Review(_alice, _alicesProgram.ProgramId, 1);
        var review = await Scalar<Guid>(_a.TenantId, null, $"SELECT id FROM reviews WHERE program_id = '{_alicesProgram.ProgramId}'");

        var wrongProgram = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/marketplace/programs/{_bobsProgram.ProgramId}/reviews/{review}/report")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { reason = "Spam" }),
        }, _bob);
        var coachHides = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/reviews/{review}/hide"), _b.OwnerId, _b.TenantId, TenantRole.Owner);
        var hidden = await Scalar<object>(_a.TenantId, null, $"SELECT hidden_at FROM reviews WHERE id = '{review}'");

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.Forbidden), (wrongProgram.StatusCode, coachHides.StatusCode));
        Assert.True(hidden is DBNull or null);
    }

    // Layer 4 alone: the public-read scope reads every review, never enrollments or progress, and cannot change a review.
    [Fact]
    public async Task RLS_public_read_shows_reviews_only_and_cannot_change_them()
    {
        await Review(_alice, _alicesProgram.ProgramId, 5);

        var reviews = await Scalar<long>(_b.TenantId, null, $"SELECT count(*) FROM reviews WHERE tenant_id = '{_a.TenantId}'", "public-read");
        var enrollments = await Scalar<long>(_b.TenantId, null, $"SELECT count(*) FROM enrollments WHERE tenant_id = '{_a.TenantId}'", "public-read");
        var progress = await Scalar<long>(_b.TenantId, null, $"SELECT count(*) FROM workout_progress WHERE tenant_id = '{_a.TenantId}'", "public-read");
        var updated = await Execute(_b.TenantId, null, $"UPDATE reviews SET rating = 1 WHERE tenant_id = '{_a.TenantId}'", "public-read");
        var deleted = await Execute(_b.TenantId, null, $"DELETE FROM reviews WHERE tenant_id = '{_a.TenantId}'", "public-read");
        var outsideTheScope = await Scalar<long>(_b.TenantId, null, $"SELECT count(*) FROM reviews WHERE tenant_id = '{_a.TenantId}'");

        Assert.Equal((1L, 0L, 0L), (reviews, enrollments, progress));
        Assert.Equal((0, 0, 0L), (updated, deleted, outsideTheScope));
    }

    private Task<HttpResponseMessage> Review(Guid customerId, Guid programId, int rating) =>
        _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/me/programs/{programId}/review") { Content = System.Net.Http.Json.JsonContent.Create(new { rating }) },
            customerId);

    private Task<HttpResponseMessage> AsCoach(Coach coach, string slug, HttpMethod method, string path, object? body = null) =>
        _api.SendAsGateway(
            new HttpRequestMessage(method, $"/api/v1/studio/{slug}{path}") { Content = body is null ? null : System.Net.Http.Json.JsonContent.Create(body) },
            coach.OwnerId, coach.TenantId, TenantRole.Owner);

    // Alice's enrollment in tenant A, with the given tenant and customer on the progress row.
    private string InsertProgress(Guid tenantId, Guid customerId) => $"""
        INSERT INTO workout_progress (id, tenant_id, enrollment_id, customer_id, workout_id, completed_at, created_at, updated_at)
        SELECT '{Guid.CreateVersion7()}', '{tenantId}', id, '{customerId}', '{Guid.CreateVersion7()}', now(), now(), now()
        FROM enrollments WHERE customer_id = '{_alice}' AND program_id = '{_alicesProgram.ProgramId}'
        """;

    private async Task<T> Scalar<T>(Guid? tenantId, Guid? userId, string sql, string scope = "")
    {
        await using var connection = await Open(tenantId, userId, scope);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Cancel))!;
    }

    private async Task<int> Execute(Guid? tenantId, Guid? userId, string sql, string scope = "")
    {
        await using var connection = await Open(tenantId, userId, scope);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(Cancel);
    }

    // A connection inside a transaction with the given settings (never committed).
    private async Task<NpgsqlConnection> Open(Guid? tenantId, Guid? userId, string scope = "")
    {
        var connection = new NpgsqlConnection(databases.AppConnectionString("enrollment"));
        await connection.OpenAsync(Cancel);
        await using (var begin = new NpgsqlCommand(
            $"BEGIN; SELECT set_config('app.tenant_id', '{tenantId}', true), set_config('app.user_id', '{userId}', true), set_config('app.scope', '{scope}', true)", connection))
        {
            await begin.ExecuteNonQueryAsync(Cancel);
        }

        return connection;
    }

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
