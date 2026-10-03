using System.Net;
using System.Net.Http.Json;

namespace Athlos.Notifications.Worker;

// Who owns a workspace, from tenancy-api (signed service-to-service call; design §5.3). The worker
// keeps no database, and only tenancy knows the owners and their addresses.
public interface IOwnerDirectory
{
    // The owners' addresses; empty if the workspace no longer exists.
    Task<IReadOnlyList<string>> GetOwnerEmailsAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class TenancyOwnerDirectory(HttpClient http) : IOwnerDirectory
{
    private sealed record Response(Guid TenantId, List<string> Emails);

    public async Task<IReadOnlyList<string>> GetOwnerEmailsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"/internal/tenants/{tenantId}/owner-emails", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Response>(cancellationToken))?.Emails ?? [];
    }
}
