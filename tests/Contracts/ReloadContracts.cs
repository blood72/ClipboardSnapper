using ClipboardSnapper;
using System.Text.Json;

static class ReloadContracts
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ClipboardSnapper-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "config.ini");
            var languageFolder = Path.Combine(root, "lang");
            Directory.CreateDirectory(languageFolder);
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "lang"), "*.json"))
                File.Copy(file, Path.Combine(languageFolder, Path.GetFileName(file)));
            var fallback = Path.Combine(root, "default");
            var loader = new SettingsLoader(config, fallback, languageFolder);
            var defaults = await loader.LoadAsync();
            Check.That(defaults.Folder.Folder == fallback && defaults.Image.Settings == ImageSettings.Default &&
                defaults.Naming.State.Formula == FilenameRule.DefaultFormula && defaults.Language.Code is null && !File.Exists(config),
                "Missing settings did not load fresh defaults without creating configuration.");
            var stateA = NamingState.Default.UpdateSelected("First", "First_$YYYY");
            var stateB = NamingState.Default.UpdateSelected("Second", "Second_$MM");
            var folderA = Path.Combine(root, "first");
            var folderB = Path.Combine(root, "second");
            var store = new PortableConfig(config);
            void Write(bool second) => store.Update(document =>
            {
                var naming = second ? stateB : stateA;
                document.Set("Storage", "SaveFolder", second ? folderB : folderA);
                document.Set("Storage", "ImageFormat", second ? "BMP" : "JPEG");
                document.Set("Storage", "JpegQuality", second ? "100" : "37");
                document.Set("Appearance", "Language", second ? "ko" : "en");
                document.Set("Naming", "Formula", naming.Formula);
                document.Set("Naming", "SelectedPreset", naming.SelectedPresetId);
                document.RemoveKeys("NamingPresets", "Preset.");
                document.Set("NamingPresets", "Preset." + naming.SelectedPresetId, JsonSerializer.Serialize(naming.Selected));
            });
            await File.WriteAllTextAsync(config, "; keep this comment\n[Future]\nKeep=untouched\n");
            Write(false);
            // A concurrent complete update cannot split a reload across two INI versions.
            var writer = Task.Run(() => { for (var i = 0; i < 50; i++) Write(i % 2 == 0); });
            for (var i = 0; i < 30; i++)
            {
                var snapshot = await loader.LoadAsync();
                var second = snapshot.Language.Code == "ko";
                Check.That(snapshot.Folder.Folder == (second ? folderB : folderA) &&
                    snapshot.Naming.State.Formula == (second ? stateB.Formula : stateA.Formula) &&
                    snapshot.Naming.State.SelectedPresetId == (second ? stateB.SelectedPresetId : stateA.SelectedPresetId) &&
                    snapshot.Image.Settings == (second ? new(ImageFormat.Bmp, 100) : new ImageSettings(ImageFormat.Jpeg, 37)),
                    "Reload mixed settings from different INI snapshots.");
            }
            await writer;
            Write(false);
            var before = await File.ReadAllTextAsync(config);
            await loader.LoadAsync();
            Check.That(await File.ReadAllTextAsync(config) == before, "Loading valid settings wrote configuration.");

            // Bad profile data and bad individual image values do not discard other valid settings.
            store.Update(d => d.Set("NamingPresets", "Preset." + stateA.SelectedPresetId, "not-json"));
            var partial = await loader.LoadAsync();
            Check.That(partial.Naming.Notice is not null && partial.Naming.State.Formula == FilenameRule.DefaultFormula &&
                partial.Folder.Folder == folderA && partial.Image.Settings == new ImageSettings(ImageFormat.Jpeg, 37),
                "Profile fallback discarded valid storage settings.");
            Write(false);
            store.Update(d => d.Set("Storage", "JpegQuality", "101"));
            partial = await loader.LoadAsync();
            Check.That(partial.Image.Settings == new ImageSettings(ImageFormat.Jpeg, 90) &&
                partial.Image.Notices.Count == 1 && partial.Naming.State.Formula == stateA.Formula,
                "Image fallback did not remain isolated.");
            await File.WriteAllTextAsync(config, "[broken\n");
            var malformed = await loader.LoadAsync();
            Check.That(malformed.Folder.Notices.Count > 0 && malformed.Naming.Notice is not null &&
                malformed.Language.Notice is not null && malformed.Image.Notices.Count > 0 &&
                malformed.Image.Settings == ImageSettings.Default && await File.ReadAllTextAsync(config) == "[broken\n",
                "Malformed configuration was overwritten or silently kept stale settings.");
            File.Delete(config);
            Write(false);
            store.Update(d => d.Set("Storage", "SaveFolder", "relative-folder"));
            partial = await loader.LoadAsync();
            Check.That(partial.Folder.Folder == fallback && partial.Folder.Notices.Count > 0 &&
                partial.Image.Settings == new ImageSettings(ImageFormat.Jpeg, 37) &&
                (await new SaveFolderPreferences(config, fallback).LoadAsync()).Folder == fallback,
                "Reload did not reuse startup's persisted folder fallback.");

            // Replace the running catalog, including new/edited/removed packs and English baseline fallback.
            var text = new UiText((await loader.LoadAsync()).Catalog);
            var custom = Path.Combine(languageFolder, "fr.json");
            await File.WriteAllTextAsync(custom, "{\"languageName\":\"Français\",\"strings\":{\"Start\":\"Premier\"}}");
            var catalog = (await loader.LoadAsync()).Catalog;
            text.ReplaceCatalog(catalog, catalog.Resolve("fr", "en"));
            Check.That(text.Language.Code == "fr" && text["Start"] == "Premier" && text["Stop"] == "Stop", "Added pack did not apply with per-key fallback.");
            await File.WriteAllTextAsync(custom, "{\"languageName\":\"Français\",\"strings\":{\"Start\":\"Deuxième\"}}");
            catalog = (await loader.LoadAsync()).Catalog;
            text.ReplaceCatalog(catalog, "fr");
            Check.That(text["Start"] == "Deuxième", "Reload retained stale translation content.");
            File.Delete(custom);
            catalog = (await loader.LoadAsync()).Catalog;
            text.ReplaceCatalog(catalog, catalog.Resolve("fr", "ko"));
            Check.That(text.Language.Code == "en" && text["Start"] == "Start", "Removed saved pack did not fall back to fresh English.");
            Console.WriteLine("::notice::Reload contracts passed: coherent single-INI snapshots, fresh defaults, isolated invalid/malformed fallback, persisted folder recovery, no writes for valid loads and live catalog replacement for added/edited/removed packs.");
        }
        finally { Directory.Delete(root, true); }
    }
}
