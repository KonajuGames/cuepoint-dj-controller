using Brutality.Audio;
using Brutality.Audio.Analysis;

namespace Brutality.Music;

/// <summary>
/// The ways one song can give way to the next in an auto-mix.
/// </summary>
enum TransitionKind
{
    /// <summary>
    /// Beats matched, over a long stretch, the bass swapping halfway. For songs with sparse ends to overlap.
    /// </summary>
    Blend,

    /// <summary>
    /// A shorter blend, for songs that are both full on where they meet.
    /// </summary>
    BassSwap,

    /// <summary>
    /// Beats matched, the old song filtered away as the new one comes in, so keys that clash aren't heard together for long.
    /// </summary>
    FilterSweep,

    /// <summary>
    /// The old song cut, leaving its echoes ringing as the new one starts, for tempos too far apart to match.
    /// </summary>
    EchoOut,

    /// <summary>
    /// A fade from one to the other: beats matched if it can be, for songs with nothing marking their beats or little
    /// time left; or plain, for songs not analysed.
    /// </summary>
    Crossfade,
}

/// <summary>
/// How loud a stretch of a song is, as <see cref="DetailWaveform.Between"/> says.
/// </summary>
readonly record struct BandLevels(float Peak, float Bass, float Mids, float Treble);

/// <summary>
/// What's known about a song on a deck, for planning a transition: its analysis if it has one, how long it is, its cue
/// point, and how loud any stretch of it is.
/// </summary>
sealed record MixSong(TrackAnalysis? Analysis, double Duration, double CuePoint, Func<double, double, BandLevels> Levels);

/// <summary>
/// A transition, all its times in seconds through the song going out, as its own position says, so a pause can't
/// upset it. <paramref name="IncomingAt"/> is when the new song starts, from <paramref name="IncomingFrom"/> seconds
/// into it, which for matched beats is a bar before it's heard, to line up. <paramref name="BeatShift"/> is how far
/// from the old song's beat the new song's grid should be held, as a fraction of a beat, so that what's heard lands
/// together even where a grid's a little out, or on the off-beat.
/// </summary>
sealed record MixPlan(TransitionKind Kind, int Bars, double StartAt, double EndAt, double IncomingAt, double IncomingFrom, bool BeatMatched, double BeatShift = 0)
{
    /// <summary>
    /// What to call it, to show.
    /// </summary>
    public string Description =>
        Kind switch
        {
            TransitionKind.Blend => $"Long blend, {Bars} bars",
            TransitionKind.BassSwap => $"Bass swap, {Bars} bars",
            TransitionKind.FilterSweep => $"Filter sweep, {Bars} bars",
            TransitionKind.EchoOut => "Echo out",
            _ => BeatMatched ? $"Beat-matched fade, {Bars} bars" : "Crossfade",
        };
}

/// <summary>
/// Where each control should be partway through a transition. <see cref="Crossfade"/> goes from 0, all the old song,
/// to 1, all the new; the rest are the two decks' EQ and filter controls, and whether the old song's echo is on.
/// </summary>
readonly record struct MixMoves(float Crossfade, float OutLow = 0, float OutMid = 0, float OutHigh = 0, float OutFilter = 0, float InLow = 0, float InFilter = 0, bool Echo = false);

/// <summary>
/// Chooses how and when one song should give way to the next, from their beat grids, keys and how their energy changes
/// over time. There's no knowing a song's genre from these, so it goes by what they can say: how close their tempos are
/// where they'd meet, whether their keys mix, and whether the ends that would overlap are sparse. Beats are matched
/// whenever both songs are analysed and their tempos are in reach, drifting tempos included.
/// </summary>
static class TransitionPlanner
{
    /// <summary>
    /// How far a song's tempo can be moved to match the one going out.
    /// </summary>
    public const double MAX_STRETCH = 0.08;

    const int PHRASE_BARS = 8;
    const int BEATS_PER_PHRASE = PHRASE_BARS * BeatGrid.BEATS_PER_BAR;
    const int ECHO_BARS = 3;
    const double CROSSFADE_SECONDS = 8;

