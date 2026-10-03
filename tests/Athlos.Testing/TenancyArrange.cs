using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Microsoft.Extensions.DependencyInjection;
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

    // The address TestTokens puts in a user's token, which an invite must match.
    public static string EmailOf(Guid userId) => $"{userId}@test.local";

    public static Task<HttpResponseMessage> AsOwner(this TenancyApiFactory api, Workspace workspace, HttpMethod method, string path, object? body = null) =>
        api.SendAsGateway(
            new HttpRequestMessage(method, $"/api/v1/studio/{workspace.Slug}{path}") { Content = body is null ? null : JsonContent.Create(body) },
            workspace.OwnerId, workspace.TenantId, TenantRole.Owner);

    // The owner invites the address; returns the token from the invite email, as the invitee gets it.
    public static async Task<string> Invite(this TenancyApiFactory api, Workspace workspace, string email)
    {
        var response = await api.AsOwner(workspace, HttpMethod.Post, "/invites", new { email });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invite = await InviteEmail(api, email);
        return new Uri(invite.Data["acceptUrl"]).Fragment.TrimStart('#');
    }

    // The newest invite email sent to the address (the outbox publisher also runs in the background).
    public static async Task<EmailNotification> InviteEmail(this TenancyApiFactory api, string email)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await OutboxDelivery.PublishPendingAsync<TenancyDbContext>(api.Services.GetRequiredService<IServiceScopeFactory>(), TestContext.Current.CancellationToken);
            var sent = api.Messages.Sent
                .Select(m => JsonSerializer.Deserialize<EmailNotification>(m.Message.Payload, JsonSerializerOptions.Web)!)
                .LastOrDefault(e => e.Template == EmailTemplates.StaffInvite && e.To.Contains(email));
            if (sent is not null)
            {
                return sent;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"No invite email was sent to {email}.");
    }

    public static Task<HttpResponseMessage> Accept(this TenancyApiFactory api, string slug, string token, Guid userId) =>
        api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invites/{slug}/accept") { Content = JsonContent.Create(new { token }) }, userId);

    private sealed record CreatedTenant(Guid Id, string Slug, string DisplayName);
}
