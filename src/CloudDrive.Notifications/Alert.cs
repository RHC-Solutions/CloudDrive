using CloudDrive.Core.Models;

namespace CloudDrive.Notifications;

/// <summary>
/// Something worth telling a human about.
///
/// Carries the identifiers rather than the objects so it can be serialised into the on-disk spool
/// and still make sense after a service restart, when the mapping it refers to may have been edited.
/// </summary>
public sealed class Alert
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public AlertKind Kind { get; set; }

    public AlertSeverity Severity { get; set; } = AlertSeverity.Info;

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>One-line summary. Becomes the email subject and the first line of a chat message.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The detail. May run to several lines.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The mapping this concerns, when it concerns one.</summary>
    public Guid? MappingId { get; set; }

    /// <summary>Mapping name at the time of the alert, so the message reads well later.</summary>
    public string? MappingName { get; set; }

    public Guid? AccountId { get; set; }

    public string? AccountName { get; set; }

    public string MachineName { get; set; } = Environment.MachineName;

    /// <summary>How many times this was raised while suppressed. Reported so a flap is visible.</summary>
    public int SuppressedCount { get; set; }

    /// <summary>
    /// The key deduplication groups on: the same problem with the same mapping is one alert, however
    /// many times it fires. Without this a mount flapping every few seconds sends hundreds of
    /// messages, the channel gets muted, and the alerting is worse than useless.
    /// </summary>
    public string DedupeKey => KeyFor(Kind, MappingId, AccountId);

    /// <summary>
    /// Builds a dedupe key, so suppressing and un-suppressing cannot disagree.
    ///
    /// They did: the property fell back to the account id when there was no mapping id, while
    /// <c>AlertDispatcher.ResetSuppression</c> built the key from the mapping id alone. Clearing the
    /// cooldown for an account-scoped alert — a re-authorisation, say — therefore looked up a key that
    /// had never been written, so the "it is fixed" message stayed suppressed for the whole cooldown.
    /// </summary>
    public static string KeyFor(AlertKind kind, Guid? mappingId, Guid? accountId = null) =>
        $"{kind}:{mappingId?.ToString("N") ?? accountId?.ToString("N") ?? "-"}";

    public static Alert Info(AlertKind kind, string title, string message) =>
        new() { Kind = kind, Severity = AlertSeverity.Info, Title = title, Message = message };

    public static Alert Warning(AlertKind kind, string title, string message) =>
        new() { Kind = kind, Severity = AlertSeverity.Warning, Title = title, Message = message };

    public static Alert Error(AlertKind kind, string title, string message) =>
        new() { Kind = kind, Severity = AlertSeverity.Error, Title = title, Message = message };

    public Alert For(Mapping mapping, Account? account = null)
    {
        MappingId = mapping.Id;
        MappingName = mapping.Name;
        if (account is not null)
        {
            AccountId = account.Id;
            AccountName = account.Name;
        }
        return this;
    }

    public Alert For(Account account)
    {
        AccountId = account.Id;
        AccountName = account.Name;
        return this;
    }

    /// <summary>Plain-text rendering, shared by every channel as a fallback.</summary>
    public string ToPlainText()
    {
        var lines = new List<string> { Title, string.Empty, Message, string.Empty };

        if (!string.IsNullOrWhiteSpace(MappingName)) lines.Add($"Mapping: {MappingName}");
        if (!string.IsNullOrWhiteSpace(AccountName)) lines.Add($"Account: {AccountName}");
        lines.Add($"Machine: {MachineName}");
        lines.Add($"Time:    {TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} local");

        if (SuppressedCount > 0)
            lines.Add($"Repeats: this also happened {SuppressedCount} more time(s) since the last message.");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>An emoji for the severity, used by the chat channels.</summary>
    public string SeverityIcon => Severity switch
    {
        AlertSeverity.Error => "🔴",
        AlertSeverity.Warning => "🟡",
        _ => "🟢",
    };
}
