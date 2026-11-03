namespace Brutality.Music;

/// <summary>
/// Work done on a thread of its own, its result taken in on the UI thread: looking through a folder, say, which can
/// take a while on a big one or a slow drive, without the screen waiting. <see cref="Start"/> it, and call
/// <see cref="Apply"/> once a frame. The work mustn't touch what only the UI thread may; what's done with its result
/// can.
/// </summary>
sealed class BackgroundWork
{
    readonly List<(Task Task, Action Done)> _pending = [];

    /// <summary>
    /// Whether any work is still going, or finished and waiting to be taken in.
    /// </summary>
    public bool IsBusy => _pending.Count > 0;

    /// <summary>
    /// Does <paramref name="work"/> in the background, then <paramref name="done"/> with what it made, on the UI thread
    /// at the next <see cref="Apply"/> after it's finished. UI thread only.
    /// </summary>
    public void Start<T>(Func<T> work, Action<T> done)
    {
        var task = Task.Run(work);
        _pending.Add((task, () => done(task.GetAwaiter().GetResult())));
    }

    /// <summary>
    /// Takes in the work that's finished, in the order it was started, returning how much. Work that went wrong throws
    /// here, on the UI thread, once it's out of the way, so the rest is taken in next time. UI thread only.
    /// </summary>
    public int Apply()
    {
        var applied = 0;
        while (_pending.FindIndex(pending => pending.Task.IsCompleted) is var at and >= 0)
        {
            var (_, done) = _pending[at];
            _pending.RemoveAt(at);
            applied++;
            done();
        }
        return applied;
    }
}
