// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EssvbSub.Core.Storage;

/// <summary>
/// Cross-platform user storage paths for Windows 11 (%LOCALAPPDATA%\ESSVB-sub)
/// and CachyOS / Linux (~/.config/essvb-sub and ~/.local/share/essvb-sub).
/// </summary>
public static class EssvbPaths
{
    public static string ConfigDir { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESSVB-sub")
        : Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } cfg
                ? cfg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "essvb-sub");

    public static string DataDir { get; } = OperatingSystem.IsWindows()
        ? ConfigDir
        : Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
                ? data
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "essvb-sub");

    public static string CacheDir => Path.Combine(DataDir, "cache");
    public static string GlossariesDir => Path.Combine(ConfigDir, "glossaries");
    public static string ProfilesDir => Path.Combine(DataDir, "tizen-profiles");
    public static string UpdatesDir => Path.Combine(DataDir, "updates");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string VaultFile => Path.Combine(ConfigDir, "vault.dat");
    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
}

/// <summary>
/// Zero-dependency encrypted local secret store.
/// Uses Windows DPAPI (crypt32.dll CryptProtectData) on Windows 11 and AES-256-GCM with 0600 keyfile on CachyOS/Linux.
/// </summary>
public sealed class SecureVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("essvb-sub-vault-entropy-v1");
    private readonly string _vaultPath;
    private readonly string _linuxKeyPath;
    private readonly object _gate = new();

    public SecureVault(string? vaultPath = null)
    {
        _vaultPath = vaultPath ?? EssvbPaths.VaultFile;
        _linuxKeyPath = _vaultPath + ".key";
    }

    public static SecureVault Default { get; } = new();

    public void Save<T>(string key, T value)
    {
        lock (_gate)
        {
            var doc = LoadAll();
            doc[key] = JsonSerializer.SerializeToNode(value);
            SaveAll(doc);
        }
    }

    public T? Load<T>(string key)
    {
        lock (_gate)
        {
            var doc = LoadAll();
            if (doc[key] is not { } node)
                return default;
            try { return node.Deserialize<T>(); }
            catch (JsonException) { return default; }
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            var doc = LoadAll();
            if (doc.Remove(key))
                SaveAll(doc);
        }
    }

    private JsonObject LoadAll()
    {
        if (!File.Exists(_vaultPath))
            return [];

        try
        {
            byte[] cipher = File.ReadAllBytes(_vaultPath);
            byte[] plain = OperatingSystem.IsWindows()
                ? DpapiUnprotect(cipher)
                : AesGcmUnprotect(cipher, GetOrCreateLinuxKey());

            return JsonNode.Parse(plain) as JsonObject ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveAll(JsonObject doc)
    {
        string dir = Path.GetDirectoryName(_vaultPath)!;
        Directory.CreateDirectory(dir);
        byte[] plain = Encoding.UTF8.GetBytes(doc.ToJsonString());
        byte[] cipher = OperatingSystem.IsWindows()
            ? DpapiProtect(plain)
            : AesGcmProtect(plain, GetOrCreateLinuxKey());

        string tmp = _vaultPath + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _vaultPath, overwrite: true);
    }

    private byte[] GetOrCreateLinuxKey()
    {
        if (File.Exists(_linuxKeyPath))
        {
            byte[] existing = File.ReadAllBytes(_linuxKeyPath);
            if (existing.Length == 32)
                return existing;
        }

        string dir = Path.GetDirectoryName(_linuxKeyPath)!;
        Directory.CreateDirectory(dir);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        string tmp = _linuxKeyPath + ".tmp";
        File.WriteAllBytes(tmp, key);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _linuxKeyPath, overwrite: true);
        return key;
    }

    private static byte[] AesGcmProtect(byte[] plain, byte[] key)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize];
        byte[] cipher = new byte[plain.Length];

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plain, cipher, tag, Entropy);

        byte[] output = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, output, nonce.Length + tag.Length, cipher.Length);
        return output;
    }

    private static byte[] AesGcmUnprotect(byte[] payload, byte[] key)
    {
        int nonceLen = AesGcm.NonceByteSizes.MaxSize;
        int tagLen = AesGcm.TagByteSizes.MaxSize;
        if (payload.Length < nonceLen + tagLen)
            throw new CryptographicException("Invalid vault payload.");

        var nonce = payload.AsSpan(0, nonceLen);
        var tag = payload.AsSpan(nonceLen, tagLen);
        var cipher = payload.AsSpan(nonceLen + tagLen);
        byte[] plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, tagLen);
        aes.Decrypt(nonce, cipher, tag, plain, Entropy);
        return plain;
    }

    private static unsafe byte[] DpapiProtect(byte[] plain)
    {
        fixed (byte* pData = plain)
        fixed (byte* pEntropy = Entropy)
        {
            var dataIn = new DataBlob { cbData = (uint)plain.Length, pbData = (nint)pData };
            var entropyBlob = new DataBlob { cbData = (uint)Entropy.Length, pbData = (nint)pEntropy };
            if (!CryptProtectData(ref dataIn, "ESSVB-sub", ref entropyBlob, nint.Zero, nint.Zero, 0x01, out var dataOut))
                throw new CryptographicException("Windows DPAPI CryptProtectData failed.");

            try
            {
                var result = new byte[dataOut.cbData];
                Marshal.Copy(dataOut.pbData, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(dataOut.pbData);
            }
        }
    }

    private static unsafe byte[] DpapiUnprotect(byte[] cipher)
    {
        fixed (byte* pCipher = cipher)
        fixed (byte* pEntropy = Entropy)
        {
            var dataIn = new DataBlob { cbData = (uint)cipher.Length, pbData = (nint)pCipher };
            var entropyBlob = new DataBlob { cbData = (uint)Entropy.Length, pbData = (nint)pEntropy };
            if (!CryptUnprotectData(ref dataIn, nint.Zero, ref entropyBlob, nint.Zero, nint.Zero, 0x01, out var dataOut))
                throw new CryptographicException("Windows DPAPI CryptUnprotectData failed.");

            try
            {
                var result = new byte[dataOut.cbData];
                Marshal.Copy(dataOut.pbData, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(dataOut.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint cbData;
        public nint pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        nint pvReserved,
        nint pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        nint ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        nint pvReserved,
        nint pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint hMem);
}
