// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CaYaFix.Core;

public sealed class HtmlReportService
{
    public async Task<string> CreateAsync(
        SessionManifest session,
        ITextProvider text,
        CancellationToken ct)
    {
        var path = EnsureSafeReportPath(session);
        var builder = new StringBuilder();
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("tr", StringComparison.OrdinalIgnoreCase)
            ? "tr"
            : "en";
        var sessionStatus = text.Get($"SessionStatus_{session.Status}");
        builder.Append($$"""
            <!doctype html><html lang="{{language}}"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>CaYaFix — {{E(text.Get("Report_Title"))}}</title><style>
            :root{color-scheme:dark;font-family:Segoe UI,Arial,sans-serif;background:#08111f;color:#eaf2ff}
            body{max-width:980px;margin:40px auto;padding:0 24px}header{padding:28px;border:1px solid #233650;border-radius:20px;background:#101d2f}
            h1{margin:0 0 8px;color:#fff}.muted{color:#9badc4}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:14px;margin-top:20px}
            .card{padding:18px;border:1px solid #233650;border-radius:16px;background:#0d1929}.critical{border-left:4px solid #ff5576}.warning{border-left:4px solid #ffc857}.info{border-left:4px solid #4ea8ff}
            code,pre{white-space:pre-wrap;word-break:break-word;background:#07101c;border-radius:8px;padding:10px;color:#bad0e8}table{width:100%;border-collapse:collapse;margin-top:14px}td,th{padding:10px;border-bottom:1px solid #233650;text-align:left}
            </style></head><body>
            """);
        builder.Append($"<header><h1>CaYaFix — {E(text.Get("Report_Title"))}</h1>");
        builder.Append($"<div class=\"muted\">{E(session.Id)} · {E(session.StartedAt.ToString("g", CultureInfo.CurrentCulture))} · {E(sessionStatus)}</div></header>");
        builder.Append($"<h2>{E(text.Get("Report_Findings"))}</h2><div class=\"grid\">");
        foreach (var finding in session.Findings)
        {
            var css = finding.Severity.ToString().ToLowerInvariant();
            var message = string.IsNullOrWhiteSpace(finding.UserMessage)
                ? text.Get(finding.MessageKey)
                : finding.UserMessage;
            var module = string.IsNullOrWhiteSpace(finding.ModuleName) ? finding.ModuleId : finding.ModuleName;
            builder.Append($"<section class=\"card {css}\"><strong>{E(message)}</strong>");
            builder.Append($"<div class=\"muted\">{E(module)} · {E(text.Get($"FindingStatus_{finding.Status}"))}</div>");
            if (!string.IsNullOrWhiteSpace(finding.TechnicalDetail))
            {
                builder.Append($"<pre>{E(finding.TechnicalDetail)}</pre>");
            }
            builder.Append("</section>");
        }
        builder.Append("</div>");
        builder.Append($"<h2>{E(text.Get("Report_Actions"))}</h2><table><thead><tr><th>{E(text.Get("Report_Action"))}</th><th>{E(text.Get("Report_Tier"))}</th><th>{E(text.Get("Report_Result"))}</th><th>{E(text.Get("Report_Backup"))}</th></tr></thead><tbody>");
        foreach (var action in session.Actions)
        {
            var title = string.IsNullOrWhiteSpace(action.TitleMessageKey)
                ? action.FixId
                : text.Get(action.TitleMessageKey);
            builder.Append($"<tr><td>{E(title)}</td><td>{E(text.Get($"Tier_{action.Tier}"))}</td><td>{E(text.Get(action.ResultMessageKey))}</td><td>{E(action.Backup?.Location ?? "—")}</td></tr>");
        }
        builder.Append("</tbody></table>");
        builder.Append($"<h2>{E(text.Get("Report_Rollback"))}</h2><p>{E(text.Get("Report_RollbackHelp"))}</p>");
        builder.Append("</body></html>");
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
        return path;
    }

    private static string EnsureSafeReportPath(SessionManifest session)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.DirectoryPath));
        var path = Path.GetFullPath(Path.Combine(root, "report.html"));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The report path is outside the session directory.");
        }

        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("The report path cannot contain a reparse point.");
            }
        }
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The report file cannot be a reparse point.");
        }
        return path;
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}

public sealed class SupportPackageService
{
    private const int MaxIncludedTextCharacters = 4 * 1024 * 1024;
    private readonly ICommandRunner _commands;
    private readonly string _sessionsRoot;
    private readonly string _logsRoot;

