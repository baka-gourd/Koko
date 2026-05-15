using System.Collections.Concurrent;

namespace Koko.Core;

public class DriveSessionManager
{
    public static Lazy<DriveSessionManager> Instance { get; } = new(Initialize);
    private readonly ConcurrentDictionary<string, Entry> _drives = new(StringComparer.Ordinal);
    private DriveSessionManager() { }
    private static DriveSessionManager Initialize()
    {
        var manager = new DriveSessionManager();
        return manager;
    }

    /// <summary>
    /// 获取 DriveBase，会将引用计数 +1。
    /// 如果不存在则调用 factory 创建。
    /// </summary>
    public DriveBase Acquire(string id, Func<string, DriveBase> factory)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is null/empty.", nameof(id));
        if (factory is null) throw new ArgumentNullException(nameof(factory));

        // GetOrAdd 可能并发调用 factory；这里通过 Entry 构造与内部锁保证最终只保留一个实例并正确计数。
        var entry = _drives.GetOrAdd(id, key => new Entry(factory(key)));

        // 如果 entry 可能处于“被移除后等待释放”的窗口，这里用 entry 自己的锁做强一致处理
        lock (entry.Gate)
        {
            if (entry.IsDisposed)
            {
                // 极端并发：entry 已被释放且标记 disposed，但字典里还残留（理论上不该发生，防御性处理）
                // 重新创建并替换
                var newEntry = new Entry(factory(id));
                _drives[id] = newEntry;
                newEntry.RefCount = 1;
                return newEntry.Drive;
            }

            checked { entry.RefCount++; }
            return entry.Drive;
        }
    }

    /// <summary>
    /// 释放一次引用（引用计数 -1）。当计数到 0 时移除并 Dispose。
    /// </summary>
    public void Release(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is null/empty.", nameof(id));

        if (!_drives.TryGetValue(id, out var entry))
        {
            throw new InvalidOperationException($"No drive session for id '{id}'.");
        }

        DriveBase? toDispose = null;

        lock (entry.Gate)
        {
            if (entry.IsDisposed)
            {
                throw new InvalidOperationException($"Drive session '{id}' already disposed.");
            }

            if (entry.RefCount <= 0)
            {
                throw new InvalidOperationException($"Drive session '{id}' refcount underflow.");
            }

            entry.RefCount--;

            if (entry.RefCount == 0)
            {
                _drives.TryRemove(id, out _);

                entry.IsDisposed = true;
                toDispose = entry.Drive;
            }
        }

        toDispose?.Dispose();
    }

    /// <summary>
    /// 推荐使用方式：using var lease = manager.Lease(id, factory);
    /// lease.Drive 为获取到的 DriveBase，using 结束自动 Release。
    /// </summary>
    public DriveLease Lease(string id, Func<string, DriveBase> factory)
        => new DriveLease(this, id, Acquire(id, factory));

    public bool TryGetRefCount(string id, out int refCount)
    {
        refCount = 0;
        if (!_drives.TryGetValue(id, out var entry)) return false;
        lock (entry.Gate)
        {
            if (entry.IsDisposed) return false;
            refCount = entry.RefCount;
            return true;
        }
    }

    private sealed class Entry(DriveBase drive)
    {
        public object Gate { get; } = new object();
        public DriveBase Drive { get; } = drive ?? throw new ArgumentNullException(nameof(drive));
        public int RefCount = 0;
        public bool IsDisposed;
    }

    public readonly struct DriveLease(DriveSessionManager manager, string id, DriveBase drive) : IDisposable
    {
        public DriveBase Drive { get; } = drive;

        public void Dispose()
            => manager.Release(id);
    }
}