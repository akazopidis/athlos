using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Athlos.Testing;
using Npgsql;

namespace Athlos.Tenancy.Api.IntegrationTests;

// /internal/email-suppressions, the notifications-worker's list of addresses that bounce (design §5.3).
public sealed class EmailSuppressionsTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly TenancyApiFactory _api = new(databases);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_recorded_address_is_suppressed_and_others_are_not()
    {
        var bounced = Hash($"{Guid.CreateVersion7()}@bounce.test");
        var fine = Hash($"{Guid.CreateVersion7()}@fine.test");

        Assert.Equal(HttpStatusCode.NoContent, (await Record(bounced, "Bounced")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Record(bounced, "Suppressed")).StatusCode);

        Assert.Equal([bounced], await Check(bounced, fine));
    }

    [Fact]
    public async Task An_old_bounce_is_forgotten_and_deleted_on_the_next_write()
    {
        var old = Hash($"{Guid.CreateVersion7()}@old.test");
        await Record(old, "Bounced");
        await Age(old, days: 181);

        Assert.Empty(await Check(old));

        await Record(Hash($"{Guid.CreateVersion7()}@new.test"), "Bounced");
        Assert.Equal(0, await Count(old));
    }

    [Theory]
    [InlineData("not-a-hash", "Bounced")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "Bounced")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Delivered")]
    public async Task Only_a_hash_and_an_undeliverable_status_are_recorded(string hash, string status)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Record(hash, status)).StatusCode);
    }

    [Fact]
    public async Task A_check_takes_hashes_only()
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/internal/email-suppressions?hash=someone@example.test"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string Hash(string address) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address)));

    private Task<HttpResponseMessage> Record(string hash, string status) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, $"/internal/email-suppressions/{hash}") { Content = JsonContent.Create(new { status }) });

    private async Task<List<string>> Check(params string[] hashes)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/email-suppressions?{string.Join("&", hashes.Select(h => $"hash={h}"))}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CheckResponse>(Cancel))!.Suppressed;
    }

    private async Task Age(string hash, int days)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand("UPDATE email_suppressions SET recorded_at = now() - make_interval(days => @days) WHERE address_hash = @hash", connection);
        command.Parameters.AddWithValue("days", days);
        command.Parameters.AddWithValue("hash", hash);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    private async Task<long> Count(string hash)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM email_suppressions WHERE address_hash = @hash", connection);
        command.Parameters.AddWithValue("hash", hash);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }

    private sealed record CheckResponse(List<string> Suppressed);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
