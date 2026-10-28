using System.Net;
using System.Net.Http.Json;
using Athlos.Messaging.Contracts;
using Athlos.Notifications.Worker;
using Athlos.Tenancy;
using Athlos.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Athlos.Tenancy.Api.IntegrationTests;

// The workspace's emails (design §5.3): its life and its team. Each test also renders what was queued
// with the worker's templates, so a producer and its template that disagree fail here, not in the worker.
public sealed class WorkspaceEmailTests(AthlosDatabases database) : IAsyncLifetime
{
    private static readonly string[] AdminRole = ["Platform.Admin"];

    private readonly FakeStripeConnect _stripe = new();
    private TenancyApiFactory _api = null!;

    public ValueTask InitializeAsync()
    {
        _api = new TenancyApiFactory(database, _stripe);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_new_workspace_gets_a_welcome_with_its_next_steps()
    {
        var workspace = await _api.CreateWorkspace();

        var welcome = await Single(EmailTemplates.WorkspaceWelcome, workspace.TenantId);

        Assert.Contains($"/c/{workspace.Slug}", Render(welcome).PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_owner_hears_who_joined_and_who_left_and_a_removed_member_is_told()
    {
        var workspace = await _api.CreateWorkspace();
        var (anna, ben) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        // The Free plan has one staff place: Anna joins and leaves, then Ben joins and is removed.
        await Join(workspace, anna);
        await Send(workspace, anna, TenantRole.Staff, HttpMethod.Delete, $"/members/{anna}");
        await Join(workspace, ben);
        await Send(workspace, workspace.OwnerId, TenantRole.Owner, HttpMethod.Delete, $"/members/{ben}");

        var emails = await _api.QueuedEmails();
        Assert.Equal(2, emails.Count(e => e.Template == EmailTemplates.InviteAccepted && e.ToOwnersOfTenant == workspace.TenantId));
        var left = Assert.Single(emails, e => e.Template == EmailTemplates.MemberLeft && e.ToOwnersOfTenant == workspace.TenantId);
        var removed = Assert.Single(emails, e => e.Template == EmailTemplates.MemberRemoved && e.To.Contains(TenancyArrange.EmailOf(ben)));
        Assert.All([left, removed, emails.First(e => e.Template == EmailTemplates.InviteAccepted)], e => Render(e));
    }

    [Fact]
    public async Task The_new_owner_hears_that_they_own_the_workspace()
    {
        var workspace = await _api.CreateWorkspace();
        var staff = await database.AddMember(workspace.TenantId, TenantRole.Staff);

        await Send(workspace, workspace.OwnerId, TenantRole.Owner, HttpMethod.Post, $"/members/{staff}/ownership");

        Render(await Single(EmailTemplates.OwnershipReceived, workspace.TenantId));
    }

    [Fact]
    public async Task Suspension_reactivation_and_a_plan_change_are_announced_once_each()
    {
        var workspace = await _api.CreateWorkspace();
        var admin = Guid.CreateVersion7();

        foreach (var status in (string[])["Suspended", "Suspended", "Active"])
        {
            await Admin(admin, HttpMethod.Put, $"/api/v1/admin/tenants/{workspace.TenantId}/status", new { status, reason = "Selling stolen programs" });
        }

        await Admin(admin, HttpMethod.Put, $"/api/v1/admin/tenants/{workspace.TenantId}/plan", new { plan = "Pro", reason = "Upgrade" });
        await Admin(admin, HttpMethod.Put, $"/api/v1/admin/tenants/{workspace.TenantId}/plan", new { plan = "Pro", reason = "Upgrade" });

        var suspended = Render(await Single(EmailTemplates.WorkspaceSuspended, workspace.TenantId));
        Assert.Contains("Reason: Selling stolen programs", suspended.PlainText, StringComparison.Ordinal);
        Render(await Single(EmailTemplates.WorkspaceReactivated, workspace.TenantId));
        var plan = await Single(EmailTemplates.PlanChanged, workspace.TenantId);
        Assert.Contains("Pro plan", Render(plan).Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finished_payouts_are_announced_once()
    {
        var workspace = await _api.CreateWorkspace();
        await Send(workspace, workspace.OwnerId, TenantRole.Owner, HttpMethod.Post, "/payouts/onboarding");
        _stripe.CompleteOnboarding(workspace.TenantId);

        await Send(workspace, workspace.OwnerId, TenantRole.Owner, HttpMethod.Get, "/payouts");
        await Send(workspace, workspace.OwnerId, TenantRole.Owner, HttpMethod.Get, "/payouts");

        Render(await Single(EmailTemplates.PayoutsReady, workspace.TenantId));
    }

    // One reminder three days after onboarding started (backlog item 46), and none once it is finished.
    [Fact]
    public async Task Unfinished_payouts_get_one_reminder_once_it_is_due()
    {
        var stuck = await _api.CreateWorkspace();
        var finished = await _api.CreateWorkspace();
        foreach (var workspace in new[] { stuck, finished })
        {
            await Send(workspace, workspace.OwnerId, TenantRole.Owner, HttpMethod.Post, "/payouts/onboarding");
            await Send(workspace, workspace.OwnerId, TenantRole.Owner, HttpMethod.Post, "/payouts/onboarding");
        }

        _stripe.CompleteOnboarding(finished.TenantId);
        await Send(finished, finished.OwnerId, TenantRole.Owner, HttpMethod.Get, "/payouts");
        var notYetDue = await RemindDue();
        await MakeDue(stuck, finished);
        var due = await RemindDue();
        var again = await RemindDue();

        Assert.Equal((0, 1, 0), (notYetDue, due, again));
        var reminder = Render(await Single(EmailTemplates.PayoutsReminder, stuck.TenantId));
        Assert.Contains($"/studio/{stuck.Slug}", reminder.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain(await _api.QueuedEmails(), e => e.Template == EmailTemplates.PayoutsReminder && e.ToOwnersOfTenant == finished.TenantId);
    }

    private Task<int> RemindDue() =>
        Athlos.Tenancy.Api.Features.Payouts.PayoutReminders.RemindDueAsync(_api.Services.GetRequiredService<IServiceScopeFactory>(), TestContext.Current.CancellationToken);

    // Three days pass: the reminders of these workspaces fall due.
    private async Task MakeDue(params TenancyArrange.Workspace[] workspaces)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("UPDATE payout_reminders SET due_at = now() - interval '1 minute' WHERE tenant_id = ANY(@ids)", connection);
        command.Parameters.AddWithValue("ids", workspaces.Select(w => w.TenantId).ToArray());
        Assert.Equal(workspaces.Length, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_deleted_account_gets_one_last_email()
    {
        var customer = Guid.CreateVersion7();
        await Profile(customer);

        var deleted = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me"), customer);
        var again = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me"), customer);

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (deleted.StatusCode, again.StatusCode));
        var email = Assert.Single(await _api.QueuedEmails(), e => e.Template == EmailTemplates.AccountDeleted && e.To.Contains(TenancyArrange.EmailOf(customer)));
        Render(email);
    }

    private async Task Join(TenancyArrange.Workspace workspace, Guid member)
    {
        await Profile(member);
        var token = await _api.Invite(workspace, TenancyArrange.EmailOf(member));
        Assert.Equal(HttpStatusCode.OK, (await _api.Accept(workspace.Slug, token, member)).StatusCode);
    }

    private async Task<EmailNotification> Single(string template, Guid tenantId) =>
        Assert.Single(await _api.QueuedEmails(), e => e.Template == template && e.ToOwnersOfTenant == tenantId);

    private static RenderedEmail Render(EmailNotification email) => new EmailRenderer().Render(email.Template, email.Data);

    private async Task Profile(Guid userId) =>
        Assert.True((await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/profile"), userId)).IsSuccessStatusCode);

    private Task<HttpResponseMessage> Send(TenancyArrange.Workspace workspace, Guid userId, TenantRole role, HttpMethod method, string path) =>
        _api.SendAsGateway(new HttpRequestMessage(method, $"/api/v1/studio/{workspace.Slug}{path}"), userId, workspace.TenantId, role);

    private async Task Admin(Guid admin, HttpMethod method, string path, object body) =>
        Assert.True((await _api.SendAsGateway(new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) }, admin, appRoles: AdminRole)).IsSuccessStatusCode);

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
