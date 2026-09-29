using Microsoft.AspNetCore.Hosting;
using Athlos.Testing;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Athlos.Gateway.Tests;

// Runs the gateway with a stand-in SPA shell, so SPA fallback behaviour is tested even when
// src/web has not been built. When it has, the real index.html (from MapStaticAssets) wins.
// Anchored on MembershipLookup: referenced test projects also have a top-level Program.
public class GatewayFactory : WebApplicationFactory<MembershipLookup>
{
    public const string SigningKey = "test-signing-key-0123456789abcdef0123456789";

    private const string SpaShell = "<!doctype html><title>Athlos test shell</title>";

    private readonly string _webRoot = Directory.CreateTempSubdirectory("athlos-gateway-").FullName;

    public GatewayFactory() => File.WriteAllText(Path.Combine(_webRoot, "index.html"), SpaShell);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebHostDefaults.WebRootKey, _webRoot);
        builder.UseSetting("GatewaySignature:Key", SigningKey);
        builder.TrustTestTokens();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(_webRoot, recursive: true);
        GC.SuppressFinalize(this);
    }
}