    public SupportPackageService(ICommandRunner commands, string? dataRoot = null)
    {
        _commands = commands;
        var root = Path.GetFullPath(dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CaYaFix"));
        _sessionsRoot = Path.Combine(root, "Sessions");
        _logsRoot = Path.Combine(root, "Logs");
    }

    public Task<string> CreateAsync(
        SessionManifest session,
        string? reportPath,
        CancellationToken ct) =>
        CreateAsync(session, reportPath, destinationZipPath: null, ct);

    /// <param name="destinationZipPath">
    /// Optional user-chosen final .zip path. The archive is always built inside the trusted
    /// session directory first, then copied to this location when provided.
    /// </param>
    public async Task<string> CreateAsync(
        SessionManifest session,
        string? reportPath,
        string? destinationZipPath,
        CancellationToken ct)
    {
        var sessionDirectory = ValidateSessionDirectory(session);
        var staging = SafeChildPath(sessionDirectory, $"SupportPackage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        if (HasReparsePoint(staging))
        {
            throw new InvalidOperationException("The support package staging path cannot contain a reparse point.");
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(reportPath) && IsSafeSessionFile(sessionDirectory, reportPath))
            {
                var report = await ReadTextPrefixAsync(reportPath, MaxIncludedTextCharacters, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(staging, "report.html"),
                    PrivacyRedactor.Redact(report),
                    Encoding.UTF8,
                    ct).ConfigureAwait(false);
            }

            await CaptureAsync("ipconfig.txt", "ipconfig.exe", ["/all"], TimeSpan.FromMinutes(2), staging, ct).ConfigureAwait(false);
            await CaptureAsync("wlan.txt", "netsh.exe", ["wlan", "show", "all"], TimeSpan.FromMinutes(3), staging, ct).ConfigureAwait(false);
            await CaptureAsync("drivers.txt", "pnputil.exe", ["/enum-drivers"], TimeSpan.FromMinutes(4), staging, ct).ConfigureAwait(false);
            await CaptureAsync("routes.txt", "route.exe", ["print"], TimeSpan.FromMinutes(2), staging, ct).ConfigureAwait(false);
            await CaptureAsync(
                "audio-devices.txt",
                "powershell.exe",
                ["-NoProfile", "-Command", "Get-CimInstance Win32_SoundDevice | Format-List Name,Manufacturer,Status,DeviceID,ConfigManagerErrorCode | Out-String -Width 240"],
                TimeSpan.FromMinutes(3),
                staging,
                ct).ConfigureAwait(false);

            if (Directory.Exists(_logsRoot) && !HasReparsePoint(_logsRoot))
            {
                var logTarget = Path.Combine(staging, "Logs");
                Directory.CreateDirectory(logTarget);
                foreach (var log in GetNewestRegularLogs(_logsRoot, 5))
                {
                    var content = await ReadTextPrefixAsync(log, MaxIncludedTextCharacters, ct).ConfigureAwait(false);
                    await File.WriteAllTextAsync(
                        Path.Combine(logTarget, Path.GetFileName(log)),
                        PrivacyRedactor.Redact(content),
                        Encoding.UTF8,
                        ct).ConfigureAwait(false);
                }
            }

            await File.WriteAllTextAsync(
                Path.Combine(staging, "PRIVACY.txt"),
                "CaYaFix support package\n\nPersonal identifiers are redacted by default. " +
                "The package contains diagnostic command output, the generated report, and up to five recent CaYaFix logs. " +
                "It never includes rollback backups, saved Wi-Fi keys, browser data, documents, credentials, or microphone recordings. " +
                "Review every file before sharing the archive.",
                Encoding.UTF8,
                ct).ConfigureAwait(false);

            var zip = SafeChildPath(sessionDirectory, $"CaYaFix-Support-{session.Id}.zip");
            if (File.Exists(zip) && File.GetAttributes(zip).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("The support package archive cannot be a reparse point.");
            }
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(staging, zip, CompressionLevel.Optimal, false);

            if (string.IsNullOrWhiteSpace(destinationZipPath))
            {
                return zip;
            }

            return await CopyZipToUserDestinationAsync(zip, destinationZipPath, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteOwnedDirectory(staging);
        }
    }

    private static async Task<string> CopyZipToUserDestinationAsync(
        string sourceZip,
        string destinationZipPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceZip);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);
        ct.ThrowIfCancellationRequested();

        var destination = Path.GetFullPath(destinationZipPath.Trim());
        if (destination.IndexOf('\0') >= 0 ||
            destination.Length > 320 ||
            !destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The support package destination must be a .zip file path.");
        }

        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException("The support package destination folder is invalid.");
        }

