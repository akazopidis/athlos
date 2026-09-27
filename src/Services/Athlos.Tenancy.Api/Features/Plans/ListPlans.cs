namespace Athlos.Tenancy.Api.Features.Plans;

// Public: the pricing page lists the plans a coach can be on.
public static class ListPlans
{
    public static IEndpointRouteBuilder MapListPlans(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/plans", () => TypedResults.Ok(Plans.All))
            .WithName("ListPlans")
            .WithTags("Plans");

        return app;
    }
}
