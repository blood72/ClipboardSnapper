using ClipboardSnapper;

try
{
    await SessionContracts.RunAsync();
    await QueueContracts.RunAsync();
    await PreferenceContracts.RunAsync();
    await ImagePreferenceContracts.RunAsync();
    await ReloadContracts.RunAsync();
    await NamingContracts.RunAsync();
    await LanguageContracts.RunAsync();
#if WINDOWS
    await EncoderContracts.RunAsync();
#endif
    Console.WriteLine("::notice::Contract tests passed: acceptance generations, queued/late results, preview races, repeated clears and immutable quality options.");
}
catch (Exception exception)
{
    Console.WriteLine($"::error::{exception}");
    return 1;
}
return 0;

static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

static class SessionContracts
{
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SaveResult Saved(long generation, string path = "old.png") =>
        new(path, DateTimeOffset.Now, true, Width: 32, Height: 24, Generation: generation);

    public static async Task RunAsync()
    {
        var session = new SessionHistory();
        var options = new SaveOptions("images", ImageFormat.Jpeg);
        Check.That(options.JpegQuality == 90 && Math.Abs(options.EncoderQuality - .9f) < .00001f, "Default JPEG quality must be 90 / 0.9.");
        Check.That((options with { JpegQuality = 1 }).EncoderQuality == .01f, "Minimum JPEG quality mapping is wrong.");
        Check.That((options with { JpegQuality = 100 }).EncoderQuality == 1f, "Maximum JPEG quality mapping is wrong.");
        foreach (var invalid in new[] { 0, 101 })
        {
            try { _ = (options with { JpegQuality = invalid }).EncoderQuality; throw new Exception("Invalid quality was accepted."); }
            catch (ArgumentOutOfRangeException) { }
        }

        // A capture accepted before Clear can still be waiting for the clipboard provider.
        var accepted = new CapturedImage([1, 2, 3], options, session.Generation);
        options = options with { Folder = "other-images", Format = ImageFormat.Bmp, JpegQuality = 100 };
        var readRelease = Gate();
        var lateRead = CompleteReadAsync();
        async Task<SaveResult> CompleteReadAsync()
        {
            await readRelease.Task;
            return Saved(accepted.Generation);
        }
        session.Clear();
        session.Clear();
        var fresh = Saved(session.Generation, "new.png");
        Check.That(session.Publish(fresh), "Post-clear capture was rejected.");
        readRelease.SetResult();
        Check.That(!session.Publish(await lateRead), "Late clipboard-read/save completion reappeared after Clear.");
        Check.That(ReferenceEquals(session.LatestSaved, fresh), "Late old save replaced the new preview candidate.");
        Check.That(accepted.Options is { JpegQuality: 90, Format: ImageFormat.Jpeg, Folder: "images" },
            "Changing the next run's options changed an accepted capture's snapshot.");

        // A result already queued for a UI timer before Clear must be rejected when drained.
        var queued = fresh;
        session.Clear();
        Check.That(!session.IsCurrent(queued.Generation) && session.LatestSaved is null, "Buffered results survived Clear.");
        var failed = queued with { Success = false, Error = "old/path: denied" };
        Check.That(!session.Publish(failed), "A late failure/path message survived Clear.");

        // Model a blocked preview decode: both its success and its error completion use the same guard.
        var previewResult = Saved(session.Generation);
        session.Publish(previewResult);
        var previewRelease = Gate();
        var preview = CompletePreviewAsync();
        var previewError = CompletePreviewErrorAsync();
        async Task<bool> CompletePreviewAsync()
        {
            await previewRelease.Task;
            return session.CanPreview(previewResult);
        }
        async Task<bool> CompletePreviewErrorAsync()
        {
            await previewRelease.Task;
            try { throw new IOException("old/path: preview loading failed"); }
            catch (IOException) { return session.CanPreview(previewResult); }
        }
        session.Clear();
        var next = Saved(session.Generation, "after-preview-clear.png");
        session.Publish(next);
        previewRelease.SetResult();
        Check.That(!await preview, "Old preview completion survived Clear.");
        Check.That(!await previewError, "An old preview failure restored a path message after Clear.");
        Check.That(!session.CanPreview(previewResult) && session.CanPreview(next), "Old preview error or new preview guard is wrong.");
        var newer = Saved(session.Generation, "newest.png");
        session.Publish(newer);
        Check.That(!session.CanPreview(next) && session.CanPreview(newer), "An older decode replaced a newer save in the same generation.");

        // Exercise Clear concurrent with publication: no old result may remain in the new generation.
        for (var i = 0; i < 1000; i++)
        {
            var result = Saved(session.Generation);
            await Task.WhenAll(Task.Run(session.Clear), Task.Run(() => session.Publish(result)));
            Check.That(session.LatestSaved is null, "Concurrent publication raced past Clear.");
        }

        var folder = Path.Combine(Path.GetTempPath(), $"ClipboardSnapper-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var existing = Path.Combine(folder, "existing.png");
            await File.WriteAllBytesAsync(existing, [9, 8, 7]);
            var pendingGeneration = session.Generation;
            var saveRelease = Gate();
            var save = FinishSaveAsync();
            async Task<SaveResult> FinishSaveAsync()
            {
                await saveRelease.Task;
                var path = Path.Combine(folder, "queued.png");
                await File.WriteAllBytesAsync(path, accepted.Bytes);
                return Saved(pendingGeneration, path);
            }
            session.Clear();
            saveRelease.SetResult();
            var result = await save;
            Check.That(!session.Publish(result) && File.Exists(result.FilePath), "Clear interrupted a pending disk write or displayed its old result.");
            Check.That((await File.ReadAllBytesAsync(existing)).SequenceEqual(new byte[] { 9, 8, 7 }), "Clear modified an existing file.");
        }
        finally { Directory.Delete(folder, true); }
    }
}
