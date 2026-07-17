// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CaYaFix.App.Properties;
using CaYaFix.App.ViewModels;

namespace CaYaFix.App;

public partial class MainWindow : Window
{
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
        Loaded += (_, _) => HookViewModel(DataContext as MainViewModel);
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