    // A bar with less bass than this, against the song's usual, is sparse; how much a beat's hit has to stand out from
    // between the beats for a song to have a beat at all; and the level below which the song has ended.
    const float SPARSE_BASS = 0.6f;
    const double RHYTHM_CONTRAST = 1.15;
    const float SILENCE = 0.03f;

    /// <param name="earliest">How soon, in seconds through the song going out, anything can start happening.</param>
    public static MixPlan Plan(MixSong outgoing, MixSong incoming, double earliest)
    {
        var end = AudibleEnd(outgoing);
        if (outgoing.Analysis is not { } outAnalysis || incoming.Analysis is not { } inAnalysis)
            return Crossfade(incoming, end, earliest);

        var (outGrid, inGrid) = (outAnalysis.Grid, inAnalysis.Grid);
        var ratio = Deck.MasterBeatsPerBeat(inGrid.MainBpm, outGrid.MainBpm);
        var inBeat = IncomingBeat(incoming, inGrid, preroll: true);
        var outroBars = OutroBars(outgoing, outGrid, end);
        var introBars = IntroBars(incoming, inGrid, inBeat);
        (TransitionKind Kind, int Bars)[] choices =
            !HasBeat(outgoing, outGrid) || !HasBeat(incoming, inGrid) ? [(TransitionKind.Crossfade, 8), (TransitionKind.Crossfade, 4), (TransitionKind.Crossfade, 2)]
            : !outAnalysis.Key.MixesWith(inAnalysis.Key) ? [(TransitionKind.FilterSweep, 8), (TransitionKind.FilterSweep, 4), (TransitionKind.Crossfade, 2)]
            : Math.Min(outroBars, introBars) >= 32 ? [(TransitionKind.Blend, 32), (TransitionKind.Blend, 16), (TransitionKind.BassSwap, 8), (TransitionKind.BassSwap, 4), (TransitionKind.Crossfade, 2)]
            : Math.Min(outroBars, introBars) >= 8 ? [(TransitionKind.Blend, 16), (TransitionKind.BassSwap, 8), (TransitionKind.BassSwap, 4), (TransitionKind.Crossfade, 2)]
            : [(TransitionKind.BassSwap, 8), (TransitionKind.BassSwap, 4), (TransitionKind.Crossfade, 2)];

        var bar = BarSeconds(outGrid);
        var preroll = BeatGrid.BEATS_PER_BAR * ratio * 60 / outGrid.MainBpm;
        var (inStart, inFrom) = (inGrid.TimeOfBeat(inBeat), inGrid.TimeOfBeat(inBeat - BeatGrid.BEATS_PER_BAR));
        foreach (var (kind, bars) in choices)
        {
            var seconds = bars * bar;
            if (seconds > (incoming.Duration - inStart) / 2)
                continue;
            if (FindStart(outGrid, earliest + preroll, end - seconds, end - outroBars * bar) is not { } start || !InReach(outGrid, inGrid, start, inStart, seconds, ratio))
                continue;
            return new MixPlan(kind, bars, start, start + seconds, start - preroll, inFrom, true, BeatShift(outgoing, incoming, outGrid, inGrid, start, inStart, seconds, ratio));
        }
        return EchoOut(outgoing, incoming, outGrid, inGrid, end, earliest) ?? Crossfade(incoming, end, earliest);
    }

    /// <summary>
    /// Whether the two songs' tempos stay close enough to match all through the stretch where they overlap, as a tempo
    /// that drifts may not.
    /// </summary>
    static bool InReach(BeatGrid outGrid, BeatGrid inGrid, double outStart, double inStart, double seconds, double ratio)
    {
        const int SAMPLES = 8;
        for (var i = 0; i < SAMPLES; i++)
        {
            var along = seconds * i / (SAMPLES - 1);
            if (Math.Abs(outGrid.BpmAt(outStart + along) / (inGrid.BpmAt(inStart + along) * ratio) - 1) > MAX_STRETCH)
                return false;
        }
        return true;
    }

