using System.Net;
using System.Net.Http.Json;

namespace Athlos.Gateway.Tests;

public sealed class GatewayTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Live_health_check_is_healthy()
    {
        var response = await _client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Version_defaults_to_local()
    {
        var body = await _client.GetFromJsonAsync<VersionResponse>("/version", TestContext.Current.CancellationToken);

        Assert.Equal("local", body?.Version);
    }

    [Fact]
    public async Task Spa_config_is_not_cached()
    {
        var response = await _client.GetAsync("/config.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        // The refund window shown to customers defaults to ordering-api's 14 days.
        var config = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(14, config.GetProperty("refundWindowDays").GetInt32());
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await _client.GetAsync("/version", TestContext.Current.CancellationToken);

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Contains("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    [Theory]
    [InlineData("/api/v1/does-not-exist")]
    [InlineData("/webhooks/unknown")]
    public async Task Unknown_api_paths_are_problem_404s_not_the_spa(string path)
    {
        var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/api/v1/does-not-exist")]
    [InlineData("/webhooks/unknown")]
    [InlineData("/internal/tenants/0198a3b2-0000-7000-8000-000000000001")]
    [InlineData("/health/unknown")]
    public async Task Server_paths_never_fall_back_to_the_spa(string path)
    {
        var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/c/some-coach")]
    [InlineData("/studio/some-coach/programs")]
    public async Task Client_side_routes_serve_the_spa_shell(string path)
    {
        var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    private sealed record VersionResponse(string Version);
}
