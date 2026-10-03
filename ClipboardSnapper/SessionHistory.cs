namespace ClipboardSnapper;

public sealed class SessionHistory
{
    private readonly object _gate = new();
    private long _generation;
    private SaveResult? _latestSaved;

    public long Generation { get { lock (_gate) return _generation; } }
    public SaveResult? LatestSaved { get { lock (_gate) return _latestSaved; } }

    public void Clear()
    {
        // Only presentation metadata is locked. Capture, encoding and disk I/O never use this lock.
        lock (_gate)
        {
            _generation++;
            _latestSaved = null;
        }
    }

    public bool Publish(SaveResult result)
    {
        lock (_gate)
        {
            if (result.Generation != _generation) return false;
            if (result.Success) _latestSaved = result;
            return true;
        }
    }

    public bool IsCurrent(long generation) { lock (_gate) return generation == _generation; }

    public bool CanPreview(SaveResult result)
    {
        lock (_gate) return result.Generation == _generation && ReferenceEquals(result, _latestSaved);
    }
}
