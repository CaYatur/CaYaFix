// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using CaYaFix.Core;

namespace CaYaFix.App;

/// <summary>
/// Resolves and persists the UI language. English is the product default and the
/// fallback whenever Windows is neither English nor Turkish.
/// </summary>
public static class AppLanguage
{
    public const string Auto = "auto";
    public const string English = "en";
    public const string Turkish = "tr";

    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    public static string Preference { get; private set; } = Auto;

    public static CultureInfo Current { get; private set; } = EnglishCulture;

    public static string PreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CaYaFix",
        "ui-language.txt");

    public static void LoadPreference()
    {
        try
        {
            var path = PreferencePath;
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path).Trim();
            if (IsSupportedPreference(text))
            {
                Preference = text.ToLowerInvariant();
            }
        }
        catch
        {
            // Keep the safe English/auto defaults when the preference file is unreadable.
        }
    }

    public static void SavePreference(string preference)
    {
        if (!IsSupportedPreference(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        Preference = preference.ToLowerInvariant();
        var path = PreferencePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Preference);
    }

    public static CultureInfo Resolve(
        string? preference = null,
        bool forceEnglish = false,
        CultureInfo? windowsUiCulture = null)
    {
        if (forceEnglish) return EnglishCulture;

        var pref = (preference ?? Preference).Trim();
        if (pref.Equals(English, StringComparison.OrdinalIgnoreCase)) return EnglishCulture;
        if (pref.Equals(Turkish, StringComparison.OrdinalIgnoreCase)) return TurkishCulture;

        // Auto / unknown preference: only Turkish Windows UI maps to Turkish.
        var windows = (windowsUiCulture ?? CultureInfo.InstalledUICulture).TwoLetterISOLanguageName;
        return windows.Equals(Turkish, StringComparison.OrdinalIgnoreCase)
            ? TurkishCulture
            : EnglishCulture;
    }

    public static void Apply(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        Current = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        if (Application.Current?.MainWindow is not null)
        {
            Application.Current.MainWindow.Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
        }
    }

    public static void ConfigureStartup(IReadOnlyList<string> arguments)
    {
        var forceEnglish = arguments.Any(argument =>
            argument.Equals("--capture-readme", StringComparison.OrdinalIgnoreCase));

        // Optional CLI override: --lang en|tr|auto
        string? cliPreference = null;
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--lang", StringComparison.OrdinalIgnoreCase) &&
                IsSupportedPreference(arguments[index + 1]))
            {
                cliPreference = arguments[index + 1].ToLowerInvariant();
                break;
            }
        }

        LoadPreference();
        if (cliPreference is not null)
        {
            Preference = cliPreference;
        }

        var windowsUi = CultureInfo.InstalledUICulture;
        var culture = Resolve(Preference, forceEnglish, windowsUi);
        Apply(culture);
    }

    public static bool IsSupportedPreference(string? value) =>
        value is not null &&
        (value.Equals(Auto, StringComparison.OrdinalIgnoreCase) ||
         value.Equals(English, StringComparison.OrdinalIgnoreCase) ||
         value.Equals(Turkish, StringComparison.OrdinalIgnoreCase));

    public static string DisplayName(string preference, ITextProvider text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return preference.ToLowerInvariant() switch
        {
            Turkish => text.Get("Language_Turkish"),
            English => text.Get("Language_English"),
            _ => text.Get("Language_System")
        };
    }
}

public sealed record LanguageOption(string Code, string DisplayName);
