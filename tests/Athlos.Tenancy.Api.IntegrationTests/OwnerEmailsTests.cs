using System.Net;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Testing;

namespace Athlos.Tenancy.Api.IntegrationTests;

// GET /internal/tenants/{id}/owner-emails, for the notifications-worker's sale email (design §5.3).
public sealed class OwnerEmailsTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly TenancyApiFactory _api = new(databases);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_the_owners_with_a_profile_and_nobody_else()
    {
        var a = await _api.CreateWorkspace();
        var b = await _api.CreateWorkspace();
        var coOwner = await databases.AddMember(a.TenantId, TenantRole.Owner);
        var staff = await databases.AddMember(a.TenantId, TenantRole.Staff);
        var neverSignedIn = await databases.AddMember(a.TenantId, TenantRole.Owner);
        foreach (var user in new[] { a.OwnerId, coOwner, staff, b.OwnerId })
        {
            await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/profile"), user);
        }

        var owners = await OwnerEmails(a.TenantId);

        Assert.Equal([$"{a.OwnerId}@test.local", $"{coOwner}@test.local"], owners.Order());
        Assert.DoesNotContain($"{neverSignedIn}@test.local", owners);
    }

    [Fact]
    public async Task An_unknown_workspace_is_not_found()
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{Guid.CreateVersion7()}/owner-emails"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<List<string>> OwnerEmails(Guid tenantId)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{tenantId}/owner-emails"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OwnerEmailsResponse>(Cancel))!.Emails;
    }

    private sealed record OwnerEmailsResponse(Guid TenantId, List<string> Emails);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
