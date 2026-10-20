using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PaymentsModule.Models.Dtos;
using PaymentsModule.Services;
using PaymentsModule.Validators;

namespace PaymentsModule.Filters;

/// <summary>
/// Ejecuta FluentValidation antes de que la petición entre a la acción del controlador.
/// Solo "fondos insuficientes" responde 422; cualquier otro error de forma responde 400.
/// </summary>
public sealed class CobroRequestValidationFilter(IValidator<CobroRequestDTO> validator) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.ActionArguments.Values.OfType<CobroRequestDTO>().FirstOrDefault();
        if (request is null)
        {
            context.Result = new BadRequestObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Detail = "El cuerpo de la petición es obligatorio.",
            });
            return;
        }

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);
        if (result.IsValid)
        {
            await next();
            return;
        }

        var soloFondosInsuficientes = result.Errors.All(e => e.ErrorCode == CobroRequestDTOValidator.CodigoFondosInsuficientes);
        if (soloFondosInsuficientes)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Unprocessable Entity",
                Detail = CobroMensajes.FondosInsuficientes,
            })
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity,
            };
            return;
        }

        context.Result = new BadRequestObjectResult(new ValidationProblemDetails(result.ToDictionary()));
    }
}
