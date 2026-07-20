using System;
using System.IO;

namespace CodexRedactionGate;

internal static class AtomicFileWriter
{
    public static void WriteAllBytes(string filePath, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(bytes);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory,
            $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(tempPath, bytes);

            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, filePath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
