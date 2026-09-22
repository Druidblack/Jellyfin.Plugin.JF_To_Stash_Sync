namespace StashWatchSync.Services;

public sealed class JellyfinUrlUpsertResult
{
    public bool Success { get; set; }

    public bool Changed { get; set; }

    public int ReplacedCount { get; set; }

    public int FinalUrlCount { get; set; }

    public string Message { get; set; } = string.Empty;

    public static JellyfinUrlUpsertResult Failure(string message)
        => new()
        {
            Success = false,
            Message = message,
        };

    public static JellyfinUrlUpsertResult SuccessResult(
        bool changed,
        int replacedCount,
        int finalUrlCount,
        string message)
        => new()
        {
            Success = true,
            Changed = changed,
            ReplacedCount = replacedCount,
            FinalUrlCount = finalUrlCount,
            Message = message,
        };
}
