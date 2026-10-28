using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

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
    public async Task Spa_config_is_revalidated_on_every_load()
    {
        var response = await _client.GetAsync("/config.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoCache);
        // The refund window shown to customers defaults to ordering-api's 14 days.
        var config = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal((14, 10), (config.GetProperty("refundWindowDays").GetInt32(), config.GetProperty("platformFeePercent").GetInt32()));
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await _client.GetAsync("/version", TestContext.Current.CancellationToken);

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Contains("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Contains("camera=()", Assert.Single(response.Headers.GetValues("Permissions-Policy")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Resource-Policy")));
    }

    // With the stage's hosts configured, the policy names them instead of wildcards (ZAP's 10055).
    [Fact]
    public void The_content_security_policy_names_the_stage_s_sign_in_and_image_hosts()
    {
        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spa:EntraAuthority"] = "https://athlos.ciamlogin.com/0000-tenant/v2.0",
            ["Spa:ImageOrigin"] = "https://stathlosstaging.blob.core.windows.net/",
        }).Build();

        var policy = SecurityHeaders.ContentSecurityPolicy(configured);
        var fallback = SecurityHeaders.ContentSecurityPolicy(new ConfigurationBuilder().Build());

        Assert.Contains("img-src 'self' data: https://stathlosstaging.blob.core.windows.net;", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self' https://athlos.ciamlogin.com https://login.microsoftonline.com;", policy, StringComparison.Ordinal);
        Assert.Contains("frame-src https://athlos.ciamlogin.com;", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("*", policy, StringComparison.Ordinal);
        Assert.Contains("img-src 'self' data: https:;", fallback, StringComparison.Ordinal);
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
        // A deploy reaches the next visit: the shell names the current release's assets.
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task A_missing_asset_is_a_404_not_the_spa_shell()
    {
        var response = await _client.GetAsync("/assets/index-0ld0ld0l.js", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Built_assets_are_cached_for_a_year()
    {
        // Needs src/web's build in the gateway's wwwroot (CI's backend job does not build the SPA).
        var shell = await _client.GetStringAsync("/index.html", TestContext.Current.CancellationToken);
        var asset = System.Text.RegularExpressions.Regex.Match(shell, "/assets/[^\"]+\\.js");
        Assert.SkipUnless(asset.Success, "src/web has not been built into the gateway's wwwroot.");

        var response = await _client.GetAsync(asset.Value, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl?.MaxAge);
        Assert.Contains("immutable", response.Headers.CacheControl?.Extensions.Select(e => e.Name) ?? []);
    }

    private sealed record VersionResponse(string Version);
}
