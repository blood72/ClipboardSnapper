using System.Threading.Channels;
using ClipboardSnapper;

static class QueueContracts
{
    private static SaveResult Result(long generation, bool success = true) =>
        new("captured.png", DateTimeOffset.Now, success, success ? "" : "Original failure details", Generation: generation);

    public static async Task RunAsync()
    {
        var history = new SessionHistory();
        var progress = new QueueProgress();
        var options = new SaveOptions("images", ImageFormat.Jpeg, 42);
        long Read() => progress.BeginRead(history.Generation, DateTimeOffset.Now, options);
        var initial = progress.Snapshot(history.Generation);
        Check.That(initial.Active == 0 && initial.Failed == 0 && initial.StatusKey(false, false) == "QueueIdle",
            "A fresh queue must be idle with empty totals.");

        // Hold channel work at read/save boundaries; the display need not consume updates.
        var images = Channel.CreateBounded<(long Id, long Generation)>(new BoundedChannelOptions(4) { SingleReader = true });
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await foreach (var image in images.Reader.ReadAllAsync())
            {
                progress.BeginSave(image.Id);
                saving.TrySetResult();
                await releaseSave.Task;
                progress.FinishSave(image.Id, Result(image.Generation));
            }
        });
        var first = Read(); progress.QueueRead(first);
        Check.That(images.Writer.TryWrite((first, history.Generation)), "First queued image was refused.");
        await saving.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 4; i++)
        {
            var id = Read(); progress.QueueRead(id);
            Check.That(images.Writer.TryWrite((id, history.Generation)), "Existing queue capacity changed.");
        }
        var rejected = Read(); progress.QueueRead(rejected);
        Check.That(!images.Writer.TryWrite((rejected, history.Generation)), "Full queue was unexpectedly unbounded.");
        progress.RejectQueued(rejected, Result(history.Generation, false));
        var reading = Read();
        var before = progress.View(history.Generation);
        Check.That(before.Snapshot is { Reading: 1, Waiting: 4, Saving: 1, Rejected: 1 } && before.Snapshot.Active == 6 &&
            before.Snapshot.StatusKey(true, false) == "QueueProcessing" && before.Snapshot.StatusKey(false, true) == "QueueDraining" && before.Snapshot.StatusKey(true, false, true) == "QueueStopping",
            "Waiting, active work, queue rejection or post-Stop draining is incorrect.");
        Check.That(before.Jobs.Count(j => j.Stage == QueueStage.Waiting) == 4 && before.Jobs.Single(j => j.Id == first) is
            { Stage: QueueStage.Saving, Format: ImageFormat.Jpeg, JpegQuality: 42 } &&
            before.Jobs.Single(j => j.Id == rejected).Result?.Error == "Original failure details",
            "Per-image identity, frozen options, stages or failure details were lost.");

        history.Clear(); _ = progress.Snapshot(history.Generation);
        history.Clear();
        var cleared = progress.View(history.Generation);
        Check.That(cleared.Snapshot.Active == before.Snapshot.Active && cleared.Snapshot.Failed == 0 &&
            cleared.Snapshot.Saved == 0 && cleared.Jobs.Length == 0,
            "Repeated Clear dropped active work or retained old outcome rows/totals.");
        progress.FailRead(reading, false, Result(0, false));
        images.Writer.TryComplete(); releaseSave.SetResult();
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        var drained = progress.View(history.Generation);
        Check.That(drained.Snapshot is { Active: 0, Saved: 0, Failed: 0 } && drained.Jobs.Length == 0 &&
            drained.Snapshot.StatusKey(false, true) == "QueueFinished" && drained.Snapshot.StatusKey(true, false) == "QueueListening",
            "Old completions reappeared after Clear, draining failed, or listening state is wrong.");

        var ignored = Read(); progress.IgnoreRead(ignored);
        Check.That(progress.View(history.Generation).Jobs.Length == 0, "Non-image clipboard content retained a queue row.");
        var readFailed = Read(); progress.FailRead(readFailed, false, Result(history.Generation, false));
        var busy = Read(); progress.FailRead(busy, true, Result(history.Generation, false));
        var saveFailed = Read(); progress.QueueRead(saveFailed); progress.BeginSave(saveFailed);
        progress.FinishSave(saveFailed, Result(history.Generation, false));
        var saved = Read(); progress.QueueRead(saved); progress.BeginSave(saved);
        progress.FinishSave(saved, Result(history.Generation));
        var outcomes = progress.View(history.Generation);
        Check.That(outcomes.Snapshot is { Active: 0, Saved: 1, ReadFailed: 1, Rejected: 1, SaveFailed: 1, Failed: 3 } &&
            outcomes.Jobs.Select(j => j.Stage).SequenceEqual(new[] { QueueStage.Saved, QueueStage.SaveFailed, QueueStage.Rejected, QueueStage.ReadFailed }),
            "Failure-stage breakdown or individual post-clear results are incorrect.");

        // A new-generation completion can win the race before the UI observes Clear.
        history.Clear();
        var fresh = Read(); progress.QueueRead(fresh); progress.BeginSave(fresh);
        progress.FinishSave(fresh, Result(history.Generation));
        Check.That(progress.View(history.Generation) is { Snapshot.Saved: 1, Jobs.Length: 1 }, "Observing Clear erased a fresh completion.");
        for (var i = 0; i < 500; i++)
        {
            var generation = history.Generation;
            var id = Read(); progress.QueueRead(id); progress.BeginSave(id);
            await Task.WhenAll(Task.Run(() => { history.Clear(); _ = progress.Snapshot(history.Generation); }),
                Task.Run(() => progress.FinishSave(id, Result(generation))));
            Check.That(progress.View(history.Generation) is { Snapshot.Active: 0, Snapshot.Saved: 0, Snapshot.Failed: 0, Jobs.Length: 0 },
                "Concurrent Clear/outcome publication restored old rows/totals.");
        }
        // Producers finish even if a UI consumer never polls snapshots during the whole batch.
        var current = history.Generation;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                var id = progress.BeginRead(current, DateTimeOffset.Now, options);
                progress.QueueRead(id); progress.BeginSave(id); progress.FinishSave(id, Result(current));
            }
        }))).WaitAsync(TimeSpan.FromSeconds(5));
        var active = Read();
        var retained = progress.View(current);
        Check.That(retained.Snapshot is { Active: 1, Saved: 8000 } && retained.Jobs.Length == 101 &&
            retained.Jobs.Single(j => j.Id == active).Stage == QueueStage.Reading,
            "Paused display blocked work, lost totals, grew completed rows without bound or evicted active work.");
        progress.IgnoreRead(active);
        Console.WriteLine("::notice::Queue contracts passed: per-image stages/frozen options/details, bounded rejection/retention, Stop/drain states, repeated/concurrent Clear and old/fresh result races, ignored clipboard content, and 8000 completions without UI polling.");
    }
}
