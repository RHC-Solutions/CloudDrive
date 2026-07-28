using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using CloudDrive.App.Services;
using CloudDrive.Core.Models;

namespace CloudDrive.App.Views;

[SupportedOSPlatform("windows")]
public partial class NotificationTargetWindow : Window
{
    private readonly AppController _controller;
    private readonly NotificationTarget _target;
    private readonly bool _isNew;

    public NotificationTargetWindow(AppController controller, NotificationTarget? existing)
    {
        _controller = controller;
        _isNew = existing is null;
        _target = existing is null ? new NotificationTarget() : Clone(existing);

        InitializeComponent();

        HeaderText.Text = _isNew ? "Add alert target" : $"Edit '{_target.Name}'";

        KindBox.ItemsSource = Enum.GetValues<NotificationChannelKind>();
        KindBox.SelectedItem = _target.Kind;
        KindBox.IsEnabled = _isNew; // the stored secret is shaped by the channel

        SeverityBox.ItemsSource = Enum.GetValues<AlertSeverity>();
        SeverityBox.SelectedItem = _target.MinimumSeverity;

        LoadFields();
    }

    /// <summary>The saved target, or null if the dialog was cancelled.</summary>
    public NotificationTarget? Target { get; private set; }

    private static NotificationTarget Clone(NotificationTarget source)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(source);
        return System.Text.Json.JsonSerializer.Deserialize<NotificationTarget>(json) ?? new NotificationTarget();
    }

    private void LoadFields()
    {
        NameBox.Text = _target.Name;
        EnabledCheck.IsChecked = _target.Enabled;

        TelegramChatBox.Text = _target.TelegramChatId ?? string.Empty;
        TelegramThreadBox.Text = _target.TelegramThreadId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SlackChannelBox.Text = _target.SlackChannel ?? string.Empty;

        SmtpHostBox.Text = _target.SmtpHost ?? string.Empty;
        SmtpPortBox.Text = _target.SmtpPort.ToString(CultureInfo.InvariantCulture);
        SmtpTlsCheck.IsChecked = _target.SmtpUseTls;
        SmtpUserBox.Text = _target.SmtpUsername ?? string.Empty;
        EmailFromBox.Text = _target.EmailFrom ?? string.Empty;
        EmailToBox.Text = string.Join(Environment.NewLine, _target.EmailTo);

        // On an edit the secrets are not readable here — they live encrypted under the service's
        // SYSTEM profile — so the boxes stay empty and blank means "keep what is stored".
        if (!_isNew)
        {
            const string keep = "Leave blank to keep the stored value.";
            TelegramTokenHint.Text = keep;
            SlackWebhookHint.Text = keep;
            SmtpPasswordHint.Text = keep;
        }

        ApplyKind();
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e) => ApplyKind();

    private void ApplyKind()
    {
        var kind = (NotificationChannelKind)(KindBox.SelectedItem ?? NotificationChannelKind.Telegram);
        TelegramPanel.Visibility = Show(kind == NotificationChannelKind.Telegram);
        SlackPanel.Visibility = Show(kind == NotificationChannelKind.Slack);
        EmailPanel.Visibility = Show(kind == NotificationChannelKind.Email);
    }

    /// <summary>Reads the form, returning the secret separately or null when nothing was typed.</summary>
    private NotificationSecret? Collect()
    {
        _target.Kind = (NotificationChannelKind)(KindBox.SelectedItem ?? NotificationChannelKind.Telegram);
        _target.Name = NameBox.Text.Trim();
        _target.Enabled = EnabledCheck.IsChecked == true;
        _target.MinimumSeverity = (AlertSeverity)(SeverityBox.SelectedItem ?? AlertSeverity.Warning);

        _target.TelegramChatId = Blank(TelegramChatBox.Text);
        _target.TelegramThreadId = int.TryParse(TelegramThreadBox.Text.Trim(), out var thread) ? thread : null;
        _target.SlackChannel = Blank(SlackChannelBox.Text);

        _target.SmtpHost = Blank(SmtpHostBox.Text);
        _target.SmtpPort = int.TryParse(SmtpPortBox.Text.Trim(), out var port) && port is > 0 and <= 65535
            ? port
            : 587;
        _target.SmtpUseTls = SmtpTlsCheck.IsChecked == true;
        _target.SmtpUsername = Blank(SmtpUserBox.Text);
        _target.EmailFrom = Blank(EmailFromBox.Text);
        _target.EmailTo = EmailToBox.Text
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var telegramToken = TelegramTokenBox.Password;
        var slackWebhook = SlackWebhookBox.Password;
        var slackToken = SlackTokenBox.Password;
        var smtpPassword = SmtpPasswordBox.Password;

        var typed = telegramToken.Length > 0 || slackWebhook.Length > 0
                    || slackToken.Length > 0 || smtpPassword.Length > 0;
        if (!typed) return null;

        return new NotificationSecret
        {
            TelegramBotToken = Blank(telegramToken),
            SlackWebhookUrl = Blank(slackWebhook),
            SlackBotToken = Blank(slackToken),
            SmtpPassword = Blank(smtpPassword),
        };
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        try
        {
            var secret = Collect();

            if (_target.Name.Length == 0) { ErrorText.Text = "The target needs a name."; return; }
            if (_isNew && secret is null)
            {
                ErrorText.Text = "Enter the credential for this channel.";
                return;
            }

            SaveButton.IsEnabled = false;
            // The service validates the channel configuration properly and rejects a broken one, so
            // a typo surfaces here rather than when the first real alert is lost.
            await _controller.SaveNotificationTargetAsync(_target, secret);
            Target = _target;
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

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        try
        {
            var secret = Collect();
            await _controller.SendTestAlertAsync(_target, secret);
            MessageBox.Show("The test message was sent.", "CloudDrive",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Test failed: {ex.Message}";
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
