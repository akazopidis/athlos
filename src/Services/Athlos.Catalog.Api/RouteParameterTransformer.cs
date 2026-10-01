using System.Text.RegularExpressions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Athlos.Catalog.Api;

// Documents every {parameter} of an operation's route as a required path parameter, including those
// no handler argument binds (studio routes' {tenantSlug}: the gateway resolves it, the handlers read
// the tenant from ITenantContext). Without it the OpenAPI document describes a path the client
// cannot build.
public sealed partial class RouteParameterTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var route = context.Description.RelativePath ?? "";
        foreach (Match match in RouteParameter().Matches(route))
        {
            var name = match.Groups[1].Value;
            operation.Parameters ??= [];
            if (operation.Parameters.Any(p => p.In == ParameterLocation.Path && p.Name == name))
            {
                continue;
            }

            operation.Parameters.Insert(0, new OpenApiParameter
            {
                Name = name,
                In = ParameterLocation.Path,
                Required = true,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }

        return Task.CompletedTask;
    }

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)(?::[^}]*)?\}")]
    private static partial Regex RouteParameter();
}
