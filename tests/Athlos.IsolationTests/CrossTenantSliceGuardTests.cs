namespace Athlos.IsolationTests;

// Design §3.2: IgnoreQueryFilters() is allowed only in slices explicitly listed as cross-tenant.
// Adding a new use means adding it here, which makes it visible in review.
public sealed class CrossTenantSliceGuardTests
{
    private static readonly string[] AllowedCrossTenantSlices =
    [
        // Public marketplace search and program pages (public-read scope, published only).
        "src/Services/Athlos.Catalog.Api/Features/Marketplace/GetPublishedProgram.cs",
        "src/Services/Athlos.Catalog.Api/Features/Marketplace/SearchPrograms.cs",
        // Checkout's price lookup: the buyer is outside the coach's tenant (published only).
        "src/Services/Athlos.Catalog.Api/Features/Internal/GetProgramOffer.cs",
        // A customer's own orders across coaches.
        "src/Services/Athlos.Ordering.Api/Features/Orders/OrderLists.cs",
        // A user's own memberships across workspaces.
        "src/Services/Athlos.Tenancy.Api/Features/Account/ListMyMemberships.cs",
    ];

    [Fact]
    public void IgnoreQueryFilters_appears_only_in_listed_cross_tenant_slices()
    {
        var root = RepositoryRoot();

        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains(".IgnoreQueryFilters(", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Except(AllowedCrossTenantSlices)
            .ToList();

        Assert.Empty(offenders);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athlos.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Athlos.slnx not found above the test output.");
    }
}
