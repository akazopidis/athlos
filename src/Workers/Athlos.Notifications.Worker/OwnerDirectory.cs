using System.Net;
using System.Net.Http.Json;

namespace Athlos.Notifications.Worker;

// Who owns a workspace, and one user's address, from tenancy-api (signed service-to-service calls;
// design §5.3). The worker keeps no database, and only tenancy knows the owners and the addresses.
public interface IOwnerDirectory
{
    // The owners' addresses; empty if the workspace no longer exists.
    Task<IReadOnlyList<string>> GetOwnerEmailsAsync(Guid tenantId, CancellationToken cancellationToken);

    // The user's address (EmailNotification.ToUser); null if they have no profile, or no longer exist.
    Task<string?> GetUserEmailAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed class TenancyOwnerDirectory(HttpClient http) : IOwnerDirectory
{
    private sealed record Response(Guid TenantId, List<string> Emails);

    private sealed record UserResponse(Guid UserId, string Email);

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

    public async Task<string?> GetUserEmailAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"/internal/users/{userId}/email", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserResponse>(cancellationToken))?.Email;
    }
}
