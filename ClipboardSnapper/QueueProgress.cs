namespace ClipboardSnapper;

public readonly record struct QueueSnapshot(int Reading, int Waiting, int Saving,
    long Saved, long ReadFailed, long Rejected, long SaveFailed)
{
    public int Active => Reading + Waiting + Saving;
    public long Failed => ReadFailed + Rejected + SaveFailed;
    public string StatusKey(bool monitoring, bool stopped) => Active > 0
        ? monitoring ? "QueueProcessing" : "QueueDraining"
        : monitoring ? "QueueListening" : stopped ? "QueueFinished" : "QueueIdle";
}

public sealed class QueueProgress
{
    private readonly object _gate = new();
    private int _reading, _waiting, _saving;
    private long _generation, _saved, _readFailed, _rejected, _saveFailed;

    // Producers only update small counters; no UI callbacks, encoding or I/O run under this lock.
    public void BeginRead() { lock (_gate) _reading++; }
    public void IgnoreRead() { lock (_gate) _reading--; }
    public void QueueRead() { lock (_gate) { _reading--; _waiting++; } }
    public void BeginSave() { lock (_gate) { _waiting--; _saving++; } }

    public void FailRead(long generation, bool rejected)
    {
        lock (_gate)
        {
            _reading--;
            if (!AcceptOutcome(generation)) return;
            if (rejected) _rejected++; else _readFailed++;
        }
    }

    public void RejectQueued(long generation)
    {
        lock (_gate)
        {
            _waiting--;
            if (AcceptOutcome(generation)) _rejected++;
        }
    }

    public void FinishSave(long generation, bool success)
    {
        lock (_gate)
        {
            _saving--;
            if (!AcceptOutcome(generation)) return;
            if (success) _saved++; else _saveFailed++;
        }
    }

    public QueueSnapshot Snapshot(long generation)
    {
        lock (_gate)
        {
            AdvanceGeneration(generation);
            return new(_reading, _waiting, _saving, _saved, _readFailed, _rejected, _saveFailed);
        }
    }

    private bool AcceptOutcome(long generation)
    {
        AdvanceGeneration(generation);
        return generation == _generation;
    }

    private void AdvanceGeneration(long generation)
    {
        if (generation <= _generation) return;
        _generation = generation;
        _saved = _readFailed = _rejected = _saveFailed = 0;
    }
}
