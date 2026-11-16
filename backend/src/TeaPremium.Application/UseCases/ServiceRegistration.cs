using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace TeaPremium.Application.UseCases;

public static class ServiceRegistration
{
    public static IServiceCollection AddUseCasesFrom(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        var types = assembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters).ToArray();
        foreach (var type in types)
        {
            foreach (var contract in type.GetInterfaces().Where(IsValidator))
            {
                services.AddTransient(contract, type);
            }
        }

        var registered = new HashSet<Type>();
        foreach (var type in types)
        {
            foreach (var contract in type.GetInterfaces().Where(IsHandler))
            {
                if (!registered.Add(contract))
                {
                    throw new InvalidOperationException($"Multiple handlers implement {contract.FullName}.");
                }

                services.AddTransient(type);
                var arguments = contract.GenericTypeArguments;
                var command = contract.GetGenericTypeDefinition() == typeof(ICommandHandler<,>);
                services.AddTransient(contract, provider =>
                {
                    var implementation = provider.GetRequiredService(type);
                    if (command)
                    {
                        var transactional = ActivatorUtilities.CreateInstance(provider,
                            typeof(TransactionCommandDecorator<,>).MakeGenericType(arguments), implementation);
                        return ActivatorUtilities.CreateInstance(provider,
                            typeof(ValidationCommandDecorator<,>).MakeGenericType(arguments), transactional);
                    }

                    return ActivatorUtilities.CreateInstance(provider,
                        typeof(ValidationQueryDecorator<,>).MakeGenericType(arguments), implementation);
                });
            }
        }

        return services;
    }

    private static bool IsValidator(Type contract) => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IValidator<>);

    private static bool IsHandler(Type contract) => contract.IsGenericType &&
        (contract.GetGenericTypeDefinition() == typeof(ICommandHandler<,>) || contract.GetGenericTypeDefinition() == typeof(IQueryHandler<,>));
}
