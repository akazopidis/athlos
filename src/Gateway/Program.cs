using Microsoft.AspNetCore.HttpOverrides;
using Athlos.Gateway;
using Athlos.Tenancy;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddGatewaySignature(builder.Configuration);
builder.Services.AddAthlosAuthentication(builder.Configuration);
builder.Services.AddHybridCache();
builder.Services.AddHttpClient<MembershipLookup>(http => http.BaseAddress = new Uri("https+http://tenancy-api"));
builder.Services.AddHttpClient<TenantPlans>(http => http.BaseAddress = new Uri("https+http://tenancy-api"));
// Link previews for storefronts and program pages (design §8.1): the services' public marketplace reads.
builder.Services.AddHttpClient(LinkPreviews.Tenancy, http => http.BaseAddress = new Uri("https+http://tenancy-api"));
builder.Services.AddHttpClient(LinkPreviews.Catalog, http => http.BaseAddress = new Uri("https+http://catalog-api"));
builder.Services.AddSingleton<LinkPreviews>();
// robots.txt and sitemap.xml for search engines (design §8.1).
builder.Services.AddSingleton<Sitemap>();
builder.Services.Configure<RateLimits.Options>(builder.Configuration.GetSection("RateLimits"));
builder.Services.AddSingleton<RateLimits>();

// Container Apps' ingress forwards the client's address; per-client limits need it (design §4).
// The ingress appends the address it saw, and only that last entry is used, so a client cannot
// pick its own by sending X-Forwarded-For.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

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

app.UseForwardedHeaders();
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
app.MapReverseProxy(proxy =>
{
    proxy.Use(StudioAccess.ResolveMembership);
    proxy.Use(RateLimits.Apply);
});
app.MapLinkPreviews();
app.MapSitemap();
app.MapSpa();

app.Run();
