using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athlos.Messaging;
using Athlos.Ordering.Api.Data;
using Athlos.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Athlos.Testing;

public static class OrderingArrange
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public sealed record CheckoutStarted(Guid OrderId, string CheckoutUrl);

    public sealed record Coach(Guid TenantId, string Slug, Guid OwnerId);

    public static Coach NewCoach() => new(Guid.CreateVersion7(), TenancyArrange.NewSlug(), Guid.CreateVersion7());

    public static Task<HttpResponseMessage> StartCheckout(this OrderingApiFactory api, Guid customerId, Guid programId, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders/checkout") { Content = JsonContent.Create(new { programId }) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return api.SendAsGateway(request, customerId);
    }

    public static async Task<CheckoutStarted> Checkout(this OrderingApiFactory api, Guid customerId, Guid programId, string? idempotencyKey = null)
    {
        var response = await api.StartCheckout(customerId, programId, idempotencyKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CheckoutStarted>(Json, TestContext.Current.CancellationToken))!;
    }

    // Checkout plus Stripe's "paid" webhook: what a completed purchase looks like to ordering-api.
    public static async Task<Guid> Buy(this OrderingApiFactory api, Guid customerId, Coach coach)
    {
        var offer = api.Sellers.Sell(coach.TenantId, coach.Slug);
        var checkout = await api.Checkout(customerId, offer.ProgramId);
        var paid = await api.SendWebhook(StripeEvents.CheckoutCompleted($"evt_{Guid.NewGuid():N}", $"cs_test_{checkout.OrderId:N}", checkout.OrderId, coach.TenantId));
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        return checkout.OrderId;
    }

    public static Task<HttpResponseMessage> AsCoach(this OrderingApiFactory api, Coach coach, string path) =>
        api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{coach.Slug}{path}"), coach.OwnerId, coach.TenantId, TenantRole.Owner);

    // One pass of the outbox publisher.
    public static Task<int> PublishOutbox(this OrderingApiFactory api) =>
        OutboxDelivery.PublishPendingAsync<OrderingDbContext>(api.Services.GetRequiredService<IServiceScopeFactory>(), TestContext.Current.CancellationToken);
}
