using System.Net;
using System.Net.Http.Json;
using Athlos.Testing;
using static Athlos.Testing.CatalogArrange;

namespace Athlos.Catalog.Api.IntegrationTests;

// A program the platform admin takes down after a report (design §2.4): tenancy-api's signed call
// unpublishes and blocks it, the coach cannot publish it again until it is allowed, and the call
// reaches only the tenant it names.
public sealed class ProgramBlockTests(AthlosDatabases databases, AthlosStorage storage) : IAsyncLifetime
{
    private readonly CatalogApiFactory _api = new(databases, storage);

    [Fact]
    public async Task A_blocked_program_leaves_the_marketplace_and_cannot_be_published_until_allowed()
    {
        var coach = NewCoach();
        var program = await _api.CreateProgram(coach, publish: true);

        var blocked = await Block(coach.TenantId, program.Id, blocked: true);
        var onSale = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{coach.Slug}/programs/{program.Slug}"));
        var republish = await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/publish");
        var allowed = await Block(coach.TenantId, program.Id, blocked: false);
        var publishedAgain = await _api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/publish");

        Assert.Equal(
            (HttpStatusCode.NoContent, HttpStatusCode.NotFound, HttpStatusCode.Conflict, HttpStatusCode.NoContent, HttpStatusCode.OK),
            (blocked.StatusCode, onSale.StatusCode, republish.StatusCode, allowed.StatusCode, publishedAgain.StatusCode));
        Assert.Contains("after a report", await republish.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocking_names_the_tenant_and_another_tenants_program_is_not_found()
    {
        var (owner, other) = (NewCoach(), NewCoach());
        var program = await _api.CreateProgram(owner, publish: true);

        var wrongTenant = await Block(other.TenantId, program.Id, blocked: true);
        var stillOnSale = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{owner.Slug}/programs/{program.Slug}"));
        var unsigned = await _api.CreateClient().PutAsJsonAsync($"/internal/tenants/{owner.TenantId}/programs/{program.Id}/block", new { blocked = true }, TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.OK, HttpStatusCode.Unauthorized), (wrongTenant.StatusCode, stillOnSale.StatusCode, unsigned.StatusCode));
    }

    private Task<HttpResponseMessage> Block(Guid tenantId, Guid programId, bool blocked) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, $"/internal/tenants/{tenantId}/programs/{programId}/block") { Content = JsonContent.Create(new { blocked }) });

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
