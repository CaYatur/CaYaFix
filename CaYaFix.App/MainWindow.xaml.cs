// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using CaYaFix.App.Properties;
using CaYaFix.App.ViewModels;

namespace CaYaFix.App;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    private bool _allowClose;
    private bool _closeAfterCancellation;
    private MainViewModel? _boundViewModel;
    private INotifyCollectionChanged? _operationFeedCollection;
    private INotifyCollectionChanged? _consoleLinesCollection;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        DataContextChanged += OnDataContextChanged;
        StateChanged += OnWindowStateChanged;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => HookViewModel(DataContext as MainViewModel);
    }

    /// <summary>
    /// Borderless windows fill the entire monitor when maximized, which hides content under the
    /// Windows taskbar. Constrain max size to the monitor work area via WM_GETMINMAXINFO.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (HwndSource.FromHwnd(handle) is { } source)
        {
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            ApplyWorkAreaMaxInfo(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ApplyWorkAreaMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var work = monitorInfo.Work;
        var monitorRect = monitorInfo.Monitor;
        info.MaxPosition.X = Math.Abs(work.Left - monitorRect.Left);
        info.MaxPosition.Y = Math.Abs(work.Top - monitorRect.Top);
        info.MaxSize.X = Math.Abs(work.Right - work.Left);
        info.MaxSize.Y = Math.Abs(work.Bottom - work.Top);
        // Keep a usable minimum even when the work area is unusually small.
        info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, 640);
        info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, 480);
        Marshal.StructureToPtr(info, lParam, fDeleteOld: true);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // Drop resize borders while maximized so WindowChrome does not add phantom insets.
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.ResizeBorderThickness = WindowState == WindowState.Maximized
                ? new Thickness(0)
                : new Thickness(6);
            chrome.CornerRadius = WindowState == WindowState.Maximized
                ? new CornerRadius(0)
                : new CornerRadius(12);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        HookViewModel(e.NewValue as MainViewModel);

    private void HookViewModel(MainViewModel? viewModel)
    {
        if (ReferenceEquals(_boundViewModel, viewModel)) return;

        if (_operationFeedCollection is not null)
        {
            _operationFeedCollection.CollectionChanged -= OnOperationFeedChanged;
            _operationFeedCollection = null;
        }

        if (_consoleLinesCollection is not null)
        {
            _consoleLinesCollection.CollectionChanged -= OnConsoleLinesChanged;
            _consoleLinesCollection = null;
        }

        _boundViewModel = viewModel;
        if (viewModel is null) return;

        _operationFeedCollection = viewModel.OperationFeed;
        _operationFeedCollection.CollectionChanged += OnOperationFeedChanged;

        _consoleLinesCollection = viewModel.ConsoleLines;
        _consoleLinesCollection.CollectionChanged += OnConsoleLinesChanged;
    }

    private void OnOperationFeedChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset))
        {
            return;
        }

        // Defer until layout updates so ExtentHeight includes the new row.
        Dispatcher.BeginInvoke(ScrollOperationFeedToEnd, DispatcherPriority.Loaded);
    }

    private void OnConsoleLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset))
        {
            return;
        }

        Dispatcher.BeginInvoke(ScrollConsoleToEnd, DispatcherPriority.Loaded);
    }

    private void ScrollOperationFeedToEnd()
    {
        if (OperationFeedScroll is null) return;
        OperationFeedScroll.UpdateLayout();
        OperationFeedScroll.ScrollToEnd();
    }

    private void ScrollConsoleToEnd()
    {
        if (ConsoleList is null || ConsoleList.Items.Count == 0) return;
        ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
        if (VisualTreeHelper.GetChild(ConsoleList, 0) is Decorator decorator &&
            decorator.Child is ScrollViewer viewer)
        {
            viewer.ScrollToEnd();
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // Ignore drags that start on window chrome buttons.
        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<Button>(source) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        try
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
                e.Handled = true;
            }
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the mouse button is no longer pressed.
        }
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || DataContext is not MainViewModel viewModel || !viewModel.IsBusy) return;

        e.Cancel = true;
        if (_closeAfterCancellation) return;
        var consent = MessageBox.Show(
            Strings.Get("Dialog_CloseBusy"),
            Strings.Get("Dialog_CloseBusy_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        _closeAfterCancellation = true;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.RequestCancellation();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_closeAfterCancellation || e.PropertyName != nameof(MainViewModel.IsBusy) ||
            sender is not MainViewModel viewModel || viewModel.IsBusy)
        {
            return;
        }

        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Dispatcher.BeginInvoke(() =>
        {
            _allowClose = true;
            Close();
        });
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
