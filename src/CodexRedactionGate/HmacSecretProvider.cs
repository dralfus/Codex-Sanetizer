using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CodexRedactionGate;

public interface IDataProtector
{
    string ProtectionKind { get; }

    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedData);
}

public sealed class DpapiProtectedHmacSecretProvider
{
    public const int SecretSizeBytes = 32;
    public const string DefaultSecretFileName = "hmac-secret.dpapi";

    private readonly IDataProtector _dataProtector;

    public DpapiProtectedHmacSecretProvider(string secretFilePath, IDataProtector dataProtector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretFilePath);
        ArgumentNullException.ThrowIfNull(dataProtector);

        SecretFilePath = secretFilePath;
        _dataProtector = dataProtector;
    }

    public string SecretFilePath { get; }

    public string ProtectionKind => _dataProtector.ProtectionKind;

    public static string DefaultSecretFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexRedactionGate",
            DefaultSecretFileName);
    }

    public static DpapiProtectedHmacSecretProvider CreateProduction()
    {
        return new DpapiProtectedHmacSecretProvider(
            DefaultSecretFilePath(),
            new WindowsDpapiDataProtector());
    }

    public byte[] GetOrCreateSecret()
    {
        if (File.Exists(SecretFilePath))
        {
            return LoadExistingSecret();
        }

        var secret = RandomNumberGenerator.GetBytes(SecretSizeBytes);
        var protectedSecret = _dataProtector.Protect(secret);

        if (protectedSecret.Length == 0)
        {
            throw new InvalidOperationException("Protected HMAC secret must not be empty.");
        }

        var directory = Path.GetDirectoryName(SecretFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            File.WriteAllBytes(SecretFilePath, protectedSecret);
        }
        catch (IOException) when (File.Exists(SecretFilePath))
        {
            return LoadExistingSecret();
        }

        return (byte[])secret.Clone();
    }

    private byte[] LoadExistingSecret()
    {
        var protectedSecret = File.ReadAllBytes(SecretFilePath);
        if (protectedSecret.Length == 0)
        {
            throw new InvalidOperationException("Protected HMAC secret file is empty.");
        }

        var secret = _dataProtector.Unprotect(protectedSecret);
        if (secret.Length != SecretSizeBytes)
        {
            throw new InvalidOperationException("HMAC secret has an invalid length.");
        }

        return secret;
    }
}

public sealed class WindowsDpapiDataProtector : IDataProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexRedactionGate.HmacSecret.v1");

    public string ProtectionKind => "windows_dpapi";

    public byte[] Protect(byte[] plaintext)
    {
        return ProtectOrUnprotect(plaintext, protect: true);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        return ProtectOrUnprotect(protectedData, protect: false);
    }

    private static byte[] ProtectOrUnprotect(byte[] input, bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Windows DPAPI is required for production HMAC secret protection.");
        }

        DataBlob inputBlob = default;
        DataBlob entropyBlob = default;
        DataBlob outputBlob = default;
        var descriptionPointer = IntPtr.Zero;

        try
        {
            inputBlob = CreateManagedBlob(input);
            entropyBlob = CreateManagedBlob(Entropy);

            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    null,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    out descriptionPointer,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);

            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return CopyBlob(outputBlob);
        }
        finally
        {
            FreeManagedBlob(inputBlob);
            FreeManagedBlob(entropyBlob);
            FreeNativeBlob(outputBlob);

            if (descriptionPointer != IntPtr.Zero)
            {
                LocalFree(descriptionPointer);
            }
        }
    }

    private static DataBlob CreateManagedBlob(byte[] data)
    {
        var dataPointer = IntPtr.Zero;

        try
        {
            dataPointer = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, dataPointer, data.Length);
            return new DataBlob
            {
                Count = data.Length,
                Data = dataPointer
            };
        }
        catch
        {
            if (dataPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(dataPointer);
            }

            throw;
        }
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        var data = new byte[blob.Count];
        Marshal.Copy(blob.Data, data, 0, blob.Count);
        return data;
    }

    private static void FreeManagedBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    private static void FreeNativeBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            LocalFree(blob.Data);
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Count;
        public IntPtr Data;
    }
}