    /// <summary>
    /// How far the new song's grid should be from the old one's, as a fraction of a beat (of the shorter beat, for half
    /// time), for what's heard in the two to land together. The sound is laid out across the beat, band by band, over
    /// the stretch that'll overlap, and the new song slid round to where the two match best, the kicks above all. Zero if either has nothing
    /// to go by. A grid can be right but for the off-beat, particularly in an intro with only hi-hats to hear.
    /// </summary>
    static double BeatShift(MixSong outgoing, MixSong incoming, BeatGrid outGrid, BeatGrid inGrid, double outStart, double inStart, double seconds, double ratio)
    {
        var units = Math.Max(ratio, 1);
        var span = Math.Max(seconds, 16 * 60 / outGrid.BpmAt(outStart));
        var heard = Profile(outgoing, outGrid, outStart, outStart + span, units / ratio);
        var coming = Profile(incoming, inGrid, inStart, inStart + span, units);

        var (best, bestScore) = (0, 0.0);
        for (var shift = 0; shift < PROFILE_BINS; shift++)
        {
            var score = 0.0;
            for (var band = 0; band < PROFILE_BANDS; band++)
            {
                for (var bin = 0; bin < PROFILE_BINS; bin++)
                    score += BandWeights[band] * heard[band, bin] * coming[band, (bin + shift) % PROFILE_BINS];
            }
            if (score > bestScore)
                (best, bestScore) = (shift, score);
        }

        // Each band counts for its weight at a perfect match, so this much of a match across them says something.
        if (bestScore / BandWeights.Sum() < MIN_PROFILE_MATCH)
            return 0;
        var fraction = (double)best / PROFILE_BINS;
        return fraction >= 0.5 ? fraction - 1 : fraction;
    }

    const int PROFILE_BINS = 32;
    const int PROFILE_BANDS = 3;
    const double MIN_PROFILE_MATCH = 0.15;

    // The kicks are what's heard as the beat, so the bass counts most, and hi-hats, which can as well be off the beat,
    // least.
    static readonly double[] BandWeights = [3, 1, 0.5];

    /// <summary>
    /// How the bass, mids and treble fall across the beat from <paramref name="from"/> to <paramref name="to"/>, each
    /// band taken from its average and scaled so it counts the same as the others; all zeros for a band that doesn't
    /// change across the beat. A beat here is <paramref name="unitsPerBeat"/> to one of the grid's.
    /// </summary>
    static double[,] Profile(MixSong song, BeatGrid grid, double from, double to, double unitsPerBeat)
    {
        var profile = new double[PROFILE_BANDS, PROFILE_BINS];
        var last = grid.BeatNumberAt(Math.Min(to, song.Duration)) * unitsPerBeat;
        for (var unit = Math.Ceiling(grid.BeatNumberAt(from) * unitsPerBeat); unit + 1 <= last; unit++)
        {
            for (var bin = 0; bin < PROFILE_BINS; bin++)
            {
                var levels = song.Levels(grid.TimeOfBeat((unit + (double)bin / PROFILE_BINS) / unitsPerBeat), grid.TimeOfBeat((unit + (bin + 1.0) / PROFILE_BINS) / unitsPerBeat));
                profile[0, bin] += levels.Bass;
                profile[1, bin] += levels.Mids;
                profile[2, bin] += levels.Treble;
            }
        }
        for (var band = 0; band < PROFILE_BANDS; band++)
        {
            var mean = Enumerable.Range(0, PROFILE_BINS).Average(bin => profile[band, bin]);
            var size = Math.Sqrt(Enumerable.Range(0, PROFILE_BINS).Sum(bin => (profile[band, bin] - mean) * (profile[band, bin] - mean)));
            for (var bin = 0; bin < PROFILE_BINS; bin++)
                profile[band, bin] = size > 1e-6 && size > mean * 0.2 ? (profile[band, bin] - mean) / size : 0;
        }
        return profile;
    }

    static MixPlan Crossfade(MixSong incoming, double end, double earliest)
    {
        var seconds = Math.Clamp(end - earliest, 1, CROSSFADE_SECONDS);
        var start = Math.Max(earliest, end - seconds);
        return new MixPlan(TransitionKind.Crossfade, 0, start, start + seconds, start, incoming.CuePoint > 0.01 ? incoming.CuePoint : FirstAudible(incoming), false);
    }

