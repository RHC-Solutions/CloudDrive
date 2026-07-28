using CloudDrive.Core.Models;
using CloudDrive.Core.OAuth;

namespace CloudDrive.Tests;

public class OAuthProviderConfigTests
{
    [Fact]
    public void Both_oauth_providers_have_a_configuration()
    {
        Assert.NotNull(OAuthProviders.For(ProviderId.OneDrive));
        Assert.NotNull(OAuthProviders.For(ProviderId.GoogleDrive));
        Assert.Null(OAuthProviders.For(ProviderId.Wasabi));
    }

    /// <summary>
    /// The single most consequential detail in the whole flow. Without a refresh token the mount stops
    /// working an hour after sign-in and cannot be revived without a browser — which defeats the point
    /// of a service that mounts before anyone signs in.
    /// </summary>
    [Fact]
    public void Microsoft_requests_offline_access_so_a_refresh_token_comes_back()
    {
        Assert.Contains("offline_access", OAuthProviders.Microsoft.Scopes);
    }

    [Fact]
    public void Google_asks_for_offline_access_and_forces_the_consent_screen()
    {
        var extra = OAuthProviders.Google.ExtraAuthorizeParameters;

        // Google returns a refresh token only with access_type=offline, and only on the first consent
        // unless prompt=consent forces the screen again. Missing either one produces an account that
        // works for an hour and then silently stops.
        Assert.Equal("offline", extra["access_type"]);
        Assert.Equal("consent", extra["prompt"]);
    }

    [Fact]
    public void Google_requests_the_full_drive_scope()
    {
        // drive.file would only ever see files CloudDrive itself created, which for a drive mount means
        // an empty folder.
        Assert.Contains("https://www.googleapis.com/auth/drive", OAuthProviders.Google.Scopes);
        Assert.DoesNotContain("drive.file", OAuthProviders.Google.Scopes);
    }

    [Fact]
    public void Microsoft_endpoints_carry_a_tenant_placeholder()
    {
        Assert.Contains("{tenant}", OAuthProviders.Microsoft.AuthorizeEndpoint);
        Assert.Contains("{tenant}", OAuthProviders.Microsoft.TokenEndpoint);
    }

