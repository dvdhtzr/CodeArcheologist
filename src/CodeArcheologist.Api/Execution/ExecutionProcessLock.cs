using System.Text;

namespace CodeArcheologist.Api.Execution;

/// <summary>
/// Gate C6b cross-process execution exclusion. Authority comes ONLY from an
/// operating-system lock held on an open handle for the process lifetime:
/// Windows share-mode exclusion, or <c>flock(LOCK_EX|LOCK_NB)</c> as applied
/// by .NET for <see cref="FileShare.None"/> on Linux and macOS. The lock file
/// existing, its contents, a PID, or a database heartbeat never establish
/// ownership. Network filesystems are unsupported: their lock semantics are
/// not verified, so the data directory must be local to one machine.
/// </summary>
public sealed class ExecutionProcessLock : IDisposable
{
    public const string FileName = "execution.lock";
    private FileStream? _handle;

    private ExecutionProcessLock(FileStream handle, string path)
    {
        _handle = handle;
        LockPath = path;
    }

    public string LockPath { get; }

    /// <summary>True while this object still holds the OS handle.</summary>
    public bool IsHeld => _handle is not null;

    /// <summary>
    /// Attempts a non-blocking exclusive acquisition. Returns null with a
    /// fixed, safe diagnostic when another process holds the lock or the
    /// platform cannot enforce locking. Never waits, never deletes a lock
    /// file, and never treats a stale-looking file as abandoned.
    /// </summary>
    public static ExecutionProcessLock? TryAcquire(string dataDirectory,
        out string diagnostic)
    {
        // .NET can be configured to skip Unix advisory locks entirely. Under
        // that configuration FileShare.None is not an exclusion boundary, so
        // execution ownership must fail closed rather than appear to work.
        if (AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var disabled) && disabled ||
            Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING") is "1" or "true")
        {
            diagnostic = "os-file-locking-disabled";
            return null;
        }

        var root = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileName);
        FileStream handle;
        try
        {
            handle = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.None
            });
        }
        catch (IOException)
        {
            diagnostic = "execution-lock-held-by-another-process";
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            diagnostic = "execution-lock-inaccessible";
            return null;
        }

        // Self-probe: a second exclusive open of the SAME path from this
        // process must be refused. Both Windows share modes and flock() locks
        // (per open file description) refuse it; if it succeeds the platform
        // is not enforcing the lock and ownership must not be claimed.
        try
        {
            using var probe = new FileStream(path, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
            handle.Dispose();
            diagnostic = "os-file-locking-not-enforced";
            return null;
        }
        catch (IOException)
        {
            // Expected: the lock is enforced.
        }
        catch (UnauthorizedAccessException)
        {
            // Windows may report a sharing violation this way; still enforced.
        }

        diagnostic = "execution-lock-acquired";
        return new ExecutionProcessLock(handle, path);
    }

    /// <summary>
    /// Diagnostic-only note in the lock file. Readers must never treat these
    /// bytes as authority; they help a human see who held the lock.
    /// </summary>
    public void WriteDiagnostic(Guid ownerId, long epoch)
    {
        var handle = _handle ?? throw new ObjectDisposedException(nameof(ExecutionProcessLock));
        var text = Encoding.UTF8.GetBytes(
            $"code-archeologist-execution-lock-v1\nowner={ownerId:D}\nepoch={epoch}\n" +
            $"pid={Environment.ProcessId}\nnote=diagnostic-only-not-authority\n");
        handle.SetLength(0);
        handle.Position = 0;
        handle.Write(text);
        handle.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        handle?.Dispose();
    }
}
