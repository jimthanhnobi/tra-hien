using Mono.Cecil;
using NetArchTest.Rules;

namespace TeaPremium.ArchitectureTests;

/// <summary>02 §5.1 / AC-P0-05: inspect compiled calls, including generated nested bodies.</summary>
internal sealed class NoDirectClockRule : ICustomRule
{
    public bool MeetsRule(TypeDefinition type)
    {
        foreach (var method in type.Methods.Where(method => method.HasBody))
        {
            foreach (var call in method.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>())
            {
                if (call.DeclaringType.FullName is "System.DateTime" or "System.DateTimeOffset"
                    && call.Name is "get_Now" or "get_UtcNow" or "get_Today") return false;
                if (call.DeclaringType.FullName == "System.TimeProvider"
                    && call.Name is "get_System" or "GetUtcNow" or "GetLocalNow"
                    && !ApprovedTimeProviderBoundary(type, call.Name)) return false;
            }
        }
        return type.NestedTypes.All(MeetsRule);
    }

    private static bool ApprovedTimeProviderBoundary(TypeDefinition type, string member)
        => (type.Module.Assembly.Name.Name, type.FullName, member) switch
        {
            ("TeaPremium.Infrastructure", "TeaPremium.Infrastructure.SystemClock", "GetUtcNow") => true,
            ("TeaPremium.Infrastructure", "TeaPremium.Infrastructure.Persistence.PersistenceRegistration", "get_System") => true,
            ("TeaPremium.Api", "Program", "get_System") => true,
            _ => false
        };
}
