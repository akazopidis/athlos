using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Athlos.Enrollment.Api.Data;

// Used only by `dotnet ef migrations add`; it never connects.
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<EnrollmentDbContext>
{
    public EnrollmentDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<EnrollmentDbContext>()
                .UseNpgsql("Host=localhost;Database=athlos_enrollment")
                .UseSnakeCaseNamingConvention()
                .Options,
            new TenantContext());
}