    static MixPlan? EchoOut(MixSong outgoing, MixSong incoming, BeatGrid outGrid, BeatGrid inGrid, double end, double earliest)
    {
        var bar = BarSeconds(outGrid);
        var seconds = ECHO_BARS * bar;
        return FindStart(outGrid, earliest, end - seconds, end) is { } start
            ? new MixPlan(TransitionKind.EchoOut, ECHO_BARS, start, start + seconds, start + bar, inGrid.TimeOfBeat(IncomingBeat(incoming, inGrid, preroll: false)), false)
            : null;
    }

    /// <summary>
    /// Where to start between <paramref name="soonest"/> and <paramref name="latest"/>: the first phrase once the
    /// outro's begun, or failing that the last phrase there's time for, or failing that the last bar.
    /// </summary>
    static double? FindStart(BeatGrid grid, double soonest, double latest, double outroStart)
    {
        if (latest < soonest)
            return null;
        var first = FirstDownbeat(grid);
        double? lastPhrase = null;
        for (var beat = first; ; beat += BEATS_PER_PHRASE)
        {
            var time = grid.TimeOfBeat(beat);
            if (time > latest)
                break;
            if (time < soonest)
                continue;
            if (time >= outroStart)
                return time;
            lastPhrase = time;
        }
        if (lastPhrase is { } phrase)
            return phrase;
        var bars = Math.Floor((grid.BeatNumberAt(latest) - first) / BeatGrid.BEATS_PER_BAR);
        var lastBar = grid.TimeOfBeat(first + bars * BeatGrid.BEATS_PER_BAR);
        return lastBar >= soonest ? lastBar : null;
    }

    static double BarSeconds(BeatGrid grid) => BeatGrid.BEATS_PER_BAR * 60 / grid.MainBpm;

    /// <summary>
    /// The number of the first downbeat in the song.
    /// </summary>
    static double FirstDownbeat(BeatGrid grid)
    {
        var beat = Math.Ceiling(grid.BeatNumberAt(0));
        while (!grid.IsBarStart(beat))
            beat++;
        return beat;
    }

    /// <summary>
    /// The downbeat a song comes in on: the one nearest its cue point, or its first, and with
    /// <paramref name="preroll"/>, one with a bar of the song before it to line up in.
    /// </summary>
    static double IncomingBeat(MixSong song, BeatGrid grid, bool preroll)
    {
        var first = FirstDownbeat(grid);
        var beat = song.CuePoint > 0.01 ? first + Math.Max(0, Math.Round((grid.BeatNumberAt(song.CuePoint) - first) / BeatGrid.BEATS_PER_BAR)) * BeatGrid.BEATS_PER_BAR : first;
        while (preroll && grid.TimeOfBeat(beat - BeatGrid.BEATS_PER_BAR) < 0)
            beat += BeatGrid.BEATS_PER_BAR;
        return beat;
    }

    /// <summary>
    /// Where the music stops, ignoring any silence at the end.
    /// </summary>
    static double AudibleEnd(MixSong song)
    {
        for (var end = song.Duration; end > 0.25; end -= 0.25)
        {
            if (song.Levels(end - 0.25, end).Peak > SILENCE)
                return end;
        }
        return song.Duration;
    }

    static double FirstAudible(MixSong song)
    {
        for (var start = 0.0; start < song.Duration; start += 0.25)
        {
            if (song.Levels(start, start + 0.25).Peak > SILENCE)
                return start;
        }
        return 0;
    }

    /// <summary>
    /// Whether something in the sound hits with <paramref name="grid"/>: some band differs on the beats from between
    /// them. A ballad with no drums has beats counted but nothing marking them, so it's given a gentle fade.
    /// </summary>
    static bool HasBeat(MixSong song, BeatGrid grid)
    {
        var beat = 60 / grid.MainBpm;
        var (from, to) = (grid.BeatNumberAt(song.Duration * 0.25), grid.BeatNumberAt(song.Duration * 0.75));
        double onBass = 0, offBass = 0, onMids = 0, offMids = 0, onTreble = 0, offTreble = 0;
        for (var n = Math.Ceiling(from); n < to; n += Math.Max(1, Math.Floor((to - from) / 64)))
        {
            var time = grid.TimeOfBeat(n);
            var on = song.Levels(time, time + beat * 0.12);
            var off = song.Levels(time + beat * 0.4, time + beat * 0.6);
            (onBass, offBass, onMids, offMids, onTreble, offTreble) = (onBass + on.Bass, offBass + off.Bass, onMids + on.Mids, offMids + off.Mids, onTreble + on.Treble, offTreble + off.Treble);
        }
        return Stands(onBass, offBass) || Stands(onMids, offMids) || Stands(onTreble, offTreble);

        static bool Stands(double on, double off) => on > 0 && off > 0 ? Math.Max(on / off, off / on) >= RHYTHM_CONTRAST : on != off;
    }

