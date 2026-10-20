using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PaymentsModule.Filters;

/// <summary>Valida con FluentValidation el argumento de tipo <typeparamref name="T"/> y responde 400 antes de entrar a la acción.</summary>
public sealed class ValidationFilter<T>(IValidator<T> validator) : IAsyncActionFilter
    where T : class
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var argument = context.ActionArguments.Values.OfType<T>().FirstOrDefault();
        if (argument is null)
        {
            await next();
            return;
        }

        var result = await validator.ValidateAsync(argument, context.HttpContext.RequestAborted);
        if (result.IsValid)
        {
            await next();
            return;
        }

        context.Result = new BadRequestObjectResult(new ValidationProblemDetails(result.ToDictionary()));
    }
}
