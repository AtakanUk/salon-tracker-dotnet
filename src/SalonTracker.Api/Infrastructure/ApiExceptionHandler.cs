using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;

namespace SalonTracker.Api.Infrastructure;

/// <summary>Turns every exception into the API's <c>{ "error": code }</c> shape.</summary>
public sealed class ApiExceptionHandler(ErrorLog errorLog, TimeProvider time, ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        switch (exception)
        {
            case ApiException api:
                await ApiErrors.Write(context.Response, api.Status, api.Code);
                return true;

            case ValidationException validation:
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(
                    new
                    {
                        error = "validation",
                        details = validation.Errors.Select(e => new { path = e.PropertyName, message = e.ErrorMessage }),
                    },
                    ct);
                return true;

            // malformed JSON, a string where a number belongs, a missing body
            case BadHttpRequestException bad:
                await ApiErrors.Write(
                    context.Response,
                    bad.StatusCode,
                    bad.StatusCode == StatusCodes.Status413PayloadTooLarge ? "too_large" : "validation");
                return true;
        }

        logger.LogError(exception, "Unhandled error on {Method} {Path}", context.Request.Method, context.Request.Path);
        errorLog.Add(new LoggedError(
            time.GetUtcNow().UtcDateTime.ToString("O"),
            context.Request.Method,
            context.Request.Path + context.Request.QueryString,
            exception.Message));
        await ApiErrors.Write(context.Response, StatusCodes.Status500InternalServerError, "internal");
        return true;
    }
}
