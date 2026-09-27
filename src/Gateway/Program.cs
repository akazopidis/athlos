using Athlos.Gateway;
using Athlos.Tenancy;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddGatewaySignature(builder.Configuration);

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver()
    .AddTransforms(context =>
    {
        // Services trust these headers because only the gateway sets them (design §3.2).
        // Strip whatever the caller sent on every route; the gateway sets them itself.
        foreach (var header in TrustedHeaders.All)
        {
            context.AddRequestHeaderRemove(header);
        }

        // Runs last, so the signature covers the final path and trusted headers.
        context.AddRequestTransform(GatewayRequestSigning.SignAsync);
    });

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseSecurityHeaders();

app.MapDefaultEndpoints();
app.MapSpaConfig();
app.MapReverseProxy();
app.MapSpa();

app.Run();
