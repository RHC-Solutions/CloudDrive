using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using CloudDrive.Core.Models;
using CloudDrive.Core.Providers;

namespace CloudDrive.CloudFiles;

/// <summary>
/// A Storage Box over WebDAV.
///
/// Hetzner runs a plain WebDAV server, so this speaks the raw verbs (PROPFIND, GET with Range,
/// PUT, DELETE, MOVE, MKCOL) rather than leaning on a vendor extension. The one notable choice is
/// walking the tree with <c>Depth: 1</c> per directory instead of a single <c>Depth: infinity</c>
/// request: infinite depth is optional in the spec and commonly disabled, and when a server does
/// allow it a large tree arrives as one enormous XML body that has to be buffered whole.
/// </summary>
public sealed class WebDavStorageClient : IRemoteStorageClient
{
    private static readonly XNamespace Dav = "DAV:";

    /// <summary>PROPFIND body asking only for the properties actually used, not allprop.</summary>
    private const string PropFindBody =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <D:propfind xmlns:D="DAV:">
          <D:prop>
            <D:resourcetype/>
            <D:getcontentlength/>
            <D:getlastmodified/>
            <D:getetag/>
          </D:prop>
        </D:propfind>
        """;

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly Action<string>? _log;

    public WebDavStorageClient(string baseUrl, string username, string password, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("Base URL is required.", nameof(baseUrl));

        _log = log;
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");

        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true,
            AutomaticDecompression = DecompressionMethods.All,
            // Each upload is one PUT and hydration fans out into parallel GETs, so the default
            // two-per-server connection limit would serialise exactly the work meant to overlap.
            MaxConnectionsPerServer = 16,
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        // Some WebDAV servers answer with a login form instead of a 401 challenge unless the
        // credentials are already present, so Basic is sent pre-emptively as well.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    /// <summary>Builds a client for a WebDAV account.</summary>
    public static WebDavStorageClient ForMapping(
        Mapping mapping, Account account, Credentials creds, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(creds);
        if (string.IsNullOrWhiteSpace(creds.Password))
            throw new InvalidOperationException("WebDAV authenticates with a password; an SSH key cannot be used.");

        // Tolerate a host pasted in as a full URL, which is how most vendors document it.
        var host = account.Host;
        var baseUrl = host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? host.TrimEnd('/')
            : (account.UseTls ? "https://" : "http://") + host.TrimEnd('/');

        return new WebDavStorageClient(
            baseUrl,
            account.Provider == ProviderId.HetznerStorageBox
                ? StorageBox.UserFor(account.Username)
                : account.Username.Trim(),
            creds.Password,
            log);
    }

    public string ProtocolName => "WebDAV";

    public bool SupportsShareLinks => false;

    public string? CreateShareLink(string key, TimeSpan expiresIn) => null;

    private Uri UriFor(string key)
    {
        var relative = (key ?? string.Empty).Replace('\\', '/').TrimStart('/');
        // Escape each segment separately: escaping the whole path would turn the separators into
        // %2F and address one absurdly-named file instead of a path.
        var escaped = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        return new Uri(_baseUri, escaped);
    }

    public async IAsyncEnumerable<RemoteEntry> ListAsync(
        string? prefix = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var pending = new Stack<string>();
        pending.Push((prefix ?? string.Empty).Replace('\\', '/').Trim('/'));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            XDocument? document;
            try
            {
                document = await PropFindAsync(dir, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // A prefix that does not exist yet lists as empty — the normal state before the
                // first upload creates it.
                continue;
            }
            if (document is null) continue;

            foreach (var response in document.Descendants(Dav + "response"))
            {
                var href = response.Element(Dav + "href")?.Value;
                if (string.IsNullOrEmpty(href)) continue;

                var key = KeyFromHref(href);
                if (key is null) continue;
                // PROPFIND echoes the directory itself back as the first entry; skip it or the
                // walk would push the same directory forever.
                if (string.Equals(key, dir, StringComparison.Ordinal)) continue;

                var prop = response.Descendants(Dav + "prop").FirstOrDefault();
                if (prop is null) continue;

                var isCollection = prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
                if (isCollection) { pending.Push(key); continue; }

                var size = ParseLong(prop.Element(Dav + "getcontentlength")?.Value);
                var modified = ParseDate(prop.Element(Dav + "getlastmodified")?.Value);
                // Prefer the server's ETag when it offers one; fall back to size+mtime otherwise.
                var etag = prop.Element(Dav + "getetag")?.Value?.Trim('"');
                if (string.IsNullOrWhiteSpace(etag)) etag = RemoteEntry.SyntheticTag(size, modified);

                yield return new RemoteEntry(key, size, modified, etag);
            }
        }
    }

    private async Task<XDocument?> PropFindAsync(string dir, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), UriFor(dir))
        {
            Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml"),
        };
        request.Headers.Add("Depth", "1");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
    }

    /// <summary>Turns an absolute or relative href back into a key relative to the mapping root.</summary>
    private string? KeyFromHref(string href)
    {
        string path;
        try
        {
            var uri = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new Uri(href)
                : new Uri(_baseUri, href);
            path = uri.AbsolutePath;
        }
        catch (UriFormatException) { return null; }

        var root = _baseUri.AbsolutePath;
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            // Some servers return hrefs rooted at "/" even when the base URL has a path prefix.
            path = path.TrimStart('/');
            root = root.TrimStart('/');
            if (root.Length > 0 && path.StartsWith(root, StringComparison.Ordinal))
                path = path[root.Length..];
        }
        else
        {
            path = path[root.Length..];
        }

        return Uri.UnescapeDataString(path).Trim('/');
    }

    public async Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UriFor(key));
        if (offset > 0 || length is not null)
        {
            var to = length is null ? (long?)null : offset + length.Value - 1;
            request.Headers.Range = new RangeHeaderValue(offset, to);
        }

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            // The response owns the stream, so it must stay alive until the caller is done reading.
            return new ResponseStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<string?> PutAsync(string key, string localPath, CancellationToken ct = default)
    {
        await EnsureDirectoryAsync(ParentKey(key), ct).ConfigureAwait(false);

        await using (var source = File.OpenRead(localPath))
        {
            using var content = new StreamContent(source);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = source.Length;

            using var response = await _http.PutAsync(UriFor(key), content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Headers.ETag?.Tag is { Length: > 0 } tag)
                return tag.Trim('"');
        }

        // No ETag came back, so read the stored size and mtime instead. They have to come from the
        // server: matching what a later listing reports is the whole point of the change token.
        return await StatAsync(key, ct).ConfigureAwait(false);
    }

    private async Task<string?> StatAsync(string key, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), UriFor(key))
            {
                Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml"),
            };
            request.Headers.Add("Depth", "0");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var document = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var prop = document.Descendants(Dav + "prop").FirstOrDefault();
            if (prop is null) return null;

            var etag = prop.Element(Dav + "getetag")?.Value?.Trim('"');
            if (!string.IsNullOrWhiteSpace(etag)) return etag;

            return RemoteEntry.SyntheticTag(
                ParseLong(prop.Element(Dav + "getcontentlength")?.Value),
                ParseDate(prop.Element(Dav + "getlastmodified")?.Value));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not stat '{key}' after upload: {ex.Message}");
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(UriFor(key), ct).ConfigureAwait(false);
        // Already gone is the desired end state, not a failure.
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
    }

    public async Task<DeleteResult> DeleteManyAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var deleted = new List<string>();
        var failed = new List<string>();

        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try { await DeleteAsync(key, ct).ConfigureAwait(false); deleted.Add(key); }
            catch (Exception ex)
            {
                _log?.Invoke($"Delete of '{key}' failed: {ex.Message}");
                failed.Add(key);
            }
        }
        return new DeleteResult(deleted, failed);
    }

    public async Task MoveAsync(string sourceKey, string destKey, CancellationToken ct = default)
    {
        await EnsureDirectoryAsync(ParentKey(destKey), ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), UriFor(sourceKey));
        request.Headers.Add("Destination", UriFor(destKey).AbsoluteUri);
        request.Headers.Add("Overwrite", "T");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Creates a collection and any missing parents (WebDAV MKCOL is one level at a time).</summary>
    private async Task EnsureDirectoryAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key)) return;

        using var probe = new HttpRequestMessage(new HttpMethod("PROPFIND"), UriFor(key));
        probe.Headers.Add("Depth", "0");
        using (var response = await _http.SendAsync(probe, ct).ConfigureAwait(false))
        {
            if (response.IsSuccessStatusCode) return;
        }

        await EnsureDirectoryAsync(ParentKey(key), ct).ConfigureAwait(false);

        using var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), UriFor(key));
        using var created = await _http.SendAsync(mkcol, ct).ConfigureAwait(false);
        // 405 means it already exists — a concurrent upload got there first, which is fine.
        if (!created.IsSuccessStatusCode && created.StatusCode != HttpStatusCode.MethodNotAllowed)
            created.EnsureSuccessStatusCode();
    }

    private static string ParentKey(string key)
    {
        var i = (key ?? string.Empty).LastIndexOf('/');
        return i <= 0 ? string.Empty : key![..i];
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static DateTime ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : DateTime.UnixEpoch;

    public void Dispose() => _http.Dispose();

    /// <summary>Keeps the HTTP response alive for as long as its content stream is being read.</summary>
    private sealed class ResponseStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;

        public ResponseStream(Stream inner, HttpResponseMessage response)
        {
            _inner = inner;
            _response = response;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _inner.ReadAsync(buffer, ct);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            _inner.ReadAsync(buffer, offset, count, ct);

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
