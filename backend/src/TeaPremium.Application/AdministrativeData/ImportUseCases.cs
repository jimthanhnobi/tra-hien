using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Application.AdministrativeData;

public sealed record PlanAdministrativeImport(ImportBundle Bundle) : IQuery<ImportPlan>;
public sealed record ApplyAdministrativeImport(ImportBundle Bundle, ImportExecution Execution) : ICommand<ImportOutcome>;
public interface IAdministrativeImport
{
    Task<Result<ImportPlan>> PlanAsync(ImportBundle bundle, CancellationToken ct);
    Task<Result<ImportOutcome>> ApplyAsync(ImportBundle bundle, ImportExecution execution, CancellationToken ct);
}
public sealed class ImportHandlers(IAdministrativeImport importer) :
    IQueryHandler<PlanAdministrativeImport, ImportPlan>, ICommandHandler<ApplyAdministrativeImport, ImportOutcome>
{
    public Task<Result<ImportPlan>> Handle(PlanAdministrativeImport query, CancellationToken cancellationToken)
        => importer.PlanAsync(query.Bundle, cancellationToken);
    public Task<Result<ImportOutcome>> Handle(ApplyAdministrativeImport command, CancellationToken cancellationToken)
        => importer.ApplyAsync(command.Bundle, command.Execution, cancellationToken);
}
