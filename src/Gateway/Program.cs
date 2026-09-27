using Athlos.Gateway;
using Athlos.Tenancy;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddGatewaySignature(builder.Configuration);
builder.Services.AddAthlosAuthentication(builder.Configuration);
builder.Services.AddHybridCache();
builder.Services.AddHttpClient<MembershipLookup>(http => http.BaseAddress = new Uri("https+http://tenancy-api"));

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

        // The gateway's own values, for studio routes (StudioAccess.ResolveMembership).
        context.AddRequestTransform(StudioAccess.AddTrustedHeaders);

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
app.UseAuthentication();

app.MapDefaultEndpoints();
app.MapSpaConfig();
app.MapReverseProxy(proxy => proxy.Use(StudioAccess.ResolveMembership));
app.MapSpa();

app.Run();
