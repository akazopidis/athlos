using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athlos.Messaging;
using Athlos.Ordering.Api.Data;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Athlos.Testing;

public static class OrderingArrange
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public sealed record CheckoutStarted(Guid CheckoutId, string CheckoutUrl)
    {
        public string SessionId => FakeCheckoutSessions.SessionIdOf(CheckoutId);
    }

    public sealed record Coach(Guid TenantId, string Slug, Guid OwnerId);

    // The Terms version checkout requires (design §5.4).
    public const string TermsVersion = Athlos.Ordering.Api.Features.Checkout.StartCheckout.CurrentTermsVersion;

    public static Coach NewCoach() => new(Guid.CreateVersion7(), TenancyArrange.NewSlug(), Guid.CreateVersion7());

    public static Task<HttpResponseMessage> StartCheckout(this OrderingApiFactory api, Guid customerId, Guid programId, string? idempotencyKey = null) =>
        api.StartCheckout(customerId, [programId], idempotencyKey);

    public static Task<HttpResponseMessage> StartCheckout(this OrderingApiFactory api, Guid customerId, IReadOnlyList<Guid> programIds, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders/checkout") { Content = JsonContent.Create(new { programIds, termsVersion = TermsVersion }) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return api.SendAsGateway(request, customerId);
    }

    public static Task<CheckoutStarted> Checkout(this OrderingApiFactory api, Guid customerId, Guid programId, string? idempotencyKey = null) =>
        api.Checkout(customerId, [programId], idempotencyKey);

    public static async Task<CheckoutStarted> Checkout(this OrderingApiFactory api, Guid customerId, IReadOnlyList<Guid> programIds, string? idempotencyKey = null)
    {
        var response = await api.StartCheckout(customerId, programIds, idempotencyKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CheckoutStarted>(Json, TestContext.Current.CancellationToken))!;
    }

    // The orders a checkout created, as Stripe would name them in the session metadata.
    public static List<Order> OrdersOf(this OrderingApiFactory api, CheckoutStarted checkout) =>
        api.CheckoutSessions.Sessions[checkout.SessionId].Orders.ToList();

    public static IEnumerable<(Guid TenantId, Guid OrderId)> Pairs(this IEnumerable<Order> orders) =>
        orders.Select(o => (o.TenantId, o.Id));

    // Stripe's "paid" webhook for a whole checkout.
    public static Task<HttpResponseMessage> Pay(this OrderingApiFactory api, CheckoutStarted checkout, string? eventId = null) =>
        api.SendWebhook(StripeEvents.CheckoutCompleted(eventId ?? $"evt_{Guid.NewGuid():N}", checkout.SessionId, api.OrdersOf(checkout).Pairs()));

    // Checkout plus Stripe's "paid" webhook: what a completed purchase looks like to ordering-api.
    public static async Task<Guid> Buy(this OrderingApiFactory api, Guid customerId, Coach coach)
    {
        var offer = api.Sellers.Sell(coach.TenantId, coach.Slug);
        var checkout = await api.Checkout(customerId, offer.ProgramId);
        Assert.Equal(HttpStatusCode.OK, (await api.Pay(checkout)).StatusCode);
        return api.OrdersOf(checkout).Single().Id;
    }

    public static Task<HttpResponseMessage> AsCoach(this OrderingApiFactory api, Coach coach, string path) =>
        api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{coach.Slug}{path}"), coach.OwnerId, coach.TenantId, TenantRole.Owner);

    // Every message ordering-api wrote to its outbox, sent or not. Read from the table: test apps share
    // it and each runs a publisher, so which app's sender sends a row is a race, but the row is there.
    public static async Task<List<(string Queue, OutboxMessage Message)>> Outbox(this OrderingApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var rows = await db.OutboxMessages.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        return rows.Select(m => (m.Queue, m)).ToList();
    }

    // One pass of the outbox publisher.
    public static Task<int> PublishOutbox(this OrderingApiFactory api) =>
        OutboxDelivery.PublishPendingAsync<OrderingDbContext>(api.Services.GetRequiredService<IServiceScopeFactory>(), TestContext.Current.CancellationToken);
}
