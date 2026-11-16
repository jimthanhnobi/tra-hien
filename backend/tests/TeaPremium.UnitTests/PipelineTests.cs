using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain;

namespace TeaPremium.UnitTests;

public sealed class PipelineTests
{
    [Fact]
    public async Task ScanRegistersValidationOutsideTransactionAndHandler()
    {
        using var provider = BuildProvider();
        var handler = provider.GetRequiredService<ICommandHandler<SampleCommand, int>>();
        var probe = provider.GetRequiredService<PipelineProbe>();

        var invalid = await handler.Handle(new SampleCommand(0), CancellationToken.None);
        Assert.False(invalid.IsSuccess);
        Assert.Equal(ErrorType.Validation, invalid.Error.Type);
        Assert.Equal("validation_failed", invalid.Error.Code);
        Assert.Equal("validate", string.Join(",", probe.Events));

        probe.Events.Clear();
        var valid = await handler.Handle(new SampleCommand(2), CancellationToken.None);
        Assert.True(valid.IsSuccess);
        Assert.Equal(2, valid.Value);
        Assert.Equal("validate,transaction.begin,handler,transaction.commit", string.Join(",", probe.Events));
    }

    [Fact]
    public async Task DomainExceptionRollsBackThenBecomesBusinessRule()
    {
        using var provider = BuildProvider();
        var handler = provider.GetRequiredService<ICommandHandler<SampleCommand, int>>();
        var probe = provider.GetRequiredService<PipelineProbe>();

        var result = await handler.Handle(new SampleCommand(13), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorType.BusinessRule, result.Error.Type);
        Assert.Equal("rule_failed", result.Error.Code);
        Assert.Equal("validate,transaction.begin,handler,transaction.rollback", string.Join(",", probe.Events));
    }

    [Fact]
    public async Task FailedResultRollsBackWithoutThrowing()
    {
        using var provider = BuildProvider();
        var handler = provider.GetRequiredService<ICommandHandler<SampleCommand, int>>();
        var probe = provider.GetRequiredService<PipelineProbe>();

        var result = await handler.Handle(new SampleCommand(14), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal("concurrency_conflict", result.Error.Code);
        Assert.Equal("validate,transaction.begin,handler,transaction.rollback", string.Join(",", probe.Events));
    }

    [Fact]
    public async Task CanceledValidationDoesNotOpenTransaction()
    {
        using var provider = BuildProvider();
        var handler = provider.GetRequiredService<ICommandHandler<SampleCommand, int>>();
        var probe = provider.GetRequiredService<PipelineProbe>();
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.Handle(new SampleCommand(2), source.Token));
        Assert.Empty(probe.Events);
    }

    [Fact]
    public async Task QueryUsesValidationWithoutUnitOfWork()
    {
        var services = new ServiceCollection();
        services.AddSingleton<PipelineProbe>();
        services.AddUseCasesFrom(typeof(PipelineTests).Assembly);
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IQueryHandler<SampleQuery, int>>();
        var probe = provider.GetRequiredService<PipelineProbe>();

        var result = await handler.Handle(new SampleQuery(4), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value);
        Assert.Equal("query.validate,query.handler", string.Join(",", probe.Events));

        probe.Events.Clear();
        var invalid = await handler.Handle(new SampleQuery(0), CancellationToken.None);
        Assert.False(invalid.IsSuccess);
        Assert.Equal(ErrorType.Validation, invalid.Error.Type);
        Assert.Equal("query.validate", string.Join(",", probe.Events));
    }

    [Fact]
    public void CommandResolutionRequiresRealUnitOfWorkRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<PipelineProbe>();
        services.AddUseCasesFrom(typeof(PipelineTests).Assembly);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICommandHandler<SampleCommand, int>>());
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<PipelineProbe>();
        services.AddTransient<IUnitOfWork, ProbeUnitOfWork>();
        services.AddUseCasesFrom(typeof(PipelineTests).Assembly);
        return services.BuildServiceProvider();
    }

    public sealed record SampleCommand(int Quantity) : ICommand<int>;

    public sealed class SampleCommandValidator : AbstractValidator<SampleCommand>
    {
        public SampleCommandValidator(PipelineProbe probe)
        {
            RuleFor(command => command.Quantity).MustAsync((quantity, ct) =>
            {
                probe.Events.Add("validate");
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(quantity > 0);
            }).WithMessage("Số lượng phải dương.");
        }
    }

    public sealed class SampleCommandHandler(PipelineProbe probe) : ICommandHandler<SampleCommand, int>
    {
        public Task<Result<int>> Handle(SampleCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe.Events.Add("handler");
            if (command.Quantity == 13)
            {
                throw new DomainException("rule_failed", "Quy tắc nghiệp vụ bị từ chối.");
            }

            if (command.Quantity == 14)
            {
                return Task.FromResult(Result.Failure<int>(new Error("concurrency_conflict", "Dữ liệu đã thay đổi.", ErrorType.Conflict)));
            }

            return Task.FromResult(Result.Success(command.Quantity));
        }
    }

    public sealed record SampleQuery(int Value) : IQuery<int>;

    public sealed class SampleQueryValidator : AbstractValidator<SampleQuery>
    {
        public SampleQueryValidator(PipelineProbe probe)
        {
            RuleFor(query => query.Value).Must(value =>
            {
                probe.Events.Add("query.validate");
                return value > 0;
            });
        }
    }

    public sealed class SampleQueryHandler(PipelineProbe probe) : IQueryHandler<SampleQuery, int>
    {
        public Task<Result<int>> Handle(SampleQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe.Events.Add("query.handler");
            return Task.FromResult(Result.Success(query.Value));
        }
    }

    public sealed class PipelineProbe
    {
        public List<string> Events { get; } = [];
    }

    public sealed class ProbeUnitOfWork(PipelineProbe probe) : IUnitOfWork
    {
        public async Task<Result<T>> ExecuteAsync<T>(Func<CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken)
        {
            probe.Events.Add("transaction.begin");
            try
            {
                var result = await operation(cancellationToken);
                probe.Events.Add(result.IsSuccess ? "transaction.commit" : "transaction.rollback");
                return result;
            }
            catch
            {
                probe.Events.Add("transaction.rollback");
                throw;
            }
        }
    }
}
