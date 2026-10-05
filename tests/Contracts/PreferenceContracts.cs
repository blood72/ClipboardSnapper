using ClipboardSnapper;

static class PreferenceContracts
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ClipboardSnapper-preferences-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "config.ini");
            var fallback = Path.Combine(root, "default-images");
            var preferences = new SaveFolderPreferences(config, fallback);
            var first = await preferences.LoadAsync();
            Check.That(first.Folder == fallback && first.CanUse && first.Warning is null && !File.Exists(config),
                "First run must use the default without requiring a configuration file.");

            var unrelated = "; Keep this comment\r\n[Updates]\r\nEnabled=false\r\nInterval=weekly\r\nCustom=one=two;#three\r\n";
            await File.WriteAllTextAsync(config, unrelated);
            var chosen = Path.Combine(root, "한글 images=1;#");
            var saved = await preferences.SaveAsync(chosen);
            Check.That(saved is { CanUse: true, Warning: null } && saved.Folder == chosen, "A valid chosen folder was not saved.");
            Check.That((await File.ReadAllTextAsync(config)).StartsWith(unrelated, StringComparison.Ordinal), "Saving destroyed unrelated INI entries/comments.");
            var restarted = await new SaveFolderPreferences(config, fallback).LoadAsync();
            Check.That(restarted.Folder == chosen && restarted.CanUse, "Restart did not restore the Unicode folder with INI punctuation.");
            Check.That(!Directory.EnumerateFileSystemEntries(chosen).Any(), "Folder validation left a probe behind.");

            var next = Path.Combine(root, "new-images");
            await preferences.SaveAsync(next);
            Check.That((await preferences.LoadAsync()).Folder == next, "The changed preference did not replace the old folder.");
            Check.That(Directory.Exists(next), "A missing but creatable folder must remain usable.");

            // Simulate a remembered folder becoming unusable without changing any real saved image.
            var existing = Path.Combine(chosen, "existing.png");
            await File.WriteAllBytesAsync(existing, [1, 2, 3]);
            var blocked = Path.Combine(root, "blocked");
            await File.WriteAllTextAsync(blocked, "not a directory");
            await File.WriteAllTextAsync(config, unrelated + $"\r\n[Storage]\r\nSaveFolder={Path.Combine(blocked, "images")}\r\n");
            var reverted = await preferences.LoadAsync();
            Check.That(reverted.Folder == fallback && reverted.CanUse && reverted.Warning is not null, "An unusable remembered folder did not fall back.");
            Check.That((await preferences.LoadAsync()).Folder == fallback, "Fallback was not persisted for the next restart.");
            Check.That((await File.ReadAllTextAsync(config)).Contains(unrelated, StringComparison.Ordinal), "Fallback removed updater preferences.");
            Check.That((await File.ReadAllBytesAsync(existing)).SequenceEqual(new byte[] { 1, 2, 3 }), "Preference changes modified an existing image.");

            foreach (var invalid in new[] { "relative-images", "", chosen + "\n[Updates]\nEnabled=true" })
            {
                var result = await preferences.SaveAsync(invalid);
                Check.That(result.Folder == fallback && result.Warning is not null, "Invalid input did not fall back safely.");
                Check.That((await preferences.LoadAsync()).Folder == fallback, "Invalid-input fallback was not persisted.");
            }

            // Reject malformed configuration instead of replacing it and losing unrelated preferences.
            foreach (var malformed in new[] { "[broken\n", "[Storage]\nSaveFolder=one\nSaveFolder=two\n", "[Updates]\ninvalid-entry\n" })
            {
                await File.WriteAllTextAsync(config, malformed);
                var read = await preferences.LoadAsync();
                Check.That(read.CanUse && read.Folder == fallback && read.Warning is not null, "Malformed configuration did not give a usable default and warning.");
                var write = await preferences.SaveAsync(chosen);
                Check.That(write.CanUse && write.Folder == chosen && write.Warning!.Contains("Could not save", StringComparison.Ordinal), "Failed persistence was reported as successful.");
                Check.That(await File.ReadAllTextAsync(config) == malformed, "Malformed configuration was overwritten.");
            }

            File.Delete(config);
            Directory.CreateDirectory(config);
            var blockedConfig = await preferences.SaveAsync(chosen);
            Check.That(blockedConfig.CanUse && blockedConfig.Folder == chosen && blockedConfig.Warning is not null,
                "Configuration write failure should retain the usable session folder with a warning.");
            Check.That(!Directory.EnumerateFiles(root, "config.ini.*.tmp").Any(), "A failed configuration update left temporary files behind.");
            Directory.Delete(config);

            var noDefault = new SaveFolderPreferences(config, Path.Combine(blocked, "default"));
            var unusable = await noDefault.SaveAsync(Path.Combine(blocked, "images"));
            Check.That(!unusable.CanUse && unusable.Warning!.Contains("default folder also", StringComparison.Ordinal),
                "An unusable default must prevent monitoring and expose feedback.");

            if (OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(config, unrelated + $"\r\n[Storage]\r\nSaveFolder={chosen}\r\n");
                var original = await File.ReadAllTextAsync(config);
                File.SetAttributes(config, FileAttributes.ReadOnly);
                try
                {
                    var readOnly = await preferences.SaveAsync(next);
                    Check.That(readOnly.CanUse && readOnly.Warning is not null, "A read-only config did not report a write failure.");
                    Check.That(await File.ReadAllTextAsync(config) == original, "A read-only config was replaced.");
                }
                finally { File.SetAttributes(config, FileAttributes.Normal); }
            }
            Console.WriteLine("::notice::Preference contracts passed: restart, Unicode paths, missing folders, invalid/unavailable paths, persisted fallback, unrelated INI entries, malformed/read-only/write-failed config, unusable default and existing-image retention.");
        }
        finally { Directory.Delete(root, true); }
    }
}
