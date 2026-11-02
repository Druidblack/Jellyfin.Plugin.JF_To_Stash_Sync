using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace JFToStashSync.Services;

/// <summary>Manual-only lookup result. A preview never modifies Jellyfin images.</summary>
public sealed class PornhubArtworkInspection
{
    public string PersonId { get; set; } = string.Empty;
    public string PersonName { get; set; } = string.Empty;
    public string ProfileUrl { get; set; } = string.Empty;
    public string LinkSource { get; set; } = string.Empty;
    public bool HasLogo { get; set; }
    public bool HasBanner { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class PornhubArtworkApplyResult
{
    public string PersonId { get; set; } = string.Empty;
    public bool LogoSaved { get; set; }
    public bool BannerSaved { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Fetches two image types from Pornhub profiles, using a validated Stash URL first.
/// Manual preview and scheduled artwork retrieval, both using exact-profile resolution and no guessed fuzzy matches.
/// Images are previewed by the administrator before explicitly saving them to Person Logo/Banner.
/// </summary>
public sealed class PornhubActorArtworkService
{
    private static readonly SemaphoreSlim NetworkGate = new(1, 1);
    private static DateTime _nextRequestUtc = DateTime.MinValue;
    private static DateTime _blockedUntilUtc = DateTime.MinValue;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PreviewTtl = TimeSpan.FromMinutes(20);
    private const int MaxHtmlChars = 2_000_000;
    private const int MaxImageBytes = 6 * 1024 * 1024;
    private readonly ILibraryManager _library;
    private readonly IProviderManager _images;
    private readonly StashClient _stash;
    private readonly ILogger<PornhubActorArtworkService> _logger;
    private readonly HttpClient _http;
    private readonly object _previewLock = new();
    private ArtworkPreview? _lastPreview;

    private sealed class ArtworkPreview
    {
        public string PersonId { get; init; } = string.Empty;
        public PornhubArtworkInspection Result { get; init; } = new();
        public (byte[] Data, string Mime)? Logo { get; init; }
        public (byte[] Data, string Mime)? Banner { get; init; }
        public DateTime ExpiresUtc { get; init; }
    }

    public PornhubActorArtworkService(ILibraryManager library, IProviderManager images,
        StashClient stash, ILogger<PornhubActorArtworkService> logger)
    {
        _library = library;
        _images = images;
        _stash = stash;
        _logger = logger;
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        }) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public async Task<PornhubArtworkInspection> InspectAsync(string personId, CancellationToken ct)
    {
        var person = GetPerson(personId);
        var key = person.Id.ToString("N");
        await NetworkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow < _blockedUntilUtc)
                throw new InvalidOperationException("Pornhub has temporarily limited requests. Try again later.");

            var (profileUrl, source) = await ResolveProfileAsync(person, ct).ConfigureAwait(false);
            var html = await GetTextAsync(profileUrl, ct).ConfigureAwait(false);
            var logoUrl = ExtractImageUrl(html, "getAvatar");
            var bannerUrl = ExtractImageUrl(html, "coverPictureDefault");
            if (logoUrl is null && bannerUrl is null)
                throw new InvalidOperationException("Pornhub profile was found, but neither #getAvatar nor #coverPictureDefault returned a valid image URL. Nothing was changed.");

            (byte[] Data, string Mime)? logo = null;
            (byte[] Data, string Mime)? banner = null;
            // Inspect returns only URLs of previews hosted by Jellyfin, never Pornhub CDN URLs.
            if (logoUrl is not null) logo = await GetImageAsync(logoUrl, ct).ConfigureAwait(false);
            if (bannerUrl is not null) banner = await GetImageAsync(bannerUrl, ct).ConfigureAwait(false);

            var result = new PornhubArtworkInspection
            {
                PersonId = key,
                PersonName = person.Name ?? string.Empty,
                ProfileUrl = profileUrl,
                LinkSource = source,
                HasLogo = logo is not null,
                HasBanner = banner is not null,
                Message = "Preview ready. Images have not been saved to Jellyfin."
            };
            lock (_previewLock)
            {
                // One bounded manual inspection in memory, not a cache of every Jellyfin Person.
                _lastPreview = new ArtworkPreview
                {
                    PersonId = key,
                    Result = result,
                    Logo = logo,
                    Banner = banner,
                    ExpiresUtc = DateTime.UtcNow.Add(PreviewTtl)
                };
            }
            _logger.LogDebug("JFToStashSync: manual Pornhub artwork preview for {PersonId} completed.", key);
            return result;
        }
        finally { NetworkGate.Release(); }
    }

    /// <summary>Returns true when the source or its image CDN is temporarily blocking requests.</summary>
    public bool IsSiteBlocked(out DateTime retryAtUtc)
    {
        retryAtUtc = _blockedUntilUtc;
        return retryAtUtc > DateTime.UtcNow;
    }

    /// <summary>
    /// Scheduled artwork refresh. The caller selects only missing/expired image types and
    /// records each successful write immediately; never buffers images for other actors.
    /// Manual preview and explicit apply remain unchanged.
    /// </summary>
    public async Task<(bool LogoSaved, bool BannerSaved)> RefreshScheduledArtworkAsync(
        string personId, bool downloadLogo, bool downloadBanner,
        Func<ImageType, Task> onSaved, CancellationToken ct)
    {
        if (!downloadLogo && !downloadBanner) return (false, false);
        var person = GetPerson(personId);
        await NetworkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow < _blockedUntilUtc)
                throw new InvalidOperationException("Pornhub temporarily limited requests. Try again later.");
            var (profileUrl, _) = await ResolveProfileAsync(person, ct).ConfigureAwait(false);
            var html = await GetTextAsync(profileUrl, ct).ConfigureAwait(false);
            var logoSaved = false;
            var bannerSaved = false;
            if (downloadLogo && ExtractImageUrl(html, "getAvatar") is { } logoUrl)
            {
                var image = await GetImageAsync(logoUrl, ct).ConfigureAwait(false);
                await SaveAndPersistImageAsync(person, ImageType.Logo, image.Data, image.Mime, ct).ConfigureAwait(false);
                await onSaved(ImageType.Logo).ConfigureAwait(false);
                logoSaved = true;
            }
            if (downloadBanner && ExtractImageUrl(html, "coverPictureDefault") is { } bannerUrl)
            {
                var image = await GetImageAsync(bannerUrl, ct).ConfigureAwait(false);
                await SaveAndPersistImageAsync(person, ImageType.Banner, image.Data, image.Mime, ct).ConfigureAwait(false);
                await onSaved(ImageType.Banner).ConfigureAwait(false);
                bannerSaved = true;
            }
            if (!logoSaved && !bannerSaved)
                throw new InvalidOperationException("Requested artwork types were not found in the Pornhub profile.");
            return (logoSaved, bannerSaved);
        }
        finally { NetworkGate.Release(); }
    }

    /// <summary>Reuse the verified Stash URL / exact-name lookup and shared network pacing for statistics.</summary>
    internal async Task<(string Url, string Html, string PersonName)> FetchProfileHtmlForStatsAsync(string personId, CancellationToken ct)
    {
        var person = GetPerson(personId);
        await NetworkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow < _blockedUntilUtc)
                throw new InvalidOperationException("Pornhub temporarily limited requests. Try again later.");
            var (url, _) = await ResolveProfileAsync(person, ct).ConfigureAwait(false);
            return (url, await GetTextAsync(url, ct).ConfigureAwait(false), person.Name ?? string.Empty);
        }
        finally { NetworkGate.Release(); }
    }

    private async Task<(string Url, string Source)> ResolveProfileAsync(Person person, CancellationToken ct)
    {
        string? profileUrl = null;
        var source = "Stash";
        if (person.ProviderIds is not null && person.ProviderIds.TryGetValue("Stash", out var stashId) &&
            !string.IsNullOrWhiteSpace(stashId))
        {
            var urls = await _stash.GetPerformerExternalUrlsAsync(stashId, ct).ConfigureAwait(false);
            if (urls is null)
                throw new InvalidOperationException("Cannot read actor profile URLs from Stash. Verify the Stash connection.");
            profileUrl = urls.Select(NormalizeProfileUrl).FirstOrDefault(u => u is not null);
        }
        if (profileUrl is null)
        {
            source = "Exact-name search";
            profileUrl = await SearchByNameAsync(person.Name ?? string.Empty, ct).ConfigureAwait(false);
        }
        return (profileUrl, source);
    }

    public (byte[] Data, string Mime)? GetPreview(string personId, string kind)
    {
        if (!Guid.TryParse(personId, out var guid)) return null;
        lock (_previewLock)
        {
            if (_lastPreview is null || _lastPreview.ExpiresUtc <= DateTime.UtcNow ||
                !_lastPreview.PersonId.Equals(guid.ToString("N"), StringComparison.OrdinalIgnoreCase)) return null;
            return kind.ToLowerInvariant() switch
            {
                "logo" => _lastPreview.Logo,
                "banner" => _lastPreview.Banner,
                _ => null
            };
        }
    }

    public async Task<PornhubArtworkApplyResult> ApplyAsync(string personId, bool saveLogo, bool saveBanner, CancellationToken ct)
    {
        if (!saveLogo && !saveBanner) throw new ArgumentException("Choose at least one image to save.");
        var person = GetPerson(personId);
        // Hold the same gate used by InspectAsync while checking the selected preview and writing.
        await NetworkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ArtworkPreview? preview;
            lock (_previewLock)
            {
                preview = _lastPreview;
                if (preview is null || preview.ExpiresUtc <= DateTime.UtcNow ||
                    preview.PersonId != person.Id.ToString("N"))
                    throw new InvalidOperationException("The preview expired or belongs to another actor. Preview the actor again.");
            }
            if (saveLogo && preview.Logo is null) throw new InvalidOperationException("No Logo image was found in the preview.");
            if (saveBanner && preview.Banner is null) throw new InvalidOperationException("No Banner image was found in the preview.");

            var result = new PornhubArtworkApplyResult { PersonId = person.Id.ToString("N") };
            if (saveLogo && preview.Logo is { } logo)
            {
                await SaveAndPersistImageAsync(person, ImageType.Logo, logo.Data, logo.Mime, ct).ConfigureAwait(false);
                result.LogoSaved = true;
            }
            if (saveBanner && preview.Banner is { } banner)
            {
                await SaveAndPersistImageAsync(person, ImageType.Banner, banner.Data, banner.Mime, ct).ConfigureAwait(false);
                result.BannerSaved = true;
            }
            result.Message = "Images saved and registered in Jellyfin: " +
                string.Join(", ", new[] { result.LogoSaved ? "Logo" : null, result.BannerSaved ? "Banner" : null }.Where(x => x is not null)) +
                ". Check the actor's Edit images screen; Jellyfin Web may not show Logo/Banner on the Person details page.";
            _logger.LogInformation("JFToStashSync: Pornhub actor artwork saved and registered for Person {PersonId}: Logo={Logo}, Banner={Banner}.",
                result.PersonId, result.LogoSaved, result.BannerSaved);
            return result;
        }
        finally { NetworkGate.Release(); }
    }

    /// <summary>
    /// SaveImage writes the image file and updates the in-memory BaseItem, but does not
    /// persist the updated ImageInfos to Jellyfin's repository on its own. Match the
    /// built-in Jellyfin ImageController flow: SaveImage + UpdateToRepositoryAsync.
    /// Keeping this per-image also preserves the first successful save if the second
    /// image unexpectedly fails.
    /// </summary>
    private async Task SaveAndPersistImageAsync(Person person, ImageType type, byte[] data, string mime, CancellationToken ct)
    {
        using (var stream = new MemoryStream(data, writable: false))
        {
            await _images.SaveImage(person, stream, mime, type, null, ct).ConfigureAwait(false);
        }

        // An image being downloaded to disk is NOT proof that Jellyfin attached it to
        // this Person. Refuse to return success if there is no registered image.
        var image = person.GetImageInfo(type, 0);
        if (image is null || string.IsNullOrWhiteSpace(image.Path) ||
            !image.IsLocalFile || !File.Exists(image.Path))
        {
            throw new InvalidOperationException($"{type} could not be verified as a local Person image. The operation was not confirmed.");
        }

        await person.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);

        // Read the item back through the library after persisting. This validates that
        // the image is registered with the Person returned by Jellyfin (not only on disk).
        var updated = _library.GetItemById(person.Id) as Person;
        var savedImage = updated?.GetImageInfo(type, 0);
        if (savedImage is null || string.IsNullOrWhiteSpace(savedImage.Path) ||
            !savedImage.IsLocalFile || !File.Exists(savedImage.Path))
        {
            throw new InvalidOperationException($"{type} was written, but Jellyfin did not expose it on the updated Person. Check the server log.");
        }
    }

    private Person GetPerson(string personId)
    {
        if (Plugin.Instance?.Configuration is not { Enabled: true })
            throw new InvalidOperationException("JF To Stash Sync is disabled.");
        if (!Guid.TryParse(personId, out var id))
            throw new ArgumentException("Enter a valid Jellyfin Person ID.");
        return _library.GetItemById(id) as Person
            ?? throw new InvalidOperationException("Jellyfin Person not found.");
    }

    internal static string? NormalizeProfileUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !(IsTrustedHost(uri, "www.pornhub.com") || IsTrustedHost(uri, "pornhub.com") ||
              IsTrustedHost(uri, "www.pornhub.org") || IsTrustedHost(uri, "pornhub.org"))) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!Regex.IsMatch(path, @"^/(?:pornstar|model)/[a-z0-9][a-z0-9_-]{0,120}$", RegexOptions.IgnoreCase, RegexTimeout))
            return null;
        return "https://www.pornhub.com" + path;
    }

    private static bool IsTrustedHost(Uri uri, string host) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Port == 443 &&
        uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedImageUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Port == 443 &&
        (uri.Host.Equals("phncdn.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".phncdn.com", StringComparison.OrdinalIgnoreCase));

    internal static string? ExtractImageUrl(string html, string id)
    {
        if (html.Length > MaxHtmlChars || (id != "getAvatar" && id != "coverPictureDefault")) return null;
        foreach (Match tag in Regex.Matches(html, @"<img\b[^>]{0,4096}>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout))
        {
            var actualId = Attribute(tag.Value, "id");
            if (!string.Equals(actualId, id, StringComparison.OrdinalIgnoreCase)) continue;
            var src = WebUtility.HtmlDecode(Attribute(tag.Value, "src") ?? Attribute(tag.Value, "data-src") ?? "");
            if (src.StartsWith("//", StringComparison.Ordinal)) src = "https:" + src;
            if (Uri.TryCreate(src, UriKind.Absolute, out var uri) && IsTrustedImageUri(uri))
                return uri.AbsoluteUri;
            return null;
        }
        return null;
    }

    private static string? Attribute(string tag, string name)
    {
        var m = Regex.Match(tag, @"\b" + Regex.Escape(name) + @"\s*=\s*(?:""([^""<>]*)""|'([^'<>]*)'|([^\s/>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        return m.Success ? m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value : null;
    }

    private static string NormalizeName(string name) =>
        Regex.Replace(name.Trim().Replace('_', ' ').Replace('-', ' '), @"\s+", " ").ToLowerInvariant();

    private async Task<string> SearchByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Actor name is empty.");
        var home = await GetTextAsync("https://www.pornhub.com", ct).ConfigureAwait(false);
        var token = Regex.Match(home, @"data-token\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase, RegexTimeout);
        if (!token.Success) throw new InvalidOperationException("Pornhub search token was not found. The site may have changed or returned an access challenge.");
        var query = "https://www.pornhub.com/api/v1/video/search_autocomplete?q=" + Uri.EscapeDataString(name) +
                    "&token=" + Uri.EscapeDataString(WebUtility.HtmlDecode(token.Groups[1].Value)) + "&pornstars=true&alt=0";
        var json = await GetTextAsync(query, ct).ConfigureAwait(false);
        JObject root;
        try { root = JObject.Parse(json); }
        catch (Exception ex) { throw new InvalidOperationException("Pornhub autocomplete returned invalid JSON.", ex); }
        var matches = new List<string>();
        foreach (var category in new[] { "models", "pornstars" })
        {
            if (root[category] is not JArray list) continue;
            foreach (var item in list.Take(50))
            {
                if (NormalizeName(item["name"]?.Value<string>() ?? "") != NormalizeName(name)) continue;
                var slug = item["slug"]?.Value<string>() ?? "";
                var uri = NormalizeProfileUrl("https://www.pornhub.com/" + (category == "models" ? "model" : "pornstar") + "/" + slug);
                if (uri is not null) matches.Add(uri);
            }
        }
        var unique = matches.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (unique.Length == 0) throw new ActorProfileNotFoundException("No exact Pornhub actor name match. Add the actor's Pornhub URL to Stash and retry.");
        if (unique.Length > 1) throw new InvalidOperationException("Multiple exact Pornhub profiles found. Add the correct profile URL in Stash to prevent a wrong match.");
        return unique[0];
    }

    private async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsTrustedHost(uri, "www.pornhub.com"))
            throw new InvalidOperationException("Untrusted Pornhub URL blocked.");
        await RespectRateLimitAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Cookie", "accessAgeDisclaimerPH=1");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/json;q=0.9,*/*;q=0.8");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // HTTP 404 is a definitive missing profile ONLY for /pornstar/ or /model/.
        // A failed home page or autocomplete endpoint remains a temporary error.
        if (response.StatusCode == HttpStatusCode.NotFound &&
            (uri.AbsolutePath.StartsWith("/pornstar/", StringComparison.OrdinalIgnoreCase) ||
             uri.AbsolutePath.StartsWith("/model/", StringComparison.OrdinalIgnoreCase)))
            throw new ActorProfileNotFoundException("Pornhub performer profile returned HTTP 404.");
        HandleStatus(response);
        if (response.Content.Headers.ContentLength is > MaxHtmlChars) throw new InvalidOperationException("Pornhub HTML response exceeds the size limit.");
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (body.Length > MaxHtmlChars) throw new InvalidOperationException("Pornhub HTML response exceeds the size limit.");
        if (body.Contains("cf-chl-opt", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase))
        {
            _blockedUntilUtc = DateTime.UtcNow.AddMinutes(30);
            throw new InvalidOperationException("Pornhub presented an access challenge; no bypass attempted.");
        }
        return body;
    }

    private async Task<(byte[] Data, string Mime)> GetImageAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsTrustedImageUri(uri))
            throw new InvalidOperationException("Untrusted image CDN URL blocked.");
        await RespectRateLimitAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        HandleStatus(response);
        if (response.Content.Headers.ContentLength is > MaxImageBytes)
            throw new InvalidOperationException("Image exceeds the 6 MB limit.");
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32768];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > MaxImageBytes) throw new InvalidOperationException("Image exceeds the 6 MB limit.");
            output.Write(buffer, 0, count);
        }
        var image = output.ToArray();
        var mime = DetectImageMime(image) ?? throw new InvalidOperationException("Image CDN did not return a supported JPG, PNG, GIF or WEBP image.");
        return (image, mime);
    }

    internal static string? DetectImageMime(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF) return "image/jpeg";
        if (content.Length >= 8 && content.AsSpan(0, 8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })) return "image/png";
        if (content.Length >= 6 && (System.Text.Encoding.ASCII.GetString(content, 0, 6) is "GIF87a" or "GIF89a")) return "image/gif";
        if (content.Length >= 12 && System.Text.Encoding.ASCII.GetString(content, 0, 4) == "RIFF" &&
            System.Text.Encoding.ASCII.GetString(content, 8, 4) == "WEBP") return "image/webp";
        return null;
    }

    private static async Task RespectRateLimitAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _blockedUntilUtc)
            throw new InvalidOperationException("Pornhub temporarily limited requests. Retry later.");
        var remaining = _nextRequestUtc - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct).ConfigureAwait(false);
        _nextRequestUtc = DateTime.UtcNow.AddSeconds(3);
    }

    private static void HandleStatus(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is 403 or 429 or 503)
        {
            var delta = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(30);
            if (delta < TimeSpan.FromMinutes(5)) delta = TimeSpan.FromMinutes(5);
            if (delta > TimeSpan.FromHours(2)) delta = TimeSpan.FromHours(2);
            _blockedUntilUtc = DateTime.UtcNow.Add(delta);
            throw new InvalidOperationException("Pornhub or image CDN returned HTTP " + (int)response.StatusCode + "; stopped requests until " +
                _blockedUntilUtc.ToString("u", CultureInfo.InvariantCulture) + ".");
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Pornhub or CDN returned HTTP " + (int)response.StatusCode + ".");
    }
}
