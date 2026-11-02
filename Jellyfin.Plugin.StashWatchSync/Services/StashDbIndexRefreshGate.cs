using System.Threading;

namespace JFToStashSync.Services;

/// <summary>
/// Serializes the heavy scheduled indexing passes across both ranking modes.
/// Only one job may scan the Jellyfin library at a time.
/// </summary>
internal static class StashDbIndexRefreshGate
{
    internal static readonly SemaphoreSlim Shared = new(1, 1);
}
