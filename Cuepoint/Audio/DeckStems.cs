using Cuepoint.Audio.Stems;

namespace Cuepoint.Audio;

/// <summary>
/// The deck's stems: once the loaded song's been separated into vocals, drums, bass and instruments, each can be
/// turned down or off on its own.
/// </summary>
sealed partial class Deck
{
    StemMixer? _stems;

    /// <summary>
    /// Whether the loaded song's stems are on the deck, so <see cref="SetStemLevel"/> does something.
    /// </summary>
    public bool HasStems => _stems is not null;

    /// <summary>
    /// The loaded song's samples as decoded, to separate into stems; null once it has them, since the samples are
    /// then the stems' mix.
    /// </summary>
    internal PcmAudio? SongAudio => _stems is null ? _pcm : null;

    /// <summary>
    /// A stem's level, from 0 to 1; 1 for all of them until the song has stems.
    /// </summary>
    public float StemLevel(Stem stem) => _stems?[stem] ?? 1;

    /// <summary>
    /// Puts the loaded song's stems on the deck, all at full. Ignored (returning false) if they aren't the loaded
    /// song's: another song was loaded while they were being separated.
    /// </summary>
    public bool AttachStems(string path, SongStems stems)
    {
        if (_pcm is not { } pcm || FilePath != path || pcm.Channels != stems.Channels || pcm.Frames != stems.Frames)
            return false;
        DetachStems();
        _stems = new StemMixer(pcm, stems);
        return true;
    }

    /// <summary>
    /// Sets a stem's level, from where the deck is on.
    /// </summary>
    public void SetStemLevel(Stem stem, float level)
    {
        if (_stems is not { } mixer)
            return;
        var levels = mixer.Levels.ToArray();
        levels[(int)stem] = level;
        SetStemLevels(levels);
    }

    /// <summary>
    /// Sets every stem's level at once, in <see cref="Stem"/> order, from where the deck is on.
    /// </summary>
    public void SetStemLevels(IReadOnlyList<float> levels) => _stems?.Set(levels, StemPlayhead());

    // The frame of the song being played now, wherever it's coming from.
    long StemPlayhead() => IsPlatterActive ? (long)Volatile.Read(ref _platterHead) : SongFrame;

    /// <summary>
    /// Takes the stems off the deck, for when another song replaces this one.
    /// </summary>
    void DetachStems()
    {
        _stems?.Cancel();
        _stems = null;
    }
}
