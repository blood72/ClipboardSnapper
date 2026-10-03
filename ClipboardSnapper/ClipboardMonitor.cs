using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace ClipboardSnapper;

public sealed class ClipboardMonitor : IAsyncDisposable
{
    private const int MaximumBytes = 128 * 1024 * 1024;
    private readonly DispatcherQueueController _controller = DispatcherQueueController.CreateOnDedicatedThread();
    private readonly Channel<CapturedImage> _images = Channel.CreateBounded<CapturedImage>(
        new BoundedChannelOptions(4) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<SaveResult> _results = Channel.CreateBounded<SaveResult>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly List<Task> _pending = [];
    private readonly Task _writer;
    private SaveOptions? _options;
    private uint _lastSequence;
    private int _reading;
    private bool _watching;
    private bool _disposed;

    public ClipboardMonitor() => _writer = Task.Run(WriteImagesAsync);
    public ChannelReader<SaveResult> Results => _results.Reader;
    public SessionHistory History { get; } = new();

    public Task StartAsync(SaveOptions options) => OnCaptureThreadAsync(() =>
    {
        if (_watching) return Task.CompletedTask;
        _options = options;
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
        if (!_watching || _options is null) return;
        var sequence = GetClipboardSequenceNumber();
        if (sequence == _lastSequence) return;
        _lastSequence = sequence;
        // Stamp acceptance before any asynchronous clipboard read, including reads still pending at Clear.
        var task = ReadImageAsync(_options, History.Generation);
        _pending.Add(task);
        _ = ForgetCompletedAsync(task);
    }

    private async Task ForgetCompletedAsync(Task task)
    {
        await task;
        _pending.Remove(task);
    }

    private async Task ReadImageAsync(SaveOptions options, long generation)
    {
        var entered = false;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap)) return;
            if (_reading >= 2)
                throw new IOException("Clipboard images are arriving too quickly. Copy the image again.");
            _reading++;
            entered = true;
            var bitmap = await content.GetBitmapAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            using var source = await bitmap.OpenReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (source.Size > MaximumBytes)
                throw new InvalidDataException("The clipboard image exceeds the 128 MB limit.");
            using var stream = source.AsStreamForRead();
            using var bytes = new MemoryStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var buffer = new byte[81920];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (bytes.Length + count > MaximumBytes)
                    throw new InvalidDataException("The clipboard image exceeds the 128 MB limit.");
                bytes.Write(buffer, 0, count);
            }
            if (!_images.Writer.TryWrite(new CapturedImage(bytes.ToArray(), options, generation)))
                throw new IOException("Image saving is busy. Copy the image again after a moment.");
        }
        catch (Exception exception)
        {
            PublishResult(new SaveResult(options.Folder, DateTimeOffset.Now, false,
                $"{exception.GetType().Name}: {exception.Message}", Generation: generation));
        }
        finally { if (entered) _reading--; }
    }

    private async Task WriteImagesAsync()
    {
        await foreach (var image in _images.Reader.ReadAllAsync())
        {
            var result = await ImageSaver.SaveAsync(image);
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
        })) completion.SetException(new InvalidOperationException("The clipboard thread is unavailable."));
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
