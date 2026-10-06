using System.Globalization;
using ClipboardSnapper;

static class NamingContracts
{
    public static async Task RunAsync()
    {
        // Use local time so the expected capture-time fields are independent of the runner's time zone.
        var accepted = new DateTimeOffset(new DateTime(2026, 10, 6, 9, 3, 5, 123, DateTimeKind.Local));
        Check.That(FilenameRule.Parse(FilenameRule.DefaultFormula).Generate(accepted, 0) == "Clipboard_20261006_090305_123", "Default formula did not use capture time.");
        var recordedOffset = new DateTimeOffset(2026, 10, 6, 23, 59, 58, TimeSpan.FromHours(9));
        Check.That(FilenameRule.Parse(FilenameRule.DefaultFormula).Generate(recordedOffset, 0) == "Clipboard_20261006_235958_000", "Queued naming reinterpreted the recorded local clock fields in the current time zone.");
        var date = FilenameRule.Parse("$YYYY-$YY-$Y_$MMMM-$MMM-$MM-$M_$DDDD-$DDD-$DD-$D_$hh-$h-$mm-$m-$ss-$s_$fff-$ff-$f");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ko-KR");
            Check.That(date.Generate(accepted, 9) == "2026-26-6_October-Oct-10-10_Tuesday-Tue-06-6_09-9-03-3-05-5_123-12-1", "Date variables changed with UI culture or lost padding.");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var numbered = FilenameRule.Parse("Photo_${start=10;padding=4;increment=2}_${}_${start=-3;increment=-1}");
        Check.That(numbered.Generate(accepted, 0) == "Photo_0010_0_-3" && numbered.Generate(accepted, 2) == "Photo_0014_2_-5", "PowerRename-style counters are incorrect.");
        for (var i = 0; i < 20; i++) Check.That(numbered.Generate(accepted, 0) == "Photo_0010_0_-3", "Preview consumed a capture number.");
        Check.That(FilenameRule.Parse("Cost_$$5").Generate(accepted, 0) == "Cost_$5", "Literal dollar escaping failed.");
        foreach (var (formula, alphabet) in new[] {
            ("${rstringalnum=32}", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"),
            ("${rstringalpha=32}", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"),
            ("${rstringdigit=32}", "0123456789") })
        {
            var result = FilenameRule.Parse(formula).Generate(accepted, 0);
            Check.That(result.Length == 32 && result.All(alphabet.Contains), "Random variables produced invalid length/characters.");
        }
        Check.That(Guid.TryParseExact(FilenameRule.Parse("${ruuidv4}").Generate(accepted, 0), "D", out _), "UUID variable did not generate a UUID.");
        foreach (var invalid in new[] { "", " ", "../escape", "C:\\escape", "NUL", "con.png", "LPT¹", "tail.", "tail ", "line\nbreak", "$unknown", "${start=1", "${start=x}", "${start=1;start=2}", "${padding=-1}", "${padding=256}", "${rstringalpha=0}", "${rstringalpha=256}", new string('a', 256) })
        {
            try { FilenameRule.Parse(invalid).Generate(accepted, 0); throw new Exception("Invalid formula accepted: " + invalid); }
            catch (FormatException) { }
        }
        var options = new SaveOptions("images", ImageFormat.Png, NamingFormula: "Old_${start=1}");
        var captured = new CapturedImage([1], options, 5) { AcceptedAt = accepted, CaptureIndex = 2 };
        options = options with { NamingFormula = "New_$YYYY" };
        Check.That(FilenameRule.Parse(captured.Options.NamingFormula).Generate(captured.AcceptedAt, captured.CaptureIndex) == "Old_3", "An option change modified a queued capture's name.");

        var root = Path.Combine(Path.GetTempPath(), $"ClipboardSnapper-naming-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await VerifyCommitAsync(root);
            await VerifyPreferencesAsync(root);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("::notice::Naming contracts passed: all date/time variables, independent/signed counters, random variables, preview isolation, invalid Windows names, frozen capture metadata, collision gaps/directories/concurrent claims, persistent preset CRUD, shared INI writes and corrupt/read-only config protection.");
    }

    private static async Task VerifyCommitAsync(string root)
    {
        var folder = Path.Combine(root, "files");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "name.png"), [9, 8, 7]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "name (2).png"), [6, 5, 4]);
        Directory.CreateDirectory(Path.Combine(folder, "name (4).png"));
        var temporary = Path.Combine(folder, "first.tmp");
        await File.WriteAllBytesAsync(temporary, [1, 2, 3]);
        Check.That(Path.GetFileName(ImageFileCommit.Commit(temporary, folder, "name", "png")) == "name (3).png", "Suffixes did not use the lowest available candidate.");
        var paths = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            var source = Path.Combine(folder, $"parallel-{i}.tmp");
            File.WriteAllBytes(source, [(byte)i, 42]);
            return ImageFileCommit.Commit(source, folder, "name", "png");
        })));
        Check.That(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 32, "Concurrent claims overwrote a destination.");
        var bytes = paths.Select(File.ReadAllBytes).ToArray();
        Check.That(bytes.All(b => b.Length == 2 && b[1] == 42) && bytes.Select(b => b[0]).Distinct().Count() == 32, "A concurrent output was partial or overwritten.");
        Check.That((await File.ReadAllBytesAsync(Path.Combine(folder, "name.png"))).SequenceEqual(new byte[] { 9, 8, 7 }) &&
            (await File.ReadAllBytesAsync(Path.Combine(folder, "name (2).png"))).SequenceEqual(new byte[] { 6, 5, 4 }) &&
            Directory.Exists(Path.Combine(folder, "name (4).png")), "Collision handling changed an existing file/directory.");
        Check.That(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Successful moves left temporary files behind.");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(Path.Combine(folder, "MIXED.PNG"), [7]);
            File.WriteAllBytes(temporary, [8]);
            Check.That(Path.GetFileName(ImageFileCommit.Commit(temporary, folder, "mixed", "png")) == "mixed (2).png", "Windows case-insensitive collision handling failed.");
        }
    }

    private static async Task VerifyPreferencesAsync(string root)
    {
        var config = Path.Combine(root, "config.ini");
        var preferences = new NamingPreferences(config);
        Check.That((await preferences.LoadAsync()) is { Warning: null, State.Formula: FilenameRule.DefaultFormula }, "First launch did not supply a default rule.");
        var unrelated = "; Preserve this comment\r\n[Updates]\r\nEnabled=false\r\nInterval=weekly\r\n";
        await File.WriteAllTextAsync(config, unrelated);
        var folderPreferences = new SaveFolderPreferences(config, Path.Combine(root, "default"));
        var folder = Path.Combine(root, "images");
        await folderPreferences.SaveAsync(folder);
        var preset = new NamingPreset(Guid.NewGuid().ToString("N"), "일별 \"photos\" =;#", "Photo_$YYYY$MM$DD_${start=1;padding=4}");
        var state = new NamingState(preset.Formula, preset.Id, [preset]);
        Check.That((await preferences.SaveAsync(state)).Warning is null, "Could not save a named preset.");
        var loaded = await new NamingPreferences(config).LoadAsync();
        Check.That(loaded.Warning is null && loaded.State.Formula == preset.Formula && loaded.State.SelectedPresetId == preset.Id &&
            loaded.State.Presets.Single() == preset, "Preset name/formula/selection did not survive restart.");
        Check.That((await folderPreferences.LoadAsync()).Folder == folder && (await File.ReadAllTextAsync(config)).StartsWith(unrelated, StringComparison.Ordinal), "Preset save changed the folder or unrelated settings.");
        preset = preset with { Formula = "Updated_${start=8}" };
        state = new(preset.Formula, preset.Id, [preset]);
        await preferences.SaveAsync(state);
        Check.That((await preferences.LoadAsync()).State.Presets.Single() == preset, "Updating a preset added a duplicate or lost its ID.");
        await Task.WhenAll(Enumerable.Range(0, 20).SelectMany(i => new Task[] {
            preferences.SaveAsync(state), folderPreferences.SaveAsync(Path.Combine(root, "folder-" + i)) }));
        Check.That((await preferences.LoadAsync()).State.Presets.Single() == preset &&
            (await folderPreferences.LoadAsync()).Folder.StartsWith(Path.Combine(root, "folder-"), StringComparison.Ordinal) &&
            (await File.ReadAllTextAsync(config)).StartsWith(unrelated, StringComparison.Ordinal), "Concurrent folder/preset writes lost unrelated state.");
        if (OperatingSystem.IsWindows())
        {
            var original = await File.ReadAllTextAsync(config);
            File.SetAttributes(config, FileAttributes.ReadOnly);
            try
            {
                Check.That((await preferences.SaveAsync(NamingState.Default)).Warning is not null &&
                    await File.ReadAllTextAsync(config) == original, "A read-only config was overwritten or claimed to be saved.");
            }
            finally { File.SetAttributes(config, FileAttributes.Normal); }
            using (var observer = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var locked = await preferences.SaveAsync(NamingState.Default);
                Check.That(locked.Warning is not null && await File.ReadAllTextAsync(config) == original,
                    "A persistent replacement lock should warn without changing the original configuration.");
            }
            Check.That((await preferences.SaveAsync(state)).Warning is null,
                "Configuration saving did not recover after a replacement lock was released.");
        }
        await preferences.SaveAsync(NamingState.Default);
        loaded = await preferences.LoadAsync();
        Check.That(loaded.State.Presets.Count == 0 && loaded.State.SelectedPresetId == "" && loaded.State.Formula == FilenameRule.DefaultFormula,
            "Deleted preset returned after restart or default selection was not restored.");
        foreach (var malformed in new[] { "[broken\n", "[NamingPresets]\nPreset." + preset.Id + "=not-json\n", "[Naming]\nFormula=NUL\n", "[Naming]\nSelectedPreset=missing\n", "[Naming]\nFormula=" + new string('a', 252) + "\n" })
        {
            await File.WriteAllTextAsync(config, malformed);
            Check.That((await preferences.LoadAsync()).Warning is not null, "Invalid preset configuration did not show a warning.");
            Check.That((await preferences.SaveAsync(state)).Warning is not null && await File.ReadAllTextAsync(config) == malformed,
                "Malformed presets were replaced and lost.");
        }
        Check.That(!Directory.EnumerateFiles(root, "config.ini.*.tmp").Any(), "Preset persistence left temporary files behind.");
    }
}
