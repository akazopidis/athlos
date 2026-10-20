using System.Net;
using System.Text.Json;
using Athlos.Testing;
using static Athlos.Testing.TenancyArrange;

namespace Athlos.Tenancy.Api.IntegrationTests;

// "Download my data" (design §5.4): the profile and workspaces from tenancy, the other services' parts
// from their export endpoints, as one file; nothing partial when a service cannot answer.
public sealed class DataExportTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly TenancyApiFactory _api = new(databases);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_export_holds_the_profile_workspaces_and_the_other_services_parts()
    {
        var workspace = await _api.CreateWorkspace();
        await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/profile"), workspace.OwnerId);

        var response = await Export(workspace.OwnerId);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("attachment; filename=\"athlos-data-", response.Content.Headers.ContentDisposition?.ToString() ?? "", StringComparison.Ordinal);
        Assert.Equal(EmailOf(workspace.OwnerId), json.GetProperty("account").GetProperty("email").GetString());
        var only = Assert.Single(json.GetProperty("workspaces").EnumerateArray());
        Assert.Equal((workspace.Slug, "Owner"), (only.GetProperty("slug").GetString(), only.GetProperty("role").GetString()));
        Assert.Equal("Strong start", json.GetProperty("orders").GetProperty("orders")[0].GetProperty("programTitle").GetString());
    }

    [Fact]
    public async Task Nothing_partial_is_handed_out_when_a_service_cannot_answer()
    {
        var customer = Guid.CreateVersion7();
        _api.CustomerExport.FailFor(customer);

        var response = await Export(customer);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    // Another user's workspaces never appear: the export reads the caller's own memberships.
    [Fact]
    public async Task The_export_shows_only_the_callers_workspaces()
    {
        var mine = await _api.CreateWorkspace();
        var theirs = await _api.CreateWorkspace();

        var json = await (await Export(mine.OwnerId)).Content.ReadAsStringAsync(Cancel);

        Assert.Contains(mine.Slug, json, StringComparison.Ordinal);
        Assert.DoesNotContain(theirs.Slug, json, StringComparison.Ordinal);
    }

    private Task<HttpResponseMessage> Export(Guid userId) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/export"), userId);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
