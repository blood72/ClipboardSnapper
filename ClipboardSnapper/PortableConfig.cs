using System.Collections.Concurrent;
using System.Text;

namespace ClipboardSnapper;

internal sealed class PortableConfig(string path)
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new object());

    public static string ExecutableConfigPath => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)
        ?? throw new InvalidOperationException("The application executable location is unavailable."), "config.ini");

    public T Read<T>(Func<IniDocument, T> read)
    {
        lock (_gate) return read(IniDocument.Read(path));
    }

    public void Update(Action<IniDocument> update)
    {
        lock (_gate)
        {
            var document = IniDocument.Read(path);
            update(document);
            var temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                    {
                        writer.Write(document.Text);
                        writer.Flush();
                    }
                    stream.Flush(true);
                }
                ReplaceFile(temporary, path);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void ReplaceFile(string temporary, string destination)
    {
        // Windows file observers/scanners can briefly deny replacement. Settings I/O runs off the UI thread.
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, destination, overwrite: true); return; }
            catch (Exception exception) when (attempt < 4 && (exception is IOException or UnauthorizedAccessException) &&
                ((exception.HResult & 0xffff) is 5 or 32 or 33) && !IsReadOnly(destination))
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    private static bool IsReadOnly(string path)
    {
        try { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static bool IsStorageError(Exception exception) => exception is
        IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}

internal sealed class IniDocument(List<string> lines, string newLine)
{
    public string Text => string.Join(newLine, lines) + newLine;

    public static IniDocument Read(string path)
    {
        string text;
        try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (FileNotFoundException) { return new([], Environment.NewLine); }
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        var document = new IniDocument(lines, newLine);
        _ = document.Entries().ToArray();
        // Preserve the existing folder validation contract even when another setting is updated.
        _ = document.Get("Storage", "SaveFolder");
        return document;
    }

    private IEnumerable<(int Index, string Section, string Key, string Value)> Entries()
    {
        var section = "";
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[')
            {
                if (!line.EndsWith(']') || line[1..^1].Trim().Length == 0)
                    throw new InvalidDataException("config.ini contains an invalid section header.");
                section = line[1..^1].Trim();
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException("config.ini contains an invalid entry.");
            yield return (i, section, line[..equals].Trim(), line[(equals + 1)..].Trim());
        }
    }

    public IReadOnlyDictionary<string, string> GetSection(string section)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries().Where(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase)))
            if (!result.TryAdd(entry.Key, entry.Value))
                throw new InvalidDataException($"config.ini contains duplicate {entry.Key} entries in [{section}].");
        return result;
    }

    public string? Get(string section, string key)
    {
        var entries = Entries().Where(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase) &&
            e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length > 1) throw new InvalidDataException($"config.ini contains duplicate {key} entries.");
        return entries.FirstOrDefault().Value;
    }

    public void Set(string section, string key, string value)
    {
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("An INI value cannot contain line breaks.");
        _ = Get(section, key);
        var entry = Entries().Where(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase) &&
            e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entry.Length != 0)
        {
            var index = entry[0].Index;
            lines[index] = lines[index][..(lines[index].IndexOf('=') + 1)] + value;
            return;
        }
        var insertion = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']') && line[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase))
            {
                insertion = i + 1;
                while (insertion < lines.Count && !lines[insertion].TrimStart().StartsWith('[')) insertion++;
                break;
            }
        }
        if (insertion >= 0) lines.Insert(insertion, key + "=" + value);
        else
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add("[" + section + "]");
            lines.Add(key + "=" + value);
        }
    }

    public void RemoveKeys(string section, string prefix)
    {
        var indices = Entries().Where(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase) &&
            e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(e => e.Index).OrderDescending().ToArray();
        foreach (var index in indices) lines.RemoveAt(index);
    }
}
