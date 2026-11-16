using System.Xml.Linq;

namespace TeaPremium.ArchitectureTests;

public sealed class ProjectGraphTests
{
    private static readonly string Backend = FindBackend();

    [Theory]
    [InlineData("Domain", "")]
    [InlineData("Application", "Domain")]
    [InlineData("Infrastructure", "Application,Domain")]
    [InlineData("Api", "Application,Infrastructure")]
    public void SourceProjectReferencesMatchApprovedGraph(string project, string expected)
    {
        var file = Path.Combine(Backend, "src", $"TeaPremium.{project}", $"TeaPremium.{project}.csproj");
        var actual = XDocument.Load(file)
            .Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(((string?)element.Attribute("Include"))?.Replace('\\', '/')))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var expectedNames = expected.Length == 0
            ? Array.Empty<string>()
            : expected.Split(',').Select(name => $"TeaPremium.{name}").OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(expectedNames, actual);
    }

    [Fact]
    public void DomainHasNoPackageReferencesAndApplicationHasNoEfCoreReference()
    {
        var domain = XDocument.Load(Path.Combine(Backend, "src", "TeaPremium.Domain", "TeaPremium.Domain.csproj"));
        var application = XDocument.Load(Path.Combine(Backend, "src", "TeaPremium.Application", "TeaPremium.Application.csproj"));

        Assert.Empty(domain.Descendants("PackageReference"));
        Assert.DoesNotContain(application.Descendants("PackageReference"), package =>
            ((string?)package.Attribute("Include"))?.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) == true);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "FluentValidation", "Microsoft.Extensions.DependencyInjection.Abstractions"
        };
        Assert.All(application.Descendants("PackageReference"), package =>
        {
            var name = (string?)package.Attribute("Include");
            Assert.NotNull(name);
            Assert.Contains(name, allowed);
        });
    }

    [Fact]
    public void DomainAndApplicationDoNotReadSystemClockDirectly()
    {
        foreach (var project in new[] { "Domain", "Application" })
        {
            var root = Path.Combine(Backend, "src", $"TeaPremium.{project}");
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                         .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                             && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                var source = File.ReadAllText(file);
                Assert.DoesNotContain("DateTime.Now", source, StringComparison.Ordinal);
                Assert.DoesNotContain("DateTimeOffset.UtcNow", source, StringComparison.Ordinal);
            }
        }
    }

    private static string FindBackend()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TeaPremium.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Cannot locate backend/TeaPremium.sln.");
    }
}
