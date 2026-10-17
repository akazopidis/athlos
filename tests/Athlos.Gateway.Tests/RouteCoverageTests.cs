using System.Text.Json;

namespace Athlos.Gateway.Tests;

// Every public endpoint of every service is reachable through the gateway, and reaches that service
// (design §2.1). Services answer only gateway-signed requests, so an endpoint the gateway does not
// route is unreachable in Azure, while the service's own integration tests, which sign as the gateway,
// still pass. The endpoints come from each service's checked-in OpenAPI document (kept in step with the
// code by CI), the routes from the gateway's appsettings.json; route choice follows YARP's: lowest
// Order first, then the most specific pattern (literal before parameter before catch-all).
public sealed class RouteCoverageTests
{
    private static readonly Dictionary<string, string> Services = new()
    {
        ["tenancy"] = "Athlos.Tenancy.Api/openapi/tenancy.json",
        ["catalog"] = "Athlos.Catalog.Api/openapi/catalog.json",
        ["ordering"] = "Athlos.Ordering.Api/openapi/ordering.json",
        ["enrollment"] = "Athlos.Enrollment.Api/openapi/enrollment.json",
    };

    public static TheoryData<string, string, string> Endpoints()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (cluster, document) in Services)
        {
            using var openApi = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "src", "Services", document)));
            foreach (var path in openApi.RootElement.GetProperty("paths").EnumerateObject())
            {
                foreach (var method in path.Value.EnumerateObject().Where(m => m.Name is "get" or "post" or "put" or "delete" or "patch"))
                {
                    data.Add(method.Name.ToUpperInvariant(), path.Name, cluster);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void Every_service_endpoint_is_routed_to_its_service(string method, string path, string cluster)
    {
        var route = Routes()
            .Where(r => r.Methods is null || r.Methods.Contains(method, StringComparer.OrdinalIgnoreCase))
            .Where(r => Matches(r.Pattern, path))
            .OrderBy(r => r.Order)
            .ThenBy(r => Specificity(r.Pattern), StringComparer.Ordinal)
            .FirstOrDefault();

        Assert.True(route is not null, $"No gateway route matches {method} {path}: add one to src/Gateway/appsettings.json.");
        Assert.Equal(cluster, route.Cluster);
    }

    private sealed record Route(string Pattern, string Cluster, int Order, string[]? Methods);

    private static List<Route> Routes()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "src", "Gateway", "appsettings.json")));
        return [.. settings.RootElement.GetProperty("ReverseProxy").GetProperty("Routes").EnumerateObject().Select(r =>
        {
            var match = r.Value.GetProperty("Match");
            return new Route(
                match.GetProperty("Path").GetString()!,
                r.Value.GetProperty("ClusterId").GetString()!,
                r.Value.TryGetProperty("Order", out var order) ? order.GetInt32() : 0,
                match.TryGetProperty("Methods", out var methods) ? [.. methods.EnumerateArray().Select(m => m.GetString()!)] : null);
        })];
    }

    // A route pattern against an OpenAPI path; {name} matches one segment (including an OpenAPI
    // parameter), {**rest} matches the rest of the path, even nothing.
    private static bool Matches(string pattern, string path)
    {
        var expected = pattern.Trim('/').Split('/');
        var actual = path.Trim('/').Split('/');
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i].StartsWith("{**", StringComparison.Ordinal))
            {
                return true;
            }

            if (i >= actual.Length || (!expected[i].StartsWith('{') && !string.Equals(expected[i], actual[i], StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return expected.Length == actual.Length;
    }

    // Lower sorts first: per segment, 1 for a literal, 2 for a parameter, 3 for a catch-all.
    private static string Specificity(string pattern) =>
        string.Concat(pattern.Trim('/').Split('/').Select(s => s.StartsWith("{**", StringComparison.Ordinal) ? '3' : s.StartsWith('{') ? '2' : '1'));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athlos.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Athlos.slnx not found above the test output.");
    }
}
