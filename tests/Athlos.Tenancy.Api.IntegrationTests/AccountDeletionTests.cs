using System.Net;
using Athlos.Tenancy;
using Athlos.Testing;
using Npgsql;
using static Athlos.Testing.TenancyArrange;

namespace Athlos.Tenancy.Api.IntegrationTests;

// Deleting your account (design §5.2): not while you own a workspace; otherwise the other services
// erase their part, tenancy removes your memberships, invites and profile, and Graph deletes the
// Entra user last. Every step can be retried.
public sealed class AccountDeletionTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly TenancyApiFactory _api = new(databases);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_owner_must_delete_their_workspaces_first()
    {
        var workspace = await _api.CreateWorkspace();

        var response = await DeleteAccount(workspace.OwnerId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(workspace.Slug, await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.OwnerId, _api.CustomerErasure.Erased);
        Assert.DoesNotContain(workspace.OwnerId, _api.EntraUsers.Deleted);
    }

    [Fact]
    public async Task A_staff_member_is_removed_everywhere_and_their_sign_in_account_is_deleted()
    {
        var workspace = await _api.CreateWorkspace();
        var invitee = Guid.CreateVersion7();
        var token = await _api.Invite(workspace, EmailOf(invitee));
        await _api.Accept(workspace.Slug, token, invitee);
        await Profile(invitee);

        var response = await DeleteAccount(invitee);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(invitee, _api.CustomerErasure.Erased);
        Assert.Contains(invitee, _api.EntraUsers.Deleted);
        Assert.Equal(0L, await Count("SELECT count(*) FROM memberships WHERE user_id = @id", invitee));
        Assert.Equal(0L, await Count("SELECT count(*) FROM user_profiles WHERE user_id = @id", invitee));
        Assert.Equal(0L, await CountText("SELECT count(*) FROM staff_invites WHERE email = @email", EmailOf(invitee)));
        // The workspace and its owner are untouched.
        Assert.Equal(1L, await Count("SELECT count(*) FROM memberships WHERE user_id = @id", workspace.OwnerId));
    }

    [Fact]
    public async Task Without_Graph_set_up_nothing_is_deleted()
    {
        var customer = Guid.CreateVersion7();
        await Profile(customer);
        _api.EntraUsers.Configured = false;

        var response = await DeleteAccount(customer);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1L, await Count("SELECT count(*) FROM user_profiles WHERE user_id = @id", customer));
    }

    [Fact]
    public async Task If_another_service_fails_nothing_here_is_deleted_yet()
    {
        var customer = Guid.CreateVersion7();
        await Profile(customer);
        _api.CustomerErasure.FailFor(customer);

        var response = await DeleteAccount(customer);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1L, await Count("SELECT count(*) FROM user_profiles WHERE user_id = @id", customer));
        Assert.DoesNotContain(customer, _api.EntraUsers.Deleted);
    }

    [Fact]
    public async Task If_Graph_fails_the_data_is_gone_and_a_retry_deletes_the_sign_in_account()
    {
        var customer = Guid.CreateVersion7();
        await Profile(customer);
        _api.EntraUsers.FailOnceFor(customer);

        var first = await DeleteAccount(customer);
        var retry = await DeleteAccount(customer);

        Assert.Equal((HttpStatusCode.BadGateway, HttpStatusCode.NoContent), (first.StatusCode, retry.StatusCode));
        Assert.Equal(0L, await Count("SELECT count(*) FROM user_profiles WHERE user_id = @id", customer));
        Assert.Contains(customer, _api.EntraUsers.Deleted);
    }

    private Task<HttpResponseMessage> DeleteAccount(Guid userId) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me"), userId);

    private async Task Profile(Guid userId) =>
        Assert.True((await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/profile"), userId)).IsSuccessStatusCode);

    // As the superuser, so RLS cannot hide what is left.
    private async Task<long> Count(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }

    private async Task<long> CountText(string sql, string email)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("email", email);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();
}
