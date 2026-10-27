using System.Net;
using Athlos.Catalog.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Athlos.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Athlos.Testing.CatalogArrange;

namespace Athlos.IsolationTests;

// Design §3.3 for catalog-api: tenant B against tenant A's programs and workouts, through every
// studio endpoint (404) and directly in PostgreSQL (RLS alone). Also the marketplace's public-read
// scope: it shows other tenants' published programs and never their drafts or workouts.
public sealed class CatalogIsolationTests(AthlosDatabases databases, AthlosStorage storage) : IAsyncLifetime
{
    private readonly CatalogApiFactory _api = new(databases, storage);
    private readonly Coach _a = NewCoach();
    private readonly Coach _b = NewCoach();
    private CreatedProgram _aDraft = null!;
    private CreatedProgram _aPublished = null!;
    private Guid _aWorkout;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _aDraft = await _api.CreateProgram(_a);
        _aPublished = await _api.CreateProgram(_a, publish: true);
        var workouts = await (await _api.AsCoach(_a, HttpMethod.Get, $"/programs/{_aPublished.Id}/workouts")).Content.ReadAsStringAsync(Cancel);
        _aWorkout = Guid.Parse(System.Text.Json.JsonDocument.Parse(workouts).RootElement[0].GetProperty("id").GetString()!);
    }

    // Every studio endpoint, as tenant B, aimed at tenant A's program or workout.
    public static TheoryData<string, string, bool> StudioEndpoints => new()
    {
        { "GET", "/programs/{draft}", false },
        { "PUT", "/programs/{draft}", true },
        { "DELETE", "/programs/{draft}", false },
        { "POST", "/programs/{draft}/publish", false },
        { "POST", "/programs/{published}/unpublish", false },
        { "GET", "/programs/{published}/workouts", false },
        { "POST", "/programs/{published}/workouts", true },
        { "PUT", "/programs/{published}/workouts/{workout}", true },
        { "DELETE", "/programs/{published}/workouts/{workout}", false },
        { "DELETE", "/programs/{published}/cover", false },
    };

    [Theory]
    [MemberData(nameof(StudioEndpoints))]
    public async Task B_gets_404_for_As_programs_and_workouts(string method, string path, bool withBody)
    {
        var body = withBody
            ? path.Contains("workouts", StringComparison.Ordinal)
                ? new { week = 1, day = 2, title = "Hijacked" }
                : (object)new { title = "Hijacked", category = "CrossFit", durationWeeks = 1, priceAmount = 1000, currency = "EUR" }
            : null;

        var response = await _api.AsCoach(_b, new HttpMethod(method), Resolve(path), body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Layer 1: every catalog-api endpoint refuses a request the gateway did not sign.
    public static TheoryData<string, string> AllEndpoints => new()
    {
        { "GET", "/api/v1/marketplace/programs" },
        { "GET", "/api/v1/marketplace/coaches/some-coach/programs/some-program" },
        { "GET", "/api/v1/studio/some-coach/programs" },
        { "POST", "/api/v1/studio/some-coach/programs" },
        { "GET", $"/api/v1/studio/some-coach/programs/{Guid.Empty}" },
        { "PUT", $"/api/v1/studio/some-coach/programs/{Guid.Empty}" },
        { "DELETE", $"/api/v1/studio/some-coach/programs/{Guid.Empty}" },
        { "POST", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/publish" },
        { "POST", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/unpublish" },
        { "GET", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/workouts" },
        { "POST", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/workouts" },
        { "PUT", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/workouts/{Guid.Empty}" },
        { "DELETE", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/workouts/{Guid.Empty}" },
        { "PUT", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/cover" },
        { "DELETE", $"/api/v1/studio/some-coach/programs/{Guid.Empty}/cover" },
        { "GET", $"/internal/programs/{Guid.Empty}/offer" },
        { "GET", $"/internal/tenants/{Guid.Empty}/programs/{Guid.Empty}/content" },
        { "DELETE", $"/internal/tenants/{Guid.Empty}" },
        { "PUT", $"/internal/tenants/{Guid.Empty}/suspension" },
        { "PUT", $"/internal/tenants/{Guid.Empty}/programs/{Guid.Empty}/block" },
        { "PUT", $"/internal/tenants/{Guid.Empty}/public-discount" },
        { "DELETE", $"/internal/tenants/{Guid.Empty}/public-discount" },
        { "GET", $"/internal/tenants/{Guid.Empty}/export" },
    };

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task Unsigned_requests_are_rejected(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TrustedHeaders.TenantId, _a.TenantId.ToString());
        request.Headers.Add(TrustedHeaders.UserRole, "Owner");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestTokens.For(_a.OwnerId));

        var response = await _api.CreateClient().SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // enrollment-api names the tenant from the customer's enrollment; naming another tenant finds
    // nothing, because catalog reads inside that tenant only.
    [Fact]
    public async Task As_paid_content_is_not_found_under_Bs_tenant()
    {
        var underB = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{_b.TenantId}/programs/{_aPublished.Id}/content"));
        var underA = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{_a.TenantId}/programs/{_aPublished.Id}/content"));

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.OK), (underB.StatusCode, underA.StatusCode));
    }

    // Taking a reported program down (design §2.4): tenancy-api names the tenant from the report;
    // naming B's tenant with A's program finds nothing, and A's program stays on sale.
    [Fact]
    public async Task Blocking_As_program_under_Bs_tenant_finds_nothing()
    {
        var underB = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, $"/internal/tenants/{_b.TenantId}/programs/{_aPublished.Id}/block")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { blocked = true }),
        });

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.OK), (underB.StatusCode, await PublicPage(_a, _aPublished)));
    }

    // Deleting a workspace (design §3.4): one tenant id erases that tenant's programs, workouts and
    // media, and nothing of another tenant's. Counted as the superuser, so RLS cannot hide a leftover.
    [Fact]
    public async Task Erasing_A_removes_its_programs_workouts_and_media_and_nothing_of_Bs()
    {
        Assert.Equal(HttpStatusCode.OK, (await _api.UploadCover(_a, _aPublished.Id, Png())).StatusCode);
        var bProgram = await _api.CreateProgram(_b, publish: true);
        Assert.Equal(HttpStatusCode.OK, (await _api.UploadCover(_b, bProgram.Id, Png())).StatusCode);
        await ShowDiscount(_a.TenantId, "ALPHA");
        await ShowDiscount(_b.TenantId, "BRAVO");

        var erased = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/tenants/{_a.TenantId}"));
        var again = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/tenants/{_a.TenantId}"));

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (erased.StatusCode, again.StatusCode));
        Assert.Equal((0L, 0L), (await RowsOf("programs", _a.TenantId), await RowsOf("workouts", _a.TenantId)));
        Assert.Equal((1L, 1L), (await RowsOf("programs", _b.TenantId), await RowsOf("workouts", _b.TenantId)));
        Assert.Equal((0L, 1L), (await RowsOf("public_discounts", _a.TenantId), await RowsOf("public_discounts", _b.TenantId)));
        Assert.Equal((0, 1), (await BlobsOf(_a.TenantId), await BlobsOf(_b.TenantId)));
    }

    // Suspending a workspace (design §3.4): one tenant id takes that tenant's programs off the
    // marketplace and out of checkout, and nothing of another tenant's. Buyers keep the content.
    [Fact]
    public async Task Suspending_A_hides_only_As_programs_and_buyers_keep_the_content()
    {
        var bProgram = await _api.CreateProgram(_b, publish: true);

        var suspended = await Suspend(_a.TenantId, true);

        Assert.Equal(HttpStatusCode.NoContent, suspended.StatusCode);
        Assert.Equal(
            (HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.OK),
            (await PublicPage(_a, _aPublished), await Offer(_aPublished), await Content(_a, _aPublished)));
        Assert.DoesNotContain(_aPublished.Slug, await Search(_a), StringComparison.Ordinal);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (await PublicPage(_b, bProgram), await Offer(bProgram)));
        Assert.Contains(bProgram.Slug, await Search(_b), StringComparison.Ordinal);

        // Layer 4 alone: the public-read policy hides the suspended coach's published program too.
        var visible = await AsAppRole(_b.TenantId, scope: "public-read", connection =>
            Count(connection, $"SELECT count(*) FROM programs WHERE tenant_id = '{_a.TenantId}'"));
        Assert.Equal(0L, visible);

        Assert.Equal(HttpStatusCode.NoContent, (await Suspend(_a.TenantId, false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await PublicPage(_a, _aPublished));
    }

    private Task<HttpResponseMessage> Suspend(Guid tenantId, bool suspended) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, $"/internal/tenants/{tenantId}/suspension") { Content = System.Net.Http.Json.JsonContent.Create(new { suspended }) });

    private async Task<HttpStatusCode> PublicPage(Coach coach, CreatedProgram program) =>
        (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{coach.Slug}/programs/{program.Slug}"))).StatusCode;

    private async Task<HttpStatusCode> Offer(CreatedProgram program) =>
        (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/programs/{program.Id}/offer"))).StatusCode;

    private async Task<HttpStatusCode> Content(Coach coach, CreatedProgram program) =>
        (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{coach.TenantId}/programs/{program.Id}/content"))).StatusCode;

    private async Task<string> Search(Coach coach) =>
        await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs?coach={coach.Slug}"))).Content.ReadAsStringAsync(Cancel);

    private async Task<long> RowsOf(string table, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("catalog"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE tenant_id = @tenant", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }

    private async Task<int> BlobsOf(Guid tenantId)
    {
        var container = new Azure.Storage.Blobs.BlobContainerClient(storage.ConnectionString, "media");
        var count = 0;
        await foreach (var _ in container.GetBlobsAsync(new Azure.Storage.Blobs.Models.GetBlobsOptions { Prefix = $"tenants/{tenantId}/" }, Cancel))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task B_cannot_upload_a_cover_to_As_program()
    {
        var response = await _api.UploadCover(_b, _aPublished.Id, Png());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Blob paths come from ITenantContext, so a tenant's media always lands under its own prefix,
    // and the link handed out allows reading that one blob, nothing else.
    [Fact]
    public async Task Covers_live_under_the_owning_tenants_prefix_and_links_are_read_only()
    {
        var upload = await _api.UploadCover(_a, _aDraft.Id, Png());
        var url = System.Text.Json.JsonDocument.Parse(await upload.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("coverImageUrl").GetString()!;

        using var web = new HttpClient();
        using var overwrite = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(Png()) };
        overwrite.Headers.Add("x-ms-blob-type", "BlockBlob");
        var write = await web.SendAsync(overwrite, Cancel);
        var delete = await web.DeleteAsync(url, Cancel);

        Assert.Contains($"/tenants/{_a.TenantId}/programs/{_aDraft.Id}/", url, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task As_program_is_unchanged_after_Bs_attempts()
    {
        await _api.AsCoach(_b, HttpMethod.Put, $"/programs/{_aDraft.Id}", new { title = "Hijacked", category = "CrossFit", durationWeeks = 1, priceAmount = 1000, currency = "EUR" });
        await _api.AsCoach(_b, HttpMethod.Delete, $"/programs/{_aDraft.Id}");

        var mine = await _api.AsCoach(_a, HttpMethod.Get, $"/programs/{_aDraft.Id}");
        var body = await mine.Content.ReadAsStringAsync(Cancel);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.DoesNotContain("Hijacked", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bs_program_list_does_not_include_As_programs()
    {
        var body = await (await _api.AsCoach(_b, HttpMethod.Get, "/programs")).Content.ReadAsStringAsync(Cancel);

        Assert.DoesNotContain(_aDraft.Id.ToString(), body, StringComparison.Ordinal);
        Assert.DoesNotContain(_aPublished.Id.ToString(), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_marketplace_shows_As_published_program_but_not_the_draft()
    {
        var body = await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/programs?coach={_a.Slug}")))
            .Content.ReadAsStringAsync(Cancel);

        Assert.Contains(_aPublished.Slug, body, StringComparison.Ordinal);
        Assert.DoesNotContain(_aDraft.Slug, body, StringComparison.Ordinal);
    }

    // Layer 4 alone: raw SQL as catalog's app role, acting as tenant B.
    [Fact]
    public async Task RLS_hides_As_rows_from_B()
    {
        var (programs, workouts) = await AsAppRole(_b.TenantId, scope: "", async connection =>
            (await Count(connection, $"SELECT count(*) FROM programs WHERE tenant_id = '{_a.TenantId}'"),
             await Count(connection, $"SELECT count(*) FROM workouts WHERE tenant_id = '{_a.TenantId}'")));

        Assert.Equal((0, 0), (programs, workouts));
    }

    [Fact]
    public async Task RLS_public_read_shows_published_programs_only_and_never_workouts()
    {
        var (published, drafts, workouts) = await AsAppRole(_b.TenantId, scope: "public-read", async connection =>
            (await Count(connection, $"SELECT count(*) FROM programs WHERE tenant_id = '{_a.TenantId}' AND is_published"),
             await Count(connection, $"SELECT count(*) FROM programs WHERE tenant_id = '{_a.TenantId}' AND NOT is_published"),
             await Count(connection, $"SELECT count(*) FROM workouts WHERE tenant_id = '{_a.TenantId}'")));

        Assert.Equal((1L, 0L, 0L), (published, drafts, workouts));
    }

    // Public-read widens reads only. Found by this test: with one combined policy, UPDATE reached
    // the row (and failed WITH CHECK) and DELETE, which has no WITH CHECK, would have succeeded.
    [Theory]
    [InlineData("UPDATE programs SET title = 'Hijacked' WHERE id = '{0}'")]
    [InlineData("DELETE FROM programs WHERE id = '{0}'")]
    public async Task RLS_public_read_does_not_allow_changing_As_programs(string sql)
    {
        var changed = await AsAppRole(_b.TenantId, scope: "public-read", async connection =>
        {
            await using var command = new NpgsqlCommand(string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, _aPublished.Id), connection);
            return await command.ExecuteNonQueryAsync(Cancel);
        });

        Assert.Equal(0, changed);
    }

    // Layer 3 off: with the EF filter bypassed, RLS still hides A's draft and workouts from B.
    [Fact]
    public async Task With_the_EF_filter_off_RLS_still_hides_As_draft_and_workouts()
    {
        var tenant = new TenantContext();
        tenant.EnterTenant(_b.TenantId, TenantRole.Owner);
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(databases.AppConnectionString("catalog"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor(tenant))
            .Options;

        await using var db = new CatalogDbContext(options, tenant);
        await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
        var programs = await db.Programs.IgnoreQueryFilters().CountAsync(p => p.TenantId == _a.TenantId, Cancel);
        var workouts = await db.Workouts.IgnoreQueryFilters().CountAsync(w => w.TenantId == _a.TenantId, Cancel);

        Assert.Equal((0, 0), (programs, workouts));
    }

    // Layer 3 fully off for writes: bulk update and delete skip both the EF filter (IgnoreQueryFilters)
    // and the SaveChanges guard, and a raw INSERT names tenant A outright. RLS alone still lets B
    // change none of A's rows and refuses the insert (design §3.3, defense in depth).
    [Fact]
    public async Task With_layer_3_off_RLS_still_blocks_Bs_writes_to_A()
    {
        var tenant = new TenantContext();
        tenant.EnterTenant(_b.TenantId, TenantRole.Owner);
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(databases.AppConnectionString("catalog"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor(tenant))
            .Options;

        await using var db = new CatalogDbContext(options, tenant);
        await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
        var renamed = await db.Programs.IgnoreQueryFilters()
            .Where(p => p.TenantId == _a.TenantId)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.Title, "Hijacked"), Cancel);
        var deleted = await db.Workouts.IgnoreQueryFilters()
            .Where(w => w.TenantId == _a.TenantId)
            .ExecuteDeleteAsync(Cancel);
        var insert = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync($"""
            INSERT INTO programs (id, tenant_id, coach_slug, slug, title, description, category, duration_weeks, price_amount, currency, is_published, created_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {_a.TenantId}, {_a.Slug}, 'planted', 'Planted by B', '', 'Powerlifting', 1, 100, 'EUR', true, now(), now())
            """, Cancel));

        Assert.Equal((0, 0), (renamed, deleted));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insert.SqlState);
    }

    // Saved programs (design §2.4) are the customer's own rows: another customer neither sees nor removes
    // them, and only a program on sale can be saved (A's draft is 404).
    [Fact]
    public async Task A_customers_saved_programs_are_theirs_alone()
    {
        var customer = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var saved = await AsCustomer(customer, HttpMethod.Put, $"/saved/{_aPublished.Id}");
        var draft = await AsCustomer(customer, HttpMethod.Put, $"/saved/{_aDraft.Id}");
        await AsCustomer(other, HttpMethod.Delete, $"/saved/{_aPublished.Id}");

        var othersList = await (await AsCustomer(other, HttpMethod.Get, "/saved")).Content.ReadAsStringAsync(Cancel);
        var ownList = await (await AsCustomer(customer, HttpMethod.Get, "/saved")).Content.ReadAsStringAsync(Cancel);

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (saved.StatusCode, draft.StatusCode));
        Assert.Equal("[]", othersList);
        Assert.Contains(_aPublished.Id.ToString(), ownList, StringComparison.Ordinal);
    }

    // Layer 4 alone: as another user, raw SQL reads none of the customer's saved rows and cannot add one in their name.
    [Fact]
    public async Task RLS_shows_saved_programs_to_their_customer_only()
    {
        var customer = Guid.CreateVersion7();
        await AsCustomer(customer, HttpMethod.Put, $"/saved/{_aPublished.Id}");

        var (own, others) = (
            await AsAppRole(_b.TenantId, scope: "public-read", connection => Count(connection, $"SELECT count(*) FROM saved_programs WHERE customer_id = '{customer}'"), customer),
            await AsAppRole(_b.TenantId, scope: "public-read", connection => Count(connection, $"SELECT count(*) FROM saved_programs WHERE customer_id = '{customer}'"), _b.OwnerId));
        var planted = await Assert.ThrowsAsync<PostgresException>(() => AsAppRole(_b.TenantId, scope: "public-read", async connection =>
        {
            await using var command = new NpgsqlCommand(
                $"INSERT INTO saved_programs (id, customer_id, program_id, saved_at) VALUES ('{Guid.CreateVersion7()}', '{customer}', '{_aPublished.Id}', now())", connection);
            return await command.ExecuteNonQueryAsync(Cancel);
        }, _b.OwnerId));

        Assert.Equal((1L, 0L), (own, others));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, planted.SqlState);
    }

    // The rating copy (design §2.4) is written inside the tenant enrollment-api names: naming B for A's
    // program changes nothing.
    [Fact]
    public async Task A_rating_sent_under_Bs_tenant_does_not_change_As_program()
    {
        var wrong = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, $"/internal/tenants/{_b.TenantId}/programs/{_aPublished.Id}/rating")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { count = 9, average = 1.0m }),
        });
        var right = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, $"/internal/tenants/{_a.TenantId}/programs/{_aPublished.Id}/rating")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { count = 2, average = 4.5m }),
        });
        var unsigned = await _api.CreateClient().PutAsync($"/internal/tenants/{_a.TenantId}/programs/{_aPublished.Id}/rating",
            System.Net.Http.Json.JsonContent.Create(new { count = 9, average = 1.0m }), Cancel);

        var page = await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{_a.Slug}/programs/{_aPublished.Slug}")))
            .Content.ReadAsStringAsync(Cancel);

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.Unauthorized), (wrong.StatusCode, right.StatusCode, unsigned.StatusCode));
        Assert.Contains("\"ratingAverage\":4.5", page, StringComparison.Ordinal);
        Assert.Contains("\"ratingCount\":2", page, StringComparison.Ordinal);
    }

    // A workspace export (design §5.4) holds that tenant's programs, drafts and paid content included,
    // and nothing of another tenant's.
    [Fact]
    public async Task As_workspace_export_holds_As_programs_only()
    {
        var bProgram = await _api.CreateProgram(_b, title: "Bravo program", publish: true);

        var export = await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{_a.TenantId}/export"))).Content.ReadAsStringAsync(Cancel);

        Assert.Contains(_aDraft.Id.ToString(), export, StringComparison.Ordinal);
        Assert.Contains(_aPublished.Id.ToString(), export, StringComparison.Ordinal);
        Assert.DoesNotContain(bProgram.Id.ToString(), export, StringComparison.Ordinal);
        Assert.DoesNotContain("Bravo program", export, StringComparison.Ordinal);
    }

    // The shown discount code (design §2.4) is written inside the tenant ordering-api names: a code sent
    // under B shows on B's programs and never on A's.
    [Fact]
    public async Task A_discount_sent_under_Bs_tenant_never_shows_on_As_programs()
    {
        var bProgram = await _api.CreateProgram(_b, publish: true);

        Assert.Equal(HttpStatusCode.NoContent, (await ShowDiscount(_b.TenantId, "BRAVO")).StatusCode);
        var aPage = await ProgramPage(_a, _aPublished.Slug);
        var bPage = await ProgramPage(_b, bProgram.Slug);

        Assert.DoesNotContain("BRAVO", aPage, StringComparison.Ordinal);
        Assert.Contains("\"discount\":null", aPage, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"BRAVO\"", bPage, StringComparison.Ordinal);
    }

    // The shown code is readable by everyone in the public-read scope, but that widening is for SELECT
    // only (a split policy): B cannot change, delete or plant A's code.
    [Theory]
    [InlineData("UPDATE public_discounts SET percent_off = 90 WHERE tenant_id = '{0}'")]
    [InlineData("DELETE FROM public_discounts WHERE tenant_id = '{0}'")]
    public async Task RLS_public_read_does_not_allow_changing_As_shown_discount(string sql)
    {
        await ShowDiscount(_a.TenantId, "ALPHA");

        var visible = await AsAppRole(_b.TenantId, scope: "public-read", connection => Count(connection, $"SELECT count(*) FROM public_discounts WHERE tenant_id = '{_a.TenantId}'"));
        var changed = await AsAppRole(_b.TenantId, scope: "public-read", async connection =>
        {
            await using var command = new NpgsqlCommand(string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, _a.TenantId), connection);
            return await command.ExecuteNonQueryAsync(Cancel);
        });
        var planted = await Assert.ThrowsAsync<PostgresException>(() => AsAppRole(_b.TenantId, scope: "public-read", async connection =>
        {
            await using var command = new NpgsqlCommand($"INSERT INTO public_discounts (tenant_id, code, percent_off) VALUES ('{Guid.CreateVersion7()}', 'FAKE', 90)", connection);
            return await command.ExecuteNonQueryAsync(Cancel);
        }));

        Assert.Equal((1L, 0), (visible, changed));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, planted.SqlState);
    }

    private Task<HttpResponseMessage> ShowDiscount(Guid tenantId, string code) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, $"/internal/tenants/{tenantId}/public-discount")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { code, percentOff = 20 }),
        });

    private async Task<string> ProgramPage(Coach coach, string programSlug) =>
        await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{coach.Slug}/programs/{programSlug}")))
            .Content.ReadAsStringAsync(Cancel);

    // A data export (design §5.4) lists the customer's own saved programs, with titles of programs on sale.
    [Fact]
    public async Task A_customers_export_holds_their_saved_programs_only()
    {
        var (alice, bob) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await AsCustomer(alice, HttpMethod.Put, $"/saved/{_aPublished.Id}");

        var alices = await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/customers/{alice}/export"))).Content.ReadAsStringAsync(Cancel);
        var bobs = await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/customers/{bob}/export"))).Content.ReadAsStringAsync(Cancel);
        var unsigned = await _api.CreateClient().GetAsync($"/internal/customers/{alice}/export", Cancel);

        Assert.Contains(_aPublished.Id.ToString(), alices, StringComparison.Ordinal);
        Assert.Contains(_aPublished.Title, alices, StringComparison.Ordinal);
        Assert.Equal("{\"saved\":[]}", bobs);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    // Deleting an account (design §5.2) erases that customer's saved programs and nobody else's; the
    // endpoint, like every internal one, needs the gateway signature.
    [Fact]
    public async Task Erasing_a_customer_removes_only_their_saved_programs()
    {
        var (alice, bob) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await AsCustomer(alice, HttpMethod.Put, $"/saved/{_aPublished.Id}");
        await AsCustomer(bob, HttpMethod.Put, $"/saved/{_aPublished.Id}");

        var unsigned = await _api.CreateClient().DeleteAsync($"/internal/customers/{alice}", Cancel);
        var erased = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Delete, $"/internal/customers/{alice}"));
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("catalog"));
        await connection.OpenAsync(Cancel);
        var left = (await Count(connection, $"SELECT count(*) FROM saved_programs WHERE customer_id = '{alice}'"),
            await Count(connection, $"SELECT count(*) FROM saved_programs WHERE customer_id = '{bob}'"));

        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.NoContent), (unsigned.StatusCode, erased.StatusCode));
        Assert.Equal((0L, 1L), left);
    }

    private Task<HttpResponseMessage> AsCustomer(Guid customer, HttpMethod method, string path) =>
        _api.SendAsGateway(new HttpRequestMessage(method, $"/api/v1/me{path}"), customer);

    private string Resolve(string path) => path
        .Replace("{draft}", _aDraft.Id.ToString(), StringComparison.Ordinal)
        .Replace("{published}", _aPublished.Id.ToString(), StringComparison.Ordinal)
        .Replace("{workout}", _aWorkout.ToString(), StringComparison.Ordinal);

    private async Task<T> AsAppRole<T>(Guid tenantId, string scope, Func<NpgsqlConnection, Task<T>> act, Guid? userId = null)
    {
        await using var connection = new NpgsqlConnection(databases.AppConnectionString("catalog"));
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using (var set = new NpgsqlCommand(
            $"SELECT set_config('app.tenant_id', '{tenantId}', true), set_config('app.scope', '{scope}', true), set_config('app.user_id', '{userId}', true)", connection))
        {
            await set.ExecuteNonQueryAsync(Cancel);
        }

        return await act(connection);
    }

    private static async Task<long> Count(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
