using CloudDrive.Core.Models;
using CloudDrive.Notifications;

namespace CloudDrive.Tests;

/// <summary>
/// Covers the routing and suppression rules, which decide whether a real problem reaches a human.
/// Every test here corresponds to a bug found in review.
/// </summary>
public class AlertDispatcherTests
{
    private static (AlertDispatcher Dispatcher, AppSettings Settings, string Spool) Create(
        Action<NotificationSettings>? configure = null)
    {
        var spool = Path.Combine(Path.GetTempPath(), $"clouddrive-spool-{Guid.NewGuid():N}");
        var settings = new AppSettings();
        settings.Notifications.DedupeCooldownMinutes = 15;
        configure?.Invoke(settings.Notifications);

        var dispatcher = new AlertDispatcher(
            () => (settings, _ => null), spoolDirectory: spool);

        return (dispatcher, settings, spool);
    }

    private static NotificationTarget Target(
        AlertSeverity floor = AlertSeverity.Warning, params AlertKind[] filter) => new()
    {
        Name = "Ops",
        Kind = NotificationChannelKind.Telegram,
        Enabled = true,
        MinimumSeverity = floor,
        EventFilter = [.. filter],
        TelegramChatId = "123",
    };

    /// <summary>
    /// The dedupe key and the reset key must be built the same way. They were not: the key fell back to
    /// the account id when there was no mapping id, while the reset built its key from the mapping id
    /// alone — so clearing the cooldown for an account-scoped alert looked up a key that had never been
    /// written, and the "it is fixed" message stayed suppressed.
    /// </summary>
    [Fact]
    public void Dedupe_key_matches_what_reset_suppression_looks_up()
    {
        var accountId = Guid.NewGuid();

        var alert = Alert.Error(AlertKind.ReauthRequired, "t", "m");
        alert.AccountId = accountId;

        Assert.Equal(
            Alert.KeyFor(AlertKind.ReauthRequired, mappingId: null, accountId),
            alert.DedupeKey);
    }

    [Fact]
    public void A_mapping_scoped_key_ignores_the_account()
    {
        var mappingId = Guid.NewGuid();
        var alert = Alert.Error(AlertKind.MountFailed, "t", "m");
        alert.MappingId = mappingId;
        alert.AccountId = Guid.NewGuid();

        Assert.Equal(Alert.KeyFor(AlertKind.MountFailed, mappingId), alert.DedupeKey);
    }

    [Fact]
    public void Keys_differ_by_kind_and_by_subject()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.NotEqual(Alert.KeyFor(AlertKind.MountFailed, a), Alert.KeyFor(AlertKind.MountLost, a));
        Assert.NotEqual(Alert.KeyFor(AlertKind.MountFailed, a), Alert.KeyFor(AlertKind.MountFailed, b));
    }

    /// <summary>
    /// An alert nothing is listening for must not consume its cooldown. It used to: suppression was
    /// recorded before routing, so the first alert after a target was added — or after its severity
    /// floor was lowered — was silently swallowed for a full cooldown despite never being delivered.
    /// </summary>
    [Fact]
    public async Task An_alert_with_no_matching_target_does_not_consume_the_cooldown()
    {
        var (dispatcher, settings, spool) = Create();
        await using var _ = dispatcher;

        try
        {
            // Nothing configured at all: this must be a complete no-op.
            await dispatcher.RaiseAsync(Alert.Error(AlertKind.MountFailed, "first", "m"));

            // Now somebody adds a target. An Error must still get through.
            settings.Notifications.Targets.Add(Target());

            await dispatcher.RaiseAsync(Alert.Error(AlertKind.MountFailed, "second", "m"));

            // Delivery fails (there is no real Telegram), so a spooled entry is the proof it was
            // routed rather than suppressed.
            Assert.True(dispatcher.SpoolDepth() > 0,
                "the second alert should have been routed and spooled, not suppressed");
        }
        finally
        {
            try { Directory.Delete(spool, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task Alerts_below_a_targets_severity_floor_are_not_delivered()
    {
        var (dispatcher, settings, spool) = Create();
        await using var _ = dispatcher;
        settings.Notifications.Targets.Add(Target(AlertSeverity.Error));

        try
        {
            await dispatcher.RaiseAsync(Alert.Info(AlertKind.MountSucceeded, "info", "m"));
            Assert.Equal(0, dispatcher.SpoolDepth());

            await dispatcher.RaiseAsync(Alert.Error(AlertKind.MountFailed, "error", "m"));
            Assert.True(dispatcher.SpoolDepth() > 0);
        }
        finally
        {
            try { Directory.Delete(spool, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task An_event_filter_excludes_other_kinds()
    {
        var (dispatcher, settings, spool) = Create();
        await using var _ = dispatcher;
        settings.Notifications.Targets.Add(Target(AlertSeverity.Info, AlertKind.ReauthRequired));

        try
        {
            await dispatcher.RaiseAsync(Alert.Error(AlertKind.MountFailed, "not wanted", "m"));
            Assert.Equal(0, dispatcher.SpoolDepth());

            await dispatcher.RaiseAsync(Alert.Error(AlertKind.ReauthRequired, "wanted", "m"));
            Assert.True(dispatcher.SpoolDepth() > 0);
        }
        finally
        {
            try { Directory.Delete(spool, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task Disabled_notifications_send_nothing()
    {
        var (dispatcher, settings, spool) = Create(n => n.Enabled = false);
        await using var _ = dispatcher;
        settings.Notifications.Targets.Add(Target());

        try
        {
            await dispatcher.RaiseAsync(Alert.Error(AlertKind.MountFailed, "t", "m"));
            Assert.Equal(0, dispatcher.SpoolDepth());
        }
        finally
        {
            try { Directory.Delete(spool, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Validation_rejects_a_telegram_target_with_no_token()
    {
        var (dispatcher, _, spool) = Create();

        try
        {
            var problem = dispatcher.Validate(Target(), secret: null);
            Assert.NotNull(problem);
            Assert.Contains("token", problem, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(spool, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Validation_rejects_an_email_target_with_no_recipients()
    {
        var (dispatcher, _, spool) = Create();

        try
        {
            var target = new NotificationTarget
            {
                Kind = NotificationChannelKind.Email,
                SmtpHost = "smtp.example.com",
                EmailFrom = "cloud@example.com",
            };

            var problem = dispatcher.Validate(target, secret: null);
            Assert.NotNull(problem);
            Assert.Contains("recipient", problem, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(spool, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Validation_accepts_an_unauthenticated_relay()
    {
        var (dispatcher, _, spool) = Create();

        try
        {
            // A relay with no credentials is legitimate on an internal network, so a missing password
            // is only a problem once a username says authentication is expected.
            var target = new NotificationTarget
            {
                Kind = NotificationChannelKind.Email,
                SmtpHost = "smtp.internal",
                EmailFrom = "cloud@example.com",
                EmailTo = ["ops@example.com"],
            };

            Assert.Null(dispatcher.Validate(target, secret: null));
        }
        finally
        {
            try { Directory.Delete(spool, recursive: true); } catch { /* temp */ }
        }
    }
}
