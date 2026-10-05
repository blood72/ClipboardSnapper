using System.Text;

namespace ClipboardSnapper;

public sealed record FolderPreference(string Folder, bool CanUse, string? Warning);

public sealed class SaveFolderPreferences(string configPath, string defaultFolder)
{
    public string DefaultFolder { get; } = defaultFolder;

    public static SaveFolderPreferences ForCurrentProcess() => new(
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("The application executable location is unavailable."), "config.ini"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ClipboardSnapper"));

    public Task<FolderPreference> LoadAsync() => Task.Run(() =>
    {
        string? saved;
        try { saved = ReadDocument().Folder; }
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

    private sealed record Document(List<string> Lines, string NewLine, int FolderIndex, int SectionEnd, string? Folder);

    private Document ReadDocument()
    {
        string text;
        try { text = File.ReadAllText(configPath, new UTF8Encoding(false, true)); }
        catch (FileNotFoundException) { return new([], Environment.NewLine, -1, -1, null); }
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        var section = "";
        var index = -1;
        var sectionEnd = -1;
        string? folder = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[')
            {
                if (!line.EndsWith(']') || line.Length < 3)
                    throw new InvalidDataException("config.ini contains an invalid section header.");
                section = line[1..^1].Trim();
                if (section.Equals("Storage", StringComparison.OrdinalIgnoreCase)) sectionEnd = i + 1;
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException("config.ini contains an invalid entry.");
            if (!section.Equals("Storage", StringComparison.OrdinalIgnoreCase)) continue;
            sectionEnd = i + 1;
            if (!line[..equals].Trim().Equals("SaveFolder", StringComparison.OrdinalIgnoreCase)) continue;
            if (index >= 0) throw new InvalidDataException("config.ini contains duplicate SaveFolder entries.");
            index = i;
            folder = line[(equals + 1)..].Trim();
        }
        return new(lines, newLine, index, sectionEnd, folder);
    }

    private void WriteFolder(string folder)
    {
        // Re-read before each update so unrelated settings are retained. Do not overwrite an unreadable/malformed file.
        var document = ReadDocument();
        if (document.FolderIndex >= 0)
        {
            var old = document.Lines[document.FolderIndex];
            document.Lines[document.FolderIndex] = old[..(old.IndexOf('=') + 1)] + folder;
        }
        else if (document.SectionEnd >= 0)
            document.Lines.Insert(document.SectionEnd, $"SaveFolder={folder}");
        else
        {
            if (document.Lines.Count > 0) document.Lines.Add("");
            document.Lines.Add("[Storage]");
            document.Lines.Add($"SaveFolder={folder}");
        }

        var temporary = configPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    writer.Write(string.Join(document.NewLine, document.Lines) + document.NewLine);
                    writer.Flush();
                }
                stream.Flush(true);
            }
            File.Move(temporary, configPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsStorageError(Exception exception) => exception is
        IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static string Join(string? first, string second) => first is null ? second : $"{first}\n{second}";
}