    [Fact]
    public void Every_endpoint_is_https()
    {
        foreach (var config in OAuthProviders.All)
        {
            Assert.StartsWith("https://", config.AuthorizeEndpoint, StringComparison.Ordinal);
            Assert.StartsWith("https://", config.TokenEndpoint, StringComparison.Ordinal);
            Assert.StartsWith("https://", config.RegistrationUrl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_provider_can_label_its_account()
    {
        // Without an identity the account list shows a GUID, which tells the user nothing about which
        // of their three Google accounts a mapping belongs to.
        foreach (var config in OAuthProviders.All)
        {
            Assert.NotNull(config.IdentityEndpoint);
            Assert.NotEmpty(config.IdentityFields);
        }
    }

    [Fact]
    public void Requiring_a_non_oauth_provider_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OAuthProviders.Require(ProviderId.Sftp));
}

public class OAuthClientRegistryTests
{
    private static OAuthClientRegistry RegistryWith(string? json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"clouddrive-oauth-{Guid.NewGuid():N}.json");
        if (json is not null) File.WriteAllText(path, json);
        return new OAuthClientRegistry(path);
    }

    [Fact]
    public void An_account_override_beats_the_machine_file()
    {
        var registry = RegistryWith("""{"MicrosoftClientId":"from-file"}""");
        var account = new Account { Provider = ProviderId.OneDrive, OAuthClientIdOverride = "from-account" };

        var (clientId, source) = registry.Resolve(account);

        Assert.Equal("from-account", clientId);
        Assert.Contains("account", source);
    }

    [Fact]
    public void The_machine_file_is_used_when_the_account_has_no_override()
    {
        var registry = RegistryWith("""{"GoogleClientId":"google-from-file"}""");
        var account = new Account { Provider = ProviderId.GoogleDrive };

        var (clientId, _) = registry.Resolve(account);

        Assert.Equal("google-from-file", clientId);
    }

    [Fact]
    public void Nothing_configured_resolves_to_empty_rather_than_throwing()
    {
        var registry = RegistryWith(null);
        var account = new Account { Provider = ProviderId.OneDrive };

        var (clientId, source) = registry.Resolve(account);

        // Empty rather than an exception, so the UI can explain the setup step instead of crashing.
        Assert.Equal(string.Empty, clientId);
        Assert.Equal("nowhere", source);
    }

    [Fact]
    public void A_missing_client_id_is_explained_with_the_registration_url()
    {
        var registry = RegistryWith(null);
        var account = new Account { Provider = ProviderId.OneDrive };

        var message = registry.DescribeMissingClientId(account);

        Assert.NotNull(message);
        Assert.Contains("entra.microsoft.com", message);
        // The redirect URI is the step people get wrong, so it has to be in the message.
        Assert.Contains("http://localhost", message);
    }

    [Fact]
    public void A_configured_client_id_produces_no_complaint()
    {
        var registry = RegistryWith("""{"MicrosoftClientId":"abc"}""");
        var account = new Account { Provider = ProviderId.OneDrive };

        Assert.Null(registry.DescribeMissingClientId(account));
    }

    [Fact]
    public void Tenant_falls_back_from_account_to_file_to_common()
    {
        var withFile = RegistryWith("""{"MicrosoftTenant":"contoso.onmicrosoft.com"}""");
        Assert.Equal("contoso.onmicrosoft.com",
            withFile.TenantFor(new Account { Provider = ProviderId.OneDrive }));

        Assert.Equal("explicit-tenant",
            withFile.TenantFor(new Account { Provider = ProviderId.OneDrive, TenantId = "explicit-tenant" }));

        var empty = RegistryWith(null);
        Assert.Equal("common", empty.TenantFor(new Account { Provider = ProviderId.OneDrive }));
    }

    [Fact]
    public void A_client_secret_is_only_offered_to_google()
    {
        var registry = RegistryWith("""{"GoogleClientSecret":"GOCSPX-secret"}""");

        Assert.Equal("GOCSPX-secret",
            registry.ClientSecretFor(new Account { Provider = ProviderId.GoogleDrive }));
        // Microsoft rejects a public client that sends a secret, so one must never be attached.
        Assert.Null(registry.ClientSecretFor(new Account { Provider = ProviderId.OneDrive }));
    }

    [Fact]
    public void A_corrupt_client_file_does_not_take_sign_in_down()
    {
        // TryLoad swallows a parse failure: a hand-edited file with a trailing comma should degrade to
        // "no client id configured", which is explainable, rather than throwing out of the UI.
        var registry = RegistryWith("{ this is not json");
        var account = new Account { Provider = ProviderId.OneDrive };

        var (clientId, _) = registry.Resolve(account);
        Assert.Equal(string.Empty, clientId);
    }
}

public class OAuthCredentialTests
{
    [Fact]
    public void An_oauth_account_is_complete_once_it_holds_a_refresh_token()
    {
        Assert.False(new Credentials().IsCompleteFor(AuthKind.OAuth));
        Assert.True(new Credentials { RefreshToken = "r" }.IsCompleteFor(AuthKind.OAuth));

        // An access token alone is not enough: it expires within the hour and cannot be renewed.
        Assert.False(new Credentials { AccessToken = "a" }.IsCompleteFor(AuthKind.OAuth));
    }

    [Fact]
    public void Oauth_protocols_need_oauth_credentials()
    {
        var oauth = new Credentials { RefreshToken = "r" };
        Assert.True(oauth.SupportsProtocol(StorageProtocol.Graph));
        Assert.True(oauth.SupportsProtocol(StorageProtocol.GoogleDrive));

        var password = new Credentials { Password = "p" };
        Assert.False(password.SupportsProtocol(StorageProtocol.Graph));
        Assert.False(password.SupportsProtocol(StorageProtocol.GoogleDrive));
    }

    [Fact]
    public void The_refresh_margin_leaves_room_before_expiry()
    {
        // Refreshing early is free; a token that expires mid-request fails the request.
        Assert.True(OAuthTokenService.RefreshMargin >= TimeSpan.FromMinutes(1));
        Assert.True(OAuthTokenService.RefreshMargin <= TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void A_reauth_flag_marks_an_account_as_needing_a_human()
    {
        var account = new Account { Provider = ProviderId.GoogleDrive };
        Assert.False(account.NeedsReauth);

        account.ReauthRequiredReason = "Token has been expired or revoked.";
        Assert.True(account.NeedsReauth);
    }
}
