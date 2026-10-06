using System.Text.Json;

namespace ClipboardSnapper;

public sealed record NamingPreset(string Id, string Name, string Formula)
{
    public static NamingPreset Default { get; } = new("", "Default (built-in)", FilenameRule.DefaultFormula);
}

public sealed record NamingState(string Formula, string SelectedPresetId, IReadOnlyList<NamingPreset> Presets)
{
    public static NamingState Default => new(FilenameRule.DefaultFormula, "", []);
}

public sealed record NamingPreference(NamingState State, string? Warning);

public sealed class NamingPreferences(string configPath)
{
    private readonly PortableConfig _config = new(configPath);

    public Task<NamingPreference> LoadAsync() => Task.Run(() =>
    {
        try { return new NamingPreference(_config.Read(ReadState), null); }
        catch (Exception exception) when (IsPreferenceError(exception))
        {
            return new NamingPreference(NamingState.Default,
                $"Could not read filename presets from config.ini. Using the default rule. The configuration was not replaced. {exception.Message}");
        }
    });

    public Task<NamingPreference> SaveAsync(NamingState state) => Task.Run(() =>
    {
        Validate(state);
        try
        {
            _config.Update(document =>
            {
                // Do not replace malformed existing presets with a partial/default collection.
                _ = ReadState(document);
                document.Set("Naming", "Formula", state.Formula);
                document.Set("Naming", "SelectedPreset", state.SelectedPresetId);
                document.RemoveKeys("NamingPresets", "Preset.");
                foreach (var preset in state.Presets)
                    document.Set("NamingPresets", "Preset." + preset.Id, JsonSerializer.Serialize(preset));
            });
            return new NamingPreference(state, null);
        }
        catch (Exception exception) when (IsPreferenceError(exception))
        {
            return new NamingPreference(state,
                $"Could not save filename presets in config.ini. Changes are available for this session only. {exception.Message}");
        }
    });

    private static NamingState ReadState(IniDocument document)
    {
        var presets = new List<NamingPreset>();
        foreach (var (key, value) in document.GetSection("NamingPresets"))
        {
            if (!key.StartsWith("Preset.", StringComparison.OrdinalIgnoreCase)) continue;
            var preset = JsonSerializer.Deserialize<NamingPreset>(value)
                ?? throw new InvalidDataException("A filename preset is empty.");
            if (!key[7..].Equals(preset.Id, StringComparison.Ordinal))
                throw new InvalidDataException("A filename preset ID does not match its INI entry.");
            presets.Add(preset);
        }
        var state = new NamingState(document.Get("Naming", "Formula") ?? FilenameRule.DefaultFormula,
            document.Get("Naming", "SelectedPreset") ?? "", presets.ToArray());
        Validate(state);
        return state;
    }

    private static void Validate(NamingState state)
    {
        ValidateFormula(state.Formula);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in state.Presets)
        {
            if (preset.Id is null || !Guid.TryParseExact(preset.Id, "N", out _) || !ids.Add(preset.Id) ||
                string.IsNullOrWhiteSpace(preset.Name) || preset.Name != preset.Name.Trim() ||
                preset.Name.Any(c => c < 32) || !names.Add(preset.Name))
                throw new InvalidDataException("Preset names and IDs must be valid and unique.");
            ValidateFormula(preset.Formula);
        }
        if (state.SelectedPresetId is null || (state.SelectedPresetId.Length > 0 && !ids.Contains(state.SelectedPresetId)))
            throw new InvalidDataException("The selected filename preset does not exist.");
    }

    private static void ValidateFormula(string formula) =>
        FilenameRule.ValidateName(FilenameRule.Parse(formula).Generate(DateTimeOffset.Now, 0) + ".png");

    private static bool IsPreferenceError(Exception exception) => PortableConfig.IsStorageError(exception) ||
        exception is FormatException or JsonException or OverflowException;
}
