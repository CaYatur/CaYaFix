// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace CaYaFix.Core;

public sealed class ProtectedIntegrityService : IIntegrityService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CaYaFix.ManifestIntegrity.v1");
    private readonly byte[] _key;

    public ProtectedIntegrityService(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var securityDirectory = Path.Combine(Path.GetFullPath(dataRoot), "Security");
        Directory.CreateDirectory(securityDirectory);
        if (HasReparsePoint(securityDirectory))
        {
            throw new CryptographicException("The CaYaFix security path cannot contain a reparse point.");
        }
        var keyPath = Path.Combine(securityDirectory, "integrity.key");
        if (File.Exists(keyPath) && File.GetAttributes(keyPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new CryptographicException("The CaYaFix integrity key cannot be a reparse point.");
        }
        _key = LoadOrCreateKey(keyPath);
    }

    public string Sign(string purpose, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var purposeKey = DerivePurposeKey(purpose);
        try
        {
            using var hmac = new HMACSHA256(purposeKey);
            return Convert.ToBase64String(hmac.ComputeHash(content.ToArray()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(purposeKey);
        }
    }

    public bool Verify(string purpose, ReadOnlySpan<byte> content, string signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        byte[]? expected = null;
        byte[]? actual = null;
        try
        {
            expected = Convert.FromBase64String(Sign(purpose, content));
            actual = Convert.FromBase64String(signature.Trim());
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            if (actual is not null) CryptographicOperations.ZeroMemory(actual);
        }
    }

    private byte[] DerivePurposeKey(string purpose)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(purpose));
    }

    private static byte[] LoadOrCreateKey(string keyPath)
    {
        if (File.Exists(keyPath)) return Unprotect(ReadProtectedKey(keyPath));

        var generated = RandomNumberGenerator.GetBytes(32);
        var protectedKey = ProtectedData.Protect(generated, Entropy, DataProtectionScope.CurrentUser);
        var temporary = keyPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                stream.Write(protectedKey);
                stream.Flush(true);
            }

            File.Move(temporary, keyPath, false);
            TryHide(keyPath);
            return generated;
        }
        catch (IOException) when (File.Exists(keyPath))
        {
            CryptographicOperations.ZeroMemory(generated);
            return Unprotect(ReadProtectedKey(keyPath));
        }
        finally
        {
            TryDelete(temporary);
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    private static byte[] ReadProtectedKey(string keyPath)
    {
        var length = new FileInfo(keyPath).Length;
        if (length is <= 0 or > 4096)
        {
            throw new CryptographicException("The protected CaYaFix integrity key has an invalid size.");
        }
        return File.ReadAllBytes(keyPath);
    }

    private static byte[] Unprotect(byte[] protectedKey)
    {
        try
        {
            var key = ProtectedData.Unprotect(protectedKey, Entropy, DataProtectionScope.CurrentUser);
            if (key.Length == 32) return key;

            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("The CaYaFix integrity key has an invalid length.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    private static bool HasReparsePoint(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            current = current.Parent;
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Temporary key files are never loaded or trusted.
        }
    }

    private static void TryHide(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (IOException)
        {
            // DPAPI encryption and directory ACLs protect the key even if Hidden cannot be set.
        }
        catch (UnauthorizedAccessException)
        {
            // Hidden is cosmetic; key creation must remain usable on restricted profiles.
        }
    }
}

public sealed class EphemeralIntegrityService : IIntegrityService
{
    private readonly byte[] _key;

    public EphemeralIntegrityService(byte[]? key = null)
    {
        _key = key is null ? RandomNumberGenerator.GetBytes(32) : key.ToArray();
        if (_key.Length < 32) throw new ArgumentException("Integrity keys must contain at least 32 bytes.", nameof(key));
    }

    public string Sign(string purpose, ReadOnlySpan<byte> content)
    {
        using var hmac = new HMACSHA256(_key);
        var purposeBytes = Encoding.UTF8.GetBytes(purpose);
        var payload = new byte[purposeBytes.Length + 1 + content.Length];
        purposeBytes.CopyTo(payload, 0);
        content.CopyTo(payload.AsSpan(purposeBytes.Length + 1));
        return Convert.ToBase64String(hmac.ComputeHash(payload));
    }

    public bool Verify(string purpose, ReadOnlySpan<byte> content, string signature)
    {
        byte[]? expected = null;
        byte[]? actual = null;
        try
        {
            expected = Convert.FromBase64String(Sign(purpose, content));
            actual = Convert.FromBase64String(signature);
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            if (actual is not null) CryptographicOperations.ZeroMemory(actual);
        }
    }
}
