namespace ClipboardSnapper;

public sealed record LanguagePreference(string? Code, UiMessage? Notice);

public sealed class LanguagePreferences(string configPath)
{
    private readonly PortableConfig _config = new(configPath);
    public Task<LanguagePreference> LoadAsync() => Task.Run(() =>
    {
        try { return new LanguagePreference(_config.Read(d => d.Get("Appearance", "Language")), null); }
        catch (Exception exception) when (PortableConfig.IsStorageError(exception))
        { return new LanguagePreference(null, new("LanguageReadFailed", UiMessage.FromException(exception))); }
    });
    public Task<LanguagePreference> SaveAsync(string code) => Task.Run(() =>
    {
        try
        {
            _config.Update(d => d.Set("Appearance", "Language", code));
            return new LanguagePreference(code, null);
        }
        catch (Exception exception) when (PortableConfig.IsStorageError(exception))
        { return new LanguagePreference(code, new("LanguageWriteFailed", UiMessage.FromException(exception))); }
    });
}
