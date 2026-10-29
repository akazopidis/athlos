using Microsoft.AspNetCore.Http;

namespace Athlos.Gateway.Tests;

// The public pages' first data requests, preloaded from the shell (design §8.1). A preload is used only
// if its URL is exactly the SPA's, so these are the requests the pages made on dev (29 October 2026):
// a change to the SPA's queries must change them too, or the preloads are wasted.
public sealed class DataPreloadTests
{
    [Theory]
    [InlineData("/", "", new[] { "/api/v1/marketplace/programs?Page=1&PageSize=12", "/api/v1/marketplace/programs?PageSize=1" })]
    [InlineData("/", "?q=yoga", new[] { "/api/v1/marketplace/programs?PageSize=1" })]
    [InlineData("/coaches", "", new[] { "/api/v1/marketplace/programs?PageSize=1" })]
    [InlineData("/c/demo-forge", "", new[] { "/api/v1/marketplace/coaches/demo-forge", "/api/v1/marketplace/programs?Coach=demo-forge&Page=1&PageSize=12" })]
    [InlineData("/c/demo-forge", "?category=Running", new[] { "/api/v1/marketplace/coaches/demo-forge" })]
    [InlineData("/c/demo-forge/programs/minimalist", "", new[]
    {
        "/api/v1/marketplace/coaches/demo-forge",
        "/api/v1/marketplace/coaches/demo-forge/programs/minimalist",
        "/api/v1/marketplace/programs?Coach=demo-forge&PageSize=5",
    })]
    [InlineData("/me", "", new string[0])]
    [InlineData("/studio/demo-forge", "", new string[0])]
    public void Each_public_page_preloads_its_first_requests(string path, string query, string[] expected) =>
        Assert.Equal(expected, DataPreloads.For(new PathString(path), new QueryString(query)));

    [Fact]
    public void The_preloads_go_in_the_head_as_fetches_and_are_encoded()
    {
        var html = DataPreloads.Insert("<html><head><title>Athlos</title></head><body></body></html>", ["/api/v1/x?a=1&b=2"]);

        Assert.Contains("<link rel=\"preload\" href=\"/api/v1/x?a=1&amp;b=2\" as=\"fetch\" crossorigin /></head>", html, StringComparison.Ordinal);
        Assert.Equal("<p>no head</p>", DataPreloads.Insert("<p>no head</p>", ["/api/v1/x"]));
    }
}
