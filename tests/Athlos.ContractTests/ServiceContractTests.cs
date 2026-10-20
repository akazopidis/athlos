using System.Net;
using System.Net.Http.Json;
using Athlos.Catalog.Api.Features.Programs;
using Athlos.Catalog.Api.Features.Workouts;
using Athlos.Enrollment.Api.Features.MyPrograms;
using Athlos.Enrollment.Api.Features.Reviews;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Features.Account;
using Athlos.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Athlos.ContractTests;

// Contracts between the services (design §15.1): each service's real client code calls the real
// endpoint of the service it depends on, signed as in production. A renamed route, field or status
// breaks a test here instead of a deploy. Every signed internal call is covered:
//   ordering   -> catalog (offer), tenancy (payout account, membership)
//   enrollment -> catalog (content, on sale, rating)
//   catalog    -> tenancy (payout readiness, plan quota), enrollment (program updated)
//   tenancy    -> ordering, enrollment, catalog (customer export and erasure)
public sealed class ServiceContractTests(AthlosDatabases databases, AthlosStorage storage) : IAsyncLifetime
{
    private readonly TenancyApiFactory _tenancy = new(databases, storage: storage);
    private readonly CatalogApiFactory _catalog = new(databases, storage);
    private readonly OrderingApiFactory _ordering = new(databases);
    private readonly EnrollmentApiFactory _enrollment = new(databases);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ordering_reads_catalogs_offer_and_tenancys_payout_account_and_membership()
    {
        var workspace = await _tenancy.CreateWorkspace();
        var coach = new CatalogArrange.Coach(workspace.TenantId, workspace.Slug, workspace.OwnerId);
        var program = await _catalog.CreateProgram(coach, publish: true);
        var sellers = new SellerDirectory(Clients((SellerDirectory.Catalog, _catalog), (SellerDirectory.Tenancy, _tenancy)));

        var offer = await sellers.GetOfferAsync(program.Id, Cancel);
        var missing = await sellers.GetOfferAsync(Guid.CreateVersion7(), Cancel);
        var account = await sellers.GetPayoutAccountAsync(workspace.TenantId, Cancel);
        var owner = await sellers.IsMemberAsync(workspace.Slug, workspace.OwnerId, Cancel);
        var stranger = await sellers.IsMemberAsync(workspace.Slug, Guid.CreateVersion7(), Cancel);

        Assert.Equal((program.Id, workspace.TenantId, workspace.Slug, 4900L, "EUR"), (offer!.ProgramId, offer.TenantId, offer.CoachSlug, offer.PriceAmount, offer.Currency));
        Assert.Null(missing);
        Assert.Equal((workspace.TenantId, true, false), (account!.TenantId, account.Active, account.OnboardingComplete));
        Assert.Equal((true, false), (owner, stranger));
    }

