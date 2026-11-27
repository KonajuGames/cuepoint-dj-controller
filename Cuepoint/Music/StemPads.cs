using Cuepoint.Audio;
using Cuepoint.Audio.Stems;

namespace Cuepoint.Music;

/// <summary>
/// A deck's pads in STEMS mode: the first four turn the vocals, drums, bass and instruments on and off, and the last
/// four play just that one (or all of them again, if it's the only one on). The song's separated into its stems the
/// first time it's in STEMS mode, in the background; a song separated before has them on the deck as soon as it's
/// loaded. The first song to need separating downloads the stem model first.
/// </summary>
sealed class StemPads(Deck deck, StemSeparator separator, GridEditor? grid = null, RollPads? roll = null, JumpPads? jump = null, SamplerPads? sampler = null)
{
    bool _active;

    // The song the deck's stems are being found for, and finding them: from disk, or by separating.
    string? _path;
    StemJob? _job;

    public Deck Deck { get; } = deck;

    public StemSeparator Separator { get; } = separator;

    /// <summary>
    /// Whether the deck's pads work its stems. Turning it on turns the other modes off, and any of them turned on
    /// turns it off.
    /// </summary>
    public bool Active
    {
        get
        {
            if (grid is { Active: true } || roll is { Active: true } || jump is { Active: true } || sampler is { Active: true })
                _active = false;
            return _active;
        }
        set
        {
            _active = value;
            if (!value)
                return;
            // Turning STEMS on again tries again to download a model that failed to.
            Separator.Download?.ForgetFailure();
            if (grid is not null)
                grid.Active = false;
            if (roll is not null)
                roll.Active = false;
            if (jump is not null)
                jump.Active = false;
            if (sampler is not null)
                sampler.Active = false;
        }
    }

    /// <summary>
    /// The stems' short names, for the pads, in <see cref="Stem"/> order.
    /// </summary>
    public static readonly IReadOnlyList<string> Labels = ["VOX", "DRUM", "BASS", "INST"];

    /// <summary>
    /// The stems' names in a sentence, in <see cref="Stem"/> order.
    /// </summary>
    public static readonly IReadOnlyList<string> Names = ["vocals", "drums", "bass", "instruments"];

    /// <summary>
    /// What pad <paramref name="index"/> says: its stem, or while separating, how far it's got on the first.
    /// </summary>
    public string Label(int index) =>
        Deck.HasStems || index != 0 || !Deck.IsLoaded ? Labels[index % SongStems.COUNT]
        : _job is { Separating: true, Done: false } job ? $"{job.Progress * 100:0}%"
        : _job is { Failed: not null } ? "FAILED"
        : Separator.HasModel ? Labels[0]
        : Separator.Download is { Downloading: true } download ? $"DL {download.Progress * 100:0}%"
        : Separator.Download is { Failed: not null } ? "FAILED"
        : "NO MODEL";

    /// <summary>
    /// What the deck's stems are doing, while they aren't ready: separating, failed, or no model to separate with.
    /// </summary>
    public string? Status =>
        !Deck.IsLoaded || Deck.HasStems ? null
        : _job is { Separating: true, Done: false } job ? $"Separating stems… {job.Progress * 100:0}%"
        : _job is { Failed: { } failure } ? $"Couldn't separate the stems: {failure.Message}"
        : Separator.HasModel ? (_job is { Done: false } ? "Looking for stems…" : null)
        : Separator.Download is { Downloading: true } download ? $"Downloading the stem model… {download.Received / 1e6:0} of {download.Size / 1e6:0} MB"
        : Separator.Download is { Failed: { } failed } ? $"Couldn't download the stem model: {failed.Message.TrimEnd('.')}. Turn STEMS on again to try again."
        : _job is { Done: false } ? "Looking for stems…"
        : Separator.Download is not null ? (Active ? "Downloading the stem model…" : null)
        : $"No stem model: tools/export_demucs.py puts one in {Path.GetDirectoryName(Separator.ModelPath)}";

    /// <summary>
    /// Whether pad <paramref name="index"/> is lit: its stem on, for the first four; for the last four, it the only one
    /// on.
    /// </summary>
    public bool IsLit(int index)
    {
        if (!Deck.HasStems)
            return false;
        var stem = (Stem)(index % SongStems.COUNT);
        if (index < SongStems.COUNT)
            return Deck.StemLevel(stem) > 0;
        return Enum.GetValues<Stem>().All(other => Deck.StemLevel(other) > 0 == (other == stem));
    }

    public void Down(int index)
    {
        if (!Deck.HasStems || index < 0 || index >= 2 * SongStems.COUNT)
            return;
        var stem = (Stem)(index % SongStems.COUNT);
        if (index < SongStems.COUNT)
            Deck.SetStemLevel(stem, Deck.StemLevel(stem) > 0 ? 0 : 1);
        else
            Deck.SetStemLevels(IsLit(index) ? [1, 1, 1, 1] : Enum.GetValues<Stem>().Select(other => other == stem ? 1f : 0f).ToArray());
    }

    /// <summary>
    /// Finds the loaded song's stems: from disk as soon as it's loaded, and by separating it once it's in STEMS mode.
    /// Puts them on the deck when they're ready. Call once a frame.
    /// </summary>
    public void Update()
    {
        if (!Deck.IsLoaded || Deck.FilePath is not { } path)
        {
            (_path, _job) = (null, null);
            return;
        }
        if (path != _path)
        {
            _path = path;
            _job = Deck.SongAudio is { } audio ? Separator.Request(path, audio, separate: Active && Separator.HasModel) : null;
        }
        else if (Active && !Deck.HasStems && !Separator.HasModel && _job is { Separating: false, Done: true, Stems: null })
        {
            // Not on disk, and nothing to separate it with: fetch the model, then it's separated below once it's there.
            Separator.Download?.Start();
        }
        else if (Active && !Deck.HasStems && Separator.HasModel && _job is { Separating: false, Done: true, Stems: null } && Deck.SongAudio is { } audio)
        {
            // Not on disk: separate it now it's wanted.
            _job = Separator.Request(path, audio, separate: true);
        }

        if (_job is { Stems: { } stems } && !Deck.HasStems && !Deck.AttachStems(path, stems))
            _job = null;
    }
}
