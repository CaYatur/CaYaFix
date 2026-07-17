// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Windows;
using System.Windows.Controls;
using SharpVectors.Converters;

namespace CaYaFix.App;

/// <summary>
/// Hosts a SharpVectors <see cref="SvgViewbox"/> and loads embedded SVG pack resources.
/// Composition (not subclassing) avoids constructor/lifecycle issues in single-file WPF hosts.
/// </summary>
public sealed class SvgIcon : UserControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(string),
        typeof(SvgIcon),
        new PropertyMetadata(null, OnIconChanged));

    private readonly SvgViewbox _viewbox;

    public SvgIcon()
    {
        IsHitTestVisible = false;
        Focusable = false;
        _viewbox = new SvgViewbox
        {
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        try
        {
            _viewbox.TextAsGeometry = true;
            _viewbox.OptimizePath = true;
        }
        catch
        {
            // Optional SharpVectors flags.
        }

        Content = _viewbox;
    }

    /// <summary>
    /// Icon file name under Assets/Icons (e.g. logo.svg) or a full pack URI.
    /// </summary>
    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SvgIcon icon)
        {
            icon.LoadIcon(e.NewValue as string);
        }
    }

    private void LoadIcon(string? value)
    {
        try
        {
            _viewbox.Source = null;
        }
        catch
        {
            // Ignore clear failures on a fresh control.
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var uri in BuildCandidateUris(value))
        {
            try
            {
                if (Application.Current is null)
                {
                    continue;
                }

                // Confirm the resource exists before assigning Source.
                using (var stream = Application.GetResourceStream(uri)?.Stream)
                {
                    if (stream is null) continue;
                }

                _viewbox.Source = uri;
                return;
            }
            catch
            {
                // Try the next pack URI form.
            }
        }
    }

    private static IEnumerable<Uri> BuildCandidateUris(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
        {
            yield return new Uri(trimmed, UriKind.Absolute);
            yield break;
        }

        var fileName = trimmed
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? trimmed;
        if (!fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".svg";
        }

        var assemblyName = typeof(SvgIcon).Assembly.GetName().Name ?? "CaYaFix";
        yield return new Uri(
            $"pack://application:,,,/{assemblyName};component/Assets/Icons/{fileName}",
            UriKind.Absolute);
        yield return new Uri($"pack://application:,,,/Assets/Icons/{fileName}", UriKind.Absolute);
    }
}
