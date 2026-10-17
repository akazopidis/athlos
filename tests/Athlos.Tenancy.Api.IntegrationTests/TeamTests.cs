using System.Net;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Testing;

namespace Athlos.Tenancy.Api.IntegrationTests;

// The team (design §5.2): listing members, removing staff, leaving, and handing ownership over, so a
// workspace always has exactly one owner. Workspace deletion asks ordering-api first (design §3.4).
public sealed class TeamTests(AthlosDatabases database) : IAsyncLifetime
{
    private readonly TenancyApiFactory _api = new(database);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Members_see_the_team_and_only_the_owner_sees_addresses()
    {
        var workspace = await _api.CreateWorkspace();
        var staff = await database.AddMember(workspace.TenantId, TenantRole.Staff);

        var asOwner = await Team(workspace, workspace.OwnerId, TenantRole.Owner);
        var asStaff = await Team(workspace, staff, TenantRole.Staff);

        Assert.Equal([TenantRole.Owner, TenantRole.Staff], asOwner.Select(m => m.Role));
        Assert.True(asOwner.Single(m => m.Role == TenantRole.Owner).IsYou);
        Assert.All(asStaff, m => Assert.Null(m.Email));
        Assert.True(asStaff.Single(m => m.UserId == staff).IsYou);
    }

    [Fact]
    public async Task The_owner_removes_staff_and_staff_can_only_remove_themselves()
    {
        var workspace = await _api.CreateWorkspace();
        var (anna, ben) = (await database.AddMember(workspace.TenantId, TenantRole.Staff), await database.AddMember(workspace.TenantId, TenantRole.Staff));

        var annaRemovesBen = await Send(workspace, anna, TenantRole.Staff, HttpMethod.Delete, $"/members/{ben}");
        var ownerRemovesBen = await Send(workspace, workspace.OwnerId, TenantRole.Owner, HttpMethod.Delete, $"/members/{ben}");
        var annaLeaves = await Send(workspace, anna, TenantRole.Staff, HttpMethod.Delete, $"/members/{anna}");
        var ownerLeaves = await Send(workspace, workspace.OwnerId, TenantRole.Owner, HttpMethod.Delete, $"/members/{workspace.OwnerId}");

        Assert.Equal(
            (HttpStatusCode.Forbidden, HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.Conflict),
            (annaRemovesBen.StatusCode, ownerRemovesBen.StatusCode, annaLeaves.StatusCode, ownerLeaves.StatusCode));
        Assert.Equal([workspace.OwnerId], (await Team(workspace, workspace.OwnerId, TenantRole.Owner)).Select(m => m.UserId));
    }

    [Fact]
    public async Task Handing_ownership_over_swaps_the_two_roles_and_disconnects_payouts()
    {
        var workspace = await _api.CreateWorkspace();
        var staff = await database.AddMember(workspace.TenantId, TenantRole.Staff);
        await ConnectStripe(workspace);

        var byStaff = await Send(workspace, staff, TenantRole.Staff, HttpMethod.Post, $"/members/{workspace.OwnerId}/ownership");
        var toNobody = await Send(workspace, workspace.OwnerId, TenantRole.Owner, HttpMethod.Post, $"/members/{Guid.CreateVersion7()}/ownership");
        var handedOver = await Send(workspace, workspace.OwnerId, TenantRole.Owner, HttpMethod.Post, $"/members/{staff}/ownership");

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.NoContent), (byStaff.StatusCode, toNobody.StatusCode, handedOver.StatusCode));
        var team = await Team(workspace, staff, TenantRole.Owner);
        Assert.Equal(TenantRole.Owner, team.Single(m => m.UserId == staff).Role);
        Assert.Equal(TenantRole.Staff, team.Single(m => m.UserId == workspace.OwnerId).Role);
        // The previous owner's Stripe account no longer receives this workspace's sales.
        var payouts = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{workspace.TenantId}/payout-account"));
        var account = await payouts.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Json, Cancel);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, account.GetProperty("stripeAccountId").ValueKind);
        Assert.False(account.GetProperty("onboardingComplete").GetBoolean());
    }

    private async Task ConnectStripe(TenancyArrange.Workspace workspace)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);
        await using var command = new Npgsql.NpgsqlCommand(
            "UPDATE tenants SET stripe_account_id = 'acct_previous_owner', stripe_onboarding_complete = true WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", workspace.TenantId);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    [Fact]
    public async Task A_workspace_ordering_api_refuses_to_close_is_not_deleted()
    {
        var workspace = await _api.CreateWorkspace();
        _api.Erasure.RefuseFor(workspace.TenantId, "Some sales can still be refunded.");

        var response = await _api.AsOwner(workspace, HttpMethod.Delete, "/workspace", new { confirmSlug = workspace.Slug });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(workspace.TenantId, _api.Erasure.Erased);
        Assert.Equal(HttpStatusCode.OK, (await Send(workspace, workspace.OwnerId, TenantRole.Owner, HttpMethod.Get, "/workspace")).StatusCode);
    }

    private Task<HttpResponseMessage> Send(TenancyArrange.Workspace workspace, Guid userId, TenantRole role, HttpMethod method, string path) =>
        _api.SendAsGateway(new HttpRequestMessage(method, $"/api/v1/studio/{workspace.Slug}{path}"), userId, workspace.TenantId, role);

    private async Task<List<Member>> Team(TenancyArrange.Workspace workspace, Guid userId, TenantRole role)
    {
        var response = await Send(workspace, userId, role, HttpMethod.Get, "/members");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<Member>>(Json, Cancel))!;
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private sealed record Member(Guid UserId, string DisplayName, string? Email, TenantRole Role, bool IsYou);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
