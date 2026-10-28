using System.Net;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Features.Storefront;
using Athlos.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athlos.Tenancy.Api.IntegrationTests;

// Catalog-api's copy of each workspace's name, for the shop's search (design §8): a pass copies every
// active workspace whose name differs from the last copy, so new workspaces, renames and the workspaces
// from before are all copied, and a failed copy is tried again on the next pass.
public sealed class CoachNameCopyTests(AthlosDatabases database) : IAsyncLifetime
{
    private static readonly string[] AdminRole = ["Platform.Admin"];

    private TenancyApiFactory _api = null!;

    public ValueTask InitializeAsync()
    {
        _api = new TenancyApiFactory(database);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_workspace_name_is_copied_once_and_again_after_a_rename()
    {
        var workspace = await _api.CreateWorkspace();

        await Copy();
        await Copy();
        var renamed = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/studio/{workspace.Slug}/workspace")
            {
                Content = JsonContent.Create(new { displayName = "Anna Strong", logoUrl = (string?)null, primaryColor = (string?)null }),
            },
            workspace.OwnerId, workspace.TenantId, TenantRole.Owner);
        var afterRename = _api.CoachNames.Names[workspace.TenantId];
        await Copy();

        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal($"Coach {workspace.Slug}", afterRename);
        Assert.Equal("Anna Strong", _api.CoachNames.Names[workspace.TenantId]);
        Assert.Equal(2, _api.CoachNames.Calls[workspace.TenantId]);
    }

    [Fact]
    public async Task A_failed_copy_is_tried_again_and_a_suspended_workspace_waits()
    {
        var (failing, suspended) = (await _api.CreateWorkspace(), await _api.CreateWorkspace());
        _api.CoachNames.FailFor(failing.TenantId);
        var suspend = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/tenants/{suspended.TenantId}/status") { Content = JsonContent.Create(new { status = "Suspended", reason = "Breaks the terms" }) },
            Guid.CreateVersion7(), appRoles: AdminRole);

        await Copy();
        var whileFailing = _api.CoachNames.Names.ContainsKey(failing.TenantId);
        _api.CoachNames.FailFor(failing.TenantId, failing: false);
        await Copy();

        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        Assert.False(whileFailing);
        Assert.Equal($"Coach {failing.Slug}", _api.CoachNames.Names[failing.TenantId]);
        Assert.False(_api.CoachNames.Names.ContainsKey(suspended.TenantId));
    }

    private Task<int> Copy() =>
        CoachNameCopies.CopyAsync(_api.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
