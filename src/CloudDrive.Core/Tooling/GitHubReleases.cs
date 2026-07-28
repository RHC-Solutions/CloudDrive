using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CloudDrive.Core.Tooling;

/// <summary>One published release.</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("body")] public string? Body { get; set; }

    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }

    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }

    [JsonPropertyName("draft")] public bool Draft { get; set; }

    [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];

    /// <summary>The tag with any leading "v" removed, so it compares as a version.</summary>
    public string Version => TagName.TrimStart('v', 'V');
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")] public long Size { get; set; }

    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

/// <summary>
/// Reads release feeds from the GitHub API.
///
/// Shared by the tool updater and by CloudDrive's own updater, because they do the same thing
/// against different repositories. Unauthenticated, which caps us at 60 requests per hour per IP —
/// ample for a six-hourly poll of three repositories, and the ETag handling below means an
/// unchanged feed usually does not count against it at all.
/// </summary>
public sealed class GitHubReleases : IDisposable
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, (string ETag, GitHubRelease[] Releases)> _cache = new(StringComparer.Ordinal);

    public GitHubReleases(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
        // GitHub rejects requests with no User-Agent outright.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CloudDrive", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// Releases for <paramref name="repo"/> (<c>owner/name</c>), newest first.
    ///
    /// Conditional on the cached ETag: an unchanged feed comes back 304 with no body, which GitHub
    /// does not count against the rate limit. That matters when several machines poll on a timer.
    /// </summary>
    public async Task<IReadOnlyList<GitHubRelease>> ListAsync(string repo, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{repo}/releases?per_page=20";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_cache.TryGetValue(repo, out var cached))
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cached.ETag));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified && _cache.TryGetValue(repo, out var hit))
            return hit.Releases;

        if (response.StatusCode == HttpStatusCode.Forbidden
            && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
            && remaining.FirstOrDefault() == "0")
        {
            // Rate-limited rather than broken. Returning the last good answer beats throwing and
            // making the caller treat a temporary throttle as "no updates exist".
            if (_cache.TryGetValue(repo, out var stale)) return stale.Releases;
            throw new HttpRequestException(
                "GitHub's API rate limit is exhausted for this address; the update check will retry later.");
        }

        response.EnsureSuccessStatusCode();

        var releases = await response.Content
            .ReadFromJsonAsync<GitHubRelease[]>(ct)
            .ConfigureAwait(false) ?? [];

        var usable = releases.Where(r => !r.Draft).ToArray();

        var etag = response.Headers.ETag?.Tag;
        if (etag is not null) _cache[repo] = (etag, usable);

        return usable;
    }

    /// <summary>
    /// The newest release, optionally including prereleases.
    ///
    /// Ordered by published date rather than by tag: tags are strings and sort badly (1.10 before
    /// 1.9), and a vendor that back-publishes a patch to an older branch would otherwise look like
    /// the newest thing available.
    /// </summary>
    public async Task<GitHubRelease?> LatestAsync(
        string repo, bool includePrereleases = false, CancellationToken ct = default)
    {
        var releases = await ListAsync(repo, ct).ConfigureAwait(false);
        return releases
            .Where(r => includePrereleases || !r.Prerelease)
            .OrderByDescending(r => r.PublishedAt ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    /// <summary>The asset matching a tool's name patterns, or null when the release has no build for us.</summary>
    public static GitHubAsset? MatchAsset(GitHubRelease release, ToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(tool);

        return release.Assets.FirstOrDefault(a =>
            tool.AssetNameContains.All(s => a.Name.Contains(s, StringComparison.OrdinalIgnoreCase))
            && !tool.AssetNameExcludes.Any(s => a.Name.Contains(s, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Downloads to <paramref name="destination"/>, reporting progress as a fraction.
    ///
    /// Written to a temp file and moved into place, so an interrupted download cannot be mistaken
    /// for a complete one on the next run.
    /// </summary>
    public async Task DownloadAsync(
        string url, string destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tmp = destination + ".part";

        using (var response = await _http
                   .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(tmp);

            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                if (total is > 0) progress?.Report((double)written / total.Value);
            }
        }

        File.Move(tmp, destination, overwrite: true);
    }

    public void Dispose() => _http.Dispose();
}
