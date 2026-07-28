using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using CloudDrive.Core.Models;

namespace CloudDrive.Core.OAuth;

/// <summary>What a completed sign-in produced.</summary>
/// <param name="RefreshToken">The long-lived credential. This is what gets stored.</param>
/// <param name="Identity">Who signed in, for labelling the account. May be null.</param>
public sealed record OAuthSignInResult(
    string RefreshToken,
    string? AccessToken,
    DateTime? AccessTokenExpiresUtc,
    string? Identity,
    string ClientId);

/// <summary>
/// The interactive half of OAuth: authorisation code with PKCE, over a loopback redirect.
///
/// <para><b>The system browser, not an embedded one.</b> RFC 8252 is explicit that a native app should
/// use the platform browser: an embedded WebView cannot show the user which site they are typing a
/// password into, defeats the browser's own phishing protections, and cannot reuse an existing
/// sign-in session or a passkey. It also means CloudDrive never sees the password.</para>
///
/// <para><b>Runs in the tray app, never in the service.</b> A LocalSystem service has no session to
/// open a browser in. This runs once, interactively; from then on the service refreshes without any
/// UI, which is what lets a OneDrive mount exist before anyone signs in.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InteractiveOAuthFlow(OAuthTokenService tokens, Action<string>? log = null)
{
    /// <summary>How long to wait for the user to finish in the browser before giving up.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Runs a full sign-in and returns the refresh token.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled, or the user took too long.</exception>
    /// <exception cref="InvalidOperationException">The user denied consent, or the flow failed.</exception>
    public async Task<OAuthSignInResult> SignInAsync(
        Account account, OAuthClientRegistry clients, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(clients);

        var config = OAuthProviders.Require(account.Provider);

        var (clientId, source) = clients.Resolve(account);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(clients.DescribeMissingClientId(account)!);
        log?.Invoke($"Signing in to {config.DisplayName} with the client ID from {source}.");

        var tenant = account.Provider == ProviderId.OneDrive ? clients.TenantFor(account) : "common";

        // PKCE: a high-entropy verifier is kept in memory, and only its SHA-256 hash goes to the
        // provider. The authorisation code is then useless to anyone who intercepts it, because
        // redeeming it requires the verifier this process never transmitted.
        var verifier = CreateCodeVerifier();
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // state ties the callback to this request, so a stray or forged redirect to our loopback
        // listener is rejected rather than acted on.
        var state = CreateCodeVerifier();

        using var listener = LoopbackListener.Start();
        var redirectUri = listener.RedirectUri;
        log?.Invoke($"Waiting for the browser to return to {redirectUri}");

        var authorizeUrl = BuildAuthorizeUrl(config, tenant, clientId, redirectUri, challenge, state);
        OpenBrowser(authorizeUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        var callback = await listener.WaitForCallbackAsync(cts.Token).ConfigureAwait(false);

        if (!string.Equals(callback.State, state, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The sign-in response did not match the request. Start again.");

        if (callback.Error is { } error)
        {
            throw new InvalidOperationException(
                error is "access_denied"
                    ? "Sign-in was declined in the browser."
                    : $"{config.DisplayName} returned '{error}': {callback.ErrorDescription ?? "no detail"}");
        }

        if (string.IsNullOrWhiteSpace(callback.Code))
            throw new InvalidOperationException("The browser returned no authorisation code.");

        var token = await tokens.RedeemCodeAsync(
            config, tenant, clientId, clients.ClientSecretFor(account),
            callback.Code!, verifier, redirectUri, cts.Token).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            // Worth failing loudly rather than storing an access token that dies in an hour and can
            // never be renewed. For Google this almost always means a previous consent still stands
            // and prompt=consent was not honoured; for Microsoft, a missing offline_access scope.
            throw new InvalidOperationException(
                $"{config.DisplayName} did not return a refresh token, so the mount would stop working "
                + "within the hour. Revoke CloudDrive's access in your account settings and sign in again.");
        }

        var identity = await tokens
            .TryGetIdentityAsync(config, token.AccessToken!, cts.Token)
            .ConfigureAwait(false);

        log?.Invoke($"Signed in to {config.DisplayName}{(identity is null ? string.Empty : $" as {identity}")}.");

        return new OAuthSignInResult(
            token.RefreshToken!,
            token.AccessToken,
            DateTime.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn)),
            identity,
            clientId);
    }

    private static string BuildAuthorizeUrl(
        OAuthProviderConfig config, string tenant, string clientId,
        string redirectUri, string codeChallenge, string state)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = clientId;
        query["response_type"] = "code";
        query["redirect_uri"] = redirectUri;
        query["scope"] = config.Scopes;
        query["state"] = state;
        query["code_challenge"] = codeChallenge;
        query["code_challenge_method"] = "S256";
        // The code comes back as a query parameter. A fragment would never reach the loopback
        // listener at all, because browsers do not send fragments to the server.
        query["response_mode"] = "query";

        foreach (var (key, value) in config.ExtraAuthorizeParameters) query[key] = value;

        var endpoint = config.AuthorizeEndpoint.Replace("{tenant}", tenant, StringComparison.Ordinal);
        return $"{endpoint}?{query}";
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"CloudDrive could not open a browser. Paste this address in manually:\n\n{url}", ex);
        }
    }

    /// <summary>
    /// A 43-character URL-safe verifier from 32 bytes of cryptographic randomness, which is what
    /// RFC 7636 asks for.
    /// </summary>
    private static string CreateCodeVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Base64url without padding, per RFC 7636.</summary>
    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>What the browser sent back to the loopback listener.</summary>
