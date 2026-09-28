using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athlos.Tenancy;
using Xunit;

namespace Athlos.Testing;

// Arrange helpers for catalog tests. A "coach" here is just a tenant id, slug and owner, as the
// gateway would forward them; catalog never looks tenants up itself.
public static class CatalogArrange
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public sealed record Coach(Guid TenantId, string Slug, Guid OwnerId);

    public sealed record CreatedProgram(Guid Id, string Slug, string Title, bool IsPublished);

    public static Coach NewCoach() => new(Guid.CreateVersion7(), TenancyArrange.NewSlug(), Guid.CreateVersion7());

    public static Task<HttpResponseMessage> AsCoach(this CatalogApiFactory api, Coach coach, HttpMethod method, string path, object? body = null) =>
        api.SendAsGateway(
            new HttpRequestMessage(method, $"/api/v1/studio/{coach.Slug}{path}") { Content = body is null ? null : JsonContent.Create(body) },
            coach.OwnerId, coach.TenantId, TenantRole.Owner);

    public static async Task<CreatedProgram> CreateProgram(this CatalogApiFactory api, Coach coach, string? title = null, bool publish = false)
    {
        var slug = TenancyArrange.NewSlug("program");
        var response = await api.AsCoach(coach, HttpMethod.Post, "/programs", new
        {
            slug,
            title = title ?? $"Program {slug}",
            description = "Twelve weeks of strength work.",
            level = "Beginner",
            durationWeeks = 12,
            priceAmount = 4900,
            currency = "EUR",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var program = (await response.Content.ReadFromJsonAsync<CreatedProgram>(Json, TestContext.Current.CancellationToken))!;

        if (publish)
        {
            var workout = await api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/workouts",
                new { week = 1, day = 1, title = "Squat day", content = "5x5 squats" });
            Assert.Equal(HttpStatusCode.Created, workout.StatusCode);
            var published = await api.AsCoach(coach, HttpMethod.Post, $"/programs/{program.Id}/publish");
            Assert.Equal(HttpStatusCode.OK, published.StatusCode);
            program = program with { IsPublished = true };
        }

        return program;
    }
}
