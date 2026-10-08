namespace ClipboardSnapper;

public sealed record FolderPreference(string Folder, bool CanUse, IReadOnlyList<UiMessage> Notices)
{
    public string? Warning => Notices.Count == 0 ? null : string.Join("\n", Notices.Select(m => m.English));
}

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
                new UiMessage("FolderReadFailed", UiMessage.FromException(exception)));
        }
        return Resolve(saved ?? DefaultFolder, false, null);
    });

    public Task<FolderPreference> SaveAsync(string folder) => Task.Run(() => Resolve(folder, true, null));

    private FolderPreference Resolve(string folder, bool persist, UiMessage? initialNotice)
    {
        var notices = new List<UiMessage>();
        if (initialNotice is not null) notices.Add(initialNotice);
        var canUse = true;
        try { folder = CheckFolder(folder); }
        catch (Exception exception) when (IsStorageError(exception))
        {
            notices.Add(new("FolderFallback", UiMessage.FromException(exception)));
            folder = DefaultFolder;
            persist = true;
            try { folder = CheckFolder(folder); }
            catch (Exception fallbackError) when (IsStorageError(fallbackError))
            {
                canUse = false;
                notices.Add(new("DefaultFolderFailed", UiMessage.FromException(fallbackError)));
            }
        }

        if (persist)
        {
            try { WriteFolder(folder); }
            catch (Exception exception) when (IsStorageError(exception))
            {
                notices.Add(new("FolderWriteFailed", UiMessage.FromException(exception)));
            }
        }
        return new FolderPreference(folder, canUse, notices.ToArray());
    }

    private static string CheckFolder(string folder)
    {
        folder = folder.Trim();
        if (folder.IndexOfAny(['\r', '\n', '\0']) >= 0 || !Path.IsPathFullyQualified(folder))
            throw UiMessage.Argument("AbsoluteFolderNoBreaks");
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

}
