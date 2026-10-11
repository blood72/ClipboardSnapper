using System.Globalization;
using System.Text.Json;
using ClipboardSnapper;

static class LanguageContracts
{
    public static async Task RunAsync()
    {
        var bundled = LanguageCatalog.Load(Path.Combine(AppContext.BaseDirectory, "lang"));
        Check.That(bundled.Notices.Count == 0 && bundled.Languages.Count == 2, "External JSON translations did not load cleanly.");
        Check.That(!typeof(UiText).Assembly.GetManifestResourceNames().Any(name => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)),
            "Translation JSON must not be an embedded assembly resource.");
        var en = bundled.Languages.Single(l => l.Code == "en");
        var ko = bundled.Languages.Single(l => l.Code == "ko");
        Check.That(en.Strings.Keys.Order().SequenceEqual(ko.Strings.Keys.Order()), "Korean is missing app-owned translation keys.");
        Check.That(bundled.Resolve(null, "ko-KR") == "ko" && bundled.Resolve(null, "en-US") == "en" &&
            bundled.Resolve(null, "fr-FR") == "en" && bundled.Resolve("en", "ko-KR") == "en" &&
            bundled.Resolve("missing", "ko-KR") == "en", "OS selection, explicit preference or English fallback is wrong.");
        var text = new UiText(bundled);
        var notifications = new List<string?>();
        text.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        var error = UiMessage.Format("ReservedName");
        var originalDiagnostic = error.Message;
        text.Select("ko");
        Check.That(text["Start"] == "시작" && text["ClearHelp"].Contains("저장 파일은 유지") && notifications.Contains("Item[]"),
            "Korean controls/help or live-binding notification is wrong.");
        Check.That(UiMessage.FromException(error).Render(text) == ko.Strings["ReservedName"] && error.Message == originalDiagnostic,
            "Translation lost the original diagnostic or app-owned error identity.");
        Check.That(new UiMessage("NamingWriteFailed", UiMessage.FromException(error)).Render(text).Contains(ko.Strings["ReservedName"]),
            "A nested configuration warning retained an English app-owned reason.");
        var captured = new DateTimeOffset(2026, 10, 8, 12, 34, 56, TimeSpan.FromHours(9));
        var filename = FilenameRule.Parse("Photo_$MMMM_${start=1;padding=4}").Generate(captured, 2);
        text.Select("en");
        Check.That(FilenameRule.Parse("Photo_$MMMM_${start=1;padding=4}").Generate(captured, 2) == filename && filename == "Photo_October_0003",
            "Changing UI language changed filename rules/counters.");

