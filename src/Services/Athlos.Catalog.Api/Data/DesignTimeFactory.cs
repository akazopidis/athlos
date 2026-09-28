using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Athlos.Catalog.Api.Data;

// Used only by `dotnet ef migrations add`; it never connects.
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
                .UseNpgsql("Host=localhost;Database=athlos_catalog")
                .UseSnakeCaseNamingConvention()
                .Options,
            new TenantContext());
}
