using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Athlos.Ordering.Api.Data;

// Used only by `dotnet ef migrations add`; it never connects.
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<OrderingDbContext>
{
    public OrderingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OrderingDbContext>()
                .UseNpgsql("Host=localhost;Database=athlos_ordering")
                .UseSnakeCaseNamingConvention()
                .Options,
            new TenantContext());
}
