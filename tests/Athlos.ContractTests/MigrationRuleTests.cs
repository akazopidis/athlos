using System.Text.RegularExpressions;

namespace Athlos.ContractTests;

// Expand, then contract (design §12.2): a rollback redeploys the previous release's images, but
// migrations only go forward, so the previous release must keep working on the newest schema.
// - A migration may add: tables, nullable columns, and required columns with a default (the previous
//   release inserts rows without them).
// - It may drop or rename a column or table only once no released version uses it, and says so in a
//   "// Contract step:" comment in Up, naming the release that stopped using it. Review checks the claim.
public sealed partial class MigrationRuleTests
{
    // Migrations from before the rule (26 October 2026), checked by hand then.
    private static readonly string[] BeforeTheRule =
    [
        "src/Services/Athlos.Catalog.Api/Data/Migrations/20261016191914_ProgramCategory.cs",
    ];

    public static TheoryData<string> Migrations()
    {
        var root = RepositoryRoot();
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Services"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                     .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal) && !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            data.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Migrations))]
    public void A_migration_keeps_the_previous_release_working(string migration)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), migration));
        var up = text[text.IndexOf("void Up(", StringComparison.Ordinal)..text.IndexOf("void Down(", StringComparison.Ordinal)];
        if (BeforeTheRule.Contains(migration) || up.Contains("// Contract step:", StringComparison.Ordinal))
        {
            return;
        }

        var steps = up.Split("migrationBuilder.").Skip(1).ToList();
        var removing = steps.Where(s => Removing().IsMatch(s)).Select(s => s[..s.IndexOf('(', StringComparison.Ordinal)]);
        var requiredWithoutDefault = steps
            .Where(s => s.StartsWith("AddColumn", StringComparison.Ordinal) && s.Contains("nullable: false", StringComparison.Ordinal)
                && !s.Contains("defaultValue", StringComparison.Ordinal))
            .Select(s => Name().Match(s).Groups[1].Value);

        Assert.Empty(removing);
        Assert.Empty(requiredWithoutDefault);
    }

    [GeneratedRegex(@"^(DropColumn|RenameColumn|DropTable|RenameTable)\b")]
    private static partial Regex Removing();

    [GeneratedRegex("name: \"([^\"]+)\"")]
    private static partial Regex Name();

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
