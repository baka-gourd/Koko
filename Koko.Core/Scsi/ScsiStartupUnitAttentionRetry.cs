using Koko.Core.Scsi.Commands;

namespace Koko.Core.Scsi;

public static class ScsiStartupUnitAttentionRetry
{
    private static readonly AsyncLocal<Scope?> CurrentScope = new();

    public static IDisposable SuppressPowerOnReset(int maxRetries = 2, string? scopeName = null)
    {
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetries));

        var scope = new Scope(CurrentScope.Value, maxRetries, scopeName);
        CurrentScope.Value = scope;
        return scope;
    }

    internal static bool ShouldRetryPowerOnReset(ScsiCommandResult result, int retryCount)
    {
        var scope = CurrentScope.Value;
        return scope is not null
            && retryCount < scope.MaxRetries
            && IsPowerOnResetUnitAttention(result);
    }

    internal static string? CurrentScopeName => CurrentScope.Value?.ScopeName;

    internal static int CurrentMaxRetries => CurrentScope.Value?.MaxRetries ?? 0;

    internal static bool IsPowerOnResetUnitAttention(ScsiCommandResult result)
    {
        var sense = result.SenseData;
        return result.ScsiStatus == 0x02
            && sense.Length >= 14
            && (sense[2] & 0x0F) == 0x06
            && sense[12] == 0x29
            && sense[13] == 0x01;
    }

    private sealed class Scope : IDisposable
    {
        private readonly Scope? parent;
        private bool disposed;

        public Scope(Scope? parent, int maxRetries, string? scopeName)
        {
            this.parent = parent;
            MaxRetries = maxRetries;
            ScopeName = scopeName;
        }

        public int MaxRetries { get; }

        public string? ScopeName { get; }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            CurrentScope.Value = parent;
        }
    }
}
