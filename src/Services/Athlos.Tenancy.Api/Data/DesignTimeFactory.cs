using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Athlos.Tenancy.Api.Data;

// Used only by `dotnet ef migrations add`; it never connects.
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TenancyDbContext>()
                .UseNpgsql("Host=localhost;Database=athlos_tenancy")
                .UseSnakeCaseNamingConvention()
                .Options,
            new TenantContext());
}
