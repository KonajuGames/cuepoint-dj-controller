using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;
using Cuepoint.Audio.Stems;

namespace Cuepoint.Music;

/// <summary>
/// A DJ edit finished, or given up on: the song it's of, and the edit's file, title and what was made, or why there's
/// none; and whether the song was separated into its stems for it, rather than having them on disk already.
/// </summary>
sealed record DjEditDone(Song Song, string? EditPath, string Title, DjEditResult? Edit, string? Why, bool Separated = false, DjEditOptions? Options = null);

/// <summary>
/// Makes DJ edits of songs (see <see cref="DjEdit"/>), one at a time in the background: decoding the song, finding its
/// stems (separating it if they aren't on disk), then making the edit and writing it beside the song as a 16-bit FLAC
/// tagged with its title and <see cref="DjEdit.SUFFIX"/>. Call <see cref="Update"/> once a frame. UI thread only.
/// </summary>
sealed class DjEdits(StemSeparator separator)
{
    sealed class Job(Song song, string title, string artist, string album, string genre, BeatGrid grid, bool replace, DjEditOptions options)
    {
        public DjEditOptions Options { get; } = options;
        public bool Replace { get; } = replace;
        public Song Song { get; } = song;
        public string Title { get; } = title;
        public string Artist { get; } = artist;
        public string Album { get; } = album;
        public string Genre { get; } = genre;
        public BeatGrid Grid { get; } = grid;
        public string EditPath { get; } = DjEdit.EditPath(song.Path);
        public Task<PcmAudio>? Decoding;
        public StemJob? Stems;
        public bool HadStems;
        public Task<(DjEditResult? Edit, string? Why)>? Making;
    }

    readonly Queue<Job> _waiting = new();
    Job? _job;

    /// <summary>
    /// Why a DJ edit of <paramref name="song"/> can't be made, or null if it can: it isn't analysed, it's an edit or
    /// extended mix already, it's being made already, or there's no model to separate it with and its stems aren't on
    /// disk.
    /// </summary>
    public string? WhyNot(Song song) =>
        song.Analysis is null ? "once it's analysed"
        : DjEdit.LooksLikeEdit(song.Title, song.Path) ? "it's an edit or extended mix already"
        : IsMaking(song) ? "it's being made"
        : !separator.HasModel && !separator.HasStems(song.Path) ? "needs the stem model"
        : null;

    public bool IsMaking(Song song) => _job?.Song == song || _waiting.Any(job => job.Song == song);

    /// <summary>
    /// Whether there's a DJ edit of <paramref name="song"/> made already: where a new one would go, or a WAV made before
    /// they were FLACs.
    /// </summary>
    public static bool HasEdit(Song song) => File.Exists(DjEdit.EditPath(song.Path)) || File.Exists(DjEdit.OldEditPath(song.Path));

    /// <summary>
    /// Starts making a DJ edit of <paramref name="song"/> as <paramref name="options"/> say (the defaults, if null),
    /// after any being made already, replacing one made before only with <paramref name="replace"/>. Returns why not,
    /// if it can't be: see <see cref="WhyNot"/>, or there's one already.
    /// </summary>
    public string? Start(Song song, bool replace = false, DjEditOptions? options = null)
    {
        if (WhyNot(song) is { } why)
            return why;
        if (!replace && HasEdit(song))
            return "there's one already";
        _waiting.Enqueue(new Job(song, DjEdit.EditTitle(song.Title), song.Artist, song.Album, song.Genre, song.Analysis!.Grid, replace, (options ?? DjEditOptions.Default).Checked()));
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
                return new DjEditDone(job.Song, edit is null ? null : job.EditPath, job.Title, edit, why, Separated(job), job.Options);
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
            {
                job.HadStems = separator.HasStems(job.Song.Path);
                job.Stems = separator.Request(job.Song.Path, decoding.GetAwaiter().GetResult(), separate: true);
            }
            return null;
        }
        catch (Exception e)
        {
            _job = null;
            ErrorLog.Write($"making a DJ edit of {job.Song.Path}", e);
            return new DjEditDone(job.Song, null, job.Title, null, e.GetBaseException().Message, Separated(job));
        }
    }

    // Whether the job separated the song itself, its stems being on disk now when they weren't before.
    bool Separated(Job job) => job.Stems is not null && !job.HadStems && separator.HasStems(job.Song.Path);

    static (DjEditResult? Edit, string? Why) Make(Job job, PcmAudio audio, SongStems stems)
    {
        var (edit, why) = DjEdit.Make(audio, stems, job.Grid, job.Options);
        if (edit is not null)
        {
            // The original's cover, as it is, so the edit looks like the song it's of.
            var cover = OperatingSystem.IsWindows() ? CoverArt.ReadImage(job.Song.Path) : null;
            FlacFile.Write(job.EditPath, edit.Audio, new SongTags(job.Title, job.Artist, job.Album, null, job.Genre), job.Replace, cover);
        }
        // The samples are big, and done with, so they don't wait on the result being taken in.
        return (edit is null ? null : edit with { Audio = new PcmAudio([], edit.Audio.Channels, edit.Audio.SampleRate) }, why);
    }
}