        Directory.CreateDirectory(parent);
        if (HasReparsePoint(parent) ||
            (File.Exists(destination) && File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new InvalidOperationException("The support package destination cannot be a reparse point.");
        }

        // Prefer copy+replace so a partial write does not leave a half-written user archive.
        var temp = Path.Combine(parent, $".CaYaFix-Support-{Guid.NewGuid():N}.partial.zip");
        try
        {
            await using (var input = new FileStream(
                             sourceZip,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(temp, destination);
            return destination;
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // Best-effort cleanup of the temporary copy.
            }
        }
    }

    private static IReadOnlyList<string> GetNewestRegularLogs(string directory, int maximumCount)
    {
        var comparer = Comparer<(DateTime Timestamp, string Path)>.Create((left, right) =>
        {
            var timestamp = left.Timestamp.CompareTo(right.Timestamp);
            return timestamp != 0
                ? timestamp
                : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
        });
        var newest = new SortedSet<(DateTime Timestamp, string Path)>(comparer);
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) continue;
                newest.Add((File.GetLastWriteTimeUtc(path), path));
                if (newest.Count > maximumCount) newest.Remove(newest.Min);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return newest.Reverse().Select(item => item.Path).ToArray();
    }

    private async Task CaptureAsync(
        string filename,
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string directory,
        CancellationToken ct)
    {
        var result = await _commands.RunAsync(executable, arguments, timeout, ct).ConfigureAwait(false);
        var raw = $"ExitCode: {result.ExitCode}{Environment.NewLine}Duration: {result.Duration}{Environment.NewLine}{Environment.NewLine}{result.StdOut}{Environment.NewLine}{result.StdErr}";
        if (raw.Length > MaxIncludedTextCharacters)
        {
            raw = string.Concat(raw.AsSpan(0, MaxIncludedTextCharacters), Environment.NewLine, "[truncated by CaYaFix]");
        }
        var content = PrivacyRedactor.Redact(raw);
        await File.WriteAllTextAsync(Path.Combine(directory, filename), content, ct).ConfigureAwait(false);
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(directory);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(child);
            else DeleteOwnedDirectory(child);
        }
        Directory.Delete(directory);
    }

    private static void TryDeleteOwnedDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) DeleteOwnedDirectory(directory);
        }
        catch
        {
            // Cleanup is best-effort; staged files are already redacted and remain inside the signed session directory.
        }
    }

    private string ValidateSessionDirectory(SessionManifest session)
    {
        if (string.IsNullOrWhiteSpace(session.Id) || session.Id.Length > 64 ||
            session.Id.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidOperationException("The support package session identifier is invalid.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_sessionsRoot));
        var expected = Path.GetFullPath(Path.Combine(root, session.Id));
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.DirectoryPath));
        if (!directory.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            !directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(directory) || HasReparsePoint(directory))
        {
            throw new InvalidOperationException("The support package session directory is not trusted.");
        }

        return directory;
    }

    private static string SafeChildPath(string root, string name)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, name));
        if (!candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The support package path is outside the session directory.");
        }
        return candidate;
    }

    private static bool IsSafeSessionFile(string sessionDirectory, string path)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory));
            var candidate = Path.GetFullPath(path);
            return File.Exists(candidate) &&
                   candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                   !HasReparsePoint(candidate);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasReparsePoint(string path)
    {
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(Path.GetFullPath(path))
            : new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
        return false;
    }

    private static async Task<string> ReadTextPrefixAsync(string path, int maxCharacters, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 81_920, leaveOpen: false);
        var buffer = new char[Math.Min(maxCharacters, 81_920)];
        var builder = new StringBuilder(Math.Min(maxCharacters, 262_144));
        while (builder.Length < maxCharacters)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxCharacters - builder.Length)), ct).ConfigureAwait(false);
            if (count == 0) break;
            builder.Append(buffer, 0, count);
        }
        if (!reader.EndOfStream) builder.AppendLine().Append("[truncated by CaYaFix]");
        return builder.ToString();
    }
}

