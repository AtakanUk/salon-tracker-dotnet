namespace SalonTracker.Api.Infrastructure;

/// <summary>
/// An expected failure with a stable code. The response is <c>{ "error": code }</c>;
/// the web app maps every code to a translated message.
/// </summary>
public sealed class ApiException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static ApiException NotFound() => new(StatusCodes.Status404NotFound, "not_found");

    public static ApiException Validation() => new(StatusCodes.Status400BadRequest, "validation");
}

public sealed record ErrorBody(string Error);

public static class ApiErrors
{
    public static Task Write(HttpResponse response, int status, string code)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new ErrorBody(code));
    }
}