internal sealed record OAuthCallback(string? Code, string? State, string? Error, string? ErrorDescription);

/// <summary>
/// A one-shot HTTP listener on <c>127.0.0.1</c> that catches the OAuth redirect.
///
/// <para>Bound to the loopback address specifically, not to <c>+</c> or a hostname: only this machine
/// can reach it, and on Windows binding a non-loopback prefix with <see cref="HttpListener"/> requires
/// administrator rights or a URL reservation, neither of which an unelevated tray app has.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class LoopbackListener : IDisposable
{
    private readonly HttpListener _listener;

    private LoopbackListener(HttpListener listener, string redirectUri)
    {
        _listener = listener;
        RedirectUri = redirectUri;
    }

    public string RedirectUri { get; }

    public static LoopbackListener Start()
    {
        // A free port is found by opening a TCP socket on port 0, letting the OS assign one, and
        // closing it. There is a theoretical race before HttpListener claims it, but this is the
        // standard approach and the alternative — a fixed port — collides with whatever else is
        // already listening and fails far more often.
        var port = FindFreePort();

        // Trailing slash is mandatory: HttpListener rejects a prefix without one.
        var prefix = $"http://127.0.0.1:{port}/";

        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            listener.Close();
            throw new InvalidOperationException(
                $"CloudDrive could not listen on {prefix} to receive the sign-in response: {ex.Message}", ex);
        }

        return new LoopbackListener(listener, prefix.TrimEnd('/'));
    }

    private static int FindFreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    public async Task<OAuthCallback> WaitForCallbackAsync(CancellationToken ct)
    {
        // GetContextAsync ignores cancellation tokens, so cancellation is delivered by stopping the
        // listener out from under it — which makes the pending call throw.
        await using var registration = ct.Register(() =>
        {
            try { _listener.Stop(); } catch { /* already stopping */ }
        }).ConfigureAwait(false);

        HttpListenerContext context;
        try
        {
            context = await _listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The sign-in listener stopped before the browser replied.", ex);
        }

        var query = context.Request.QueryString;
        var callback = new OAuthCallback(
            query["code"], query["state"], query["error"], query["error_description"]);

        await RespondAsync(context, callback.Error is null).ConfigureAwait(false);
        return callback;
    }

    /// <summary>
    /// Shows the user something in the browser tab the redirect landed in. Without this they get a
    /// blank page or a connection error and cannot tell whether it worked.
    /// </summary>
    private static async Task RespondAsync(HttpListenerContext context, bool success)
    {
        var title = success ? "Signed in" : "Sign-in failed";
        var message = success
            ? "CloudDrive has what it needs. You can close this tab and go back to the app."
            : "CloudDrive did not receive an authorisation. Close this tab and try again from the app.";

        // Self-contained and inline-styled: this is served from a throwaway listener that is about to
        // stop, so it cannot reference anything.
        var html = $"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>CloudDrive</title></head>
            <body style="font-family:Segoe UI,system-ui,sans-serif;background:#1a1a2e;color:#eaeaea;
                         display:flex;align-items:center;justify-content:center;height:100vh;margin:0">
              <div style="text-align:center;max-width:30rem;padding:2rem">
                <div style="display:grid;grid-template-columns:1fr 1fr;gap:4px;width:56px;margin:0 auto 1.5rem">
                  <div style="background:#EC6327;aspect-ratio:1;border-radius:3px"></div>
                  <div style="background:#93C842;aspect-ratio:1;border-radius:3px"></div>
                  <div style="background:#5A4DA1;aspect-ratio:1;border-radius:3px"></div>
                  <div style="background:#47C2BE;aspect-ratio:1;border-radius:3px"></div>
                </div>
                <h1 style="font-size:1.4rem;font-weight:600;margin:0 0 .75rem">{title}</h1>
                <p style="color:#a0a0a0;line-height:1.5;margin:0">{message}</p>
              </div>
            </body></html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = success ? 200 : 400;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;

        try
        {
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            // The browser closed the connection. The code is already in hand, so this changes nothing.
        }
    }

    public void Dispose()
    {
        try { _listener.Close(); } catch { /* already closed */ }
    }
}
