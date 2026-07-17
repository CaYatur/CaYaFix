// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Serilog;

namespace CaYaFix.Core;

public sealed class CommandRunner : ICommandRunner
{
    private const int MaxCapturedCharacters = 4 * 1024 * 1024;
    private const int MaxConsoleCharactersPerStream = 512 * 1024;
    private const int MaxConsoleLinesPerStream = 4_000;
    private const int MaxConsoleLineCharacters = 16 * 1024;
    private const string OutputTruncatedMessage = "[command output truncated by CaYaFix]";
    private static readonly Encoding CommandOutputEncoding = ResolveCommandOutputEncoding();
    private static readonly HashSet<string> AllowedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "arp.exe", "chkdsk.exe", "chkntfs.exe", "cmd.exe", "dism.exe", "fsutil.exe", "ipconfig.exe",
        "netsh.exe", "nslookup.exe", "ping.exe", "pnputil.exe", "powercfg.exe", "powershell.exe", "reg.exe",
        "route.exe", "sc.exe", "sfc.exe", "w32tm.exe", "wsreset.exe"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly IConsoleSink _console;
    private readonly ILogger _logger;

    public CommandRunner(IConsoleSink console, ILogger logger)
    {
        _console = console;
        _logger = logger;
    }

    public async Task<CmdResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        if (arguments.Count > 128 || arguments.Any(argument => argument is null || argument.Length > 32_768 || argument.IndexOf('\0') >= 0))
        {
            throw new ArgumentException("The command argument list is outside the supported safety limits.", nameof(arguments));
        }

        var trustedExecutable = ResolveTrustedExecutable(executable);
        if (trustedExecutable is null)
        {
            return Failure(-3, $"Command '{Path.GetFileName(executable)}' is not on the trusted executable list.", TimeSpan.Zero);
        }

