using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Athlos.Tenancy;

public static class Authentication
{
    // Every service validates the Entra External ID access token itself, as the gateway does
    // (design §3.2, "belt and braces"). Configuration section "Auth":
    //   Authority: https://<tenant>.ciamlogin.com/<tenant id>/v2.0
    //   Audience:  the Athlos API app registration's client id
    public static IServiceCollection AddAthlosAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Auth");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = section["Authority"];
                options.Audience = section["Audience"];
                // Keep claim names as Entra sends them ("oid", "email", "name").
                options.MapInboundClaims = false;
            });

        services.AddAuthorization();
        return services;
    }
}
