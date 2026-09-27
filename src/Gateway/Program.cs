using Athlos.Gateway;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver()
    .AddTransforms(context =>
    {
        // Services trust these headers because only the gateway can reach them (design §3.2).
        // Strip whatever the caller sent on every route; the gateway sets them itself.
        foreach (var header in TrustedHeaders.All)
        {
            context.AddRequestHeaderRemove(header);
        }
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
