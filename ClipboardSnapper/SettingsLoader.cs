namespace ClipboardSnapper;

public sealed record LoadedSettings(FolderPreference Folder, NamingPreference Naming,
    LanguagePreference Language, ImagePreference Image, LanguageCatalog Catalog);

public sealed class SettingsLoader(string configPath, string defaultFolder, string languageFolder)
{
    private readonly PortableConfig _config = new(configPath);
    private readonly SaveFolderPreferences _folder = new(configPath, defaultFolder);

    public Task<LoadedSettings> LoadAsync() => Task.Run(() =>
    {
        var catalog = LanguageCatalog.Load(languageFolder);
        try
        {
            // Read one INI snapshot and keep application writes outside this load boundary.
            return _config.Read(document => new LoadedSettings(_folder.Read(document), NamingPreferences.Read(document),
                LanguagePreferences.Read(document), ImagePreferences.Read(document), catalog));
        }
        catch (Exception exception) when (PortableConfig.IsStorageError(exception))
        {
            return new LoadedSettings(_folder.ReadFailure(exception), NamingPreferences.ReadFailure(exception),
                LanguagePreferences.ReadFailure(exception), ImagePreferences.ReadFailure(exception), catalog);
        }
    });
}
