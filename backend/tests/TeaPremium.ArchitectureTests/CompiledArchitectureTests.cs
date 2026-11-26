using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NetArchTest.Rules;
using TeaPremium.Application.Time;
using TeaPremium.Domain;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.ArchitectureTests;

public sealed class CompiledArchitectureTests
{
    // P0-10 / 02 §3: check actual type dependencies, alongside the existing direct csproj graph.
    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    [InlineData("Infrastructure")]
    [InlineData("Api")]
    public void CompiledLayersDoNotDependOnForbiddenLayers(string layer)
    {
        var assembly = SourceAssembly(layer);
        // Assembly names also catch references to global-namespace types such as Program.
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => IsForbiddenReference(layer, reference));
        if (layer is "Domain" or "Application")
            Assert.All(assembly.GetReferencedAssemblies(), reference => Assert.True(IsAllowedCoreReference(layer, reference), reference.FullName));
        Assert.NotEmpty(Types.InAssembly(assembly).GetTypes());
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(Forbidden(layer)).GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // AC-P0-05 / 02 §5.1: IL catches aliases/static imports and generated async/lambda bodies.
    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    [InlineData("Infrastructure")]
    [InlineData("Api")]
    public void ProductionTypesUseApprovedClockBoundary(string layer)
    {
        var result = Types.InAssembly(SourceAssembly(layer)).Should().MeetCustomRule(new NoDirectClockRule()).GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [InlineData("Application", "DependsOnEf")]
    [InlineData("Application", "DependsOnNpgsql")]
    [InlineData("Application", "DependsOnInfrastructure")]
    [InlineData("Infrastructure", "DependsOnApi")]
    [InlineData("Domain", "DependsOnApplication")]
    public void DependencyRuleRejectsDeliberateCompiledViolation(string layer, string sample)
    {
        var result = Sample(sample).ShouldNot().HaveDependencyOnAny(Forbidden(layer)).GetResult();
        Assert.False(result.IsSuccessful);
        Assert.Contains(result.FailingTypes, type => type.Name == sample);
    }

    [Theory]
    [InlineData("Domain", "Application")]
    [InlineData("Application", "Infrastructure")]
    [InlineData("Infrastructure", "Api")]
    public void AssemblyReferenceCheckRejectsUpwardAssemblyIncludingGlobalTypes(string layer, string dependency)
        => Assert.True(IsForbiddenReference(layer, SourceAssembly(dependency).GetName()));

    [Theory]
    [InlineData("Domain", typeof(IClock))]
    [InlineData("Domain", typeof(Types))]
    [InlineData("Application", typeof(Microsoft.EntityFrameworkCore.DbContext))]
    [InlineData("Application", typeof(Npgsql.NpgsqlConnection))]
    public void CoreAssemblyAllowlistRejectsRealNonApprovedReferences(string layer, Type dependency)
        => Assert.False(IsAllowedCoreReference(layer, dependency.Assembly.GetName()));

    [Theory]
    [InlineData("LocalDateTime")]
    [InlineData("UtcDateTime")]
    [InlineData("TodayDateTime")]
    [InlineData("OffsetClock")]
    [InlineData("StaticImportClock")]
    [InlineData("AsyncClock")]
    [InlineData("LambdaClock")]
    [InlineData("SystemProviderClock")]
    public void ClockRuleRejectsDeliberateCompiledViolation(string sample)
    {
        var result = Sample(sample).Should().MeetCustomRule(new NoDirectClockRule()).GetResult();
        Assert.False(result.IsSuccessful);
        Assert.Contains(result.FailingTypes, type => type.Name == sample);
    }

    [Fact]
    public void SameRulesAcceptInjectedClockAndBclDomainValue()
    {
        Assert.True(Sample("InjectedClock").Should().MeetCustomRule(new NoDirectClockRule()).GetResult().IsSuccessful);
        Assert.True(Sample("DomainValue").ShouldNot().HaveDependencyOnAny(Forbidden("Domain")).GetResult().IsSuccessful);
    }

    // P2-03 / G4 P2-02 carry-over [P2]: compiled IL check enforcing CRM module boundary across async state machines and closures.
    [Fact]
    public void CrmInfrastructureDoesNotTouchForeignModulePersistenceEntitiesOrInfraNamespaces()
    {
        var crmTypes = Types.InAssembly(SourceAssembly("Infrastructure")).That()
            .ResideInNamespace("TeaPremium.Infrastructure.Crm");
        Assert.NotEmpty(crmTypes.GetTypes());

        var result = crmTypes.Should().MeetCustomRule(new CrmModuleBoundaryRule()).GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));

