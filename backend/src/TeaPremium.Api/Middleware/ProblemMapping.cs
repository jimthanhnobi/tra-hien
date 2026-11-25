using Microsoft.AspNetCore.Mvc;
using TeaPremium.Application.Results;

namespace TeaPremium.Api.Middleware;

public static class ProblemMapping
{
    private static readonly HashSet<string> ConflictBusinessCodes = new(StringComparer.Ordinal)
    {
        "invalid_status_transition", "out_of_stock", "price_changed", "customer_phone_exists",
        "account_exists", "unique_piece_violation", "cart_has_unavailable_items"
    };

    public static IResult ToProblem(this Error error, HttpContext context)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.BusinessRule when ConflictBusinessCodes.Contains(error.Code) => StatusCodes.Status409Conflict,
            ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
            _ => throw new ArgumentOutOfRangeException(nameof(error))
        };

        var problem = new ProblemDetails
        {
            Type = $"https://teapremium/errors/{error.Code}",
            Title = error.Message,
            Status = status,
            Detail = error.Message
        };
        problem.Extensions["code"] = error.Code;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        if (error.ValidationErrors is not null)
        {
            problem.Extensions["errors"] = error.ValidationErrors;
        }
        if (error.Data is not null)
        {
            problem.Extensions["data"] = error.Data;
        }

        return Results.Problem(problem);
    }
}
