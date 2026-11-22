using System.Numerics;

namespace Cuepoint.Audio.Stems;

/// <summary>
/// The four parts a song is separated into.
/// </summary>
enum Stem
{
    Vocals,
    Drums,
    Bass,

    /// <summary>Everything else: what's left of the song once the vocals, drums and bass are taken out.</summary>
    Instruments,
}

/// <summary>
/// A song's vocals, drums and bass, separated out, as interleaved samples in the same format as the deck plays it.
/// The instruments aren't kept: they're whatever the song has that the other three don't, so all four together are
/// the song exactly.
/// </summary>
sealed record SongStems(float[] Vocals, float[] Drums, float[] Bass, int Channels, int SampleRate)
{
    public const int COUNT = 4;

    public long Frames => Vocals.Length / Channels;
}

/// <summary>
/// Plays a deck's stems at the levels set, by writing the mix of them into the samples the deck plays from. SDL_mixer
/// plays the song straight from those samples, so its loops, scratching, key lock and everything else carry on
/// untouched. A change of level is written first just ahead of where the deck is, easing from the old levels to the
/// new so it doesn't click, then through the rest of the song in the background.
/// </summary>
sealed class StemMixer
{
    // How far ahead of the playhead a change starts (allowing for what SDL_mixer has already read), how long it takes
    // to ease in, and how much after that is written straight away, all in seconds.
    const double LEAD_SECONDS = 0.08;
    const double RAMP_SECONDS = 0.015;
    const double NEAR_SECONDS = 2;

    // Written a chunk at a time in the background, so a newer change can take over between chunks.
    const int CHUNK_FRAMES = 1 << 16;

    readonly float[] _target;
    readonly float[] _original;
    readonly SongStems _stems;
    readonly int _channels;
    readonly long _frames;

    // Taken while writing, so a background pass and a change written straight away never write over each other.
    readonly Lock _lock = new();

    float[] _levels = [1, 1, 1, 1];
    int _version;

    /// <param name="target">The samples the deck plays from, which this writes the mix into.</param>
    public StemMixer(PcmAudio target, SongStems stems)
    {
        if (target.Channels != stems.Channels || target.Frames != stems.Frames)
            throw new ArgumentException("The stems aren't in the same format as the song", nameof(stems));
        _target = target.Samples;
        _original = target.Samples.ToArray();
        _stems = stems;
        _channels = target.Channels;
        _frames = target.Frames;
    }

    /// <summary>
    /// The song as it was before any stem was turned down, for whatever needs the whole of it.
    /// </summary>
    public float[] Original => _original;

    /// <summary>
    /// Each stem's level, from 0 (silent) to 1, in <see cref="Stem"/> order.
    /// </summary>
    public IReadOnlyList<float> Levels => _levels;

    public float this[Stem stem] => _levels[(int)stem];

    /// <summary>
    /// Sets the stems' levels, in <see cref="Stem"/> order, from just after <paramref name="playhead"/> (a frame of the
    /// song) on, then the rest of the song in the background. Returns the background pass, for tests.
    /// </summary>
    public Task Set(IReadOnlyList<float> levels, long playhead)
    {
        if (levels.Count != SongStems.COUNT)
            throw new ArgumentException($"There are {SongStems.COUNT} stems", nameof(levels));
        var old = _levels;
        var next = levels.Select(level => Math.Clamp(level, 0f, 1f)).ToArray();
        if (old.SequenceEqual(next))
            return Task.CompletedTask;
        _levels = next;
        var version = Interlocked.Increment(ref _version);

        var rate = _stems.SampleRate;
        var rampStart = Math.Clamp(playhead + (long)(LEAD_SECONDS * rate), 0, _frames);
        var rampEnd = Math.Min(_frames, rampStart + (long)(RAMP_SECONDS * rate));
        var nearEnd = Math.Min(_frames, rampEnd + (long)(NEAR_SECONDS * rate));
        lock (_lock)
        {
            WriteRamp(rampStart, rampEnd, old, next);
            Write(rampEnd, nearEnd, next);
        }

        return Task.Run(async () =>
        {
            // Everything but the ease, starting after it: the song still to come first.
            foreach (var (from, to) in new[] { (nearEnd, _frames), (0L, rampStart) })
            {
                for (var start = from; start < to; start += CHUNK_FRAMES)
                {
                    lock (_lock)
                    {
                        if (Volatile.Read(ref _version) != version)
                            return;
                        Write(start, Math.Min(to, start + CHUNK_FRAMES), next);
                    }
                }
            }

            // The ease itself once the deck's played past it, so going back over it later doesn't ease again.
            await Task.Delay(TimeSpan.FromSeconds(LEAD_SECONDS + RAMP_SECONDS + 0.25));
            lock (_lock)
            {
                if (Volatile.Read(ref _version) == version)
                    Write(rampStart, rampEnd, next);
            }
        });
    }

    /// <summary>
    /// Stops any writing still going on in the background, for when the song leaves the deck.
    /// </summary>
    public void Cancel() => Interlocked.Increment(ref _version);

    // The mix, as the original song with the stems taken back out as far as they're turned down: the instruments
    // being the original less the other three, original × instruments + Σ stem × (its level - instruments) is the
    // same as Σ stem × its level.
    void Write(long fromFrame, long toFrame, float[] levels)
    {
        if (toFrame <= fromFrame)
            return;
        var (start, length) = ((int)(fromFrame * _channels), (int)((toFrame - fromFrame) * _channels));
        var rest = levels[(int)Stem.Instruments];
        var (vocals, drums, bass) = (levels[(int)Stem.Vocals] - rest, levels[(int)Stem.Drums] - rest, levels[(int)Stem.Bass] - rest);

        var target = _target.AsSpan(start, length);
        var original = _original.AsSpan(start, length);
        var v = _stems.Vocals.AsSpan(start, length);
        var d = _stems.Drums.AsSpan(start, length);
        var b = _stems.Bass.AsSpan(start, length);

        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var width = Vector<float>.Count;
            var (gr, gv, gd, gb) = (new Vector<float>(rest), new Vector<float>(vocals), new Vector<float>(drums), new Vector<float>(bass));
            for (; i <= length - width; i += width)
            {
                var mixed = new Vector<float>(original[i..]) * gr + new Vector<float>(v[i..]) * gv + new Vector<float>(d[i..]) * gd + new Vector<float>(b[i..]) * gb;
                mixed.CopyTo(target[i..]);
            }
        }
        for (; i < length; i++)
            target[i] = original[i] * rest + v[i] * vocals + d[i] * drums + b[i] * bass;
    }

    // From the old levels to the new across the frames, a little at a time.
    void WriteRamp(long fromFrame, long toFrame, float[] from, float[] to)
    {
        var length = toFrame - fromFrame;
        Span<float> levels = stackalloc float[SongStems.COUNT];
        for (var frame = fromFrame; frame < toFrame; frame++)
        {
            var t = (float)(frame - fromFrame + 1) / length;
            for (var s = 0; s < SongStems.COUNT; s++)
                levels[s] = from[s] + (to[s] - from[s]) * t;
            var rest = levels[(int)Stem.Instruments];
            var (vocals, drums, bass) = (levels[(int)Stem.Vocals] - rest, levels[(int)Stem.Drums] - rest, levels[(int)Stem.Bass] - rest);
            for (var c = 0; c < _channels; c++)
            {
                var i = frame * _channels + c;
                _target[i] = _original[i] * rest + _stems.Vocals[i] * vocals + _stems.Drums[i] * drums + _stems.Bass[i] * bass;
            }
        }
    }
}
