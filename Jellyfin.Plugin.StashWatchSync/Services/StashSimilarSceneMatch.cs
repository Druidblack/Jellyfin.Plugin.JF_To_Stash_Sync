namespace JFToStashSync.Services;

/// <summary>
/// A Stash scene selected by the Stash Similar Scenes scoring algorithm.
/// </summary>
public sealed class StashSimilarSceneMatch
{
    public string SceneId { get; init; } = string.Empty;

    public int Score { get; init; }
}
