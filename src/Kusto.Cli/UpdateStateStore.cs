using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

internal sealed class UpdateStateStore
{
    private static readonly TimeSpan StaleLockThreshold = TimeSpan.FromMinutes(15);
    private readonly string _statePath;
    private readonly string _lockPath;
    private readonly ILogger<UpdateStateStore> _logger;
    private FileStream? _lockStream;

    public UpdateStateStore(ILogger<UpdateStateStore> logger)
        : this(logger, AppPaths.GetUpdateStatePath(), AppPaths.GetUpdateLockPath())
    {
    }

    internal UpdateStateStore(
        ILogger<UpdateStateStore> logger,
        string statePath,
        string lockPath)
    {
        _logger = logger;
        _statePath = statePath;
        _lockPath = lockPath;
    }

    public string StatePath => _statePath;
    public string LockPath => _lockPath;

    public UpdateState Load()
    {
        if (!File.Exists(_statePath))
        {
            return new UpdateState();
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize(json, KustoJsonSerializerContext.Default.UpdateState)
                ?? new UpdateState();
        }
        catch (JsonException ex)
        {
            throw new UserFacingException(
                $"The update state file at '{_statePath}' is malformed. Delete it and retry the update.",
                ex);
        }
    }

    public void Save(UpdateState state)
    {
        var json = JsonSerializer.Serialize(state, KustoJsonSerializerContext.Default.UpdateState);
        AtomicWrite(_statePath, json);
    }

    public void Clear()
    {
        if (File.Exists(_statePath))
        {
            File.Delete(_statePath);
        }
    }

    public bool TryAcquireLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        try
        {
            _lockStream = OpenLock();
            WriteLockIdentity(_lockStream);
            return true;
        }
        catch (IOException)
        {
            if (!IsStaleLock())
            {
                return false;
            }

            _logger.LogWarning("Recovering stale update lock at {Path}", _lockPath);
            try
            {
                File.Delete(_lockPath);
                _lockStream = OpenLock();
                WriteLockIdentity(_lockStream);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    public void ReleaseLock()
    {
        if (_lockStream is null)
        {
            return;
        }

        _lockStream?.Dispose();
        _lockStream = null;
        try
        {
            File.Delete(_lockPath);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete update lock {Path}", _lockPath);
        }
    }

    private FileStream OpenLock()
        => new(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private bool IsStaleLock()
    {
        try
        {
            var lines = File.ReadAllLines(_lockPath);
            if (lines.Length >= 2
                && int.TryParse(lines[0], out var pid)
                && DateTimeOffset.TryParse(lines[1], out var expectedStartTime))
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    var actualStartTime =
                        new DateTimeOffset(process.StartTime.ToUniversalTime());
                    return Math.Abs((actualStartTime - expectedStartTime).TotalSeconds) > 1;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            }

            return DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_lockPath)
                > StaleLockThreshold;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void WriteLockIdentity(FileStream stream)
    {
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.WriteLine(Environment.ProcessId);
        writer.WriteLine(
            System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O"));
        writer.Flush();
    }

    private static void AtomicWrite(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
