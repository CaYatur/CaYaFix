// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CaYaFix.App.Properties;
using CaYaFix.App.ViewModels;

namespace CaYaFix.App;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _closeAfterCancellation;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        DragMove();
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
