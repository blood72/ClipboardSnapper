using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace ClipboardSnapper;

public sealed class ClipboardMonitor : IAsyncDisposable
{
    private const int MaximumBytes = 128 * 1024 * 1024;
    private readonly DispatcherQueueController _controller = DispatcherQueueController.CreateOnDedicatedThread();
    private readonly Channel<SaveWork> _images = Channel.CreateBounded<SaveWork>(
        new BoundedChannelOptions(4) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<SaveResult> _results = Channel.CreateBounded<SaveResult>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly List<Task> _pending = [];
    private readonly Task _writer;
    private CaptureRun? _run;
    private uint _lastSequence;
    private int _reading;
    private bool _watching;
    private bool _disposed;

    public ClipboardMonitor() => _writer = Task.Run(WriteImagesAsync);
    public ChannelReader<SaveResult> Results => _results.Reader;
    public SessionHistory History { get; } = new();
    public QueueProgress Progress { get; } = new();

    private sealed record SaveWork(CapturedImage? Image, TaskCompletionSource? Barrier = null);

    private sealed class CaptureRun(SaveOptions options)
    {
        public SaveOptions Options { get; } = options;
        public long NextCaptureIndex { get; set; }
    }

    public Task DrainAsync() => OnCaptureThreadAsync(async () =>
    {
        if (_watching) throw UiMessage.InvalidOperation("StopBeforeReload");
        await Task.WhenAll(_pending.ToArray());
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _images.Writer.WriteAsync(new(null, barrier));
        await barrier.Task;
    });

    public Task StartAsync(SaveOptions options) => OnCaptureThreadAsync(() =>
    {
        if (_watching) return Task.CompletedTask;
        _run = new(options);
        _lastSequence = GetClipboardSequenceNumber();
        Clipboard.ContentChanged += OnContentChanged;
        _watching = true;
        return Task.CompletedTask;
    });

    public Task StopAsync() => OnCaptureThreadAsync(() =>
    {
        StopWatching();
        return Task.CompletedTask;
    });

    private void StopWatching()
    {
        if (_watching) Clipboard.ContentChanged -= OnContentChanged;
        _watching = false;
    }

    private void OnContentChanged(object? sender, object args)
    {
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherQueueSynchronizationContext(_controller.DispatcherQueue));
        if (!_watching || _run is not { } run) return;
        var sequence = GetClipboardSequenceNumber();
        if (sequence == _lastSequence) return;
        _lastSequence = sequence;
        // Stamp acceptance before any asynchronous clipboard read, including reads still pending at Clear.
        var task = ReadImageAsync(sequence, run, History.Generation, DateTimeOffset.Now);
        _pending.Add(task);
        _ = ForgetCompletedAsync(task);
    }

    private async Task ForgetCompletedAsync(Task task)
    {
        await task;
        _pending.Remove(task);
    }

    private async Task ReadImageAsync(uint sequence, CaptureRun run, long generation, DateTimeOffset acceptedAt)
    {
        var queued = false;
        var rejected = false;
        long? captureIndex = null;
        var attempts = 0;
        var operation = "Clipboard.GetContent";
        var options = run.Options;
        var progressId = Progress.BeginRead(generation, acceptedAt, options);
        try
        {
            var bytes = await ClipboardReadRetry.ReadAsync(sequence, GetClipboardSequenceNumber, ReadBytesAsync);
            if (bytes is null)
            {
                Progress.IgnoreRead(progressId);
                return;
            }

            var image = new CapturedImage(bytes, options, generation)
                { AcceptedAt = acceptedAt, CaptureIndex = captureIndex!.Value, ProgressId = progressId };
            // Reserve the waiting count before publishing: the writer may dequeue immediately.
            Progress.QueueRead(progressId);
            queued = true;
            if (!_images.Writer.TryWrite(new(image)))
                throw UiMessage.Io("SavingBusy");
        }
        catch (Exception exception)
        {
            var failureStage = queued || rejected ? QueueStage.Rejected : QueueStage.ReadFailed;
            var details = failureStage == QueueStage.ReadFailed
                ? $"{operation} (attempt {attempts}): {exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}"
                : exception.GetType().Name;
            if (!string.IsNullOrWhiteSpace(exception.Message)) details += $": {exception.Message}";
            var result = new SaveResult(options.Folder, DateTimeOffset.Now, false, details, Generation: generation)
            {
                FailureStage = failureStage,
                ErrorMessage = ClipboardReadRetry.IsInteropError(exception) || string.IsNullOrWhiteSpace(exception.Message)
                    ? new("ClipboardReadError") : UiMessage.FromException(exception)
            };
            if (queued) Progress.RejectQueued(progressId, result); else Progress.FailRead(progressId, rejected, result);
            PublishResult(result);
        }
        async Task<byte[]?> ReadBytesAsync()
        {
            attempts++;
            operation = "Clipboard.GetContent";
            var content = Clipboard.GetContent();
            // A retry must still target the original item after obtaining a fresh view.
            if (attempts > 1 && GetClipboardSequenceNumber() != sequence)
                throw UiMessage.Io("ClipboardChangedBeforeRead");

            operation = "DataPackageView.Contains(Bitmap)";
            if (!content.Contains(StandardDataFormats.Bitmap))
            {
                if (captureIndex.HasValue) throw UiMessage.Io("ClipboardImageUnavailable");
                return null;
            }
            captureIndex ??= run.NextCaptureIndex++;
            if (_reading >= 2)
            {
                rejected = true;
                throw UiMessage.Io("ClipboardTooFast");
            }

            // Only active acquisitions use a slot. Waiting to retry leaves room for new captures.
            _reading++;
            try
            {
                operation = "DataPackageView.GetBitmapAsync";
                var bitmap = await content.GetBitmapAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                operation = "RandomAccessStreamReference.OpenReadAsync";
                using var source = await bitmap.OpenReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                operation = "Read clipboard image stream";
                if (source.Size > MaximumBytes)
                    throw UiMessage.InvalidData("ClipboardTooLarge");
                using var stream = source.AsStreamForRead();
                using var bytes = new MemoryStream();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var buffer = new byte[81920];
                int count;
                while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    if (bytes.Length + count > MaximumBytes)
                        throw UiMessage.InvalidData("ClipboardTooLarge");
                    bytes.Write(buffer, 0, count);
                }
                return bytes.ToArray();
            }
            finally { _reading--; }
        }
    }

    private async Task WriteImagesAsync()
    {
        await foreach (var work in _images.Reader.ReadAllAsync())
        {
            if (work.Barrier is { } barrier) { barrier.SetResult(); continue; }
            var image = work.Image!;
            Progress.BeginSave(image.ProgressId);
            var result = await ImageSaver.SaveAsync(image);
            Progress.FinishSave(image.ProgressId, result);
            PublishResult(result);
        }
    }

    private void PublishResult(SaveResult result)
    {
        // Old images still finish saving. Their results cannot replace the current session's latest image.
        if (History.Publish(result)) _results.Writer.TryWrite(result);
    }

    private Task OnCaptureThreadAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_controller.DispatcherQueue.TryEnqueue(async () =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(_controller.DispatcherQueue));
            try { await action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        })) completion.SetException(UiMessage.InvalidOperation("ClipboardThreadUnavailable"));
        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await OnCaptureThreadAsync(async () =>
        {
            StopWatching();
            await Task.WhenAll(_pending.ToArray());
        });
        _images.Writer.TryComplete();
        await _writer;
        _results.Writer.TryComplete();
        await _controller.ShutdownQueueAsync();
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
