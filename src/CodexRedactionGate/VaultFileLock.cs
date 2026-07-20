using System;
using System.IO;
using System.Threading;

namespace CodexRedactionGate;

internal sealed class VaultFileLock : IDisposable
{
    private const int MaxAttempts = 100;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly FileStream _stream;

    private VaultFileLock(FileStream stream)
    {
        _stream = stream;
    }

    public static VaultFileLock Acquire(string vaultFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultFilePath);

        var directory = Path.GetDirectoryName(vaultFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lockPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory,
            $"{Path.GetFileName(vaultFilePath)}.lock");

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return new VaultFileLock(stream);
            }
            catch (IOException) when (attempt < MaxAttempts - 1)
            {
                Thread.Sleep(RetryDelay);
            }
        }

        throw new IOException("Timed out acquiring mapping vault file lock.");
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}
