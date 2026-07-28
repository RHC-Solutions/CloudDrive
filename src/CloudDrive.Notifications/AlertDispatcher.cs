using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using CloudDrive.Core.Models;
using CloudDrive.Core.Stores;

namespace CloudDrive.Notifications;

/// <summary>
/// Decides which alerts go where, suppresses repeats, and keeps trying until delivery succeeds.
///
/// Runs inside the Windows service rather than the tray app, which is the entire point: an alert
/// about a mount that failed at 3am is worthless if it needs someone signed in to be sent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AlertDispatcher : IAsyncDisposable
{
    private readonly Func<(AppSettings Settings, Func<Guid, NotificationSecret?> Secrets)> _configure;
    private readonly Dictionary<NotificationChannelKind, INotificationChannel> _channels;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;

    /// <summary>When each dedupe key last went out, and how many repeats were swallowed since.</summary>
    private readonly ConcurrentDictionary<string, Suppression> _suppressed = new(StringComparer.Ordinal);

    private readonly string _spoolDir;

    public AlertDispatcher(
        Func<(AppSettings, Func<Guid, NotificationSecret?>)> configure,
        string? spoolDirectory = null,
        Action<string>? log = null)
    {
        _configure = configure;
        _log = log;
        _spoolDir = spoolDirectory ?? AppPaths.SpoolDir;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _channels = new Dictionary<NotificationChannelKind, INotificationChannel>
        {
            [NotificationChannelKind.Telegram] = new TelegramChannel(_http),
            [NotificationChannelKind.Slack] = new SlackChannel(_http),
            [NotificationChannelKind.Email] = new EmailChannel(),
        };
    }

    public INotificationChannel ChannelFor(NotificationChannelKind kind) => _channels[kind];

    /// <summary>Validates a target's configuration without sending anything.</summary>
    public string? Validate(NotificationTarget target, NotificationSecret? secret) =>
        _channels.TryGetValue(target.Kind, out var channel)
            ? channel.Validate(target, secret)
            : $"Unknown channel type '{target.Kind}'.";

    /// <summary>
    /// Routes an alert to every target that wants it.
    ///
    /// Never throws. A failure inside the alerting path must not take down the operation that raised
    /// the alert — the mount reconciler calling this after a failed mount should not itself fail
    /// because Slack is down.
    /// </summary>
    public async Task RaiseAsync(Alert alert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        try
        {
            var (settings, secrets) = _configure();
            var notifications = settings.Notifications;
            if (!notifications.Enabled || notifications.Targets.Count == 0) return;

            // Work out who wants it *before* consuming the cooldown. The other order meant an alert
            // that nothing was listening for still marked its key as just-sent, so the first alert
            // after someone added a target — or raised a target's severity floor — was suppressed for
            // a cooldown period despite never having been delivered.
            var targets = notifications.Targets.Where(t => Accepts(t, alert)).ToList();
            if (targets.Count == 0) return;

            if (ShouldSuppress(alert, notifications, out var suppressedCount)) return;
            alert.SuppressedCount = suppressedCount;

            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                await DeliverAsync(alert, target, secrets(target.Id), spoolOnFailure: true, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Alerting failed for '{alert.Title}': {ex.Message}");
        }
    }

    /// <summary>Sends one alert to one target, ignoring filters. Used by the "send test" button.</summary>
    public async Task SendDirectAsync(
        Alert alert, NotificationTarget target, NotificationSecret? secret, CancellationToken ct = default)
    {
        if (!_channels.TryGetValue(target.Kind, out var channel))
            throw new InvalidOperationException($"Unknown channel type '{target.Kind}'.");

        var problem = channel.Validate(target, secret);
        if (problem is not null) throw new InvalidOperationException(problem);

        await channel.SendAsync(alert, target, secret ?? new NotificationSecret(), ct).ConfigureAwait(false);
    }

    private async Task DeliverAsync(
        Alert alert, NotificationTarget target, NotificationSecret? secret, bool spoolOnFailure,
        CancellationToken ct)
    {
        if (!_channels.TryGetValue(target.Kind, out var channel)) return;

        try
        {
            await channel.SendAsync(alert, target, secret ?? new NotificationSecret(), ct).ConfigureAwait(false);
            _log?.Invoke($"Alert '{alert.Title}' sent to {target.Name} ({target.Kind}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"Alert '{alert.Title}' could not be sent to {target.Name}: {ex.Message}");
            // Spool rather than drop. The commonest reason an alert fails to send is the same network
            // problem that caused the alert, and that will usually have cleared in a few minutes.
            if (spoolOnFailure) Spool(alert, target.Id);
        }
    }

    /// <summary>
    /// Whether this target wants this alert: enabled, severe enough, and of a type it subscribes to.
    /// </summary>
    private static bool Accepts(NotificationTarget target, Alert alert)
    {
        if (!target.Enabled) return false;
        if (alert.Severity < target.MinimumSeverity) return false;
        // An empty filter means "everything at or above the severity floor", which is the sane
        // default for a target someone just created.
        return target.EventFilter.Count == 0 || target.EventFilter.Contains(alert.Kind);
    }

    /// <summary>
    /// Applies the cooldown, counting how many repeats were swallowed so the eventual message can
    /// say so. A flapping mount then produces one message every cooldown period reporting the flap,
    /// rather than one message per flap.
    /// </summary>
    private bool ShouldSuppress(Alert alert, NotificationSettings settings, out int suppressedCount)
    {
        suppressedCount = 0;

        var cooldown = TimeSpan.FromMinutes(Math.Max(0, settings.DedupeCooldownMinutes));
        if (cooldown <= TimeSpan.Zero) return false;

        var now = DateTime.UtcNow;
        var key = alert.DedupeKey;

        while (true)
        {
            if (!_suppressed.TryGetValue(key, out var existing))
            {
                if (_suppressed.TryAdd(key, new Suppression(now, 0))) return false;
                continue; // lost the race; re-read
            }

            if (now - existing.LastSentUtc >= cooldown)
            {
                if (_suppressed.TryUpdate(key, new Suppression(now, 0), existing))
                {
                    suppressedCount = existing.Swallowed;
                    return false;
                }
                continue;
            }

            _suppressed.TryUpdate(key, existing with { Swallowed = existing.Swallowed + 1 }, existing);
            return true;
        }
    }

    /// <summary>
    /// Clears the cooldown for a key, so a recovery message is not swallowed because the failure it
    /// resolves went out moments ago. "Mount restored" is exactly the alert a user most wants
    /// promptly.
    ///
    /// Uses <see cref="Alert.KeyFor"/> so it cannot drift from how the key was written in the first
    /// place — it previously did, and account-scoped resets silently matched nothing.
    /// </summary>
    public void ResetSuppression(AlertKind kind, Guid? mappingId, Guid? accountId = null) =>
        _suppressed.TryRemove(Alert.KeyFor(kind, mappingId, accountId), out _);

    // ---------------------------------------------------------------- Spool -------------------

    private void Spool(Alert alert, Guid targetId)
    {
        try
        {
            Directory.CreateDirectory(_spoolDir);
            // Timestamp-prefixed so the retry pass replays in the order things happened.
            var name = $"{alert.TimestampUtc:yyyyMMddHHmmssfff}-{targetId:N}-{alert.Id:N}.json";
            var entry = new SpoolEntry { TargetId = targetId, Alert = alert };
            File.WriteAllText(Path.Combine(_spoolDir, name), JsonSerializer.Serialize(entry));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not spool the alert: {ex.Message}");
        }
    }

    /// <summary>
    /// Retries spooled alerts and discards ones too old to be useful.
    ///
    /// Called on a timer by the service. Entries are deleted on success, and on a repeated failure
    /// they stay put until the retention window expires — a week-old "mount failed" helps nobody and
    /// would only bury whatever is wrong now.
    /// </summary>
    public async Task<int> FlushSpoolAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_spoolDir)) return 0;

        var (settings, secrets) = _configure();
        var retention = TimeSpan.FromHours(Math.Max(1, settings.Notifications.SpoolRetentionHours));
        var targets = settings.Notifications.Targets.ToDictionary(t => t.Id);

        var delivered = 0;

        foreach (var file in Directory.EnumerateFiles(_spoolDir, "*.json").OrderBy(f => f))
        {
            ct.ThrowIfCancellationRequested();

            SpoolEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<SpoolEntry>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            }
            catch
            {
                // Unreadable: nothing will ever make it readable, so stop carrying it.
                TryDelete(file);
                continue;
            }

            if (entry?.Alert is null) { TryDelete(file); continue; }

            if (DateTime.UtcNow - entry.Alert.TimestampUtc > retention)
            {
                _log?.Invoke($"Discarding a spooled alert older than {retention.TotalHours:0} hours: {entry.Alert.Title}");
                TryDelete(file);
                continue;
            }

            // The target may have been deleted since the alert was spooled.
            if (!targets.TryGetValue(entry.TargetId, out var target)) { TryDelete(file); continue; }

            try
            {
                if (!_channels.TryGetValue(target.Kind, out var channel)) { TryDelete(file); continue; }
                await channel
                    .SendAsync(entry.Alert, target, secrets(target.Id) ?? new NotificationSecret(), ct)
                    .ConfigureAwait(false);
                TryDelete(file);
                delivered++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Still failing. Leave it; the next pass tries again until retention expires.
            }
        }

        if (delivered > 0) _log?.Invoke($"Delivered {delivered} spooled alert(s).");
        return delivered;
    }

    /// <summary>How many alerts are waiting to be delivered, for the UI's status line.</summary>
    public int SpoolDepth()
    {
        try { return Directory.Exists(_spoolDir) ? Directory.EnumerateFiles(_spoolDir, "*.json").Count() : 0; }
        catch { return 0; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* next pass */ }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private readonly record struct Suppression(DateTime LastSentUtc, int Swallowed);

    private sealed class SpoolEntry
    {
        public Guid TargetId { get; set; }

        public Alert? Alert { get; set; }
    }
}
