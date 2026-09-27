using Azure.Core;
using Azure.Identity;
using Npgsql;

namespace Athlos.Tenancy.Data;

// Azure PostgreSQL runs with password authentication disabled (design §11). A connection string
// without a password therefore signs in with an Entra token for the app's managed identity,
// refreshed before it expires. Locally (Aspire, tests) the connection string has a password and
// this does nothing.
public static class PostgresEntraAuthentication
{
    private static readonly string[] Scopes = ["https://ossrdbms-aad.database.windows.net/.default"];

    public static NpgsqlDataSourceBuilder UseEntraTokenIfNoPassword(this NpgsqlDataSourceBuilder builder, string? managedIdentityClientId)
    {
        if (!string.IsNullOrEmpty(builder.ConnectionStringBuilder.Password))
        {
            return builder;
        }

        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = managedIdentityClientId });
        builder.UsePeriodicPasswordProvider(
            async (_, cancellationToken) => (await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken)).Token,
            successRefreshInterval: TimeSpan.FromMinutes(50),
            failureRefreshInterval: TimeSpan.FromSeconds(10));
        return builder;
    }
}
