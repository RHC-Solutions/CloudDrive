using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudDrive.Core.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace CloudDrive.Notifications;

/// <summary>One place alerts can be sent.</summary>
public interface INotificationChannel
{
    NotificationChannelKind Kind { get; }

    /// <summary>
    /// Delivers one alert. Throws on failure so the dispatcher can spool and retry — swallowing the
    /// error here would silently lose the alert, which defeats the point of alerting.
    /// </summary>
    Task SendAsync(Alert alert, NotificationTarget target, NotificationSecret secret, CancellationToken ct);

    /// <summary>
    /// Explains why this target cannot work, or null when it is configured. Checked when the target
    /// is saved so a typo surfaces then rather than at 3am when the first real alert is lost.
    /// </summary>
    string? Validate(NotificationTarget target, NotificationSecret? secret);
}

/// <summary>
/// Telegram, over the Bot API.
///
/// The bot has to be added to the destination chat first and, for a group, either made an
/// administrator or have privacy mode disabled — otherwise <c>sendMessage</c> returns a 403 that
/// reads like an authentication failure but is a membership problem. <see cref="Validate"/> cannot
/// detect that, so the error text below says it.
/// </summary>
public sealed class TelegramChannel(HttpClient http) : INotificationChannel
{
    public NotificationChannelKind Kind => NotificationChannelKind.Telegram;

    public string? Validate(NotificationTarget target, NotificationSecret? secret)
    {
        if (string.IsNullOrWhiteSpace(secret?.TelegramBotToken))
            return "A Telegram bot token is required. Create a bot with @BotFather to get one.";
        if (!secret.TelegramBotToken!.Contains(':'))
            return "That does not look like a bot token; they are of the form 123456789:AA...";
        if (string.IsNullOrWhiteSpace(target.TelegramChatId))
            return "A chat id is required. Message the bot, then read the chat id from getUpdates.";
        return null;
    }

    public async Task SendAsync(
        Alert alert, NotificationTarget target, NotificationSecret secret, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{secret.TelegramBotToken}/sendMessage";

        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = target.TelegramChatId!,
            ["text"] = Render(alert),
            ["parse_mode"] = "HTML",
            // A mount alert rarely has a URL worth previewing, and an unfurl card buries the text.
            ["disable_web_page_preview"] = true,
        };
        if (target.TelegramThreadId is { } thread) payload["message_thread_id"] = thread;

