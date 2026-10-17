using System;
using System.Text.RegularExpressions;

namespace XingyiStarry.Mp;

/// <summary>
/// Render ${key} placeholders only at the display boundary. Session messages, status
/// and history retain their template strings so every peer uses its own game language.
/// </summary>
internal static partial class MpText
{
    private static readonly Regex Placeholder = new Regex(@"\$\{([a-z0-9_]+)\}", RegexOptions.CultureInvariant);
    internal static bool IsChinese { get; private set; }
    internal static event Action? LanguageChanged;

    internal static void SetLanguage(bool chinese)
    {
        if (IsChinese == chinese) return;
        IsChinese = chinese;
        LanguageChanged?.Invoke();
    }

    internal static string Render(string template) => Render(template, IsChinese);

    internal static string Render(string template, bool chinese)
    {
        if (string.IsNullOrEmpty(template) || template.IndexOf("${", StringComparison.Ordinal) < 0) return template;
        return Placeholder.Replace(template, match => Catalog.TryGetValue(match.Groups[1].Value, out var text)
            ? chinese ? text.Chinese : text.English
            : match.Value);
    }
}
