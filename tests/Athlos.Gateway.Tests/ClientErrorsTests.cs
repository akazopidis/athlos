using System.Net;
using System.Net.Http.Json;

namespace Athlos.Gateway.Tests;

// Page crashes reported by the SPA's error boundary (design §6): accepted and logged, within limits.
public sealed class ClientErrorsTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_crash_report_is_accepted_and_a_bad_one_refused()
    {
        var accepted = await _client.PostAsJsonAsync("/client-errors", new { message = "Cannot read properties of undefined", stack = "at Cart", path = "/cart?code=SECRET" }, Cancel);
        var empty = await _client.PostAsJsonAsync("/client-errors", new { message = "" }, Cancel);
        var tooBig = await _client.PostAsJsonAsync("/client-errors", new { message = "x", stack = new string('s', 20_000) }, Cancel);

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge),
            (accepted.StatusCode, empty.StatusCode, tooBig.StatusCode));
    }

    // One client cannot flood the logs: 10 reports a minute, then 429. A gateway of its own, so the
    // other test's budget is untouched.
    [Fact]
    public async Task A_client_may_report_ten_errors_a_minute()
    {
        await using var own = new GatewayFactory();
        var client = own.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/client-errors", new { message = $"Crash {i}" }, Cancel)).StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
