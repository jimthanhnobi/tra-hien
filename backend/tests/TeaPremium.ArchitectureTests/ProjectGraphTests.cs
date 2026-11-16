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
