namespace CloudDrive.Core.Models;

/// <summary>
/// The secret half of a <see cref="NotificationTarget"/>, kept in the encrypted credential store
/// under the same id rather than in <c>settings.json</c>.
///
/// A Telegram bot token or a Slack webhook URL is a bearer credential: anyone holding it can post as
/// you, and a Slack webhook cannot even be scoped. Leaving one in a plaintext settings file that an
/// administrator might paste into a support ticket is the kind of leak that happens by accident.
/// </summary>
public sealed class NotificationSecret
{
    /// <summary>Telegram bot token from BotFather, <c>&lt;id&gt;:&lt;hash&gt;</c>.</summary>
    public string? TelegramBotToken { get; set; }

    /// <summary>Slack incoming-webhook URL. Mutually exclusive with <see cref="SlackBotToken"/>.</summary>
    public string? SlackWebhookUrl { get; set; }

    /// <summary>
    /// Slack bot token (<c>xoxb-…</c>), used with <see cref="NotificationTarget.SlackChannel"/>.
    /// Preferred over a webhook when one target should reach several channels, since a webhook is
    /// permanently bound to the single channel it was created for.
    /// </summary>
    public string? SlackBotToken { get; set; }

    /// <summary>SMTP password for <see cref="NotificationTarget.SmtpUsername"/>.</summary>
    public string? SmtpPassword { get; set; }

    public NotificationSecret Clone() => (NotificationSecret)MemberwiseClone();
}
