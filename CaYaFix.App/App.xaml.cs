// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using CaYaFix.App.Properties;
using CaYaFix.App.ViewModels;
using CaYaFix.Core;
using CaYaFix.Modules;
using Serilog;

namespace CaYaFix.App;

public partial class App : Application
{
    private ILogger? _logger;
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;

    public App()
    {
        EnsureResourceAssembly();
    }

    /// <summary>
    /// Drop the global single-instance mutex before starting a replacement process.
    /// Otherwise the new instance exits immediately as "already running".
    /// </summary>
    public static void ReleaseSingleInstanceForRestart()
    {
        if (Current is not App app) return;
        app.ReleaseSingleInstanceLock();
    }

    /// <summary>
    /// WPF often pre-assigns ResourceAssembly. Setting it again throws
    /// InvalidOperationException and terminates the process before any window appears.
    /// </summary>
    private static void EnsureResourceAssembly()
    {
        try
        {
            if (ResourceAssembly is null)
            {
                ResourceAssembly = typeof(App).Assembly;
            }
        }
        catch (InvalidOperationException)
        {
            // Already assigned by the WPF host; pack URIs use the assembly-qualified form.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Catch UI/XAML failures as early as possible (before window construction).
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            EnsureResourceAssembly();
            AppLanguage.ConfigureStartup(e.Args);
            base.OnStartup(e);
        }
        catch (Exception exception)
        {
            TryWriteStartupError(exception);
            try
            {
                MessageBox.Show(
                    exception.Message,
                    "CaYaFix",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // Last-resort: nothing more we can show.
            }

            Shutdown(1);
            return;
        }

        try
        {
            _singleInstance = new Mutex(true, @"Global\CaYaFix.SingleInstance.v1", out var createdNew);
            _ownsSingleInstance = createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(
                Strings.Get("Dialog_SingleInstanceFailure"),
                Strings.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        if (!_ownsSingleInstance)
        {
            MessageBox.Show(Strings.Get("Dialog_AlreadyRunning"), Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(2);
            return;
        }
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CaYaFix");
        try
        {
            DataDirectorySecurity.CreateRestricted(dataRoot);
            DataDirectorySecurity.CreateRestricted(Path.Combine(dataRoot, "Logs"));
            DataDirectorySecurity.CreateRestricted(Path.Combine(dataRoot, "Sessions"));
            _logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(dataRoot, "Logs", "cayafix-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true)
                .CreateLogger();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            MessageBox.Show(
                Strings.Get("Dialog_DataRootFailure") + Environment.NewLine + Environment.NewLine + exception.Message,
                Strings.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(3);
            return;
        }

        var console = new EventConsoleSink();
        var commands = new CommandRunner(console, _logger!);
        ProtectedIntegrityService integrity;
        SessionManager sessions;
        var userDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaYaFix");
        try
        {
            DataDirectorySecurity.CreateRestricted(userDataRoot);
            DataDirectorySecurity.CreateRestricted(Path.Combine(userDataRoot, "Security"));
            DataDirectorySecurity.CreateRestricted(Path.Combine(userDataRoot, "ReadmeScreenshots"));
            integrity = new ProtectedIntegrityService(userDataRoot);
            sessions = new SessionManager(dataRoot, integrity);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or
            UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            _logger!.Fatal(exception, "Manifest integrity initialization failed");
            MessageBox.Show(
                Strings.Get("Dialog_IntegrityFailure"),
                Strings.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(3);
            return;
        }

        BackupService backups;
        MainViewModel viewModel;
        try
        {
            backups = new BackupService(commands, dataRoot);
            backups.RegisterRestoreHandler("audio-defaults-v1", CaYaFix.Modules.Audio.AudioModule.RestoreDefaultDevicesBackupAsync);
            backups.RegisterRestoreHandler("audio-levels-v1", CaYaFix.Modules.Audio.AudioModule.RestoreLevelsBackupAsync);
            var text = new ResourceTextProvider();
            viewModel = new MainViewModel(
                ModuleCatalog.CreateAll(),
                new DiagnosticEngine(),
                new FixEngine(sessions),
                sessions,
                backups,
                new RestorePointService(commands),
                new HtmlReportService(),
                new SupportPackageService(commands, dataRoot),
                commands,
                console,
                text);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
            UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            _logger!.Fatal(exception, "Application services failed to initialize");
            MessageBox.Show(
                Strings.Get("Dialog_DataRootFailure") + Environment.NewLine + Environment.NewLine + exception.Message,
                Strings.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(3);
            return;
        }

        MainWindow window;
        try
        {
            window = new MainWindow { DataContext = viewModel };
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            _logger?.Fatal(exception, "Main window failed to open");
            TryWriteStartupError(exception);
            MessageBox.Show(
                string.Format(Strings.Get("Status_Failed"), exception.Message),
                Strings.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var captureReadme = e.Args.Any(argument =>
            argument.Equals("--capture-readme", StringComparison.OrdinalIgnoreCase));
        if (captureReadme)
        {
            var outputDirectory = Path.Combine(userDataRoot, "ReadmeScreenshots");
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await ScreenshotCaptureService.CaptureReadmeSetAsync(window, viewModel, outputDirectory);
                }
                catch (Exception exception)
                {
                    _logger?.Error(exception, "README screenshot capture failed");
                }
                finally
                {
                    Shutdown();
                }
            });
        }
        else
        {
            // Run startup recovery on the UI dispatcher after the window is shown so
            // buttons stay disabled (IsBusy) until recovery state is known.
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await viewModel.InitializeAsync();
                }
                catch (Exception exception)
                {
                    _logger?.Error(exception, "Startup initialization failed");
                    MessageBox.Show(
                        string.Format(Strings.Get("Status_Failed"), exception.Message),
                        Strings.AppName,
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            });
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        _logger?.Error(args.Exception, "Unhandled UI exception");
        TryWriteStartupError(args.Exception);
        try
        {
            MessageBox.Show(
                string.Format(Strings.Get("Status_Failed"), args.Exception.Message),
                Strings.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Ignore secondary UI failures.
        }

        args.Handled = true;
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (_logger as IDisposable)?.Dispose();
        ReleaseSingleInstanceLock();
        base.OnExit(e);
    }

    private void ReleaseSingleInstanceLock()
    {
        if (_singleInstance is null) return;
        if (_ownsSingleInstance)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
        }
        try { _singleInstance.Dispose(); } catch { /* already disposed */ }
        _singleInstance = null;
        _ownsSingleInstance = false;
    }

    private static void TryWriteStartupError(Exception exception)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "cayafix-startup-error.txt");
            File.WriteAllText(path, DateTimeOffset.Now + Environment.NewLine + exception);
        }
        catch
        {
            // Ignore secondary logging failures.
        }
    }

}

public sealed class ResourceTextProvider : ITextProvider
{
    public string Get(string key, params object[] arguments)
    {
        var text = Strings.Get(key);
        return arguments.Length == 0 ? text : string.Format(text, arguments);
    }
}
