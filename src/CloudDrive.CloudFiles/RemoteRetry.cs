namespace CloudDrive.CloudFiles;

/// <summary>Small exponential-backoff retry wrapper for transient remote failures.</summary>
internal static class RemoteRetry
{
    public static async Task RunAsync(Func<Task> action, Action<string>? log = null, int attempts = 4)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                var delayMs = 250 * (int)Math.Pow(2, attempt - 1); // 250, 500, 1000…
                log?.Invoke($"Remote op failed (attempt {attempt}/{attempts}): {ex.Message}; retrying in {delayMs}ms");
                await Task.Delay(delayMs).ConfigureAwait(false);
            }
        }
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> action, Action<string>? log = null, int attempts = 4)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < attempts)
            {
                var delayMs = 250 * (int)Math.Pow(2, attempt - 1);
                log?.Invoke($"Remote op failed (attempt {attempt}/{attempts}): {ex.Message}; retrying in {delayMs}ms");
                await Task.Delay(delayMs).ConfigureAwait(false);
            }
        }
    }
}
