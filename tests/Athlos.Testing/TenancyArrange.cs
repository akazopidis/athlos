using System.Net;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Npgsql;
using Xunit;

namespace Athlos.Testing;

// Arrange helpers shared by the tenancy integration tests and the isolation tests.
public static class TenancyArrange
{
    public sealed record Workspace(Guid TenantId, string Slug, Guid OwnerId);

    public static string NewSlug(string prefix = "coach") => $"{prefix}-{Guid.NewGuid():N}"[..24];

    // A workspace created through the API, the way a coach signs up.
    public static async Task<Workspace> CreateWorkspace(this TenancyApiFactory api, string? slug = null)
    {
        var ownerId = Guid.CreateVersion7();
        slug ??= NewSlug();

        var response = await api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenants")
            {
                Content = JsonContent.Create(new { slug, displayName = $"Coach {slug}" }),
            },
            userId: ownerId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<CreatedTenant>(TestContext.Current.CancellationToken);
        return new Workspace(created!.Id, slug, ownerId);
    }

    // Staff invites arrive in a later phase; until then staff are added directly as the owner role.
    public static async Task<Guid> AddMember(this AthlosDatabases database, Guid tenantId, TenantRole role, Guid? existingUser = null)
    {
        var userId = existingUser ?? Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "INSERT INTO memberships (id, tenant_id, user_id, role, created_at, updated_at) VALUES ($1, $2, $3, $4, now(), now())",
            connection);
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        command.Parameters.AddWithValue(tenantId);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(role.ToString());
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return userId;
    }

    private sealed record CreatedTenant(Guid Id, string Slug, string DisplayName);
}
