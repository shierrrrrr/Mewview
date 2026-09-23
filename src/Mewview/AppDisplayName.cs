using System;
using System.Globalization;

namespace Mewview;

/// <summary>
/// User-facing application name, resolved once from the system UI language.
/// Chinese (zh) systems get 瞄瞄; every other language falls back to Mewview.
/// <para>
/// Only display strings go through this type. Technical identifiers — AssemblyName,
/// namespaces, Mewview.exe, %LOCALAPPDATA%\Mewview\models, mewview_error.log —
/// stay "Mewview" in every locale and must never be localized.
/// </para>
/// </summary>
public static class AppDisplayName
{
    /// <summary>"瞄瞄" on Chinese systems, "Mewview" elsewhere.</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        // CurrentUICulture reflects the user's Windows display language
        // (GetUserDefaultUILanguage), not the OS install language.
        // The invariant culture has no two-letter code, so it falls through to "Mewview".
        var ui = CultureInfo.CurrentUICulture;

        return string.Equals(ui.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase)
            ? "瞄瞄"
            : "Mewview";
    }
}
