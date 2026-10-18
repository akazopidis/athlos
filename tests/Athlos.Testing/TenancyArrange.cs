using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Microsoft.EntityFrameworkCore;
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

    // Adds a member straight in the database, skipping the invite flow, for tests that only need one.
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

    // The newest invite email queued for the address.
    public static async Task<EmailNotification> InviteEmail(this TenancyApiFactory api, string email) =>
        (await api.QueuedEmails()).FirstOrDefault(e => e.Template == EmailTemplates.StaffInvite && e.To.Contains(email))
        ?? throw new InvalidOperationException($"No invite email was queued for {email}.");

    // The emails tenancy queued, newest first. Read from the outbox table, where each is written with the
    // change it announces: every tenancy test app shares that table and runs a background publisher, so
    // which app's sender ends up sending it is a race, but the row is always there.
    public static async Task<List<EmailNotification>> QueuedEmails(this TenancyApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var queued = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Queue == EmailNotification.Queue)
            .OrderByDescending(m => m.OccurredAt)
            .Select(m => m.Payload)
            .Take(1000)
            .ToListAsync(TestContext.Current.CancellationToken);
        return [.. queued.Select(payload => JsonSerializer.Deserialize<EmailNotification>(payload, JsonSerializerOptions.Web)!)];
    }

    public static Task<HttpResponseMessage> Accept(this TenancyApiFactory api, string slug, string token, Guid userId) =>
        api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invites/{slug}/accept") { Content = JsonContent.Create(new { token }) }, userId);

    private sealed record CreatedTenant(Guid Id, string Slug, string DisplayName);
}
