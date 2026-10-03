using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Data;
using Athlos.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.IsolationTests;

// Layer 4 on its own (design §3.3): connect as the service's role, act as tenant B, and try to
// reach tenant A's rows with raw SQL, with no application code in between.
public sealed class RowLevelSecurityTests(AthlosDatabases database) : IAsyncLifetime
{
    private readonly TenancyApiFactory _api = new(database);
    private TenancyArrange.Workspace _a = null!;
    private TenancyArrange.Workspace _b = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _a = await _api.CreateWorkspace();
        _b = await _api.CreateWorkspace();
    }

    [Fact]
    public async Task The_app_role_cannot_bypass_RLS()
    {
        var (superuser, bypassRls) = await AsAppRole(null, null, async connection =>
        {
            await using var command = new NpgsqlCommand("SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user", connection);
            await using var reader = await command.ExecuteReaderAsync(Cancel);
            await reader.ReadAsync(Cancel);
            return (reader.GetBoolean(0), reader.GetBoolean(1));
        });

        Assert.False(superuser);
        Assert.False(bypassRls);
    }

    [Fact]
    public async Task Tenant_B_sees_none_of_As_memberships()
    {
        var visible = await AsAppRole(_b.TenantId, _b.OwnerId, connection =>
            Scalar<long>(connection, $"SELECT count(*) FROM memberships WHERE tenant_id = '{_a.TenantId}'"));

        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task Without_a_tenant_nothing_is_visible()
    {
        var visible = await AsAppRole(null, null, connection => Scalar<long>(connection, "SELECT count(*) FROM memberships"));

        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task Tenant_B_cannot_insert_rows_for_A()
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => AsAppRole(_b.TenantId, _b.OwnerId, connection =>
            Scalar<int>(connection,
                $"INSERT INTO memberships (id, tenant_id, user_id, role, created_at, updated_at) " +
                $"VALUES ('{Guid.CreateVersion7()}', '{_a.TenantId}', '{_b.OwnerId}', 'Owner', now(), now()) RETURNING 1")));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState); // "new row violates row-level security policy"
    }

    [Fact]
    public async Task Tenant_B_cannot_change_As_workspace()
    {
        var changed = await AsAppRole(_b.TenantId, _b.OwnerId, async connection =>
        {
            await using var command = new NpgsqlCommand($"UPDATE tenants SET display_name = 'Hijacked' WHERE id = '{_a.TenantId}'", connection);
            return await command.ExecuteNonQueryAsync(Cancel);
        });

        Assert.Equal(0, changed);
    }

    // A user may read their memberships in every workspace, but only change them inside the tenant
    // they are acting for. Found by the catalog public-read test: DELETE follows the USING clause.
    [Fact]
    public async Task A_user_acting_in_B_cannot_delete_their_membership_in_A()
    {
        await database.AddMember(_a.TenantId, TenantRole.Staff, existingUser: _b.OwnerId);

        var (visible, deleted) = await AsAppRole(_b.TenantId, _b.OwnerId, async connection =>
        {
            var count = await Scalar<long>(connection, $"SELECT count(*) FROM memberships WHERE tenant_id = '{_a.TenantId}' AND user_id = '{_b.OwnerId}'");
            await using var command = new NpgsqlCommand($"DELETE FROM memberships WHERE tenant_id = '{_a.TenantId}' AND user_id = '{_b.OwnerId}'", connection);
            return (count, await command.ExecuteNonQueryAsync(Cancel));
        });

        Assert.Equal(1, visible);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task A_user_sees_only_their_own_profile()
    {
        var other = Guid.CreateVersion7();
        await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/profile"), other);

        var visible = await AsAppRole(null, _b.OwnerId, connection =>
            Scalar<long>(connection, $"SELECT count(*) FROM user_profiles WHERE user_id = '{other}'"));

        Assert.Equal(0, visible);
    }

    // Layer 4 for staff_invites: tenant B's session sees none of A's invites and cannot change them.
    [Fact]
    public async Task Tenant_B_sees_and_changes_none_of_As_invites()
    {
        await _api.Invite(_a, TenancyArrange.EmailOf(Guid.CreateVersion7()));

        var (visible, changed) = await AsAppRole(_b.TenantId, _b.OwnerId, async connection =>
            (await Scalar<long>(connection, $"SELECT count(*) FROM staff_invites WHERE tenant_id = '{_a.TenantId}'"),
             await new NpgsqlCommand($"UPDATE staff_invites SET expires_at = now() + interval '1 year' WHERE tenant_id = '{_a.TenantId}'", connection).ExecuteNonQueryAsync(Cancel)));

        Assert.Equal((0L, 0), (visible, changed));
    }

    // tenant_members_read (for the sale email, design §5.3): inside tenant A, the profiles of A's
    // members are readable, those of B's members are not, and none of them can be changed.
    [Fact]
    public async Task Inside_a_tenant_only_its_members_profiles_are_readable_and_none_are_writable()
    {
        await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/profile"), _a.OwnerId);
        await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/profile"), _b.OwnerId);

        var (aOwner, bOwner, changed) = await AsAppRole(_a.TenantId, null, async connection =>
            (await Scalar<long>(connection, $"SELECT count(*) FROM user_profiles WHERE user_id = '{_a.OwnerId}'"),
             await Scalar<long>(connection, $"SELECT count(*) FROM user_profiles WHERE user_id = '{_b.OwnerId}'"),
             await new NpgsqlCommand($"UPDATE user_profiles SET email = 'x@y.z' WHERE user_id = '{_a.OwnerId}'", connection).ExecuteNonQueryAsync(Cancel)
                + await new NpgsqlCommand($"DELETE FROM user_profiles WHERE user_id = '{_a.OwnerId}'", connection).ExecuteNonQueryAsync(Cancel)));

        Assert.Equal((1L, 0L, 0), (aOwner, bOwner, changed));
    }

    // Design §3.3: "turn layer 3 off and show layer 4 still blocks". The EF filter is bypassed
    // explicitly; RLS alone keeps tenant A's memberships out of tenant B's session.
    [Fact]
    public async Task With_the_EF_filter_off_RLS_still_hides_As_rows()
    {
        var tenant = new TenantContext();
        tenant.EnterTenant(_b.TenantId, TenantRole.Owner);
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(database.AppConnectionString("tenancy"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor(tenant))
            .Options;

        await using var db = new TenancyDbContext(options, tenant);
        await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
        var visible = await db.Memberships
            .IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == _a.TenantId, Cancel);

        Assert.Equal(0, visible);
    }

    // set_config(..., true) must not outlive the transaction, or a pooled connection would carry one
    // request's tenant into the next (design §3.2).
    [Fact]
    public async Task The_tenant_setting_does_not_survive_the_transaction()
    {
        await using var connection = new NpgsqlConnection(database.AppConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);

        await using (var transaction = await connection.BeginTransactionAsync(Cancel))
        {
            await Scalar<string>(connection, $"SELECT set_config('app.tenant_id', '{_a.TenantId}', true)");
            await transaction.CommitAsync(Cancel);
        }

        var after = await Scalar<string>(connection, "SELECT coalesce(current_setting('app.tenant_id', true), '')");
        Assert.Equal("", after);
    }

    private async Task<T> AsAppRole<T>(Guid? tenantId, Guid? userId, Func<NpgsqlConnection, Task<T>> act)
    {
        await using var connection = new NpgsqlConnection(database.AppConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await Scalar<string>(connection,
            $"SELECT set_config('app.tenant_id', '{tenantId}', true) || set_config('app.user_id', '{userId}', true)");
        return await act(connection);
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Cancel))!;
    }

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
