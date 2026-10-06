namespace JFToStashSync.Services;

public sealed class StashConnectionTestResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public string Endpoint { get; init; } = string.Empty;

    public int? HttpStatusCode { get; init; }

    public long? ElapsedMilliseconds { get; init; }

    public static StashConnectionTestResult Ok(string endpoint, long elapsedMilliseconds)
        => new()
        {
            Success = true,
            Message = "Connection to Stash succeeded.",
            Endpoint = endpoint,
            HttpStatusCode = 200,
            ElapsedMilliseconds = elapsedMilliseconds
        };

    public static StashConnectionTestResult Fail(
        string message,
        string endpoint = "",
        int? httpStatusCode = null,
        long? elapsedMilliseconds = null)
        => new()
        {
            Success = false,
            Message = message,
            Endpoint = endpoint,
            HttpStatusCode = httpStatusCode,
            ElapsedMilliseconds = elapsedMilliseconds
        };
}
