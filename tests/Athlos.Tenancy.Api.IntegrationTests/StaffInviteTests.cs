using System.Net;
using System.Net.Http.Json;
using Athlos.Messaging.Contracts;
using Athlos.Notifications.Worker;
using Athlos.Tenancy;
using Athlos.Testing;
using Npgsql;
using static Athlos.Testing.TenancyArrange;

namespace Athlos.Tenancy.Api.IntegrationTests;

// Staff invites (design §5.2): the owner invites by email, the invite email carries a token, and only
// the signed-in user with that email can accept it, once, within 7 days.
public sealed class StaffInviteTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly TenancyApiFactory _api = new(databases);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_invitee_accepts_and_becomes_staff()
    {
        var workspace = await _api.CreateWorkspace();
        var invitee = Guid.CreateVersion7();
        var token = await _api.Invite(workspace, EmailOf(invitee));

        var accepted = await _api.Accept(workspace.Slug, token, invitee);
        var role = await RoleOf(workspace, invitee);
        var open = await OpenInvites(workspace);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Staff", role);
        Assert.Empty(open);
    }

    [Fact]
    public async Task The_free_plan_holds_one_staff_place_and_an_open_invite_takes_it()
    {
        var workspace = await _api.CreateWorkspace();
        var first = EmailOf(Guid.CreateVersion7());
        await _api.Invite(workspace, first);

        var second = await _api.AsOwner(workspace, HttpMethod.Post, "/invites", new { email = EmailOf(Guid.CreateVersion7()) });
        var again = await _api.AsOwner(workspace, HttpMethod.Post, "/invites", new { email = first });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("allows 1 staff member", await second.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task The_invite_email_links_to_the_SPA_with_the_token_in_the_fragment()
    {
        var workspace = await _api.CreateWorkspace();
        var email = EmailOf(Guid.CreateVersion7());
        await _api.Invite(workspace, email);

        var invite = await _api.InviteEmail(email);
        var rendered = new EmailRenderer().Render(invite.Template, invite.Data);

        Assert.StartsWith($"{TenancyApiFactory.PublicUrl}/invites/{workspace.Slug}#", invite.Data["acceptUrl"], StringComparison.Ordinal);
        Assert.Contains(invite.Data["acceptUrl"], rendered.PlainText, StringComparison.Ordinal);
        Assert.Equal([email], invite.To);
    }

    [Fact]
    public async Task Only_the_invited_address_can_accept_and_only_once()
    {
        var workspace = await _api.CreateWorkspace();
        var invitee = Guid.CreateVersion7();
        var token = await _api.Invite(workspace, EmailOf(invitee));

        var someoneElse = await _api.Accept(workspace.Slug, token, Guid.CreateVersion7());
        var first = await _api.Accept(workspace.Slug, token, invitee);
        var again = await _api.Accept(workspace.Slug, token, invitee);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.OK, HttpStatusCode.NotFound), (someoneElse.StatusCode, first.StatusCode, again.StatusCode));
    }

    [Fact]
    public async Task An_expired_or_replaced_invite_cannot_be_accepted()
    {
        var workspace = await _api.CreateWorkspace();
        var late = Guid.CreateVersion7();
        var lateToken = await _api.Invite(workspace, EmailOf(late));
        await Expire(workspace, EmailOf(late));
        var replaced = Guid.CreateVersion7();
        var oldToken = await _api.Invite(workspace, EmailOf(replaced));
        await Task.Delay(50, Cancel);
        await _api.AsOwner(workspace, HttpMethod.Post, "/invites", new { email = EmailOf(replaced) });

        var expired = await _api.Accept(workspace.Slug, lateToken, late);
        var stale = await _api.Accept(workspace.Slug, oldToken, replaced);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (expired.StatusCode, stale.StatusCode));
    }

    [Fact]
    public async Task A_revoked_invite_cannot_be_accepted()
    {
        var workspace = await _api.CreateWorkspace();
        var invitee = Guid.CreateVersion7();
        var token = await _api.Invite(workspace, EmailOf(invitee));
        var id = Assert.Single(await OpenInvites(workspace)).Id;

        var revoked = await _api.AsOwner(workspace, HttpMethod.Delete, $"/invites/{id}");
        var accept = await _api.Accept(workspace.Slug, token, invitee);

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (revoked.StatusCode, accept.StatusCode));
    }

    [Fact]
    public async Task Staff_cannot_manage_the_team()
    {
        var workspace = await _api.CreateWorkspace();
        var staff = await databases.AddMember(workspace.TenantId, TenantRole.Staff);

        var invite = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/studio/{workspace.Slug}/invites") { Content = JsonContent.Create(new { email = "x@test.local" }) },
            staff, workspace.TenantId, TenantRole.Staff);
        var list = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{workspace.Slug}/invites"), staff, workspace.TenantId, TenantRole.Staff);

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (invite.StatusCode, list.StatusCode));
    }

    [Fact]
    public async Task Inviting_a_member_is_a_conflict()
    {
        var workspace = await _api.CreateWorkspace();
        await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/profile"), workspace.OwnerId);

        var response = await _api.AsOwner(workspace, HttpMethod.Post, "/invites", new { email = EmailOf(workspace.OwnerId).ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private async Task<List<OpenInvite>> OpenInvites(Workspace workspace)
    {
        var response = await _api.AsOwner(workspace, HttpMethod.Get, "/invites");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<OpenInvite>>(Cancel))!;
    }

    private async Task<string?> RoleOf(Workspace workspace, Guid userId)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{workspace.Slug}/members/{userId}"));
        return response.StatusCode == HttpStatusCode.OK ? (await response.Content.ReadFromJsonAsync<Member>(Cancel))!.Role : null;
    }

    private async Task Expire(Workspace workspace, string email)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand("UPDATE staff_invites SET expires_at = now() - interval '1 minute' WHERE tenant_id = $1 AND email = $2", connection);
        command.Parameters.AddWithValue(workspace.TenantId);
        command.Parameters.AddWithValue(email);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    private sealed record OpenInvite(Guid Id, string Email, DateTimeOffset ExpiresAt, bool Expired);

    private sealed record Member(Guid TenantId, string Role);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