    /// <summary>
    /// The song's usual bass, from its middle half, bar by bar: the middle one.
    /// </summary>
    static float BodyBass(MixSong song, BeatGrid grid)
    {
        var bars = new List<float>();
        var first = FirstDownbeat(grid);
        for (var beat = first; grid.TimeOfBeat(beat) < song.Duration * 0.75; beat += BeatGrid.BEATS_PER_BAR)
        {
            var time = grid.TimeOfBeat(beat);
            if (time >= song.Duration * 0.25)
                bars.Add(song.Levels(time, grid.TimeOfBeat(beat + BeatGrid.BEATS_PER_BAR)).Bass);
        }
        if (bars.Count == 0)
            return 0;
        bars.Sort();
        return bars[bars.Count / 2];
    }

    /// <summary>
    /// How many bars at the end of the song, counting back from where it stops, are sparse.
    /// </summary>
    static int OutroBars(MixSong song, BeatGrid grid, double end)
    {
        var limit = BodyBass(song, grid) * SPARSE_BASS;
        var first = FirstDownbeat(grid);
        var beat = first + Math.Floor((grid.BeatNumberAt(end) - first) / BeatGrid.BEATS_PER_BAR) * BeatGrid.BEATS_PER_BAR - BeatGrid.BEATS_PER_BAR;
        var bars = 0;
        for (; beat >= first && song.Levels(grid.TimeOfBeat(beat), grid.TimeOfBeat(beat + BeatGrid.BEATS_PER_BAR)).Bass < limit; beat -= BeatGrid.BEATS_PER_BAR)
            bars++;
        return bars;
    }

    /// <summary>
    /// How many bars from <paramref name="from"/> are sparse.
    /// </summary>
    static int IntroBars(MixSong song, BeatGrid grid, double from)
    {
        var limit = BodyBass(song, grid) * SPARSE_BASS;
        var bars = 0;
        for (var beat = from; grid.TimeOfBeat(beat + BeatGrid.BEATS_PER_BAR) < song.Duration && song.Levels(grid.TimeOfBeat(beat), grid.TimeOfBeat(beat + BeatGrid.BEATS_PER_BAR)).Bass < limit; beat += BeatGrid.BEATS_PER_BAR)
            bars++;
        return bars;
    }

    /// <summary>
    /// Where each control should be <paramref name="progress"/> (0 to 1) of the way through a transition.
    /// </summary>
    public static MixMoves At(TransitionKind kind, double progress)
    {
        var p = Math.Clamp(progress, 0, 1);
        switch (kind)
        {
            case TransitionKind.Blend:
            case TransitionKind.BassSwap:
                // In to the middle, held there, and out; the basses change places in the hold, so they're never both up.
                var swap = Ramp(p, 0.46, 0.54);
                var fade = p < 0.5 ? Ramp(p, 0, 0.4) * 0.5f : 0.5f + Ramp(p, 0.6, 1) * 0.5f;
                return new MixMoves(fade, OutLow: -swap, InLow: swap - 1);
            case TransitionKind.FilterSweep:
                return new MixMoves(Ramp(p, 0, 1), OutFilter: 0.85f * Ramp(p, 0, 0.9), InFilter: -0.7f * (1 - Ramp(p, 0.35, 0.95)));
            case TransitionKind.EchoOut:
                // A bar of echoes over the song, then its sound cut ahead of the echo, so only the repeats ring on.
                var cut = -Ramp(p, 0.33, 0.37);
                return new MixMoves(Ramp(p, 0.33, 0.75), cut, cut, cut, Echo: true);
            default:
                return new MixMoves(Ramp(p, 0, 1));
        }
    }

    static float Ramp(double p, double from, double to) => (float)Math.Clamp((p - from) / (to - from), 0, 1);
}
