namespace ClipboardSnapper;

public sealed record FolderPreference(string Folder, bool CanUse, string? Warning);

public sealed class SaveFolderPreferences(string configPath, string defaultFolder)
{
    public string DefaultFolder { get; } = defaultFolder;
    public string ConfigPath { get; } = configPath;
    private readonly PortableConfig _config = new(configPath);

    public static SaveFolderPreferences ForCurrentProcess() => new(
        PortableConfig.ExecutableConfigPath,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ClipboardSnapper"));

    public Task<FolderPreference> LoadAsync() => Task.Run(() =>
    {
        string? saved;
        try { saved = _config.Read(document => document.Get("Storage", "SaveFolder")); }
        catch (Exception exception) when (IsStorageError(exception))
        {
            return Resolve(DefaultFolder, false,
                $"Could not read config.ini. Using the default folder. The configuration was not replaced. {exception.Message}");
        }
        return Resolve(saved ?? DefaultFolder, false, null);
    });

    public Task<FolderPreference> SaveAsync(string folder) => Task.Run(() => Resolve(folder, true, null));

    private FolderPreference Resolve(string folder, bool persist, string? warning)
    {
        var canUse = true;
        try { folder = CheckFolder(folder); }
        catch (Exception exception) when (IsStorageError(exception))
        {
            warning = Join(warning, $"The save folder cannot be used. Reverted to the default folder. {exception.Message}");
            folder = DefaultFolder;
            persist = true;
            try { folder = CheckFolder(folder); }
            catch (Exception fallbackError) when (IsStorageError(fallbackError))
            {
                canUse = false;
                warning = Join(warning, $"The default folder also cannot be used. Choose another folder before starting. {fallbackError.Message}");
            }
        }

        if (persist)
        {
            try { WriteFolder(folder); }
            catch (Exception exception) when (IsStorageError(exception))
            {
                warning = Join(warning, $"Could not save the folder in config.ini. The current selection is available for this session only. {exception.Message}");
            }
        }
        return new FolderPreference(folder, canUse, warning);
    }

    private static string CheckFolder(string folder)
    {
        folder = folder.Trim();
        if (folder.IndexOfAny(['\r', '\n', '\0']) >= 0 || !Path.IsPathFullyQualified(folder))
            throw new ArgumentException("Choose an absolute save folder path without line breaks.");
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        // Check write access away from the UI/capture threads; remove only our own probe.
        var probe = Path.Combine(folder, $".ClipboardSnapper-write-check-{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1, FileOptions.DeleteOnClose))
        {
            stream.WriteByte(0);
            stream.Flush(true);
        }
        return folder;
    }

    private void WriteFolder(string folder) => _config.Update(document => document.Set("Storage", "SaveFolder", folder));

    private static bool IsStorageError(Exception exception) => PortableConfig.IsStorageError(exception);

    private static string Join(string? first, string second) => first is null ? second : $"{first}\n{second}";
}
