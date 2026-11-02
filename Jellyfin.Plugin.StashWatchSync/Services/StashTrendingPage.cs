using System.Collections.Generic;

namespace JFToStashSync.Services;

public sealed record StashTrendingEntry(string StashSceneId, int Rank);

public sealed record StashTrendingPage(int Page, IReadOnlyList<StashTrendingEntry> Entries, bool HasMore);
