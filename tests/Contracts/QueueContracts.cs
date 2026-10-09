using System.Threading.Channels;
using ClipboardSnapper;

static class QueueContracts
{
    public static async Task RunAsync()
    {
        var history = new SessionHistory();
        var progress = new QueueProgress();
        var initial = progress.Snapshot(history.Generation);
        Check.That(initial.Active == 0 && initial.Failed == 0 && initial.StatusKey(false, false) == "QueueIdle",
            "A fresh queue must be idle with empty totals.");

        // Hold actual channel work at read/save boundaries; the display need not consume updates.
        var images = Channel.CreateBounded<long>(new BoundedChannelOptions(4) { SingleReader = true });
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await foreach (var generation in images.Reader.ReadAllAsync())
            {
                progress.BeginSave();
                saving.TrySetResult();
                await releaseSave.Task;
                progress.FinishSave(generation, success: true);
            }
        });
        progress.BeginRead();
        progress.QueueRead();
        Check.That(images.Writer.TryWrite(history.Generation), "First queued image was refused.");
        await saving.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 4; i++)
        {
            progress.BeginRead();
            progress.QueueRead();
            Check.That(images.Writer.TryWrite(history.Generation), "Existing queue capacity changed.");
        }
        progress.BeginRead();
        progress.QueueRead();
        Check.That(!images.Writer.TryWrite(history.Generation), "Full queue was unexpectedly unbounded.");
        progress.RejectQueued(history.Generation);
        progress.BeginRead();
        var before = progress.Snapshot(history.Generation);
        Check.That(before is { Reading: 1, Waiting: 4, Saving: 1, Rejected: 1 } && before.Active == 6 &&
            before.StatusKey(true, false) == "QueueProcessing" && before.StatusKey(false, true) == "QueueDraining",
            "Waiting, active work, queue rejection or post-Stop draining is incorrect.");

        history.Clear();
        var cleared = progress.Snapshot(history.Generation);
        history.Clear();
        cleared = progress.Snapshot(history.Generation);
        Check.That(cleared.Active == before.Active && cleared.Failed == 0 && cleared.Saved == 0,
            "Repeated Clear dropped active work or retained old outcome totals.");
        progress.FailRead(0, rejected: false);
        images.Writer.TryComplete();
        releaseSave.SetResult();
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        var drained = progress.Snapshot(history.Generation);
        Check.That(drained.Active == 0 && drained.Saved == 0 && drained.Failed == 0 &&
            drained.StatusKey(false, true) == "QueueFinished" && drained.StatusKey(true, false) == "QueueListening",
            "Old completions reappeared after Clear, draining failed, or listening state is wrong.");

        progress.BeginRead(); progress.IgnoreRead();
        Check.That(progress.Snapshot(history.Generation).Failed == 0, "Non-image clipboard content counted as a failure.");
        progress.BeginRead(); progress.FailRead(history.Generation, rejected: false);
        progress.BeginRead(); progress.FailRead(history.Generation, rejected: true);
        progress.BeginRead(); progress.QueueRead(); progress.BeginSave(); progress.FinishSave(history.Generation, success: false);
        progress.BeginRead(); progress.QueueRead(); progress.BeginSave(); progress.FinishSave(history.Generation, success: true);
        var outcomes = progress.Snapshot(history.Generation);
        Check.That(outcomes is { Active: 0, Saved: 1, ReadFailed: 1, Rejected: 1, SaveFailed: 1, Failed: 3 },
            "Failure-stage breakdown or new post-clear success is incorrect.");

        // A new-generation completion can win the race before the UI observes Clear.
        history.Clear();
        progress.BeginRead(); progress.QueueRead(); progress.BeginSave(); progress.FinishSave(history.Generation, true);
        Check.That(progress.Snapshot(history.Generation).Saved == 1, "Observing Clear erased a fresh completion.");
        for (var i = 0; i < 500; i++)
        {
            var generation = history.Generation;
            progress.BeginRead(); progress.QueueRead(); progress.BeginSave();
            await Task.WhenAll(Task.Run(() => { history.Clear(); _ = progress.Snapshot(history.Generation); }),
                Task.Run(() => progress.FinishSave(generation, true)));
            Check.That(progress.Snapshot(history.Generation) is { Active: 0, Saved: 0, Failed: 0 },
                "Concurrent Clear/outcome publication restored old totals.");
        }
        // Producers finish even if a UI consumer never polls snapshots during the whole batch.
        var current = history.Generation;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            { progress.BeginRead(); progress.QueueRead(); progress.BeginSave(); progress.FinishSave(current, true); }
        }))).WaitAsync(TimeSpan.FromSeconds(5));
        Check.That(progress.Snapshot(current) is { Active: 0, Saved: 8000 }, "Paused display blocked work or lost concurrent totals.");
        Console.WriteLine("::notice::Queue contracts passed: waiting/reading/saving, bounded rejection, Stop/drain states, repeated/concurrent Clear, old and fresh completion races, failure breakdown, ignored clipboard content, and 8000 completions without UI polling.");
    }
}
