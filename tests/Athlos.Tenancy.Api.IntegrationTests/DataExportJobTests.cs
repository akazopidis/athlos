using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Athlos.Messaging.Contracts;
using Athlos.Notifications.Worker;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Features.Account;
using Athlos.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Athlos.Testing.TenancyArrange;

namespace Athlos.Tenancy.Api.IntegrationTests;

// Data exports as a queued job (design §5.4, backlog item 55): asking answers at once, the file is
// prepared after the response (or by the retries), kept in blob storage for seven days, downloaded by
// its requester only, and announced by email.
public sealed class DataExportJobTests(AthlosDatabases databases, AthlosStorage storage) : IAsyncLifetime
{
    private TenancyApiFactory _api = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new TenancyApiFactory(databases, storage: storage);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task An_account_export_is_prepared_announced_and_downloaded_by_its_requester_only()
    {
        var workspace = await _api.CreateWorkspace();
        await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/profile"), workspace.OwnerId);

        var asked = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/exports"), workspace.OwnerId);
        var again = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/exports"), workspace.OwnerId);
        var item = (await asked.Content.ReadFromJsonAsync<Item>(Json, Cancel))!;
        var ready = await ReadyAsync(workspace.OwnerId, item.Id);
        var file = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/exports/{item.Id}/file"), workspace.OwnerId);
        var stranger = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/exports/{item.Id}/file"), Guid.CreateVersion7());

        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (asked.StatusCode, again.StatusCode));
        Assert.Equal(item.Id, (await again.Content.ReadFromJsonAsync<Item>(Json, Cancel))!.Id);
        Assert.Equal(("Account", "Ready"), (ready.Kind, ready.Status));
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        var json = JsonDocument.Parse(await file.Content.ReadAsStringAsync(Cancel)).RootElement;
        Assert.Equal(EmailOf(workspace.OwnerId), json.GetProperty("account").GetProperty("email").GetString());
        Assert.Equal("Strong start", json.GetProperty("orders").GetProperty("orders")[0].GetProperty("programTitle").GetString());
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);

        var email = Assert.Single(await _api.QueuedEmails(), e => e.Template == EmailTemplates.DataExportReady && e.ToUser == workspace.OwnerId);
        Assert.Empty(email.To);
        var rendered = new EmailRenderer().Render(email.Template, email.Data);
        Assert.Contains("Your Athlos data is ready", rendered.PlainText, StringComparison.Ordinal);
        Assert.Contains("/me/account", rendered.PlainText, StringComparison.Ordinal);
    }

    // When a service cannot answer, the export waits (Preparing) and the retries finish it.
    [Fact]
    public async Task An_export_that_cannot_be_prepared_at_once_is_finished_by_the_retries()
    {
        var customer = Guid.CreateVersion7();
        _api.CustomerExport.FailFor(customer);

        var asked = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/exports"), customer);
        var item = (await asked.Content.ReadFromJsonAsync<Item>(Json, Cancel))!;
        await DataExports.RunDueAsync(Scopes(), Cancel);
        var waiting = Assert.Single(await ListAsync(customer));
        _api.CustomerExport.Recover(customer);
        await Execute("UPDATE data_exports SET next_attempt_at = now() - interval '1 second' WHERE id = @id", item.Id);
        await DataExports.RunDueAsync(Scopes(), Cancel);

        Assert.Equal("Preparing", waiting.Status);
        Assert.Equal("Ready", Assert.Single(await ListAsync(customer)).Status);
    }

    // The owner's workspace export holds the team; staff cannot ask for one.
    [Fact]
    public async Task The_owner_gets_the_workspace_export_and_staff_cannot_ask()
    {
        var workspace = await _api.CreateWorkspace();
        var staff = await databases.AddMember(workspace.TenantId, TenantRole.Staff);

        var asStaff = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/studio/{workspace.Slug}/workspace/exports"), staff, workspace.TenantId, TenantRole.Staff);
        var asked = await _api.AsOwner(workspace, HttpMethod.Post, "/workspace/exports");
        var item = (await asked.Content.ReadFromJsonAsync<Item>(Json, Cancel))!;
        var ready = await ReadyAsync(workspace.OwnerId, item.Id);
        var file = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/exports/{item.Id}/file"), workspace.OwnerId);

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Accepted), (asStaff.StatusCode, asked.StatusCode));
        Assert.Equal(("Workspace", workspace.Slug), (ready.Kind, ready.Workspace));
        var json = JsonDocument.Parse(await file.Content.ReadAsStringAsync(Cancel)).RootElement;
        Assert.Equal(workspace.Slug, json.GetProperty("workspace").GetProperty("slug").GetString());
        Assert.Equal(2, json.GetProperty("team").GetArrayLength());
        Assert.StartsWith($"athlos-{workspace.Slug}-", file.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "", StringComparison.Ordinal);
    }

    // After seven days the file and the row go; deleting the account takes them at once.
    [Fact]
    public async Task Exports_expire_after_seven_days_and_go_with_the_account()
    {
        var (expiring, leaving) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var first = await AskAndWaitAsync(expiring);
        var second = await AskAndWaitAsync(leaving);

        await Execute("UPDATE data_exports SET expires_at = now() - interval '1 second' WHERE id = @id", first);
        await DataExports.RunDueAsync(Scopes(), Cancel);
        using (var scope = _api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Athlos.Tenancy.Api.Data.TenancyDbContext>();
            await DataExports.DeleteForUserAsync(db, scope.ServiceProvider.GetRequiredService<Athlos.Media.MediaStore>(), leaving, Cancel);
        }

        Assert.Empty(await ListAsync(expiring));
        Assert.Empty(await ListAsync(leaving));
        Assert.Equal(0L, await Count("SELECT count(*) FROM data_exports WHERE id = @id OR id = @other", first, second));
        Assert.Null(await storage.ReadAsync($"users/{expiring}/exports/{first}.json"));
        Assert.Null(await storage.ReadAsync($"users/{leaving}/exports/{second}.json"));
    }

    private async Task<Guid> AskAndWaitAsync(Guid userId)
    {
        var asked = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/exports"), userId);
        var item = (await asked.Content.ReadFromJsonAsync<Item>(Json, Cancel))!;
        await ReadyAsync(userId, item.Id);
        return item.Id;
    }

    // The job runs after the response; the pass is run too, in case it has not started yet (the lease
    // keeps the two from both working on it).
    private async Task<Item> ReadyAsync(Guid userId, Guid exportId)
    {
        for (var tries = 0; tries < 50; tries++)
        {
            await DataExports.RunDueAsync(Scopes(), Cancel);
            if ((await ListAsync(userId)).SingleOrDefault(e => e.Id == exportId) is { Status: "Ready" } ready)
            {
                return ready;
            }

            await Task.Delay(100, Cancel);
        }

        throw new TimeoutException("The export did not become ready.");
    }

    private async Task<List<Item>> ListAsync(Guid userId)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/exports"), userId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<Item>>(Json, Cancel))!;
    }

    private IServiceScopeFactory Scopes() => _api.Services.GetRequiredService<IServiceScopeFactory>();

    private async Task Execute(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    private async Task<long> Count(string sql, Guid id, Guid other)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("other", other);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Item(Guid Id, string Kind, string? Workspace, string Status);

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();
}
