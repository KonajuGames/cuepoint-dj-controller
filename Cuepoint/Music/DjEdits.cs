using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;
using Cuepoint.Audio.Stems;

namespace Cuepoint.Music;

/// <summary>
/// A DJ edit finished, or given up on: the song it's of, and the edit's file and what was made, or why there's none.
/// </summary>
sealed record DjEditDone(Song Song, string? EditPath, DjEditResult? Edit, string? Why);

/// <summary>
/// Makes DJ edits of songs (see <see cref="DjEdit"/>), one at a time in the background: decoding the song, finding its
/// stems (separating it if they aren't on disk), then making the edit and writing it beside the song as a WAV tagged
/// with its title and "(DJ Edit)". Call <see cref="Update"/> once a frame. UI thread only.
/// </summary>
sealed class DjEdits(StemSeparator separator)
{
    sealed class Job(Song song, string title, string artist, string album, string genre, BeatGrid grid)
    {
        public Song Song { get; } = song;
        public string Title { get; } = title;
        public string Artist { get; } = artist;
        public string Album { get; } = album;
        public string Genre { get; } = genre;
        public BeatGrid Grid { get; } = grid;
        public string EditPath { get; } = DjEdit.EditPath(song.Path);
        public Task<PcmAudio>? Decoding;
        public StemJob? Stems;
        public Task<(DjEditResult? Edit, string? Why)>? Making;
    }

    readonly Queue<Job> _waiting = new();
    Job? _job;

    /// <summary>
    /// Why a DJ edit of <paramref name="song"/> can't be made, or null if it can: it isn't analysed, it's an edit or
    /// extended mix already, it's being made already, there's one made already, or there's no model to separate it
    /// with and its stems aren't on disk.
    /// </summary>
    public string? WhyNot(Song song) =>
        song.Analysis is null ? "once it's analysed"
        : DjEdit.LooksLikeEdit(song.Title, song.Path) ? "it's an edit or extended mix already"
        : IsMaking(song) ? "it's being made"
        : File.Exists(DjEdit.EditPath(song.Path)) ? "there's one already"
        : !separator.HasModel && !separator.HasStems(song.Path) ? "needs the stem model"
        : null;

    public bool IsMaking(Song song) => _job?.Song == song || _waiting.Any(job => job.Song == song);

    /// <summary>
    /// Starts making a DJ edit of <paramref name="song"/>, after any being made already. Returns why not, if it can't
    /// be: see <see cref="WhyNot"/>.
    /// </summary>
    public string? Start(Song song)
    {
        if (WhyNot(song) is { } why)
            return why;
        _waiting.Enqueue(new Job(song, DjEdit.EditTitle(song.Title), song.Artist, song.Album, song.Genre, song.Analysis!.Grid));
        return null;
    }

    /// <summary>
    /// What the edit being made is doing, to show; null if there's none.
    /// </summary>
    public string? Status =>
        _job is not { } job ? null
        : job.Stems is { Separating: true, Done: false } stems ? $"Making a DJ edit of {job.Song.Title}: separating its stems, {stems.Progress * 100:0}%"
        : $"Making a DJ edit of {job.Song.Title}...";

    /// <summary>
    /// Moves the edit being made on to its next step, and starts the next once it's done. Returns the edit finished,
    /// or given up on, if one was.
    /// </summary>
    public DjEditDone? Update()
    {
        if (_job is null)
        {
            if (!_waiting.TryDequeue(out var next))
                return null;
            _job = next;
            var path = next.Song.Path;
            next.Decoding = Task.Run(() => AudioDecoder.Decode(path).Audio);
        }

        var job = _job;
        try
        {
            if (job.Making is { IsCompleted: true } making)
            {
                _job = null;
                var (edit, why) = making.GetAwaiter().GetResult();
                return new DjEditDone(job.Song, edit is null ? null : job.EditPath, edit, why);
            }
            if (job.Making is not null)
                return null;

            if (job.Stems is { Done: true } stemJob)
            {
                if (stemJob.Failed is { } failed)
                    throw failed;
                if (stemJob.Stems is not { } stems)
                    throw new InvalidOperationException("its stems couldn't be found");
                var audio = job.Decoding!.GetAwaiter().GetResult();
                job.Decoding = null;
                job.Making = Task.Run(() => Make(job, audio, stems));
                return null;
            }
            if (job.Stems is not null)
                return null;

            if (job.Decoding is { IsCompleted: true } decoding)
                job.Stems = separator.Request(job.Song.Path, decoding.GetAwaiter().GetResult(), separate: true);
            return null;
        }
        catch (Exception e)
        {
            _job = null;
            ErrorLog.Write($"making a DJ edit of {job.Song.Path}", e);
            return new DjEditDone(job.Song, null, null, e.GetBaseException().Message);
        }
    }

    static (DjEditResult? Edit, string? Why) Make(Job job, PcmAudio audio, SongStems stems)
    {
        var (edit, why) = DjEdit.Make(audio, stems, job.Grid);
        if (edit is not null)
            WavFile.Write(job.EditPath, edit.Audio, new SongTags(job.Title, job.Artist, job.Album, null, job.Genre));
        // The samples are big, and done with, so they don't wait on the result being taken in.
        return (edit is null ? null : edit with { Audio = new PcmAudio([], edit.Audio.Channels, edit.Audio.SampleRate) }, why);
    }
}
