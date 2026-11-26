using Mono.Cecil;
using NetArchTest.Rules;

namespace TeaPremium.ArchitectureTests;

/// <summary>
/// 02 §3 / G4 P2-02 carry-over [P2]: inspect compiled IL (including compiler-generated async state machines,
/// closures, MethodSpecification/GenericInstanceMethod, ReturnType and ldtoken/isinst operands) to guarantee
/// TeaPremium.Infrastructure.Crm never touches foreign module persistence entities or foreign infra namespaces.
/// </summary>
internal sealed class CrmModuleBoundaryRule : ICustomRule
{
    private static readonly HashSet<string> AllowedPersistenceTypes = new(StringComparer.Ordinal)
    {
        // Shared persistence infrastructure and append-only audit ledger
        "TeaPremium.Infrastructure.Persistence.AppDbContext",
        "TeaPremium.Infrastructure.Persistence.IAuditContext",
        "TeaPremium.Infrastructure.Persistence.AuditLog",
        // Ten persistence entities owned by schema crm
        "TeaPremium.Infrastructure.Persistence.Customer",
        "TeaPremium.Infrastructure.Persistence.Tag",
        "TeaPremium.Infrastructure.Persistence.CustomerTag",
        "TeaPremium.Infrastructure.Persistence.CustomerAddress",
        "TeaPremium.Infrastructure.Persistence.CustomerAccount",
        "TeaPremium.Infrastructure.Persistence.CustomerLogin",
        "TeaPremium.Infrastructure.Persistence.Interaction",
        "TeaPremium.Infrastructure.Persistence.CustomerInterest",
        "TeaPremium.Infrastructure.Persistence.Consent",
        "TeaPremium.Infrastructure.Persistence.DataRequest"
    };

    public bool MeetsRule(TypeDefinition type)
    {
        if (ContainsForbiddenType(type.BaseType)) return false;
        if (type.Interfaces.Any(i => ContainsForbiddenType(i.InterfaceType))) return false;
        if (HasForbiddenGenericParameters(type.GenericParameters)) return false;
        if (HasForbiddenAttributes(type.CustomAttributes)) return false;

        foreach (var field in type.Fields)
        {
            if (ContainsForbiddenType(field.FieldType) || HasForbiddenAttributes(field.CustomAttributes))
                return false;
        }

        foreach (var property in type.Properties)
        {
            if (ContainsForbiddenType(property.PropertyType) || HasForbiddenAttributes(property.CustomAttributes))
                return false;
        }

        foreach (var ev in type.Events)
        {
            if (ContainsForbiddenType(ev.EventType) || HasForbiddenAttributes(ev.CustomAttributes))
                return false;
        }

        foreach (var method in type.Methods)
        {
            if (HasForbiddenMethod(method)) return false;
        }

        return type.NestedTypes.All(MeetsRule);
    }

    internal static bool IsForbiddenClrType(Type clrType)
        => IsForbiddenTypeName(clrType.FullName ?? clrType.Name, clrType.Namespace ?? "", clrType.Name);

    private static bool HasForbiddenMethod(MethodDefinition method)
    {
        if (ContainsForbiddenType(method.ReturnType)) return true;
        if (method.MethodReturnType.HasCustomAttributes && HasForbiddenAttributes(method.MethodReturnType.CustomAttributes)) return true;
        if (HasForbiddenGenericParameters(method.GenericParameters)) return true;
        if (HasForbiddenAttributes(method.CustomAttributes)) return true;

        foreach (var parameter in method.Parameters)
        {
            if (ContainsForbiddenType(parameter.ParameterType) || HasForbiddenAttributes(parameter.CustomAttributes))
                return true;
        }

        if (!method.HasBody) return false;

        foreach (var variable in method.Body.Variables)
        {
            if (ContainsForbiddenType(variable.VariableType)) return true;
        }

        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (ContainsForbiddenType(handler.CatchType)) return true;
        }

        foreach (var instruction in method.Body.Instructions)
        {
            switch (instruction.Operand)
            {
                case TypeReference typeRef when ContainsForbiddenType(typeRef):
                    return true;
                case MethodReference methodRef when HasForbiddenMethodReference(methodRef):
                    return true;
                case FieldReference fieldRef when ContainsForbiddenType(fieldRef.DeclaringType) || ContainsForbiddenType(fieldRef.FieldType):
                    return true;
                case CallSite callSite when ContainsForbiddenType(callSite.ReturnType)
                    || callSite.Parameters.Any(p => ContainsForbiddenType(p.ParameterType)):
                    return true;
            }
        }

