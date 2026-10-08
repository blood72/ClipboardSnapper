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
        { ["en"] = new("en", "English", UiText.EnglishStrings) };
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
                        if (UiText.EnglishStrings.TryGetValue(entry.Name, out var english))
                        {
                            var expected = Placeholder.Matches(english).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
                            var actual = Placeholder.Matches(value).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
                            if (!expected.SetEquals(actual)) throw UiMessage.InvalidData("InvalidTranslationPlaceholder", entry.Name);
                            if (expected.Count > 0) _ = CompositeFormat.Parse(value);
                        }
                    }
                    if (!languages.TryAdd(code, new(code, name, strings)))
                    {
                        if (!code.Equals("en", StringComparison.OrdinalIgnoreCase))
                            throw UiMessage.InvalidData("DuplicateLanguageTag");
                        languages["en"] = new("en", name, strings);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                    InvalidOperationException or KeyNotFoundException or FormatException or DecoderFallbackException)
                { notices.Add(new("LanguageFileInvalid", file, UiMessage.FromException(exception))); }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { notices.Add(new("LanguageFolderFailed", exception.Message)); }
        return new(languages.Values.OrderBy(l => l.Code, StringComparer.Ordinal).ToArray(), notices);
    }

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
    internal static IReadOnlyDictionary<string, string> EnglishStrings { get; } = ReadEnglish();
    public LanguageCatalog Catalog { get; private set; };
    public LanguageOption Language { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public UiText() : this(LanguageCatalog.Load(Path.Combine(AppContext.BaseDirectory, "lang"))) { }
    public UiText(LanguageCatalog catalog)
    {
        Catalog = catalog;
        Language = catalog.Languages.First(l => l.Code == "en");
    }
    public string this[string key] => Language.Strings.TryGetValue(key, out var value) ? value : EnglishStrings[key];
    public string Format(string key, params object[] arguments) => FormatValue(this[key], arguments);
    public static string English(string key, params object[] arguments) => FormatValue(EnglishStrings[key], arguments);

    public void Select(string code)
    {
        Language = Catalog.Languages.First(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
    public void ReplaceCatalog(LanguageCatalog catalog)
    {
        var code = Language.Code;
        Catalog = catalog;
        Select(catalog.Resolve(code, "en"));
    }
    private static string FormatValue(string template, object[] arguments) => arguments.Length == 0
        ? template : string.Format(CultureInfo.InvariantCulture, template, arguments);
    private static IReadOnlyDictionary<string, string> ReadEnglish()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("ClipboardSnapper.lang.en.json")
            ?? throw new InvalidOperationException("The embedded English translation is missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("strings").EnumerateObject()
            .ToDictionary(e => e.Name, e => e.Value.GetString()!, StringComparer.Ordinal);
    }
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
