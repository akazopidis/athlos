using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Athlos.Messaging.Contracts;
using Athlos.Notifications.Worker;
using Athlos.Tenancy;
using Athlos.Testing;
using static Athlos.Testing.TenancyArrange;

namespace Athlos.Tenancy.Api.IntegrationTests;

// Reporting a program or a coach (design §2.4): reports collect per program or coach, one per
// reporter, and the platform admin takes a program down (the coach is told why), keeps it, allows a
// taken-down program back, keeps a coach, or suspends one, always with a reason.
public sealed class ContentReportTests(AthlosDatabases databases) : IAsyncLifetime
{
    private static readonly string[] AdminRole = ["Platform.Admin"];

    private readonly TenancyApiFactory _api = new(databases);
    private readonly Guid _admin = Guid.CreateVersion7();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reports_collect_per_program_once_per_reporter()
    {
        var coach = await _api.CreateWorkspace();
        var program = _api.ReportedPrograms.Sell(coach.TenantId, coach.Slug);
        var (anna, ben) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        var first = await ReportProgram(anna, program.ProgramId, "Copyright", "Copied from another coach's book");
        await ReportProgram(anna, program.ProgramId, "Misleading");
        await ReportProgram(ben, program.ProgramId, "Misleading");
        var unknown = await ReportProgram(anna, Guid.CreateVersion7(), "Other");
        var anonymous = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/reports")
        {
            Content = JsonContent.Create(new { kind = "Program", programId = program.ProgramId, reason = "Other" }),
        });

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound, HttpStatusCode.Unauthorized), (first.StatusCode, unknown.StatusCode, anonymous.StatusCode));
        var entry = Assert.Single(await Open(), r => r.GetProperty("programId").ValueKind == JsonValueKind.String && r.GetProperty("programId").GetGuid() == program.ProgramId);
        Assert.Equal((2, program.Title), (entry.GetProperty("reports").GetInt32(), entry.GetProperty("subject").GetString()));
        Assert.Equal(["Copied from another coach's book"], entry.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
    }

    // Taking a program down: catalog unpublishes and blocks it, the reports close, the coach's owners
    // are emailed the reason, and the audit trail records it; allowing it back lifts the block.
    [Fact]
    public async Task The_admin_takes_a_program_down_with_a_reason_and_can_allow_it_back()
    {
        var coach = await _api.CreateWorkspace();
        var program = _api.ReportedPrograms.Sell(coach.TenantId, coach.Slug);
        await ReportProgram(Guid.CreateVersion7(), program.ProgramId, "Illegal");

        var withoutReason = await Admin(HttpMethod.Post, $"/api/v1/admin/reports/programs/{program.ProgramId}/unpublish", new { reason = "" });
        _api.ReportedPrograms.FailFor(program.ProgramId);
        var catalogDown = await Admin(HttpMethod.Post, $"/api/v1/admin/reports/programs/{program.ProgramId}/unpublish", new { reason = "Sells a banned substance plan" });
        var stillOpen = (await Open()).Any(r => Is(r, program.ProgramId));

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadGateway, true), (withoutReason.StatusCode, catalogDown.StatusCode, stillOpen));

        var fixedApi = new TenancyApiFactory(databases);
        await using (fixedApi)
        {
            var reported = fixedApi.ReportedPrograms.Sell(coach.TenantId, coach.Slug, program.Title);
            await ReportProgram(Guid.CreateVersion7(), reported.ProgramId, "Illegal", api: fixedApi);

            var takenDown = await Admin(HttpMethod.Post, $"/api/v1/admin/reports/programs/{reported.ProgramId}/unpublish", new { reason = "Sells a banned substance plan" }, fixedApi);
            var list = await AdminJson(fixedApi, "/api/v1/admin/reports");
            var allowed = await Admin(HttpMethod.Post, $"/api/v1/admin/reports/programs/{reported.ProgramId}/allow", new { reason = "The coach removed the plan" }, fixedApi);
            var listAfter = await AdminJson(fixedApi, "/api/v1/admin/reports");

            Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (takenDown.StatusCode, allowed.StatusCode));
            Assert.False(fixedApi.ReportedPrograms.Blocked[reported.ProgramId]);
            Assert.DoesNotContain(list.GetProperty("open").EnumerateArray(), r => Is(r, reported.ProgramId));
            Assert.Contains(list.GetProperty("takenDown").EnumerateArray(), t => t.GetProperty("programId").GetGuid() == reported.ProgramId);
            Assert.DoesNotContain(listAfter.GetProperty("takenDown").EnumerateArray(), t => t.GetProperty("programId").GetGuid() == reported.ProgramId);
            var email = Assert.Single(await fixedApi.QueuedEmails(), e => e.Template == EmailTemplates.ProgramUnpublished && e.ToOwnersOfTenant == coach.TenantId);
            Assert.Contains("Reason: Sells a banned substance plan", new EmailRenderer().Render(email.Template, email.Data).PlainText, StringComparison.Ordinal);
            var actions = (await AdminJson(fixedApi, "/api/v1/admin/actions")).EnumerateArray()
                .Where(a => a.GetProperty("subject").GetString()!.Contains($"/c/{coach.Slug})", StringComparison.Ordinal))
                .Select(a => a.GetProperty("action").GetString());
            Assert.Equal(["ProgramAllowed", "ProgramUnpublished"], actions);
        }
    }

    // Keeping closes the reports with a reason; suspending a reported coach closes theirs too.
    [Fact]
    public async Task Keeping_or_suspending_closes_the_reports()
    {
        var (kept, suspended) = (await _api.CreateWorkspace(), await _api.CreateWorkspace());
        var program = _api.ReportedPrograms.Sell(kept.TenantId, kept.Slug);
        await ReportProgram(Guid.CreateVersion7(), program.ProgramId, "Misleading");
        await ReportCoach(Guid.CreateVersion7(), kept.Slug, "Abusive");
        await ReportCoach(Guid.CreateVersion7(), suspended.Slug, "Illegal");

        var keepProgram = await Admin(HttpMethod.Post, $"/api/v1/admin/reports/programs/{program.ProgramId}/keep", new { reason = "Claims are accurate" });
        var keepCoach = await Admin(HttpMethod.Post, $"/api/v1/admin/reports/coaches/{kept.TenantId}/keep", new { reason = "Nothing abusive found" });
        var suspend = await Admin(HttpMethod.Put, $"/api/v1/admin/tenants/{suspended.TenantId}/status", new { status = "Suspended", reason = "Sells illegal content" });
        var again = await Admin(HttpMethod.Post, $"/api/v1/admin/reports/coaches/{kept.TenantId}/keep", new { reason = "Again" });
        var reportSuspended = await ReportCoach(Guid.CreateVersion7(), suspended.Slug, "Illegal");

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.NotFound),
            (keepProgram.StatusCode, keepCoach.StatusCode, suspend.StatusCode, again.StatusCode, reportSuspended.StatusCode));
        Assert.DoesNotContain(await Open(), r => r.GetProperty("tenantId").GetGuid() == kept.TenantId || r.GetProperty("tenantId").GetGuid() == suspended.TenantId);
    }

    [Theory]
    [InlineData("GET", "/api/v1/admin/reports")]
    [InlineData("POST", "/api/v1/admin/reports/programs/0199a000-0000-7000-8000-000000000001/unpublish")]
    [InlineData("POST", "/api/v1/admin/reports/programs/0199a000-0000-7000-8000-000000000001/allow")]
    [InlineData("POST", "/api/v1/admin/reports/programs/0199a000-0000-7000-8000-000000000001/keep")]
    [InlineData("POST", "/api/v1/admin/reports/coaches/0199a000-0000-7000-8000-000000000001/keep")]
    public async Task The_report_queue_is_for_the_platform_admin_only(string method, string path)
    {
        var coach = await _api.CreateWorkspace();

        var response = await _api.SendAsGateway(
            new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "POST" ? JsonContent.Create(new { reason = "Mine" }) : null },
            coach.OwnerId, coach.TenantId, TenantRole.Owner);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static bool Is(JsonElement report, Guid programId) =>
        report.GetProperty("programId").ValueKind == JsonValueKind.String && report.GetProperty("programId").GetGuid() == programId;

    private Task<HttpResponseMessage> ReportProgram(Guid reporter, Guid programId, string reason, string? note = null, TenancyApiFactory? api = null) =>
        (api ?? _api).SendAsGateway(new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/reports")
        {
            Content = JsonContent.Create(new { kind = "Program", programId, reason, note }),
        }, reporter);

    private Task<HttpResponseMessage> ReportCoach(Guid reporter, string coachSlug, string reason) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/reports")
        {
            Content = JsonContent.Create(new { kind = "Coach", coachSlug, reason }),
        }, reporter);

    private Task<HttpResponseMessage> Admin(HttpMethod method, string path, object body, TenancyApiFactory? api = null) =>
        (api ?? _api).SendAsGateway(new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) }, _admin, appRoles: AdminRole);

    private async Task<JsonElement> AdminJson(TenancyApiFactory api, string path)
    {
        var response = await api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, path), _admin, appRoles: AdminRole);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
    }

    private async Task<List<JsonElement>> Open() => [.. (await AdminJson(_api, "/api/v1/admin/reports")).GetProperty("open").EnumerateArray()];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