        // Verify parity against every EF Core entity registered in AppDbContext (including Reservation and future modules).
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=teapremium_arch_model_only")
            .Options;
        using var db = new AppDbContext(options);
        var foreignEntities = db.Model.GetEntityTypes()
            .Where(e => !string.Equals(e.GetSchema(), "crm", StringComparison.Ordinal)
                && !(string.Equals(e.GetSchema(), "platform", StringComparison.Ordinal)
                     && string.Equals(e.GetTableName(), "audit_logs", StringComparison.Ordinal)))
            .Select(e => e.ClrType)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(foreignEntities);
        Assert.All(foreignEntities, entityType =>
            Assert.True(CrmModuleBoundaryRule.IsForbiddenClrType(entityType),
                $"Foreign EF entity {entityType.FullName} must be rejected by CrmModuleBoundaryRule."));
    }

    [Theory]
    [InlineData("CrmDirectDbSetPropertySample")]
    [InlineData("CrmGenericSetMethodSample")]
    [InlineData("CrmAsyncMoveNextLinqSample")]
    [InlineData("CrmLambdaClosureSample")]
    [InlineData("CrmTypeCheckAndTokenSample")]
    [InlineData("CrmForeignModuleInfraSample")]
    [InlineData("CrmReturnAttributeSample")]
    public void CrmModuleBoundaryRuleRejectsDeliberateCompiledViolations(string sample)
    {
        var result = Sample(sample).Should().MeetCustomRule(new CrmModuleBoundaryRule()).GetResult();
        Assert.False(result.IsSuccessful);
        Assert.Contains(result.FailingTypes, type => type.Name == sample);
    }

    [Fact]
    public void CrmModuleBoundaryRuleAcceptsOwnCrmEntitiesAndAuditLog()
    {
        var result = Sample("CrmAllowedOwnEntitySample").Should().MeetCustomRule(new CrmModuleBoundaryRule()).GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static PredicateList Sample(string name)
    {
        // Select real compiled fixtures, not a manufactured successful TestResult.
        var selected = Types.InAssembly(typeof(CompiledArchitectureTests).Assembly).That()
            .ResideInNamespace("TeaPremium.ArchitectureTests.Samples").And().HaveName(name);
        Assert.Single(selected.GetTypes());
        return selected;
    }

    private static string[] Forbidden(string layer) => layer switch
    {
        "Domain" => ["TeaPremium.Application", "TeaPremium.Infrastructure", "TeaPremium.Api", "Microsoft.EntityFrameworkCore", "Npgsql", "FluentValidation", "Microsoft.Extensions", "TeaPremium.UnitTests", "TeaPremium.IntegrationTests"],
        "Application" => ["TeaPremium.Infrastructure", "TeaPremium.Api", "Microsoft.EntityFrameworkCore", "Npgsql", "TeaPremium.IntegrationTests"],
        "Infrastructure" => ["TeaPremium.Api", "TeaPremium.IntegrationTests"],
        "Api" => ["TeaPremium.UnitTests", "TeaPremium.IntegrationTests", "TeaPremium.ArchitectureTests"],
        _ => throw new ArgumentOutOfRangeException(nameof(layer))
    };

    private static bool IsForbiddenReference(string layer, AssemblyName reference)
        => Forbidden(layer).Any(prefix => reference.Name == prefix
            || reference.Name?.StartsWith(prefix + ".", StringComparison.Ordinal) == true);

    private static bool IsAllowedCoreReference(string layer, AssemblyName reference)
    {
        var name = reference.Name ?? "";
        if (name is "mscorlib" or "netstandard" or "System" or "Microsoft.CSharp"
            || name.StartsWith("System.", StringComparison.Ordinal)) return true;
        return layer == "Application" && (name is "TeaPremium.Domain" or "FluentValidation"
            || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) && name.EndsWith(".Abstractions", StringComparison.Ordinal));
    }

    private static Assembly SourceAssembly(string layer) => layer switch
    {
        "Domain" => typeof(Money).Assembly,
        "Application" => typeof(IClock).Assembly,
        "Infrastructure" => typeof(AppDbContext).Assembly,
        "Api" => typeof(Program).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(layer))
    };
}
