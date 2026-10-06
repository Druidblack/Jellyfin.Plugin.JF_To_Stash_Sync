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
    private const string ScriptTag = Marker + "<script src=\"../JFToStashSync/PlayerOCounterScript?v=1.7.5.3\" defer></script>";

    public static string TransformIndexHtml(FileTransformationInput? input)
    {
        var html = input?.Contents ?? string.Empty;
        if (string.IsNullOrEmpty(html) || html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
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
