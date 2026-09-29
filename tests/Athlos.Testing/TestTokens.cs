using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Athlos.Testing;

// Access tokens shaped like Entra External ID's ("oid", "email", "name"), signed with a test key
// that the services under test are configured to trust instead of Entra's.
public static class TestTokens
{
    public const string Issuer = "https://test.athlos.local/v2.0";
    public const string Audience = "athlos-api-test";

    public static SymmetricSecurityKey SigningKey { get; } =
        new("athlos-test-signing-key-0123456789abcdef-0123456789abcdef"u8.ToArray());

    // Makes the app under test (gateway or service) accept these tokens instead of Entra's.
    public static void TrustTestTokens(this IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:Audience", Audience);
        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.ValidIssuer = Issuer;
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            }));
    }

    public static string For(Guid userId, string? email = null, string? name = null)
    {
        var claims = new List<Claim> { new("oid", userId.ToString()), new("scp", "access_as_user") };
        if (email is not null) claims.Add(new Claim("email", email));
        if (name is not null) claims.Add(new Claim("name", name));

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }
}
