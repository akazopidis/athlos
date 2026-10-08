using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Athlos.Gateway.Tests;

// Noisy-neighbor protection (design §4): each coach's traffic spends its own plan's budget, so one
// coach's flood gets 429s while another coach's requests still go through.
public sealed class RateLimitTests : IAsyncLifetime
{
    private const int PlanRate = 3;

    private WebApplication? _backend;
    private WebApplicationFactory<MembershipLookup>? _gateway;
    private bool _limitsOff;

    public async ValueTask InitializeAsync()
    {
        // One stand-in for tenancy-api and catalog-api: the plan lookup and the public routes.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _backend = builder.Build();
        _backend.MapGet("/internal/tenants/{slug}/plan", (string slug) => slug switch
        {
            "coach-a" or "coach-b" => Results.Ok(new { tenantId = Guid.CreateVersion7(), plan = new { name = "Free", requestsPerSecond = PlanRate } }),
            "coach-broken" => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            _ => Results.NotFound(),
        });
        _backend.MapGet("/api/v1/marketplace/coaches/{slug}", () => Results.Ok());
        _backend.MapGet("/api/v1/marketplace/programs", () => Results.Ok());
        await _backend.StartAsync(TestContext.Current.CancellationToken);
    }

    private HttpClient Gateway()
    {
        var backend = _backend!.Urls.First();
        _gateway ??= new GatewayFactory().WithWebHostBuilder(web =>
            web.UseSetting("ReverseProxy:Clusters:tenancy:Destinations:tenancy-api:Address", backend)
               .UseSetting("ReverseProxy:Clusters:catalog:Destinations:catalog-api:Address", backend)
               .UseSetting("services:tenancy-api:http:0", backend)
               .UseSetting("RateLimits:PerClientRequestsPerSecond", "2")
               .UseSetting("RateLimits:Enabled", _limitsOff ? "false" : "true"));
        return _gateway.CreateClient();
    }

    [Fact]
    public async Task A_flooding_coach_gets_429_and_another_coach_does_not()
    {
        var client = Gateway();

        var flood = await Send(client, "/api/v1/marketplace/coaches/coach-a", times: PlanRate * 3);
        var neighbour = await Send(client, "/api/v1/marketplace/coaches/coach-b", times: PlanRate);

        var refused = flood.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        Assert.NotEmpty(refused);
        Assert.All(refused, r => Assert.True(int.Parse(r.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) >= 1));
        Assert.All(neighbour, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    // Tenant-aware telemetry (design §6): the gateway's request span names the coach and its plan.
    [Fact]
    public async Task A_coach_request_is_tagged_with_its_tenant_and_plan()
    {
        var spans = new System.Collections.Concurrent.ConcurrentBag<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        await Send(Gateway(), "/api/v1/marketplace/coaches/coach-b", times: 1);

        // The gateway's server span is the trace's root (the stand-in backend's span is its
        // descendant). The listener sees the whole process, so other tests' spans may be there too.
        Assert.Contains(spans, s => s.Parent is null && s.ParentId is null && s.Kind == System.Diagnostics.ActivityKind.Server
            && s.GetTagItem("tenant.id") is string && s.GetTagItem("tenant.plan") as string == "Free");
    }

    [Fact]
    public async Task A_storefront_program_list_spends_that_coach_budget()
    {
        var client = Gateway();
        await Send(client, "/api/v1/marketplace/coaches/coach-a", times: PlanRate * 2);

        var list = await Send(client, "/api/v1/marketplace/programs?coach=coach-a", times: 1);

        Assert.Equal(HttpStatusCode.TooManyRequests, list.Single().StatusCode);
    }

    [Fact]
    public async Task The_cross_coach_search_is_limited_per_client()
    {
        var responses = await Send(Gateway(), "/api/v1/marketplace/programs", times: 6);

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_failed_plan_lookup_does_not_fail_the_request()
    {
        var response = await Send(Gateway(), "/api/v1/marketplace/coaches/coach-broken", times: 1);

        Assert.Equal(HttpStatusCode.OK, response.Single().StatusCode);
    }

    [Fact]
    public async Task With_limits_off_every_request_goes_through()
    {
        _limitsOff = true;

        var responses = await Send(Gateway(), "/api/v1/marketplace/coaches/coach-a", times: PlanRate * 3);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    private static async Task<List<HttpResponseMessage>> Send(HttpClient client, string path, int times)
    {
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < times; i++)
        {
            responses.Add(await client.GetAsync(path, TestContext.Current.CancellationToken));
        }

        return responses;
    }

    public async ValueTask DisposeAsync()
    {
        if (_gateway is not null)
        {
            await _gateway.DisposeAsync();
        }

        if (_backend is not null)
        {
            await _backend.DisposeAsync();
        }
    }
}
