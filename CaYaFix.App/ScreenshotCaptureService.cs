// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CaYaFix.App.ViewModels;

namespace CaYaFix.App;

internal static class ScreenshotCaptureService
{
    public static async Task CaptureReadmeSetAsync(
        Window window,
        MainViewModel viewModel,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        window.WindowState = WindowState.Normal;
        window.Width = 1360;
        window.Height = 860;
        window.Left = 40;
        window.Top = 40;

        await CaptureStageAsync(window, viewModel, 0, Path.Combine(outputDirectory, "dashboard.png"));
        await CaptureStageAsync(window, viewModel, 1, Path.Combine(outputDirectory, "findings.png"));
        await CaptureStageAsync(window, viewModel, 2, Path.Combine(outputDirectory, "live-tests.png"));
    }

    private static async Task CaptureStageAsync(
        Window window,
        MainViewModel viewModel,
        int page,
        string destination)
    {
        viewModel.LoadReadmeDemo(page);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(650);
        window.UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var temporary = destination + ".new";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81_920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                encoder.Save(stream);
                await stream.FlushAsync();
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