        return false;
    }

    private static bool HasForbiddenMethodReference(MethodReference methodRef)
    {
        if (ContainsForbiddenType(methodRef.DeclaringType) || ContainsForbiddenType(methodRef.ReturnType))
            return true;

        if (methodRef.Parameters.Any(p => ContainsForbiddenType(p.ParameterType)))
            return true;

        if (methodRef is GenericInstanceMethod genericMethod
            && genericMethod.GenericArguments.Any(ContainsForbiddenType))
            return true;

        if (methodRef is MethodSpecification spec && HasForbiddenMethodReference(spec.ElementMethod))
            return true;

        return false;
    }

    private static bool HasForbiddenGenericParameters(Mono.Collections.Generic.Collection<GenericParameter> genericParameters)
    {
        foreach (var parameter in genericParameters)
        {
            if (HasForbiddenAttributes(parameter.CustomAttributes)) return true;
            if (parameter.Constraints.Any(c => ContainsForbiddenType(c.ConstraintType))) return true;
        }

        return false;
    }

    private static bool HasForbiddenAttributes(Mono.Collections.Generic.Collection<CustomAttribute> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (ContainsForbiddenType(attribute.AttributeType)) return true;
            if (attribute.Constructor is not null && HasForbiddenMethodReference(attribute.Constructor)) return true;
            if (attribute.ConstructorArguments.Any(HasForbiddenAttributeArgument)) return true;
            if (attribute.Fields.Any(f => HasForbiddenAttributeArgument(f.Argument))) return true;
            if (attribute.Properties.Any(p => HasForbiddenAttributeArgument(p.Argument))) return true;
        }

        return false;
    }

    private static bool HasForbiddenAttributeArgument(CustomAttributeArgument argument)
    {
        if (ContainsForbiddenType(argument.Type)) return true;
        return argument.Value switch
        {
            TypeReference typeRef => ContainsForbiddenType(typeRef),
            CustomAttributeArgument nested => HasForbiddenAttributeArgument(nested),
            CustomAttributeArgument[] array => array.Any(HasForbiddenAttributeArgument),
            _ => false
        };
    }

    private static bool ContainsForbiddenType(TypeReference? typeRef)
    {
        if (typeRef is null) return false;

        switch (typeRef)
        {
            case GenericInstanceType genericType:
                if (ContainsForbiddenType(genericType.ElementType)) return true;
                return genericType.GenericArguments.Any(ContainsForbiddenType);
            case FunctionPointerType fnPtr:
                return ContainsForbiddenType(fnPtr.ReturnType)
                    || fnPtr.Parameters.Any(p => ContainsForbiddenType(p.ParameterType));
            case RequiredModifierType reqMod:
                return ContainsForbiddenType(reqMod.ModifierType) || ContainsForbiddenType(reqMod.ElementType);
            case OptionalModifierType optMod:
                return ContainsForbiddenType(optMod.ModifierType) || ContainsForbiddenType(optMod.ElementType);
            case TypeSpecification spec:
                return ContainsForbiddenType(spec.ElementType);
        }

        if (typeRef.IsNested && ContainsForbiddenType(typeRef.DeclaringType))
            return true;

        var ns = ResolveRootNamespace(typeRef);
        return IsForbiddenTypeName(typeRef.FullName, ns, typeRef.Name);
    }

    private static string ResolveRootNamespace(TypeReference typeRef)
    {
        var current = typeRef;
        while (current.DeclaringType is not null)
        {
            current = current.DeclaringType;
        }

        return current.Namespace ?? "";
    }

    private static bool IsForbiddenTypeName(string fullName, string ns, string shortName)
    {
        if (ns == "TeaPremium.Infrastructure.Persistence"
            || ns.StartsWith("TeaPremium.Infrastructure.Persistence.", StringComparison.Ordinal))
        {
            return !AllowedPersistenceTypes.Contains(fullName);
        }

        if (ns.StartsWith("TeaPremium.Infrastructure.", StringComparison.Ordinal)
            && ns != "TeaPremium.Infrastructure.Crm"
            && !ns.StartsWith("TeaPremium.Infrastructure.Crm.", StringComparison.Ordinal))
        {
            return true;
        }

        if (fullName == "Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey")
        {
            return true;
        }

        if (ns == "Microsoft.AspNetCore.Identity"
            && (shortName.StartsWith("IdentityUser", StringComparison.Ordinal)
                || shortName.StartsWith("IdentityRole", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }
}
