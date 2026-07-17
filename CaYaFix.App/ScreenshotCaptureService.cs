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
    // Logical (DIP) capture frame — wider than the default window for documentation.
    private const double CaptureDipWidth = 1600;
    private const double CaptureDipHeight = 1000;

    // Render at 2× (192 DPI) so text stays sharp on high-DPI displays and GitHub.
    private const double CaptureDpi = 192.0;
    private const double CaptureScale = CaptureDpi / 96.0;

    public static async Task CaptureReadmeSetAsync(
        Window window,
        MainViewModel viewModel,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        // Prefer universally available fonts during capture (Variable fonts may be missing on CI).
        window.FontFamily = new FontFamily("Segoe UI");
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(window, TextHintingMode.Fixed);
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;

        window.WindowState = WindowState.Normal;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 20;
        window.Top = 20;
        window.MinWidth = CaptureDipWidth;
        window.MinHeight = CaptureDipHeight;
        window.MaxWidth = CaptureDipWidth;
        window.MaxHeight = CaptureDipHeight;
        ForceExactLayout(window);

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(250);

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
        ForceExactLayout(window);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(900);
        ForceExactLayout(window);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(200);

        var pixelWidth = Math.Max(1, (int)Math.Round(CaptureDipWidth * CaptureScale));
        var pixelHeight = Math.Max(1, (int)Math.Round(CaptureDipHeight * CaptureScale));

        // High-DPI RenderTargetBitmap: window stays at 1600×1000 DIP; raster is 3200×2000 @ 192 DPI.
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            CaptureDpi,
            CaptureDpi,
            PixelFormats.Pbgra32);
        bitmap.Render(window);
        bitmap.Freeze();

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

    private static void ForceExactLayout(Window window)
    {
        window.Width = CaptureDipWidth;
        window.Height = CaptureDipHeight;
        window.Measure(new Size(CaptureDipWidth, CaptureDipHeight));
        window.Arrange(new Rect(0, 0, CaptureDipWidth, CaptureDipHeight));
        window.UpdateLayout();
    }
}
