using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudDrive.Core.Models;

namespace CloudDrive.Core.OAuth;

/// <summary>A token endpoint response.</summary>
public sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")] public string? Scope { get; set; }

    [JsonPropertyName("id_token")] public string? IdToken { get; set; }

    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

/// <summary>
/// Thrown when a refresh fails in a way only an interactive sign-in can fix — a revoked grant, a
/// changed password, an expired unverified-app token. Distinguished from a transient failure because
/// the two need opposite responses: retry, or stop and tell a human.
/// </summary>
public sealed class OAuthReauthRequiredException(string message) : Exception(message);

/// <summary>
/// Exchanges authorisation codes and refresh tokens for access tokens.
///
/// <para>This is the half of OAuth that needs no browser, which is what lets a OneDrive or Google
/// Drive mount come up before anyone signs in. The interactive half runs once, in the tray app; from
/// then on the LocalSystem service only ever does the exchange below.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OAuthTokenService(HttpClient? http = null, Action<string>? log = null) : IDisposable
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private readonly bool _ownsHttp = http is null;

    /// <summary>
    /// How long before expiry a token is treated as stale. A token that expires mid-request fails the
    /// request, and refreshing early costs nothing.
    /// </summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Errors the provider returns when the grant itself is gone. Retrying any of these forever would
    /// be pointless; they need a human at a browser.
    /// </summary>
    private static readonly HashSet<string> TerminalErrors = new(StringComparer.OrdinalIgnoreCase)
    {
        "invalid_grant",       // revoked, expired, or password changed
        "unauthorized_client", // the registration was removed or is blocked in the tenant
        "invalid_client",      // wrong or deleted client id
        "consent_required",
        "interaction_required",
    };

    /// <summary>Redeems an authorisation code. Called once, by the interactive flow.</summary>
    public Task<OAuthTokenResponse> RedeemCodeAsync(
        OAuthProviderConfig config, string tenant, string clientId, string? clientSecret,
        string code, string codeVerifier, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            // Proves this process is the one that started the flow, which is what makes a public
            // client safe without a secret.
            ["code_verifier"] = codeVerifier,
            ["scope"] = config.Scopes,
        };
        if (!string.IsNullOrWhiteSpace(clientSecret)) form["client_secret"] = clientSecret!;

        return PostAsync(config, tenant, form, ct);
    }

    /// <summary>
    /// Exchanges a refresh token for a fresh access token.
    ///
    /// A provider may return a *new* refresh token, in which case the old one must be discarded — the
    /// OAuth spec permits the server to revoke it. Callers persist whatever comes back.
    /// </summary>
    public Task<OAuthTokenResponse> RefreshAsync(
        OAuthProviderConfig config, string tenant, string clientId, string? clientSecret,
        string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = config.Scopes,
        };
        if (!string.IsNullOrWhiteSpace(clientSecret)) form["client_secret"] = clientSecret!;

        return PostAsync(config, tenant, form, ct);
    }

    private async Task<OAuthTokenResponse> PostAsync(
        OAuthProviderConfig config, string tenant, Dictionary<string, string> form, CancellationToken ct)
    {
        var endpoint = config.TokenEndpoint.Replace("{tenant}", tenant, StringComparison.Ordinal);

        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        OAuthTokenResponse? token;
        try
        {
            token = JsonSerializer.Deserialize<OAuthTokenResponse>(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"{config.DisplayName} returned a token response CloudDrive could not parse "
                + $"({(int)response.StatusCode}).");
        }

        if (token?.Error is { } error)
        {
            var detail = token.ErrorDescription ?? error;

            // Split terminal from transient here rather than at the call site, because only this layer
            // can see the provider's error code and the distinction decides whether to retry forever
            // or to raise a "sign in again" alert.
            if (TerminalErrors.Contains(error))
                throw new OAuthReauthRequiredException(FirstLine(detail));

            throw new HttpRequestException(
                $"{config.DisplayName} refused the token request: {FirstLine(detail)}");
        }

        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(token?.AccessToken))
        {
            throw new HttpRequestException(
                $"{config.DisplayName} returned {(int)response.StatusCode} with no access token.");
        }

        return token;
    }

    /// <summary>
    /// Microsoft's error descriptions run to several lines with trace and correlation ids. The first
    /// line is the part a human needs; the rest belongs in a support ticket, not an alert.
    /// </summary>
    private static string FirstLine(string value)
    {
        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                   ?? value;
        return line.Trim();
    }

    /// <summary>
    /// Ensures <paramref name="credentials"/> holds a usable access token, refreshing if needed.
    ///
    /// Returns true when the stored credentials were changed and must be persisted. The caller writes
    /// them back, because only it knows which store they came from.
    /// </summary>
    /// <exception cref="OAuthReauthRequiredException">
    /// The grant is gone. The caller should record this on the account and alert, rather than retry.
    /// </exception>
    public async Task<bool> EnsureAccessTokenAsync(
        Account account, Credentials credentials, OAuthClientRegistry clients, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(credentials);

        var config = OAuthProviders.Require(account.Provider);

        if (credentials.HasFreshAccessToken(RefreshMargin)) return false;

        if (!credentials.HasOAuth)
            throw new OAuthReauthRequiredException(
                $"This {config.DisplayName} account has never been signed in.");

        var (clientId, _) = clients.Resolve(account);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(clients.DescribeMissingClientId(account)!);

        var tenant = account.Provider == ProviderId.OneDrive ? clients.TenantFor(account) : "common";

        log?.Invoke($"Refreshing the {config.DisplayName} token for '{account.Name}'.");

        var token = await RefreshAsync(
            config, tenant, clientId, clients.ClientSecretFor(account),
            credentials.RefreshToken!, ct).ConfigureAwait(false);

        credentials.AccessToken = token.AccessToken;
        credentials.AccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn));

        // Rotate only when a new one came back. Overwriting with null would destroy the only thing
        // keeping this account working.
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            credentials.RefreshToken = token.RefreshToken;

        return true;
    }

    /// <summary>
    /// Reads the signed-in identity, so an account can be labelled with something a human recognises
    /// rather than a GUID. Best-effort: a failure here must not fail a sign-in that otherwise worked.
    /// </summary>
    public async Task<string?> TryGetIdentityAsync(
        OAuthProviderConfig config, string accessToken, CancellationToken ct)
    {
        if (config.IdentityEndpoint is null) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, config.IdentityEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", accessToken);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            foreach (var field in config.IdentityFields)
            {
                if (document.RootElement.TryGetProperty(field, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text)
                {
                    return text;
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"Could not read the {config.DisplayName} identity: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
