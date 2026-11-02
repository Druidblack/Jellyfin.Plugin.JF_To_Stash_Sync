using System;
using Newtonsoft.Json;

namespace JFToStashSync.Web;

public sealed class FileTransformationInput
{
    [JsonProperty("contents")]
    public string Contents { get; set; } = string.Empty;
}

/// <summary>
/// File Transformation callback. It injects one external script reference into Jellyfin Web's index.html.
/// </summary>
public static class WebFileTransformation
{
    private const string Marker = "<!-- JFToStashSync Player O Counter -->";
    private const string ScriptTag = Marker
        + "<script src=\"../JFToStashSync/HomeDataWarmupScript?v=1.2.0.61\" defer></script>"
        + "<script src=\"../JFToStashSync/HomeInitialLoaderScript?v=1.2.0.61\" defer></script>"
        + "<script src=\"../JFToStashSync/PlayerOCounterScript?v=1.2.0.61\" defer></script>"
        + "<script src=\"../JFToStashSync/TrendingHomeScript?v=1.2.0.61\" defer></script>"
        + "<script src=\"../JFToStashSync/PopularityHomeScript?v=1.2.0.61\" defer></script>"
        + "<script src=\"../JFToStashSync/PerformerPopularityHomeScript?v=1.2.0.61\" defer></script>"
        + "<script src=\"../JFToStashSync/BabepediaActorStatsScript?v=1.2.0.61\" defer></script>"
        + "<script src=\"../JFToStashSync/BabepediaTopHomeScript?v=1.2.0.61\" defer></script>"
        + "<script src=\"../JFToStashSync/PornhubActorStatsScript?v=1.2.0.61\" defer></script>"
        + "<script src=\"../JFToStashSync/RankedPerformerFlagsScript?v=1.2.0.61\" defer></script>"
        + "<script src=\"../JFToStashSync/PornhubFeaturedBannerScript?v=1.2.0.61\" defer></script>"
        + "<script src=\"../JFToStashSync/FeaturedMediaBannerScript?v=1.2.0.61\" defer></script>"
        + "<script src=\"../JFToStashSync/PersonDetailEnhancementsScript?v=1.2.0.61\" defer></script>";

    public static string TransformIndexHtml(FileTransformationInput? input)
    {
        var html = input?.Contents ?? string.Empty;
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        var markerIndex = html.IndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            // File Transformation can process HTML that is already injected.
            // Upgrade the existing references instead of keeping an older ?v=
            // URL, which causes the browser to reuse the old Trending script.
            var cursor = markerIndex + Marker.Length;
            for (var i = 0; i < 13; i++)
            {
                while (cursor < html.Length && char.IsWhiteSpace(html[cursor]))
                {
                    cursor++;
                }

                if (!html.AsSpan(cursor).StartsWith("<script src=\"../JFToStashSync/", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var scriptEnd = html.IndexOf("</script>", cursor, StringComparison.OrdinalIgnoreCase);
                if (scriptEnd < 0)
                {
                    break;
                }
                cursor = scriptEnd + "</script>".Length;
            }
            return html.Remove(markerIndex, cursor - markerIndex).Insert(markerIndex, ScriptTag);
        }

        var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyIndex >= 0)
        {
            return html.Insert(bodyIndex, ScriptTag);
        }

        var headIndex = html.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return headIndex >= 0 ? html.Insert(headIndex, ScriptTag) : html;
    }
}
