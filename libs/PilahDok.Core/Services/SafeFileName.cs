using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PilahDok.Core.Services;

public static partial class SafeFileName
{
    private const int MaxBaseLength = 100;
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Sanitize(string? value)
    {
        var raw = Path.GetFileName(value ?? string.Empty);
        var withoutExtension = Path.GetFileNameWithoutExtension(raw).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(withoutExtension.Length);

        foreach (var character in withoutExtension)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        var normalized = UnsafeCharacters().Replace(builder.ToString().Normalize(NormalizationForm.FormC), "-");
        normalized = RepeatedSeparators().Replace(normalized, "-").Trim(' ', '.', '-', '_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "bagian";
        }

        if (ReservedWindowsNames.Contains(normalized))
        {
            normalized = $"file-{normalized}";
        }

        if (normalized.Length > MaxBaseLength)
        {
            normalized = normalized[..MaxBaseLength].TrimEnd(' ', '.', '-', '_');
        }

        return normalized + ".docx";
    }

    public static IReadOnlyList<string> MakeUnique(IEnumerable<string> names)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var name in names)
        {
            var sanitized = Sanitize(name);
            var extension = Path.GetExtension(sanitized);
            var baseName = Path.GetFileNameWithoutExtension(sanitized);
            var candidate = sanitized;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = $"{baseName}-{suffix}{extension}";
                suffix++;
            }

            result.Add(candidate);
        }

        return result;
    }

    public static string NormalizeTitle(string? text)
    {
        var collapsed = Whitespace().Replace(text?.Trim() ?? string.Empty, " ");
        return collapsed.Length <= 160 ? collapsed : collapsed[..157] + "...";
    }

    [GeneratedRegex("[^\\p{L}\\p{N} _.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeCharacters();

    [GeneratedRegex("[\\s._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedSeparators();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