        using var response = await http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var description = TryReadTelegramError(body) ?? body;
        throw new InvalidOperationException(
            $"Telegram refused the message ({(int)response.StatusCode}): {description}. "
            + "Check that the bot has been added to the chat and, in a group, that it can post.");
    }

    private static string? TryReadTelegramError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Telegram's HTML mode allows a small tag set and rejects the whole message on a stray "&lt;",
    /// so every interpolated value is escaped.
    /// </summary>
    private static string Render(Alert alert)
    {
        var builder = new StringBuilder();
        builder.Append(alert.SeverityIcon).Append(" <b>").Append(Escape(alert.Title)).Append("</b>\n\n");
        builder.Append(Escape(alert.Message)).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(alert.MappingName))
            builder.Append("<b>Mapping:</b> ").Append(Escape(alert.MappingName!)).Append('\n');
        if (!string.IsNullOrWhiteSpace(alert.AccountName))
            builder.Append("<b>Account:</b> ").Append(Escape(alert.AccountName!)).Append('\n');

        builder.Append("<b>Machine:</b> ").Append(Escape(alert.MachineName)).Append('\n');
        builder.Append("<i>").Append(alert.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")).Append("</i>");

        if (alert.SuppressedCount > 0)
            builder.Append("\n<i>+").Append(alert.SuppressedCount).Append(" repeat(s) suppressed</i>");

        return builder.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>
/// Slack, over either an incoming webhook or a bot token.
///
/// A webhook is easier to set up but is permanently bound to the one channel it was created for; a
/// bot token lets several targets share credentials and post to different channels. Both are
/// supported because the easy path should exist and the flexible one should be possible.
/// </summary>
public sealed class SlackChannel(HttpClient http) : INotificationChannel
{
    public NotificationChannelKind Kind => NotificationChannelKind.Slack;

    public string? Validate(NotificationTarget target, NotificationSecret? secret)
    {
        var hasWebhook = !string.IsNullOrWhiteSpace(secret?.SlackWebhookUrl);
        var hasToken = !string.IsNullOrWhiteSpace(secret?.SlackBotToken);

        if (!hasWebhook && !hasToken)
            return "Either an incoming-webhook URL or a bot token is required.";
        if (hasToken && string.IsNullOrWhiteSpace(target.SlackChannel))
            return "A bot token needs a channel to post to, e.g. #alerts.";
        if (hasWebhook && !secret!.SlackWebhookUrl!.StartsWith("https://hooks.slack.com/", StringComparison.OrdinalIgnoreCase))
            return "A Slack webhook URL should start with https://hooks.slack.com/.";
        return null;
    }

    public async Task SendAsync(
        Alert alert, NotificationTarget target, NotificationSecret secret, CancellationToken ct)
    {
        var blocks = BuildBlocks(alert);

        HttpResponseMessage response;
        if (!string.IsNullOrWhiteSpace(secret.SlackBotToken))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/chat.postMessage")
            {
                Content = JsonContent.Create(new
                {
                    channel = target.SlackChannel,
                    text = alert.Title, // fallback for notifications and screen readers
                    blocks,
                }),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", secret.SlackBotToken);
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        else
        {
            response = await http
                .PostAsJsonAsync(secret.SlackWebhookUrl!, new { text = alert.Title, blocks }, ct)
                .ConfigureAwait(false);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Slack returned {(int)response.StatusCode}: {body}");

            // chat.postMessage answers 200 with {"ok":false} on failure, so the status code alone is
            // not enough to know the message arrived.
            if (body.Contains("\"ok\":false", StringComparison.Ordinal))
                throw new InvalidOperationException($"Slack rejected the message: {body}");
        }
    }

    private static object[] BuildBlocks(Alert alert)
    {
        var fields = new List<object>();

        if (!string.IsNullOrWhiteSpace(alert.MappingName))
            fields.Add(new { type = "mrkdwn", text = $"*Mapping*\n{alert.MappingName}" });
        if (!string.IsNullOrWhiteSpace(alert.AccountName))
            fields.Add(new { type = "mrkdwn", text = $"*Account*\n{alert.AccountName}" });

        fields.Add(new { type = "mrkdwn", text = $"*Machine*\n{alert.MachineName}" });
        fields.Add(new
        {
            type = "mrkdwn",
            text = $"*Time*\n{alert.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
        });

        var blocks = new List<object>
        {
            new
            {
                type = "header",
                text = new { type = "plain_text", text = $"{alert.SeverityIcon} {Truncate(alert.Title, 150)}" },
            },
            new { type = "section", text = new { type = "mrkdwn", text = Truncate(alert.Message, 2900) } },
            new { type = "section", fields = fields.ToArray() },
        };

        if (alert.SuppressedCount > 0)
        {
            blocks.Add(new
            {
                type = "context",
                elements = new object[]
                {
                    new { type = "mrkdwn", text = $"_{alert.SuppressedCount} repeat(s) suppressed_" },
                },
            });
        }

        return blocks.ToArray();
    }

    /// <summary>Slack rejects a whole message when any block exceeds its limit, so text is clipped.</summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}

/// <summary>Email over SMTP.</summary>
public sealed class EmailChannel : INotificationChannel
{
    public NotificationChannelKind Kind => NotificationChannelKind.Email;

    public string? Validate(NotificationTarget target, NotificationSecret? secret)
    {
        if (string.IsNullOrWhiteSpace(target.SmtpHost)) return "An SMTP server is required.";
        if (target.SmtpPort is <= 0 or > 65535) return "The SMTP port is not valid.";
        if (string.IsNullOrWhiteSpace(target.EmailFrom)) return "A From address is required.";
        if (target.EmailTo.Count == 0) return "At least one recipient is required.";

        foreach (var address in target.EmailTo.Append(target.EmailFrom!))
        {
            if (!MailboxAddress.TryParse(address, out _))
                return $"'{address}' is not a valid email address.";
        }

        // An unauthenticated relay is legitimate on an internal network, so a missing password is
        // only a problem once a username says authentication is expected.
        if (!string.IsNullOrWhiteSpace(target.SmtpUsername) && string.IsNullOrWhiteSpace(secret?.SmtpPassword))
            return "A password is required when an SMTP username is given.";

        return null;
    }

    public async Task SendAsync(
        Alert alert, NotificationTarget target, NotificationSecret secret, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(target.EmailFrom));
        foreach (var to in target.EmailTo) message.To.Add(MailboxAddress.Parse(to));

        // The machine name goes in the subject because these land in a mailbox alongside alerts from
        // every other machine, and "Mount failed" on its own is not actionable.
        message.Subject = $"[CloudDrive · {alert.MachineName}] {alert.Title}";
        message.Body = new TextPart("plain") { Text = alert.ToPlainText() };

        using var client = new SmtpClient();

        // Port 465 is implicit TLS; 587 and 25 negotiate STARTTLS. Getting this from the port rather
        // than from a checkbox avoids the commonest misconfiguration, which is picking the wrong one
        // and getting an error about the server "not responding".
        var options = !target.SmtpUseTls ? SecureSocketOptions.None
            : target.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;

        await client.ConnectAsync(target.SmtpHost, target.SmtpPort, options, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(target.SmtpUsername))
            await client.AuthenticateAsync(target.SmtpUsername, secret.SmtpPassword ?? string.Empty, ct)
                .ConfigureAwait(false);

        await client.SendAsync(message, ct).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
    }
}
