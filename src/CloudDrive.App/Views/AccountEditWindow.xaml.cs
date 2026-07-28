using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using CloudDrive.App.Services;
using CloudDrive.Core.Models;
using CloudDrive.Core.Providers;

namespace CloudDrive.App.Views;

/// <summary>
/// Add or edit one account.
///
/// The form is driven by <see cref="ProviderDescriptor"/> rather than by a switch over the brand:
/// picking a provider decides which panels are visible, what the fields are called, whether there is
/// a region list, and which protocols may be chosen. That is what keeps twelve brands sharing one
/// dialog instead of needing twelve.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class AccountEditWindow : Window
{
    private readonly AppController _controller;
    private readonly Account _account;
    private readonly bool _isNew;

    /// <summary>
    /// Set when the user typed a new secret. Left null on an edit that did not touch the credential
    /// fields, which tells the service to keep what it already has — so renaming an account does not
    /// force the user to retype a password they may not have to hand.
    /// </summary>
    private Credentials? _credentials;

    public AccountEditWindow(AppController controller, Account? existing)
    {
        _controller = controller;
        _isNew = existing is null;
        _account = existing?.Clone() ?? new Account();

        InitializeComponent();

        HeaderText.Text = _isNew ? "Add account" : $"Edit '{_account.Name}'";

        ProviderBox.ItemsSource = ProviderCatalog.All;
        ProviderBox.SelectedItem = ProviderCatalog.Find(_account.Provider) ?? ProviderCatalog.All[0];
        // A provider cannot be changed after the fact: the stored credential shape and every mapping
        // hanging off the account assume it. Deleting and re-adding is the honest path.
        ProviderBox.IsEnabled = _isNew;

        LoadFields();
    }

    public Account? Result { get; private set; }

    private ProviderDescriptor Descriptor => (ProviderDescriptor)ProviderBox.SelectedItem;

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedItem is null) return;
        ApplyDescriptor();
    }

    /// <summary>Shows, hides and relabels the form for the selected provider.</summary>
    private void ApplyDescriptor()
    {
        var d = Descriptor;
        ProviderDescription.Text = d.Description;

        RegionPanel.Visibility = Show(d.Has(ProviderCapabilities.Regions));
        if (d.Has(ProviderCapabilities.Regions))
        {
            RegionBox.ItemsSource = d.Regions;
            RegionBox.SelectedItem =
                ProviderCatalog.FindRegion(d.Id, _account.RegionCode)
                ?? ProviderCatalog.FindRegion(d.Id, d.DefaultRegion)
                ?? d.Regions.FirstOrDefault();
        }

        // A host box appears when the endpoint is typed, or as an override for a Storage Box whose
        // hostname is normally derived from the username.
        var wantsHost = d.Has(ProviderCapabilities.CustomEndpoint) || d.Id == ProviderId.HetznerStorageBox;
        HostPanel.Visibility = Show(wantsHost);
        HostLabel.Text = d.IsS3 ? "Endpoint" : "Server";
        HostHint.Text = d.Id switch
        {
            ProviderId.HetznerStorageBox =>
                "Leave blank: the hostname is derived from the username as <user>.your-storagebox.de.",
            ProviderId.GenericS3 => "Host or URL, e.g. minio.example.com or https://s3.example.com.",
            _ => string.Empty,
        };

        PortPanel.Visibility = Show(!d.IsS3 && !d.IsOAuth);
        UserPanel.Visibility = Show(!d.IsS3 && !d.IsOAuth);
        UserHint.Text = d.Id == ProviderId.HetznerStorageBox
            ? "The Storage Box account, e.g. u123456, or a sub-account such as u123456-sub1."
            : string.Empty;

        PasswordPanel.Visibility = Show(d.Auth is AuthKind.Password or AuthKind.PasswordOrKey);
        PasswordHint.Text = _isNew ? string.Empty : "Leave blank to keep the stored password.";

        KeyPanel.Visibility = Show(d.Auth == AuthKind.PasswordOrKey);
        KeyPairPanel.Visibility = Show(d.Auth == AuthKind.KeyPair);
        OAuthPanel.Visibility = Show(d.Auth == AuthKind.OAuth);

        // Backblaze names its credential pair differently from every other S3 dialect, and a user
        // holding a "keyID" will not recognise "access key".
        var isB2 = d.Id == ProviderId.BackblazeB2;
        AccessKeyLabel.Text = isB2 ? "keyID" : "Access key";
        SecretKeyLabel.Text = isB2 ? "applicationKey" : "Secret key";
        if (!_isNew && d.Auth == AuthKind.KeyPair)
            SecretKeyLabel.Text += " — leave blank to keep the stored one";

        ProtocolPanel.Visibility = Show(d.SupportsProtocolBenchmark);
        if (d.SupportsProtocolBenchmark)
        {
            var choices = new List<StorageProtocol> { StorageProtocol.Auto };
            choices.AddRange(d.Protocols);
            ProtocolBox.ItemsSource = choices;
            ProtocolBox.SelectedItem = choices.Contains(_account.Protocol) ? _account.Protocol : StorageProtocol.Auto;
        }

        TlsCheck.Visibility = Show(d.Id is ProviderId.Ftp or ProviderId.WebDav or ProviderId.GenericS3);
        TlsCheck.Content = d.Id == ProviderId.Ftp ? "Use FTPS (TLS)" : "Use HTTPS";

        if (PortBox.Text.Length == 0 && d.DefaultPort > 0)
            PortBox.Text = d.DefaultPort.ToString();

        UpdateOAuthStatus();
    }

    private void LoadFields()
    {
        NameBox.Text = _account.Name;
        HostBox.Text = _account.HostOverride ?? string.Empty;
        UserBox.Text = _account.Username;
        PortBox.Text = _account.Port > 0 ? _account.Port.ToString() : string.Empty;
        TlsCheck.IsChecked = _account.UseTls;
        ApplyDescriptor();
    }

    private void UpdateOAuthStatus()
    {
        if (Descriptor.Auth != AuthKind.OAuth) return;

        if (_account.NeedsReauth)
        {
            OAuthStatus.Text = $"Signed in as {_account.OAuthIdentity ?? "(unknown)"}, but the sign-in has "
                               + $"expired: {_account.ReauthRequiredReason}";
            OAuthButton.Content = "Sign in again…";
        }
        else if (!string.IsNullOrWhiteSpace(_account.OAuthIdentity))
        {
            OAuthStatus.Text = $"Signed in as {_account.OAuthIdentity}.";
            OAuthButton.Content = "Sign in as someone else…";
        }
        else
        {
            OAuthStatus.Text = "This account has not been signed in yet. A browser window will open.";
            OAuthButton.Content = "Sign in…";
        }
    }

    private void OnOAuthSignIn(object sender, RoutedEventArgs e)
    {
        // Phase 2. Saying so plainly beats a button that appears to work and silently does nothing.
        MessageBox.Show(
            $"Signing in to {Descriptor.DisplayName} is not available in this build.\n\n"
            + "Drive-letter mappings for it work through rclone once an account exists; interactive "
            + "sign-in and Files On-Demand arrive in the next release.",
            "CloudDrive", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a private key",
            Filter = "Private keys (*.pem;*.key;id_*)|*.pem;*.key;id_*|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (dialog.ShowDialog(this) == true) KeyFileBox.Text = dialog.FileName;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        try
        {
            Collect();

            var problem = Validate();
            if (problem is not null) { ErrorText.Text = problem; return; }

            SaveButton.IsEnabled = false;
            Result = await _controller.SaveAccountAsync(_account, _credentials);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>Reads the form into the account, and into a Credentials only if a secret was typed.</summary>
    private void Collect()
    {
        var d = Descriptor;

        _account.Provider = d.Id;
        _account.Name = NameBox.Text.Trim();
        _account.Username = UserBox.Text.Trim();
        _account.HostOverride = string.IsNullOrWhiteSpace(HostBox.Text) ? null : HostBox.Text.Trim();
        _account.UseTls = TlsCheck.IsChecked == true;

        _account.Port = int.TryParse(PortBox.Text.Trim(), out var port) && port is > 0 and <= 65535
            ? port
            : 0;

        if (d.Has(ProviderCapabilities.Regions) && RegionBox.SelectedItem is ProviderRegion region)
            _account.RegionCode = region.Code;

        if (d.SupportsProtocolBenchmark && ProtocolBox.SelectedItem is StorageProtocol protocol)
        {
            // Changing the protocol invalidates whatever the benchmark last measured.
            if (_account.Protocol != protocol) _account.ResolvedProtocol = null;
            _account.Protocol = protocol;
        }

        var password = PasswordBox.Password;
        var secretKey = SecretKeyBox.Password;
        var keyFile = KeyFileBox.Text.Trim();
        var keyPass = KeyPassBox.Password;
        var accessKey = AccessKeyBox.Text.Trim();

        var typedSomething = password.Length > 0 || secretKey.Length > 0
                             || keyFile.Length > 0 || keyPass.Length > 0 || accessKey.Length > 0;
        if (!typedSomething) return;

        _credentials = new Credentials
        {
            Password = password,
            SshKeyFile = keyFile.Length > 0 ? keyFile : null,
            SshKeyPassphrase = keyPass.Length > 0 ? keyPass : null,
            AccessKeyId = accessKey,
            SecretAccessKey = secretKey,
        };
    }

    private string? Validate()
    {
        var d = Descriptor;

        if (_account.Name.Length == 0) return "The account needs a name.";

        if (!d.IsS3 && !d.IsOAuth && _account.Username.Length == 0)
            return "A username is required.";

        if (d.Id == ProviderId.HetznerStorageBox && _account.Host.Length == 0)
            return "Enter the Storage Box username; the hostname is derived from it.";

        if (d.Has(ProviderCapabilities.CustomEndpoint) && !d.Has(ProviderCapabilities.Regions)
            && string.IsNullOrWhiteSpace(_account.HostOverride) && d.Id != ProviderId.HetznerStorageBox)
        {
            return d.IsS3 ? "An endpoint is required." : "A server is required.";
        }

        // On an edit with no new secret, the stored one stands and there is nothing to check.
        if (_credentials is null)
        {
            return _isNew && d.Auth != AuthKind.OAuth
                ? "Credentials are required."
                : null;
        }

        return d.Auth switch
        {
            AuthKind.KeyPair when !_credentials.HasKeyPair =>
                $"Both {(d.Id == ProviderId.BackblazeB2 ? "keyID and applicationKey" : "an access key and a secret key")} are required.",
            AuthKind.Password when string.IsNullOrWhiteSpace(_credentials.Password) =>
                "A password is required.",
            AuthKind.PasswordOrKey when !_credentials.HasSshAuth =>
                "Either a password or a private key is required.",
            _ => null,
        };
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
