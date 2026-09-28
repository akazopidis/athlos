using System.Net;
using Athlos.Catalog.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Athlos.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Athlos.Testing.CatalogArrange;

namespace Athlos.IsolationTests;

// Design §3.3 for catalog-api: tenant B against tenant A's programs and workouts, through every
// studio endpoint (404) and directly in PostgreSQL (RLS alone). Also the marketplace's public-read
// scope: it shows other tenants' published programs and never their drafts or workouts.
public sealed class CatalogIsolationTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly CatalogApiFactory _api = new(databases);
    private readonly Coach _a = NewCoach();
    private readonly Coach _b = NewCoach();
    private CreatedProgram _aDraft = null!;
    private CreatedProgram _aPublished = null!;
    private Guid _aWorkout;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _aDraft = await _api.CreateProgram(_a);
        _aPublished = await _api.CreateProgram(_a, publish: true);
        var workouts = await (await _api.AsCoach(_a, HttpMethod.Get, $"/programs/{_aPublished.Id}/workouts")).Content.ReadAsStringAsync(Cancel);
        _aWorkout = Guid.Parse(System.Text.Json.JsonDocument.Parse(workouts).RootElement[0].GetProperty("id").GetString()!);
    }

    // Every studio endpoint, as tenant B, aimed at tenant A's program or workout.
    public static TheoryData<string, string, bool> StudioEndpoints => new()
    {
        { "GET", "/programs/{draft}", false },
        { "PUT", "/programs/{draft}", true },
        { "DELETE", "/programs/{draft}", false },
        { "POST", "/programs/{draft}/publish", false },
        { "POST", "/programs/{published}/unpublish", false },
        { "GET", "/programs/{published}/workouts", false },
        { "POST", "/programs/{published}/workouts", true },
        { "PUT", "/programs/{published}/workouts/{workout}", true },
        { "DELETE", "/programs/{published}/workouts/{workout}", false },
    };

    [Theory]
    [MemberData(nameof(StudioEndpoints))]
    public async Task B_gets_404_for_As_programs_and_workouts(string method, string path, bool withBody)
    {
        var body = withBody
            ? path.Contains("workouts", StringComparison.Ordinal)
                ? new { week = 1, day = 2, title = "Hijacked" }
                : (object)new { title = "Hijacked", level = "Advanced", durationWeeks = 1, priceAmount = 1, currency = "EUR" }
            : null;

        var response = await _api.AsCoach(_b, new HttpMethod(method), Resolve(path), body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Layer 1: every catalog-api endpoint refuses a request the gateway did not sign.
    public static TheoryData<string, string> AllEndpoints => new()
    {
        { "GET", "/api/v1/marketplace/programs" },
        { "GET", "/api/v1/marketplace/coaches/some-coach/programs/some-program" },
        { "GET", "/api/v1/studio/some-coach/programs" },
        { "POST", "/api/v1/studio/some-coach/programs" },
        { "GET", $"/api/v1/studio/some-coach/programs/{Guid.Empty}" },
        { "PUT", $"/api/v1/studio/some-coach/programs/{Guid.Empty}" },
        { "DELETE", $"/api/v1/studio/some-coach/programs/{Guid.Empty}" },
        { "POST", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/publish" },
        { "POST", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/unpublish" },
        { "GET", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/workouts" },
        { "POST", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/workouts" },
        { "PUT", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/workouts/{Guid.Empty}" },
        { "DELETE", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/workouts/{Guid.Empty}" },
    };

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task Unsigned_requests_are_rejected(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TrustedHeaders.TenantId, _a.TenantId.ToString());
        request.Headers.Add(TrustedHeaders.UserRole, "Owner");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestTokens.For(_a.OwnerId));

        var response = await _api.CreateClient().SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task As_program_is_unchanged_after_Bs_attempts()
    {
        await _api.AsCoach(_b, HttpMethod.Put, $"/programs/{_aDraft.Id}", new { title = "Hijacked", level = "Advanced", durationWeeks = 1, priceAmount = 1, currency = "EUR" });
        await _api.AsCoach(_b, HttpMethod.Delete, $"/programs/{_aDraft.Id}");

        var mine = await _api.AsCoach(_a, HttpMethod.Get, $"/programs/{_aDraft.Id}");
        var body = await mine.Content.ReadAsStringAsync(Cancel);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.DoesNotContain("Hijacked", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bs_program_list_does_not_include_As_programs()
    {
        var body = await (await _api.AsCoach(_b, HttpMethod.Get, "/programs")).Content.ReadAsStringAsync(Cancel);

        Assert.DoesNotContain(_aDraft.Id.ToString(), body, StringComparison.Ordinal);
        Assert.DoesNotContain(_aPublished.Id.ToString(), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_marketplace_shows_As_published_program_but_not_the_draft()
    {
        var body = await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs?coach={_a.Slug}")))
            .Content.ReadAsStringAsync(Cancel);

        Assert.Contains(_aPublished.Slug, body, StringComparison.Ordinal);
        Assert.DoesNotContain(_aDraft.Slug, body, StringComparison.Ordinal);
    }

    // Layer 4 alone: raw SQL as catalog's app role, acting as tenant B.
    [Fact]
    public async Task RLS_hides_As_rows_from_B()
    {
        var (programs, workouts) = await AsAppRole(_b.TenantId, scope: "", async connection =>
            (await Count(connection, $"SELECT count(*) FROM programs WHERE tenant_id = '{_a.TenantId}'"),
             await Count(connection, $"SELECT count(*) FROM workouts WHERE tenant_id = '{_a.TenantId}'")));

        Assert.Equal((0, 0), (programs, workouts));
    }

    [Fact]
    public async Task RLS_public_read_shows_published_programs_only_and_never_workouts()
    {
        var (published, drafts, workouts) = await AsAppRole(_b.TenantId, scope: "public-read", async connection =>
            (await Count(connection, $"SELECT count(*) FROM programs WHERE tenant_id = '{_a.TenantId}' AND is_published"),
             await Count(connection, $"SELECT count(*) FROM programs WHERE tenant_id = '{_a.TenantId}' AND NOT is_published"),
             await Count(connection, $"SELECT count(*) FROM workouts WHERE tenant_id = '{_a.TenantId}'")));

        Assert.Equal((1L, 0L, 0L), (published, drafts, workouts));
    }

    // Public-read widens reads only. Found by this test: with one combined policy, UPDATE reached
    // the row (and failed WITH CHECK) and DELETE, which has no WITH CHECK, would have succeeded.
    [Theory]
    [InlineData("UPDATE programs SET title = 'Hijacked' WHERE id = '{0}'")]
    [InlineData("DELETE FROM programs WHERE id = '{0}'")]
    public async Task RLS_public_read_does_not_allow_changing_As_programs(string sql)
    {
        var changed = await AsAppRole(_b.TenantId, scope: "public-read", async connection =>
        {
            await using var command = new NpgsqlCommand(string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, _aPublished.Id), connection);
            return await command.ExecuteNonQueryAsync(Cancel);
        });

        Assert.Equal(0, changed);
    }

    // Layer 3 off: with the EF filter bypassed, RLS still hides A's draft and workouts from B.
    [Fact]
    public async Task With_the_EF_filter_off_RLS_still_hides_As_draft_and_workouts()
    {
        var tenant = new TenantContext();
        tenant.EnterTenant(_b.TenantId, TenantRole.Owner);
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(databases.AppConnectionString("catalog"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor(tenant))
            .Options;

        await using var db = new CatalogDbContext(options, tenant);
        await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
        var programs = await db.Programs.IgnoreQueryFilters().CountAsync(p => p.TenantId == _a.TenantId, Cancel);
        var workouts = await db.Workouts.IgnoreQueryFilters().CountAsync(w => w.TenantId == _a.TenantId, Cancel);

        Assert.Equal((0, 0), (programs, workouts));
    }

    private string Resolve(string path) => path
        .Replace("{draft}", _aDraft.Id.ToString(), StringComparison.Ordinal)
        .Replace("{published}", _aPublished.Id.ToString(), StringComparison.Ordinal)
        .Replace("{workout}", _aWorkout.ToString(), StringComparison.Ordinal);

    private async Task<T> AsAppRole<T>(Guid tenantId, string scope, Func<NpgsqlConnection, Task<T>> act)
    {
        await using var connection = new NpgsqlConnection(databases.AppConnectionString("catalog"));
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using (var set = new NpgsqlCommand(
            $"SELECT set_config('app.tenant_id', '{tenantId}', true), set_config('app.scope', '{scope}', true)", connection))
        {
            await set.ExecuteNonQueryAsync(Cancel);
        }

        return await act(connection);
    }

    private static async Task<long> Count(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
