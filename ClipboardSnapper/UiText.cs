using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipboardSnapper;

public sealed record LanguageOption(string Code, string Name, IReadOnlyDictionary<string, string> Strings);
public sealed record LanguageCatalog(IReadOnlyList<LanguageOption> Languages, IReadOnlyList<UiMessage> Notices)
{
    private static readonly Regex CodePattern = new("^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)(?:[^}]*)\}", RegexOptions.CultureInvariant);

    public static LanguageCatalog Load(string folder)
    {
        var languages = new Dictionary<string, LanguageOption>(StringComparer.OrdinalIgnoreCase)
        { ["en"] = UiText.StartupEnglish };
        var notices = new List<UiMessage>();
        var fileCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    var code = Path.GetFileNameWithoutExtension(file);
                    if (!CodePattern.IsMatch(code)) throw UiMessage.InvalidData("InvalidLanguageTag");
                    if (!fileCodes.Add(code)) throw UiMessage.InvalidData("DuplicateLanguageTag");
                    languages[code] = ReadPack(file, code, UiText.EnglishStrings);
                }
                catch (Exception exception) when (IsPackError(exception))
                { notices.Add(new("LanguageFileInvalid", file, UiMessage.FromException(exception))); }
            }
            if (!fileCodes.Contains("en")) notices.Add(new("EnglishPackMissing", Path.Combine(folder, "en.json")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { notices.Add(new("LanguageFolderFailed", exception.Message)); }
        return new(languages.Values.OrderBy(l => l.Code, StringComparer.Ordinal).ToArray(), notices);
    }

    internal static LanguageOption ReadPack(string file, string code, IReadOnlyDictionary<string, string>? baseline = null)
    {
        if (new FileInfo(file).Length > 1_048_576) throw UiMessage.InvalidData("LanguageFileTooLarge");
        using var json = JsonDocument.Parse(File.ReadAllText(file, new UTF8Encoding(false, true)));
        var root = json.RootElement;
        var name = root.GetProperty("languageName").GetString();
        if (string.IsNullOrWhiteSpace(name)) throw UiMessage.InvalidData("InvalidLanguageName");
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("strings").EnumerateObject())
        {
            var value = entry.Value.GetString();
            if (string.IsNullOrWhiteSpace(value) || !strings.TryAdd(entry.Name, value))
                throw UiMessage.InvalidData("InvalidTranslationEntry");
            var actual = Placeholder.Matches(value).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            if (baseline is not null && baseline.TryGetValue(entry.Name, out var english))
            {
                var expected = Placeholder.Matches(english).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
                if (!expected.SetEquals(actual)) throw UiMessage.InvalidData("InvalidTranslationPlaceholder", entry.Name);
            }
            if (actual.Count > 0) _ = CompositeFormat.Parse(value);
        }
        if (strings.Count == 0) throw UiMessage.InvalidData("InvalidTranslationEntry");
        return new(code.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en" : code, name, strings);
    }

    internal static bool IsPackError(Exception exception) => exception is IOException or InvalidDataException or
        UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or
        FormatException or DecoderFallbackException;

    public string Resolve(string? saved, string windowsLanguage)
    {
        if (saved is not null) return Languages.FirstOrDefault(l => l.Code.Equals(saved, StringComparison.OrdinalIgnoreCase))?.Code ?? "en";
        var candidate = windowsLanguage;
        while (candidate.Length > 0)
        {
            var match = Languages.FirstOrDefault(l => l.Code.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Code;
            var separator = candidate.LastIndexOf('-');
            if (separator < 0) break;
            candidate = candidate[..separator];
        }
        return "en";
    }
}

public sealed class UiText : INotifyPropertyChanged
{
    private static LanguageOption? _startupEnglish;
    internal static LanguageOption StartupEnglish => _startupEnglish
        ?? throw new InvalidOperationException("Load lang/en.json before starting the application.");
    internal static IReadOnlyDictionary<string, string> EnglishStrings => StartupEnglish.Strings;

    // Keep the successfully read startup file in memory for diagnostics and invalid-file recovery.
    // Translation text is never embedded or written by the application.
    internal static void InitializeEnglish(string folder) =>
        _startupEnglish = LanguageCatalog.ReadPack(Path.Combine(folder, "en.json"), "en");

    public LanguageCatalog Catalog { get; private set; }
    public LanguageOption Language { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public UiText() : this(LanguageCatalog.Load(PortableConfig.LanguageDirectory)) { }
    public UiText(LanguageCatalog catalog)
    {
        Catalog = catalog;
        Language = catalog.Languages.First(l => l.Code == "en");
    }
    public string this[string key] => Language.Strings.TryGetValue(key, out var value) ? value :
        Catalog.Languages.First(l => l.Code == "en").Strings.TryGetValue(key, out var english) ? english : EnglishStrings.GetValueOrDefault(key, key);
    public string Format(string key, params object[] arguments) => FormatValue(this[key], arguments);
    public static string English(string key, params object[] arguments) =>
        _startupEnglish is not null && _startupEnglish.Strings.TryGetValue(key, out var value)
            ? FormatValue(value, arguments) : key + (arguments.Length == 0 ? "" : ": " + string.Join(", ", arguments));

    public void ReplaceCatalog(LanguageCatalog catalog, string code)
    {
        Catalog = catalog;
        Select(code);
    }

    public void Select(string code)
    {
        Language = Catalog.Languages.First(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
    private static string FormatValue(string template, object[] arguments) => arguments.Length == 0
        ? template : string.Format(CultureInfo.InvariantCulture, template, arguments);
}

public sealed record UiMessageList(IReadOnlyList<UiMessage> Messages);

public sealed record UiMessage(string Key, params object[] Arguments)
{
    private const string ErrorDataKey = "ClipboardSnapper.UiMessage";
    public string English => UiText.English(Key, Arguments.Select(a => a is UiMessage m ? m.English : a is UiMessageList list ? string.Join("\n", list.Messages.Select(message => message.English)) : a).ToArray());
    public string Render(UiText text) => text.Format(Key, Arguments.Select(a => a is UiMessage m ? m.Render(text) : a is UiMessageList list ? string.Join("\n", list.Messages.Select(message => message.Render(text))) : a).ToArray());
    public static UiMessage FromException(Exception exception) => exception.Data[ErrorDataKey] as UiMessage ?? new("Raw", exception.Message);
    private static T Attach<T>(T exception, UiMessage message) where T : Exception
    { exception.Data[ErrorDataKey] = message; return exception; }
    public static FormatException Format(string key, params object[] args) => Attach(new FormatException(UiText.English(key, args)), new(key, args));
    public static ArgumentException Argument(string key, params object[] args) => Attach(new ArgumentException(UiText.English(key, args)), new(key, args));
    public static IOException Io(string key, params object[] args) => Attach(new IOException(UiText.English(key, args)), new(key, args));
    public static InvalidDataException InvalidData(string key, params object[] args) => Attach(new InvalidDataException(UiText.English(key, args)), new(key, args));
    public static InvalidOperationException InvalidOperation(string key, params object[] args) => Attach(new InvalidOperationException(UiText.English(key, args)), new(key, args));
    public static ArgumentOutOfRangeException OutOfRange(string parameter, string key) => Attach(new ArgumentOutOfRangeException(parameter, UiText.English(key)), new(key));
}