        var display = TruncateForConsole(FormatCommand(executable, arguments));
        var started = Stopwatch.StartNew();
        _console.Write(new ConsoleLine(DateTimeOffset.Now, "CMD", display, true));
        _logger.Information("Command started: {Executable}; argumentCount={ArgumentCount}", executable, arguments.Count);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = trustedExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = CommandOutputEncoding,
                StandardErrorEncoding = CommandOutputEncoding
            },
            EnableRaisingEvents = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return Failure(-1, "The process could not be started.", started.Elapsed);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Command could not start: {Executable}", executable);
            return Failure(-1, ex.Message, started.Elapsed);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stdoutTask = PumpAsync(process.StandardOutput, stdout, "OUT", pumpCts.Token);
        var stderrTask = PumpAsync(process.StandardError, stderr, "ERR", pumpCts.Token);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await FinishPumpsAsync(stdoutTask, stderrTask, pumpCts).ConfigureAwait(false);
            started.Stop();
            var timeoutMessage = $"The command timed out after {timeout.TotalSeconds:0} seconds.";
            _console.Write(new ConsoleLine(DateTimeOffset.Now, "ERR", timeoutMessage));
            _logger.Warning(
                "Command timed out after {Timeout}: {Executable}; argumentCount={ArgumentCount}",
                timeout,
                executable,
                arguments.Count);
            return new CmdResult(-2, stdout.ToString(), timeoutMessage, started.Elapsed, true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await FinishPumpsAsync(stdoutTask, stderrTask, pumpCts).ConfigureAwait(false);
            _console.Write(new ConsoleLine(DateTimeOffset.Now, "WARN", "The operation was cancelled by the user."));
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            TryKill(process);
            await FinishPumpsAsync(stdoutTask, stderrTask, pumpCts).ConfigureAwait(false);
            _logger.Error(ex, "Command output could not be read: {Executable}", executable);
            return Failure(-4, "The command output stream could not be read safely.", started.Elapsed);
        }

        started.Stop();
        var result = new CmdResult(process.ExitCode, stdout.ToString(), stderr.ToString(), started.Elapsed);
        _logger.Information(
            "Command finished: {Executable}; exit={ExitCode}; duration={Duration}",
            executable,
            result.ExitCode,
            result.Duration);
        return result;
    }

    public async Task<T?> RunPsJsonAsync<T>(string psCommand, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(psCommand);
        if (psCommand.Length > 128 * 1024)
        {
            throw new ArgumentException("The PowerShell query exceeds the supported safety limit.", nameof(psCommand));
        }

        var script = $"& {{ $ErrorActionPreference='Stop'; $result = @({psCommand}); ConvertTo-Json -InputObject $result -Depth 6 -Compress }}";
        var result = await RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new InvalidOperationException(
                $"The structured PowerShell query failed with exit code {result.ExitCode}.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(result.StdOut, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.Warning(ex, "PowerShell JSON could not be parsed as {Type}", typeof(T).Name);
            _console.Write(new ConsoleLine(DateTimeOffset.Now, "ERR", "PowerShell output could not be parsed as JSON."));
            throw new InvalidDataException("PowerShell returned invalid structured output.", ex);
        }
    }

    private async Task PumpAsync(
        StreamReader reader,
        StringBuilder target,
        string level,
        CancellationToken ct)
    {
        var buffer = new char[4096];
        var line = new StringBuilder(Math.Min(MaxConsoleLineCharacters, 4096));
        var endedWithCarriageReturn = false;
        var consoleCharacters = 0;
        var consoleLines = 0;
        var consoleTruncated = false;
        var captureTruncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (!captureTruncated)
            {
                var remaining = MaxCapturedCharacters - target.Length;
                if (remaining > 0)
                {
                    target.Append(buffer, 0, Math.Min(count, remaining));
                }
                if (count > remaining)
                {
                    target.AppendLine().Append(OutputTruncatedMessage);
                    captureTruncated = true;
                    _logger.Warning("Command {Stream} stream exceeded the captured-output safety limit.", level);
                }
            }

            if (consoleTruncated)
            {
                continue;
            }

            var consoleCount = Math.Min(count, MaxConsoleCharactersPerStream - consoleCharacters);
            consoleCharacters += consoleCount;
            for (var index = 0; index < consoleCount; index++)
            {
                var character = buffer[index];
                if (character == '\r')
                {
                    if (!TryEmitConsoleLine(level, line, ref consoleLines))
                    {
                        consoleTruncated = true;
                        break;
                    }
                    endedWithCarriageReturn = true;
                    continue;
                }

                if (character == '\n')
                {
                    if (!endedWithCarriageReturn && !TryEmitConsoleLine(level, line, ref consoleLines))
                    {
                        consoleTruncated = true;
                        break;
                    }
                    endedWithCarriageReturn = false;
                    continue;
                }

                endedWithCarriageReturn = false;
                line.Append(character);
                if (line.Length >= MaxConsoleLineCharacters)
                {
                    if (!TryEmitConsoleLine(level, line, ref consoleLines))
                    {
                        consoleTruncated = true;
                        break;
                    }
                }
            }

            if (consoleCount < count)
            {
                consoleTruncated = true;
            }

            if (consoleTruncated)
            {
                line.Clear();
                _console.Write(new ConsoleLine(DateTimeOffset.Now, "WARN", OutputTruncatedMessage));
                _logger.Warning("Command {Stream} stream exceeded the console output safety limit.", level);
            }
        }

        if (!consoleTruncated && line.Length > 0 && !TryEmitConsoleLine(level, line, ref consoleLines))
        {
            _console.Write(new ConsoleLine(DateTimeOffset.Now, "WARN", OutputTruncatedMessage));
        }
    }

    private bool TryEmitConsoleLine(string level, StringBuilder line, ref int emittedLines)
    {
        if (emittedLines >= MaxConsoleLinesPerStream)
        {
            line.Clear();
            return false;
        }

        _console.Write(new ConsoleLine(DateTimeOffset.Now, level, line.ToString()));
        emittedLines++;
        line.Clear();
        return true;
    }

    private static async Task FinishPumpsAsync(
        Task stdoutTask,
        Task stderrTask,
        CancellationTokenSource pumpCts)
    {
        var pumps = Task.WhenAll(stdoutTask, stderrTask);
        if (await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) != pumps)
        {
            pumpCts.Cancel();
        }

        try
        {
            await pumps.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The process was terminated; any bounded output already captured remains usable.
        }
    }

    private CmdResult Failure(int exitCode, string error, TimeSpan duration)
    {
        _console.Write(new ConsoleLine(DateTimeOffset.Now, "ERR", error));
        return new CmdResult(exitCode, string.Empty, error, duration);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            // Process may have exited between the check and Kill.
        }
    }

    private static string FormatCommand(string executable, IReadOnlyList<string> arguments)
    {
        static string Quote(string value) =>
            value.Any(char.IsWhiteSpace) || value.Contains('"')
                ? $"\"{value.Replace("\"", "\\\"")}\""
                : value;

        return $"> {Quote(executable)} {string.Join(' ', arguments.Select(Quote))}".TrimEnd();
    }

    private static string TruncateForConsole(string value) =>
        value.Length <= MaxConsoleLineCharacters
            ? value
            : string.Concat(value.AsSpan(0, MaxConsoleLineCharacters - 1), "…");

    private static string? ResolveTrustedExecutable(string executable)
    {
        var fileName = Path.GetFileName(executable);
        if (!AllowedExecutables.Contains(fileName) ||
            !string.Equals(fileName, executable, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidate = fileName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", fileName)
            : Path.Combine(windows, "System32", fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    private static Encoding ResolveCommandOutputEncoding()
    {
        if (!OperatingSystem.IsWindows()) return Encoding.UTF8;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}
