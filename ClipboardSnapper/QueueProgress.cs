namespace ClipboardSnapper;

public enum QueueStage { Reading, Waiting, Saving, Saved, ReadFailed, Rejected, SaveFailed }
public sealed record QueueJob(long Id, long Generation, DateTimeOffset AcceptedAt, ImageFormat Format,
    int JpegQuality, QueueStage Stage, SaveResult? Result = null)
{
    public bool IsActive => Stage is QueueStage.Reading or QueueStage.Waiting or QueueStage.Saving;
}
public readonly record struct QueueView(QueueSnapshot Snapshot, QueueJob[] Jobs);
public readonly record struct QueueSnapshot(int Reading, int Waiting, int Saving,
    long Saved, long ReadFailed, long Rejected, long SaveFailed, long Revision)
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
    private readonly Dictionary<long, QueueJob> _jobs = [];
    private readonly Queue<long> _completed = new();
    private int _reading, _waiting, _saving;
    private long _generation, _saved, _readFailed, _rejected, _saveFailed, _nextId, _revision;

    // Producers only update bounded metadata; no UI callbacks, encoding or I/O run under this lock.
    public long BeginRead(long generation, DateTimeOffset acceptedAt, SaveOptions options)
    {
        lock (_gate)
        {
            AdvanceGeneration(generation);
            var id = ++_nextId;
            _jobs.Add(id, new(id, generation, acceptedAt, options.Format, options.JpegQuality, QueueStage.Reading));
            _reading++; _revision++;
            return id;
        }
    }
    public void IgnoreRead(long id)
    {
        lock (_gate) { _jobs.Remove(id); _reading--; _revision++; }
    }
    public void QueueRead(long id)
    {
        lock (_gate) { _jobs[id] = _jobs[id] with { Stage = QueueStage.Waiting }; _reading--; _waiting++; _revision++; }
    }
    public void BeginSave(long id)
    {
        lock (_gate) { _jobs[id] = _jobs[id] with { Stage = QueueStage.Saving }; _waiting--; _saving++; _revision++; }
    }
    public void FailRead(long id, bool rejected, SaveResult result)
    {
        lock (_gate)
        {
            _reading--;
            Complete(id, rejected ? QueueStage.Rejected : QueueStage.ReadFailed, result);
        }
    }
    public void RejectQueued(long id, SaveResult result)
    {
        lock (_gate) { _waiting--; Complete(id, QueueStage.Rejected, result); }
    }
    public void FinishSave(long id, SaveResult result)
    {
        lock (_gate) { _saving--; Complete(id, result.Success ? QueueStage.Saved : QueueStage.SaveFailed, result); }
    }
    public QueueSnapshot Snapshot(long generation)
    {
        lock (_gate)
        {
            AdvanceGeneration(generation);
            return new(_reading, _waiting, _saving, _saved, _readFailed, _rejected, _saveFailed, _revision);
        }
    }
    public QueueView View(long generation)
    {
        QueueJob[] jobs;
        QueueSnapshot snapshot;
        lock (_gate)
        {
            AdvanceGeneration(generation);
            jobs = _jobs.Values.Where(j => j.Generation == _generation).ToArray();
            snapshot = new(_reading, _waiting, _saving, _saved, _readFailed, _rejected, _saveFailed, _revision);
        }
        // Sorting/rendering takes place outside the producer lock.
        Array.Sort(jobs, (a, b) => b.Id.CompareTo(a.Id));
        return new(snapshot, jobs);
    }
    private void Complete(long id, QueueStage stage, SaveResult result)
    {
        var job = _jobs[id];
        AdvanceGeneration(job.Generation);
        _revision++;
        if (job.Generation != _generation) { _jobs.Remove(id); return; }
        _jobs[id] = job with { Stage = stage, Result = result };
        switch (stage)
        {
            case QueueStage.Saved: _saved++; break;
            case QueueStage.ReadFailed: _readFailed++; break;
            case QueueStage.Rejected: _rejected++; break;
            case QueueStage.SaveFailed: _saveFailed++; break;
        }
        _completed.Enqueue(id);
        // Retain the same number of completed rows as Recent files; never evict active work.
        while (_completed.Count > 100) _jobs.Remove(_completed.Dequeue());
    }
    private void AdvanceGeneration(long generation)
    {
        if (generation <= _generation) return;
        _generation = generation;
        _saved = _readFailed = _rejected = _saveFailed = 0;
        foreach (var id in _completed) _jobs.Remove(id);
        _completed.Clear();
        _revision++;
    }
}
