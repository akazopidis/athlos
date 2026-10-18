using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy.Api.Data;

namespace Athlos.Tenancy.Api.Features.Tenants;

// The workspace's own emails (design §5.3): its life (welcome, payouts ready, suspended, reactivated,
// plan changed) and its team (invite accepted, member left, ownership received) go to the owners, whom
// the notifications-worker looks up when it sends (toOwnersOfTenant), so tenancy never puts an owner's
// address in a message here. A removed member is written to directly. Each is added to the outbox in the
// transaction of the change it announces.
public static class WorkspaceEmails
{
    public static void ToOwners(TenancyDbContext db, string template, Tenant workspace, IConfiguration configuration, Dictionary<string, string>? extra = null) =>
        db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue,
            new EmailNotification(template, To: [], ToOwnersOfTenant: workspace.Id, Locale: "en", Data(workspace, configuration, extra))));

    public static void ToMember(TenancyDbContext db, string template, string email, Tenant workspace, IConfiguration configuration) =>
        db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue,
            new EmailNotification(template, To: [email], ToOwnersOfTenant: null, Locale: "en", Data(workspace, configuration, extra: null))));

    private static Dictionary<string, string> Data(Tenant workspace, IConfiguration configuration, Dictionary<string, string>? extra)
    {
        var publicUrl = new Uri(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
        var data = new Dictionary<string, string>
        {
            ["workspaceName"] = workspace.DisplayName,
            ["studioUrl"] = new Uri(publicUrl, $"/studio/{workspace.Slug}").ToString(),
            ["teamUrl"] = new Uri(publicUrl, $"/studio/{workspace.Slug}/settings").ToString(),
            ["storefrontUrl"] = new Uri(publicUrl, $"/c/{workspace.Slug}").ToString(),
        };
        foreach (var (key, value) in extra ?? [])
        {
            data[key] = value;
        }

        return data;
    }
}
