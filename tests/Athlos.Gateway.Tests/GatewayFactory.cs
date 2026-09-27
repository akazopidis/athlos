using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Athlos.Gateway.Tests;

// Runs the gateway with a stand-in SPA shell, so SPA fallback behaviour is tested even when
// src/web has not been built. When it has, the real index.html (from MapStaticAssets) wins.
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    private const string SpaShell = "<!doctype html><title>Athlos test shell</title>";

    private readonly string _webRoot = Directory.CreateTempSubdirectory("athlos-gateway-").FullName;

    public GatewayFactory() => File.WriteAllText(Path.Combine(_webRoot, "index.html"), SpaShell);

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting(WebHostDefaults.WebRootKey, _webRoot);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(_webRoot, recursive: true);
    }
}
