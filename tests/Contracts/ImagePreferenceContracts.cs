using ClipboardSnapper;

static class ImagePreferenceContracts
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ClipboardSnapper-image-preferences-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "config.ini");
            var preferences = new ImagePreferences(config);
            var first = await preferences.LoadAsync();
            Check.That(first.Settings == ImageSettings.Default && first.Notices.Count == 0 && !File.Exists(config),
                "Missing image preferences must use PNG/90 without writing configuration.");
            var unrelated = "; retained comment\n[Updates]\nEnabled=false\nCustom=one=two;#three\n";
            await File.WriteAllTextAsync(config, unrelated);
            foreach (var format in Enum.GetValues<ImageFormat>())
            foreach (var quality in new[] { 1, 90, 100 })
            {
                var settings = new ImageSettings(format, quality);
                var saved = await preferences.SaveAsync(settings);
                var restarted = await new ImagePreferences(config).LoadAsync();
                Check.That(saved.Notices.Count == 0 && restarted.Settings == settings && restarted.Notices.Count == 0,
                    $"{format}/{quality} did not survive a new preference loader.");
                Check.That((await File.ReadAllTextAsync(config)).StartsWith(unrelated, StringComparison.Ordinal),
                    "Image preference writes destroyed unrelated configuration/comments.");
            }

            foreach (var (text, settings) in new[]
            {
                ("[storage]\nimageformat=jpeg\njpegquality=42\n", new ImageSettings(ImageFormat.Jpeg, 42)),
                ("[Storage]\nImageFormat=BMP\n", new ImageSettings(ImageFormat.Bmp, 90)),
                ("[Storage]\nJpegQuality=1\n", new ImageSettings(ImageFormat.Png, 1))
            })
            {
                await File.WriteAllTextAsync(config, text);
                var loaded = await preferences.LoadAsync();
                Check.That(loaded.Settings == settings && loaded.Notices.Count == 0 && await File.ReadAllTextAsync(config) == text,
                    "Case-insensitive keys/values or missing individual defaults are wrong.");
            }
            foreach (var invalid in new[] { "GIF", "1", "", "Jpg" })
            {
                var text = $"[Storage]\nImageFormat={invalid}\nJpegQuality=27\n";
                await File.WriteAllTextAsync(config, text);
                var loaded = await preferences.LoadAsync();
                Check.That(loaded.Settings == new ImageSettings(ImageFormat.Png, 27) && loaded.Notices.Count == 1 &&
                    loaded.Notices[0].Key == "StoredImageFormatInvalid" && await File.ReadAllTextAsync(config) == text,
                    "An invalid format lost the valid quality or silently rewrote configuration.");
            }
            foreach (var invalid in new[] { "0", "101", "-1", "", "NaN", "90.5", "999999999999999" })
            {
                var text = $"[Storage]\nImageFormat=BMP\nJpegQuality={invalid}\n";
                await File.WriteAllTextAsync(config, text);
                var loaded = await preferences.LoadAsync();
                Check.That(loaded.Settings == new ImageSettings(ImageFormat.Bmp, 90) && loaded.Notices.Count == 1 &&
                    loaded.Notices[0].Key == "StoredQualityInvalid" && await File.ReadAllTextAsync(config) == text,
                    "An invalid quality lost the valid format or silently rewrote configuration.");
            }
            foreach (var malformed in new[]
            {
                "[broken\n", "[Storage]\nImageFormat=PNG\nImageFormat=JPEG\n",
                "[Storage]\nJpegQuality=90\nJpegQuality=1\n"
            })
            {
                await File.WriteAllTextAsync(config, malformed);
                var loaded = await preferences.LoadAsync();
                Check.That(loaded.Settings == ImageSettings.Default && loaded.Notices.Single().Key == "ImageOptionsReadFailed",
                    "Malformed/duplicate preferences did not report a read failure and defaults.");
                var failedWrite = await preferences.SaveAsync(new(ImageFormat.Jpeg, 42));
                Check.That(failedWrite.Notices.Single().Key == "ImageOptionsWriteFailed" &&
                    await File.ReadAllTextAsync(config) == malformed,
                    "Saving replaced malformed data or falsely reported persistence.");
            }

            File.Delete(config);
            Directory.CreateDirectory(config);
            var blocked = await preferences.SaveAsync(new(ImageFormat.Jpeg, 42));
            Check.That(blocked.Settings == new ImageSettings(ImageFormat.Jpeg, 42) && blocked.Notices.Count == 1 &&
                (await preferences.LoadAsync()).Settings == ImageSettings.Default &&
                !Directory.EnumerateFiles(root, "config.ini.*.tmp").Any(),
                "Failed storage did not keep the session choice, restore defaults or clean temporary files.");
            Directory.Delete(config);

            await File.WriteAllTextAsync(config, unrelated);
            var folder = new SaveFolderPreferences(config, Path.Combine(root, "images"));
            var naming = new NamingPreferences(config);
            var language = new LanguagePreferences(config);
            var preset = NamingState.Default;
            var jpeg = new ImageSettings(ImageFormat.Jpeg, 1);
            var bmp = new ImageSettings(ImageFormat.Bmp, 100);
            await preferences.SaveAsync(jpeg);
            var writes = Enumerable.Range(0, 30).Select(i => preferences.SaveAsync(i % 2 == 0 ? jpeg : bmp)).ToArray();
            var reads = Enumerable.Range(0, 30).Select(async _ =>
            {
                var snapshot = await preferences.LoadAsync();
                Check.That(snapshot.Notices.Count == 0 && (snapshot.Settings == jpeg || snapshot.Settings == bmp),
                    "A reader saw a torn format/quality pair during concurrent writes.");
            }).ToArray();
            await Task.WhenAll(writes.Cast<Task>().Concat(reads).Append(folder.SaveAsync(folder.DefaultFolder))
                .Append(naming.SaveAsync(preset)).Append(language.SaveAsync("ko")));
            Check.That((await folder.LoadAsync()).Folder == folder.DefaultFolder &&
                (await naming.LoadAsync()).State.SelectedPresetId == preset.SelectedPresetId &&
                (await language.LoadAsync()).Code == "ko" &&
                (await File.ReadAllTextAsync(config)).StartsWith(unrelated, StringComparison.Ordinal),
                "Concurrent image/folder/profile/language writes destroyed another preference.");

            if (OperatingSystem.IsWindows())
            {
                var original = await File.ReadAllTextAsync(config);
                File.SetAttributes(config, FileAttributes.ReadOnly);
                try
                {
                    var result = await preferences.SaveAsync(new(ImageFormat.Png, 90));
                    Check.That(result.Notices.Single().Key == "ImageOptionsWriteFailed" && await File.ReadAllTextAsync(config) == original,
                        "A read-only configuration was modified or treated as saved.");
                }
                finally { File.SetAttributes(config, FileAttributes.Normal); }
            }
            foreach (var settings in new[] { new ImageSettings((ImageFormat)3, 90), new ImageSettings(ImageFormat.Jpeg, 0), new ImageSettings(ImageFormat.Jpeg, 101) })
            {
                var original = await File.ReadAllTextAsync(config);
                try { await preferences.SaveAsync(settings); throw new InvalidOperationException("Invalid image preferences were accepted."); }
                catch (ArgumentException) { }
                Check.That(await File.ReadAllTextAsync(config) == original, "Invalid preferences changed the file.");
            }
            Console.WriteLine("::notice::Image preferences passed: defaults, all formats and quality limits, case-insensitive restoration, isolated fallback, malformed/read-only/failed storage, atomic paired values and concurrent unrelated settings preservation.");
        }
        finally { Directory.Delete(root, true); }
    }
}