public static partial class PrivacyRedactor
{
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        try
        {
            var redacted = value;
            if (!string.IsNullOrWhiteSpace(Environment.UserName))
            {
                redacted = redacted.Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase);
            }
            if (!string.IsNullOrWhiteSpace(Environment.MachineName))
            {
                redacted = redacted.Replace(Environment.MachineName, "<computer>", StringComparison.OrdinalIgnoreCase);
            }
            redacted = UserPathPattern().Replace(redacted, match => $"{match.Groups[1].Value}<user>");
            redacted = MacAddressPattern().Replace(redacted, "<mac-address>");
            redacted = SensitiveJsonPropertyPattern().Replace(redacted, match =>
                $"{match.Groups[1].Value}<redacted-{StableToken(match.Groups[2].Value)}>{match.Groups[3].Value}");
            redacted = SensitiveLinePattern().Replace(redacted, match =>
                $"{match.Groups[1].Value}: <redacted-{StableToken(match.Groups[2].Value)}>");
            redacted = GenericNameLinePattern().Replace(redacted, match =>
                $"{match.Groups[1].Value}: <redacted-{StableToken(match.Groups[2].Value)}>");
            redacted = EmailPattern().Replace(redacted, "<email-address>");
            redacted = SidPattern().Replace(redacted, "<windows-sid>");
            redacted = GuidPattern().Replace(redacted, "<identifier>");
            redacted = Ipv4Pattern().Replace(redacted, RedactIpv4);
            redacted = Ipv6Pattern().Replace(redacted, RedactIpv6);
            return redacted;
        }
        catch (RegexMatchTimeoutException)
        {
            return "[content omitted because privacy redaction exceeded its safe time limit]";
        }
    }

    private static string RedactIpv4(Match match)
    {
        var parts = match.Value.Split('.');
        if (parts.Length != 4) return "<ip-address>";
        return parts[0] switch
        {
            "10" => "10.x.x.x",
            "127" => "127.0.0.1",
            "169" when parts[1] == "254" => "169.254.x.x",
            "172" => "172.x.x.x",
            "192" when parts[1] == "168" => "192.168.x.x",
            _ => "<public-ip>"
        };
    }

    private static string RedactIpv6(Match match)
    {
        var candidate = match.Value;
        var zone = candidate.IndexOf('%');
        if (zone >= 0) candidate = candidate[..zone];
        return IPAddress.TryParse(candidate, out var address) &&
               address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? IPAddress.IsLoopback(address) ? "::1" : "<ipv6-address>"
            : match.Value;
    }

    private static string StableToken(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    [GeneratedRegex(@"(?i)([a-z]:\\users\\)[^\\\r\n]+", RegexOptions.None, 2_000)]
    private static partial Regex UserPathPattern();

    [GeneratedRegex(@"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", RegexOptions.None, 2_000)]
    private static partial Regex MacAddressPattern();

    [GeneratedRegex(@"(?im)^\s*(.*(?:host\s*name|computer\s*name|primary\s*dns\s*suffix|ssid|profile\s*name|device\s*id|instance\s*id|serial(?:\s*number)?|key\s*content|password|passphrase|secret|access\s*token|api\s*key|ana\s*bilgisayar\s*adı|dns\s*soneki|profil\s*adı|aygıt\s*kimliği|seri\s*numarası|anahtar\s*içeriği|parola|gizli\s*anahtar).*)\s*:\s*(.+)$", RegexOptions.None, 2_000)]
    private static partial Regex SensitiveLinePattern();

    [GeneratedRegex(@"(?im)^\s*((?:friendly\s+)?name|ad|device\s+name|aygıt\s+adı|cihaz\s+adı)\s*:\s*(.+)$", RegexOptions.None, 2_000)]
    private static partial Regex GenericNameLinePattern();

    [GeneratedRegex(@"(?i)([\""']?(?:deviceid|instanceid|serialnumber|password|passphrase|secret|token|apikey)[\""']?\s*:\s*[\""'])([^\""'\r\n]+)([\""'])", RegexOptions.None, 2_000)]
    private static partial Regex SensitiveJsonPropertyPattern();

    [GeneratedRegex(@"(?i)\b[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,63}\b", RegexOptions.None, 2_000)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?i)\bS-\d-\d+(?:-\d+){1,15}\b", RegexOptions.None, 2_000)]
    private static partial Regex SidPattern();

    [GeneratedRegex(@"(?i)\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b", RegexOptions.None, 2_000)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d.])", RegexOptions.None, 2_000)]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?i)(?<![0-9a-f:])[0-9a-f]{0,4}(?::[0-9a-f]{0,4}){2,7}(?:%\d+)?(?![0-9a-f:])", RegexOptions.None, 2_000)]
    private static partial Regex Ipv6Pattern();
}