    [Fact]
    public async Task Enrollment_reads_catalogs_content_and_offer_and_sets_the_rating()
    {
        var coach = CatalogArrange.NewCoach();
        var program = await _catalog.CreateProgram(coach, publish: true);
        var catalog = Signed(_catalog);

        var content = await new CatalogProgramContent(catalog).GetAsync(coach.TenantId, program.Id, Cancel);
        var otherTenant = await new CatalogProgramContent(catalog).GetAsync(Guid.CreateVersion7(), program.Id, Cancel);
        var onSale = await new CatalogProgramContent(catalog).GetOnSaleAsync(program.Id, Cancel);
        await new CatalogProgramRatings(catalog).SetAsync(coach.TenantId, program.Id, 3, 4.33m, Cancel);
        var page = await (await _catalog.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{coach.Slug}/programs/{program.Slug}")))
            .Content.ReadFromJsonAsync<RatedProgram>(Cancel);

        Assert.Equal((program.Id, coach.Slug, "Squat day"), (content!.ProgramId, content.CoachSlug, Assert.Single(content.Workouts).Title));
        Assert.Null(otherTenant);
        Assert.Equal((coach.TenantId, coach.Slug, program.Title), (onSale!.TenantId, onSale.CoachSlug, onSale.Title));
        Assert.Equal((4.33m, 3), (page!.RatingAverage, page.RatingCount));
    }

    [Fact]
    public async Task Catalog_reads_tenancys_payout_readiness_and_plan()
    {
        var workspace = await _tenancy.CreateWorkspace();
        var tenancy = Signed(_tenancy);

        var canSell = await new TenancyPayoutReadiness(tenancy).CanSellAsync(workspace.TenantId, Cancel);
        var maxPrograms = await new TenancyProgramQuota(tenancy).MaxProgramsAsync(workspace.Slug, workspace.TenantId, Cancel);
        var wrongTenant = await new TenancyProgramQuota(tenancy).MaxProgramsAsync(workspace.Slug, Guid.CreateVersion7(), Cancel);

        Assert.False(canSell); // No Stripe onboarding yet.
        Assert.True(maxPrograms > 0);
        Assert.Null(wrongTenant);
    }

    [Fact]
    public async Task Catalog_tells_enrollment_a_program_has_a_new_workout()
    {
        var coach = OrderingArrange.NewCoach();
        var customer = Guid.CreateVersion7();
        var program = await _enrollment.Enroll(customer, coach);

        await new EnrollmentProgramUpdates(Signed(_enrollment), NullLogger<EnrollmentProgramUpdates>.Instance)
            .WorkoutAddedAsync(coach.TenantId, program.ProgramId, "Bonus day", Cancel);

        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("enrollment"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM enrollments WHERE program_id = @p AND update_emailed_at IS NOT NULL", connection);
        command.Parameters.AddWithValue("p", program.ProgramId);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(Cancel))!);
    }

    [Fact]
    public async Task Tenancy_exports_and_erases_a_customer_in_every_service()
    {
        var customer = Guid.CreateVersion7();
        var clients = Clients(
            (ServiceCustomerErasure.Ordering, _ordering), (ServiceCustomerErasure.Enrollment, _enrollment), (ServiceCustomerErasure.Catalog, _catalog));
        var program = await _enrollment.Enroll(customer, OrderingArrange.NewCoach());

        var export = await new ServiceCustomerExport(clients).GetAsync(customer, Cancel);
        await new ServiceCustomerErasure(clients).EraseCustomerAsync(customer, Cancel);
        var afterErasure = await new ServiceCustomerExport(clients).GetAsync(customer, Cancel);

        Assert.Equal(program.ProgramId, export.Programs.GetProperty("programs")[0].GetProperty("programId").GetGuid());
        Assert.Equal(0, export.Orders.GetProperty("orders").GetArrayLength());
        Assert.Equal(0, export.Saved.GetProperty("saved").GetArrayLength());
        Assert.Equal(0, afterErasure.Programs.GetProperty("programs").GetArrayLength());
    }

    // An HttpClient that signs like AddSignedRequests, against a service under test.
    private static HttpClient Signed<T>(WebApplicationFactory<T> service)
        where T : class =>
        service.CreateDefaultClient(new SignedRequestHandler(TestGateway.Signer(TimeProvider.System)));

    private static NamedClients Clients(params (string Name, object Service)[] services) =>
        new NamedClients(services.ToDictionary(s => s.Name, s => s.Service switch
        {
            TenancyApiFactory t => Signed(t),
            CatalogApiFactory c => Signed(c),
            OrderingApiFactory o => Signed(o),
            EnrollmentApiFactory e => Signed(e),
            _ => throw new ArgumentException("Unknown service", nameof(services)),
        }));

    private sealed class NamedClients(Dictionary<string, HttpClient> clients) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => clients[name];
    }

    private sealed record RatedProgram(decimal RatingAverage, int RatingCount);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _tenancy.DisposeAsync();
        await _catalog.DisposeAsync();
        await _ordering.DisposeAsync();
        await _enrollment.DisposeAsync();
    }
}