        var root = Path.Combine(Path.GetTempPath(), "ClipboardSnapper-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "config.ini");
            var preferences = new LanguagePreferences(config);
            Check.That((await preferences.LoadAsync()).Code is null && !File.Exists(config), "First run must follow Windows without writing a preference.");
            var unrelated = "; Keep comment\n[Updates]\nInterval=weekly\n";
            await File.WriteAllTextAsync(config, unrelated);
            var folder = new SaveFolderPreferences(config, Path.Combine(root, "images"));
            var naming = new NamingPreferences(config);
            var state = NamingState.Default.UpdateSelected("My rule", "Photo_${start=5}");
            await Task.WhenAll(preferences.SaveAsync("ko"), folder.SaveAsync(Path.Combine(root, "chosen")), naming.SaveAsync(state));
            Check.That((await new LanguagePreferences(config).LoadAsync()).Code == "ko" &&
                (await naming.LoadAsync()).State.SelectedPresetId == state.SelectedPresetId &&
                (await folder.LoadAsync()).Folder == Path.Combine(root, "chosen") &&
                (await File.ReadAllTextAsync(config)).StartsWith(unrelated, StringComparison.Ordinal),
                "Concurrent language/folder/profile writes or restart lost state.");
            if (OperatingSystem.IsWindows())
            {
                var original = await File.ReadAllTextAsync(config);
                File.SetAttributes(config, FileAttributes.ReadOnly);
                try
                {
                    var failed = await preferences.SaveAsync("en");
                    Check.That(failed.Code == "en" && failed.Notice?.Key == "LanguageWriteFailed" &&
                        await File.ReadAllTextAsync(config) == original, "Read-only language writes must warn and preserve configuration.");
                }
                finally { File.SetAttributes(config, FileAttributes.Normal); }
            }
            await File.WriteAllTextAsync(config, "[broken\n");
            Check.That((await preferences.LoadAsync()).Notice?.Key == "LanguageReadFailed" &&
                (await preferences.SaveAsync("en")).Notice is not null && await File.ReadAllTextAsync(config) == "[broken\n",
                "Malformed settings must be preserved with a language warning.");

            // A new, partial translation becomes usable without recompilation; missing keys fall back independently.
            await File.WriteAllTextAsync(Path.Combine(root, "ja.json"), JsonSerializer.Serialize(new { languageName = "日本語", strings = new { Start = "開始" } }));
            var custom = LanguageCatalog.Load(root);
            text = new UiText(custom);
            text.Select("ja");
            Check.That(text["Start"] == "開始" && text["Stop"] == "Stop" && custom.Resolve(null, "ja-JP") == "ja",
                "Adding an unknown-to-the-app language or per-key English fallback failed.");
            await File.WriteAllTextAsync(Path.Combine(root, "ja.json"), JsonSerializer.Serialize(new { languageName = "日本語", strings = new { Start = "始める" } }));
            Check.That(text["Start"] == "開始", "Editing a file changed the running catalog.");
            text = new UiText(LanguageCatalog.Load(root));
            text.Select(text.Catalog.Resolve("ja", "en"));
            Check.That(text.Language.Code == "ja" && text["Start"] == "始める", "Startup did not load edited translation text.");
            await File.WriteAllTextAsync(Path.Combine(root, "ko.json"), "not-json");
            await File.WriteAllTextAsync(Path.Combine(root, "de.json"), "{\"languageName\":\"Deutsch\",\"strings\":{\"Example\":\"{5}\"}}");
            await File.WriteAllTextAsync(Path.Combine(root, "fr.json"), "{\"languageName\":\"Français\",\"strings\":{\"Start\":\"A\",\"Start\":\"B\"}}");
            custom = LanguageCatalog.Load(root);
            Check.That(custom.Notices.Count == 4 && custom.Languages.Count == 2 && custom.Languages.All(l => l.Code is "en" or "ja"),
                "Malformed JSON, placeholder injection or duplicate keys were not isolated from usable languages.");
            await File.WriteAllTextAsync(Path.Combine(root, "en.json"), "{\"languageName\":\"English\",\"strings\":{\"Stop\":\"Edited English Stop\"}}");
            text = new UiText(LanguageCatalog.Load(root));
            text.Select("ja");
            Check.That(text["Stop"] == "Edited English Stop", "Missing custom keys did not use the edited external English pack.");
            File.Delete(Path.Combine(root, "en.json"));
            File.Delete(Path.Combine(root, "ja.json"));
            Check.That(text.Language.Code == "ja" && text["Start"] == "始める", "Deleting a file changed the running catalog.");
            text = new UiText(LanguageCatalog.Load(root));
            text.Select(text.Catalog.Resolve("ja", "en"));
            Check.That(text.Language.Code == "en" && text["Start"] == "Start", "Removing an active translation did not safely fall back to English.");
            var missing = LanguageCatalog.Load(Path.Combine(root, "missing"));
            Check.That(missing.Notices.Count == 1 && missing.Languages.Count == 1 &&
                missing.Resolve(null, "ko-KR") == "en" && missing.Languages.Single().Strings["Start"] == "Start",
                "A removed language directory must warn and use the English file read at startup without retaining Korean.");
            try
            {
                UiText.InitializeEnglish(Path.Combine(root, "missing"));
                throw new Exception("A missing startup English file was accepted.");
            }
            catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException) { }
            Check.That(UiText.English("Stop") == "Stop", "A failed initial read replaced the valid in-memory English file.");
            await File.WriteAllTextAsync(Path.Combine(root, "ko.json"), "{\"languageName\":\"Custom Korean\",\"strings\":{\"Start\":\"외부 시작\"}}");
            text = new UiText(LanguageCatalog.Load(root));
            text.Select("ko");
            Check.That(text.Language.Name == "Custom Korean" && text["Start"] == "외부 시작" && text["Stop"] == "Stop",
                "An external Korean pack must be discovered and retain per-key English fallback.");
            await File.WriteAllTextAsync(Path.Combine(root, "EN.json"), "{\"languageName\":\"Edited English\",\"strings\":{\"Stop\":\"External Stop\"}}");
            text = new UiText(LanguageCatalog.Load(root));
            text.Select("ko");
            Check.That(text["Stop"] == "External Stop", "A case-insensitive English file broke per-key fallback.");
            await File.WriteAllTextAsync(Path.Combine(root, "EN.json"), "not-json");
            custom = LanguageCatalog.Load(root);
            text = new UiText(custom);
            text.Select("ko");
            Check.That(custom.Notices.Any(notice => notice.Key == "LanguageFileInvalid") && text["Stop"] == "Stop",
                "Invalid external English must warn and use only the file read at startup.");
            File.Delete(Path.Combine(root, "EN.json"));
            custom = LanguageCatalog.Load(root);
            Check.That(custom.Notices.Any(notice => notice.Key == "EnglishPackMissing"),
                "Removing external English during a session must report the required file.");
            var preservedKorean = await File.ReadAllTextAsync(Path.Combine(root, "ko.json"));
            File.Delete(Path.Combine(root, "ko.json"));
            custom = LanguageCatalog.Load(root);
            Check.That(custom.Resolve("ko", "ko-KR") == "en" && custom.Languages.All(l => l.Code != "ko"),
                "Removing Korean must remove it from the catalog; there is no hidden built-in pack.");
            await File.WriteAllTextAsync(Path.Combine(root, "ko.json"), preservedKorean);
            Check.That(LanguageCatalog.Load(root).Resolve("ko", "en-US") == "ko" &&
                await File.ReadAllTextAsync(Path.Combine(root, "ko.json")) == preservedKorean,
                "Restoring Korean must rediscover it without rewriting user translation files.");
            Console.WriteLine("::notice::Language contracts passed: complete Korean keys, Windows/explicit/fallback policy, live notifications, structured errors with original diagnostics, immutable filename rules, concurrent INI persistence, malformed/read-only settings, startup-added/edited/removed JSON languages, per-key fallback and isolated invalid files.");
        }
        finally { Directory.Delete(root, true); }
    }
}
