using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace ClipboardSnapper;

public sealed class FilenameRule
{
    public const string DefaultFormula = "Clipboard_$YYYY$MM$DD_$hh$mm$ss_$fff";
    private static readonly string[] DateTokens =
        ["YYYY", "MMMM", "DDDD", "MMM", "DDD", "fff", "YY", "MM", "DD", "hh", "mm", "ss", "ff", "Y", "M", "D", "h", "m", "s", "f"];
    private readonly List<Func<DateTimeOffset, long, string>> _parts;

    private FilenameRule(List<Func<DateTimeOffset, long, string>> parts) => _parts = parts;

    public static FilenameRule Parse(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula)) throw UiMessage.Format("EnterFormula");
        var parts = new List<Func<DateTimeOffset, long, string>>();
        for (var i = 0; i < formula.Length;)
        {
            if (formula[i] != '$')
            {
                var end = formula.IndexOf('$', i);
                if (end < 0) end = formula.Length;
                var literal = formula[i..end];
                parts.Add((_, _) => literal);
                i = end;
                continue;
            }
            if (i + 1 < formula.Length && formula[i + 1] == '$')
            {
                parts.Add((_, _) => "$");
                i += 2;
                continue;
            }
            if (i + 1 < formula.Length && formula[i + 1] == '{')
            {
                var end = formula.IndexOf('}', i + 2);
                if (end < 0) throw UiMessage.Format("CloseVariable");
                parts.Add(ParseExpression(formula[(i + 2)..end]));
                i = end + 1;
                continue;
            }
            var token = DateTokens.FirstOrDefault(token => formula.AsSpan(i + 1).StartsWith(token, StringComparison.Ordinal));
            if (token is null) throw UiMessage.Format("UnknownVariable");
            // Retain the local clock fields recorded at acceptance even if the OS time zone later changes.
            parts.Add((time, _) => FormatDate(time.DateTime, token));
            i += token.Length + 1;
        }
        return new FilenameRule(parts);
    }

    public string Generate(DateTimeOffset acceptedAt, long captureIndex)
    {
        if (captureIndex < 0) throw new ArgumentOutOfRangeException(nameof(captureIndex));
        var result = new StringBuilder();
        foreach (var part in _parts) result.Append(part(acceptedAt, captureIndex));
        var stem = result.ToString();
        ValidateName(stem);
        return stem;
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Length > 255 || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            throw UiMessage.Format("InvalidWindowsName");
        var device = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                "123456789¹²³".Contains(device[3])))
            throw UiMessage.Format("ReservedName");
    }

    private static Func<DateTimeOffset, long, string> ParseExpression(string expression)
    {
        if (expression == "ruuidv4") return (_, _) => Guid.NewGuid().ToString("D");
        foreach (var (key, alphabet) in new[] {
            ("rstringalnum", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"),
            ("rstringalpha", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"),
            ("rstringdigit", "0123456789") })
        {
            if (!expression.StartsWith(key + "=", StringComparison.Ordinal)) continue;
            if (!int.TryParse(expression[(key.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length is < 1 or > 255)
                throw UiMessage.Format("RandomLength");
            return (_, _) => string.Create(length, alphabet, (span, characters) =>
            {
                for (var i = 0; i < span.Length; i++) span[i] = characters[RandomNumberGenerator.GetInt32(characters.Length)];
            });
        }
        long start = 0, increment = 1;
        var padding = 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (expression.Length != 0)
        {
            foreach (var option in expression.Split(';'))
            {
                var pair = option.Split('=');
                if (pair.Length != 2 || !keys.Add(pair[0]) ||
                    !long.TryParse(pair[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                    throw UiMessage.Format("CounterSyntax");
                switch (pair[0])
                {
                    case "start": start = number; break;
                    case "increment": increment = number; break;
                    case "padding" when number is >= 0 and <= 255: padding = (int)number; break;
                    default: throw UiMessage.Format("CounterOptions");
                }
            }
        }
        return (_, index) => (new BigInteger(start) + new BigInteger(increment) * index)
            .ToString("D" + padding, CultureInfo.InvariantCulture);
    }

    private static string FormatDate(DateTime date, string token) => token switch
    {
        "YYYY" => date.Year.ToString("D4", CultureInfo.InvariantCulture),
        "YY" => (date.Year % 100).ToString("D2", CultureInfo.InvariantCulture),
        "Y" => (date.Year % 10).ToString(CultureInfo.InvariantCulture),
        "MMMM" => date.ToString("MMMM", CultureInfo.InvariantCulture),
        "MMM" => date.ToString("MMM", CultureInfo.InvariantCulture),
        "DDDD" => date.ToString("dddd", CultureInfo.InvariantCulture),
        "DDD" => date.ToString("ddd", CultureInfo.InvariantCulture),
        "MM" => date.Month.ToString("D2", CultureInfo.InvariantCulture),
        "M" => date.Month.ToString(CultureInfo.InvariantCulture),
        "DD" => date.Day.ToString("D2", CultureInfo.InvariantCulture),
        "D" => date.Day.ToString(CultureInfo.InvariantCulture),
        "hh" => date.Hour.ToString("D2", CultureInfo.InvariantCulture),
        "h" => date.Hour.ToString(CultureInfo.InvariantCulture),
        "mm" => date.Minute.ToString("D2", CultureInfo.InvariantCulture),
        "m" => date.Minute.ToString(CultureInfo.InvariantCulture),
        "ss" => date.Second.ToString("D2", CultureInfo.InvariantCulture),
        "s" => date.Second.ToString(CultureInfo.InvariantCulture),
        "fff" => date.Millisecond.ToString("D3", CultureInfo.InvariantCulture),
        "ff" => date.Millisecond.ToString("D3", CultureInfo.InvariantCulture)[..2],
        "f" => date.Millisecond.ToString("D3", CultureInfo.InvariantCulture)[..1],
        _ => throw UiMessage.Format("UnknownDate")
    };
}

public static class ImageFileCommit
{
    public static string Commit(string temporaryPath, string folder, string stem, string extension)
    {
        // Move a complete file without replacement. Retry only destination collisions, never overwrite.
        for (long suffix = 1; ; suffix = checked(suffix + 1))
        {
            var name = stem + (suffix == 1 ? "" : " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")") + "." + extension;
            FilenameRule.ValidateName(name);
            var destination = Path.Combine(folder, name);
            if (File.Exists(destination) || Directory.Exists(destination)) continue;
            try { File.Move(temporaryPath, destination, overwrite: false); return destination; }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 80 or 183 ||
                File.Exists(destination) || Directory.Exists(destination)) { }
        }
    }
}
