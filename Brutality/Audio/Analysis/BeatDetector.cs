namespace Brutality.Audio.Analysis;

/// <summary>
/// Finds a song's beat grid: where its beats fall, usually at one steady tempo, as dance music has, but with tempo
/// changes where the song needs them, as with a live drummer. Works on mono audio at around 11 kHz.
/// <list type="number">
/// <item>An onset envelope: how much the spectrum grows from one 12 ms step to the next, which spikes on drum hits and
/// note starts.</item>
/// <item>A rough tempo from the envelope's autocorrelation: which beat length lines it up best with itself, one to
/// four beats on.</item>
/// <item>The exact tempo and the beats' phase, by folding the whole envelope at tempos close to the rough one:
/// at the right tempo the hits all land in the same place in the fold and make the tallest peak.</item>
/// <item>Each beat, followed out both ways from the stretch with the strongest beats (found by Ellis's dynamic
/// programming beat tracker), taking only strong onsets close to where the next beat is due, so it can't slip onto
/// the off-beats, and coasting at the tempo so far through breakdowns. Where the steady grid drifts off those beats,
/// the song gets a variable grid instead: the beats split into sections of steady tempo, at the points that fit them
/// best.</item>
/// </list>
/// </summary>
static class BeatDetector
{
    /// <summary>
    /// Tempos are reported within one octave, from this up to twice it, the way DJ software usually does: a song at
    /// 170 shows as 85, which mixes the same.
    /// </summary>
    public const double MIN_BPM = 80;

    public const double MAX_BPM = 2 * MIN_BPM;

    const int FRAME = 512;
    const int HOP = 128;

    /// <summary>
    /// Where in a frame an onset is when it makes the envelope peak, as a fraction of the frame; found by timing
    /// synthetic clicks.
    /// </summary>
    const double ONSET_POSITION = 0.82;

    /// <summary>
    /// How far either side of the rough tempo the fold searches, and in what steps.
    /// </summary>
    const double REFINE_RANGE_BPM = 1.0;

    const double REFINE_STEP_BPM = 0.01;

    /// <summary>
    /// A tempo this close to a whole number becomes it, if the grid fits nearly as well, since most dance music is
    /// made at whole-number tempos.
    /// </summary>
    const double SNAP_DISTANCE_BPM = 0.05;

    const double SNAP_SCORE = 0.97;

    /// <summary>
    /// How far, in seconds, the steady grid has to sit off the beats (on average over <see cref="DRIFT_WINDOW_BEATS"/>)
    /// before a variable grid is tried: about where two hits stop sounding like one.
    /// </summary>
    const double DRIFT_SECONDS = 0.015;

    const int DRIFT_WINDOW_BEATS = 16;

    /// <summary>
    /// The shortest section of a variable grid: two bars of four, short enough to follow a drummer who wanders.
    /// </summary>
    const int MIN_SECTION_BEATS = 8;

    /// <summary>
    /// How strictly the beat tracker holds the tempo from one beat to the next; Ellis's suggested value.
    /// </summary>
    const double TIGHTNESS = 100;

    /// <summary>
    /// How far from where it's due, as a fraction of a beat, an onset can be and still be taken as the next beat. Well
    /// short of the half a beat away the off-beats are.
    /// </summary>
    const double FOLLOW_REACH = 0.1;

    /// <summary>
    /// How strong an onset has to be, against the song's typical strong beat, to be taken as the beat straight after
    /// one taken. After coasting it takes a typical strong beat to pick the beat up again, so stray onsets in a
    /// breakdown (a vocal, a guitar strum) can't walk it off onto the off-beats.
    /// </summary>
    const double FOLLOW_STRENGTH = 0.4;

    /// <summary>
    /// How far the followed tempo can stray from the steady one.
    /// </summary>
    const double FOLLOW_TEMPO_RANGE = 0.15;

    public readonly record struct Result(BeatGrid Grid, double Confidence);

    /// <summary>
    /// The song's beat grid. Confidence is how much the steady grid stands out from a wrong one, from 0 up.
    /// </summary>
    public static Result Detect(ReadOnlySpan<float> mono, int sampleRate, CancellationToken cancel = default)
    {
        var envelope = OnsetEnvelope(mono, cancel);
        var duration = (double)mono.Length / sampleRate;
        var (steady, confidence) = SteadyGrid(envelope, sampleRate, duration, cancel);

        cancel.ThrowIfCancellationRequested();
        var period = 60.0 * sampleRate / HOP / steady.Markers[0].Bpm;
        var beats = FollowBeats(envelope, TrackBeats(envelope, period), period, sampleRate);
        var grid = VariableGrid(beats, steady) ?? steady;
        return new Result(grid, confidence);
    }

    /// <summary>
    /// The one steady grid that fits the whole song best, and how much it stands out from a wrong one.
    /// </summary>
    internal static (BeatGrid Grid, double Confidence) SteadyGrid(
        float[] envelope,
        int sampleRate,
        double duration,
        CancellationToken cancel = default
    )
    {
        var framesPerSecond = (double)sampleRate / HOP;
        if (envelope.Length < framesPerSecond * 4)
            return (BeatGrid.Constant(120, 0, duration), 0);

        cancel.ThrowIfCancellationRequested();
        var rough = RoughTempo(envelope, framesPerSecond);

        cancel.ThrowIfCancellationRequested();
        var best = (Bpm: rough, Score: 0.0, Phase: 0.0, Mean: 0.0);
        for (var bpm = rough - REFINE_RANGE_BPM; bpm <= rough + REFINE_RANGE_BPM; bpm += REFINE_STEP_BPM)
        {
            var (score, phase, mean) = Fold(envelope, 60 * framesPerSecond / bpm);
            if (score > best.Score)
                best = (bpm, score, phase, mean);
        }

        var whole = Math.Round(best.Bpm);
        if (Math.Abs(whole - best.Bpm) <= SNAP_DISTANCE_BPM)
        {
            var (score, phase, mean) = Fold(envelope, 60 * framesPerSecond / whole);
            if (score >= best.Score * SNAP_SCORE)
                best = (whole, score, phase, mean);
        }

        var tempo = Math.Round(best.Bpm, 2);
        var beatSeconds = 60 / tempo;
        var firstBeat = ((FrameToSeconds(best.Phase, sampleRate) % beatSeconds) + beatSeconds) % beatSeconds;
        var confidence = best.Mean > 0 ? best.Score / best.Mean - 1 : 0;
        return (BeatGrid.Constant(tempo, firstBeat, duration), confidence);
    }

    /// <summary>
    /// Envelope frames (fractional) from the start to seconds, allowing for where in a frame an onset sits when the
    /// envelope peaks for it.
    /// </summary>
    static double FrameToSeconds(double frame, int sampleRate) =>
        (frame * HOP + FRAME * ONSET_POSITION - HOP / 2.0) / sampleRate;

    /// <summary>
    /// One beat found in the envelope: where, in frames, and how strong the onset there was.
    /// </summary>
    internal readonly record struct OnsetPeak(double Frame, double Strength);

    /// <summary>
    /// One beat as followed through the song: when, in seconds, and how strong the onset it was taken from; zero
    /// where there was none strong enough and it was coasted past.
    /// </summary>
    internal readonly record struct TrackedBeat(double Time, double Strength);

    /// <summary>
    /// Ellis's dynamic programming beat tracker: the beats that best combine landing on strong onsets with keeping
    /// near <paramref name="period"/> frames apart. Free to wander, so only its strongest stretch is relied on.
    /// </summary>
    internal static List<OnsetPeak> TrackBeats(float[] envelope, double period)
    {
        var n = envelope.Length;
        var spread = Math.Sqrt(envelope.Select(x => (double)x * x).Average());
        if (spread <= 0)
            return [];

        var score = new double[n];
        var previous = new int[n];
        var shortest = Math.Max(1, (int)Math.Round(period / 2));
        var longest = (int)Math.Round(period * 2);
        var stretchCost = new double[longest + 1];
        for (var gap = shortest; gap <= longest; gap++)
            stretchCost[gap] = TIGHTNESS * Math.Pow(Math.Log(gap / period), 2);

        for (var t = 0; t < n; t++)
        {
            var best = 0.0;
            var from = -1;
            for (var gap = shortest; gap <= longest && gap <= t; gap++)
            {
                var candidate = score[t - gap] - stretchCost[gap];
                if (from < 0 || candidate > best)
                    (best, from) = (candidate, t - gap);
            }
            score[t] = envelope[t] / spread + Math.Max(0, best);
            previous[t] = best > 0 ? from : -1;
        }

        // The path ends at the best-scoring frame in the last beat or so, and is followed back from there.
        var end = n - 1;
        for (var t = Math.Max(0, n - (int)Math.Ceiling(period)); t < n; t++)
        {
            if (score[t] > score[end])
                end = t;
        }
        var frames = new List<int>();
        for (var t = end; t >= 0; t = previous[t])
            frames.Add(t);
        frames.Reverse();

        return frames.Select(frame => PeakNear(envelope, frame, 2)).ToList();
    }

    /// <summary>
    /// The strongest onset within <paramref name="reach"/> frames of <paramref name="frame"/>, placed between frames
    /// by fitting a parabola through its peak.
    /// </summary>
    static OnsetPeak PeakNear(float[] envelope, double frame, double reach)
    {
        var n = envelope.Length;
        var from = Math.Clamp((int)Math.Ceiling(frame - reach), 0, n - 1);
        var to = Math.Clamp((int)Math.Floor(frame + reach), 0, n - 1);
        var peak = Math.Clamp((int)Math.Round(frame), from, to);
        for (var t = from; t <= to; t++)
        {
            if (envelope[t] > envelope[peak])
                peak = t;
        }
        var offset = 0.0;
        if (peak > 0 && peak < n - 1)
        {
            double left = envelope[peak - 1], middle = envelope[peak], right = envelope[peak + 1];
            var curve = left - 2 * middle + right;
            if (curve < 0)
                offset = Math.Clamp(0.5 * (left - right) / curve, -0.5, 0.5);
        }
        return new OnsetPeak(peak + offset, envelope[peak]);
    }

    /// <summary>
    /// The song's beats, followed out both ways from the middle of the tracker's strongest stretch: each next beat
    /// is the strongest onset near where it's due, if strong enough, or else where it's due, coasting. The tempo
    /// follows the beats taken.
    /// </summary>
    internal static List<TrackedBeat> FollowBeats(float[] envelope, List<OnsetPeak> tracked, double period, int sampleRate)
    {
        const int STRETCH = 16;
        if (tracked.Count < STRETCH)
            return [];

        var strong = tracked.Select(beat => beat.Strength).Order().ToList();
        var typical = strong[strong.Count * 3 / 4];

        var bestStart = 0;
        var bestStrength = double.MinValue;
        for (var i = 0; i + STRETCH <= tracked.Count; i++)
        {
            var strength = 0.0;
            for (var j = i; j < i + STRETCH; j++)
                strength += tracked[j].Strength;
            if (strength > bestStrength)
                (bestStart, bestStrength) = (i, strength);
        }
        var anchor = tracked[bestStart + STRETCH / 2];
        var lowest = period * (1 - FOLLOW_TEMPO_RANGE);
        var highest = period * (1 + FOLLOW_TEMPO_RANGE);
        var startPeriod = Math.Clamp(
            (tracked[bestStart + STRETCH - 1].Frame - tracked[bestStart].Frame) / (STRETCH - 1),
            lowest,
            highest
        );

        List<TrackedBeat> Follow(int direction)
        {
            var beats = new List<TrackedBeat>();
            var (at, beatFrames, sure, since) = (anchor.Frame, startPeriod, anchor.Frame, 0);
            while (true)
            {
                var due = at + direction * beatFrames;
                if (due < 0 || due > envelope.Length - 1)
                    return beats;
                since++;
                var peak = PeakNear(envelope, due, FOLLOW_REACH * beatFrames);
                var following = since == 1;
                if (peak.Strength >= (following ? FOLLOW_STRENGTH * typical : typical))
                {
                    at = peak.Frame;
                    // The tempo eases toward the gap from the beat before, so one early hit doesn't throw it, and only
                    // between beats both taken, so scattered onsets can't drag it about.
                    if (following)
                        beatFrames = Math.Clamp(beatFrames + 0.25 * (Math.Abs(at - sure) - beatFrames), lowest, highest);
                    (sure, since) = (at, 0);
                    beats.Add(new TrackedBeat(FrameToSeconds(at, sampleRate), peak.Strength));
                }
                else
                {
                    at = due;
                    beats.Add(new TrackedBeat(FrameToSeconds(at, sampleRate), 0));
                }
            }
        }

        var before = Follow(-1);
        before.Reverse();
        return [.. before, new TrackedBeat(FrameToSeconds(anchor.Frame, sampleRate), anchor.Strength), .. Follow(1)];
    }

    /// <summary>
    /// A grid with tempo changes, if <paramref name="steady"/> drifts off the tracked beats somewhere and sections of
    /// steady tempo fit them clearly better; otherwise null, and the steady grid stands.
    /// </summary>
    internal static BeatGrid? VariableGrid(List<TrackedBeat> beats, BeatGrid steady)
    {
        if (beats.Count < 2 * MIN_SECTION_BEATS)
            return null;

        // Beats taken from a typical strong onset or better count fully, fainter ones less, and those coasted past
        // not at all, so breakdowns don't pull the grid about.
        var taken = beats.Where(beat => beat.Strength > 0).Select(beat => beat.Strength).Order().ToList();
        if (taken.Count < 2 * MIN_SECTION_BEATS)
            return null;
        var typical = taken[taken.Count * 3 / 4];
        var weights = beats
            .Select(beat => beat.Strength <= 0 ? 0 : Math.Clamp((beat.Strength / typical - FOLLOW_STRENGTH) / (1 - FOLLOW_STRENGTH), 0.05, 1))
            .ToArray();
        var times = beats.Select(beat => beat.Time).ToArray();

        var marker = steady.Markers[0];
        double OffSteady(int i)
        {
            var off = (times[i] - marker.Time) % marker.BeatSeconds;
            if (off > marker.BeatSeconds / 2)
                off -= marker.BeatSeconds;
            else if (off < -marker.BeatSeconds / 2)
                off += marker.BeatSeconds;
            return off;
        }

        // Drift shows as the beats sitting consistently early or late for a while; random jitter averages out.
        var drifts = false;
        for (var start = 0; start + DRIFT_WINDOW_BEATS <= beats.Count && !drifts; start++)
        {
            double sum = 0, weight = 0;
            for (var i = start; i < start + DRIFT_WINDOW_BEATS; i++)
            {
                sum += weights[i] * OffSteady(i);
                weight += weights[i];
            }
            drifts = weight > DRIFT_WINDOW_BEATS / 4.0 && Math.Abs(sum / weight) > DRIFT_SECONDS;
        }
        if (!drifts)
            return null;

        var sections = Sections(times, weights);
        if (sections.Count < 2)
            return null;

        // A marker goes where each pair of sections' lines meet: on the section's first beat, or the beat before,
        // whichever the two lines agree on best, since the beat where the tempo changes fits both. A section's tempo
        // is then whatever fits its whole number of beats exactly between its marker and the next, so the grid
        // joins up.
        double Line(int section, int beat) => sections[section].Intercept + sections[section].Slope * beat;
        var at = new int[sections.Count];
        var markers = new List<GridMarker> { new(Line(0, sections[0].First), 0) };
        for (var s = 1; s < sections.Count; s++)
        {
            var first = sections[s].First;
            var corner = Math.Abs(Line(s - 1, first - 1) - Line(s, first - 1)) < Math.Abs(Line(s - 1, first) - Line(s, first))
                ? first - 1
                : first;
            at[s] = corner;
            markers.Add(new GridMarker((Line(s - 1, corner) + Line(s, corner)) / 2, 0));
        }
        at[0] = sections[0].First;
        for (var s = 0; s < sections.Count; s++)
        {
            var bpm = s + 1 < sections.Count
                ? 60 * (at[s + 1] - at[s]) / (markers[s + 1].Time - markers[s].Time)
                : 60 / sections[s].Slope;
            if (!(bpm > 0) || (s + 1 < sections.Count && markers[s].Time >= markers[s + 1].Time))
                return null;
            markers[s] = markers[s] with { Bpm = Math.Round(bpm, 2) };
        }
        // A marker that changes nothing (the same tempo, on a beat of the grid before it) goes.
        for (var s = markers.Count - 1; s > 0; s--)
        {
            var before = markers[s - 1];
            var along = (markers[s].Time - before.Time) / before.BeatSeconds;
            var off = Math.Abs(along - Math.Round(along)) * before.BeatSeconds;
            if (Math.Abs(markers[s].Bpm - before.Bpm) < 0.05 && off < 0.003)
                markers.RemoveAt(s);
        }
        if (markers.Count < 2)
            return null;

        var variable = new BeatGrid(markers, steady.Duration);

        // Only worth it if the beats sit clearly closer to it than to the steady grid.
        return MeanOffset(variable, times, weights) < 0.7 * MeanOffset(steady, times, weights) ? variable : null;
    }

    /// <summary>
    /// Splits the beats into sections of steady tempo: straight lines through beat time against beat number. It's
    /// the split with the least weighted squared error plus a penalty for each extra section, found by dynamic
    /// programming over where sections can start. The penalty scales with how noisy the beat times are, so a steady
    /// song with sloppy onsets stays in one piece. Returns each section's first beat, beat count, and fitted line.
    /// </summary>
    static List<(int First, int Count, double Intercept, double Slope)> Sections(double[] times, double[] weights)
    {
        var n = times.Length;

        // Running sums, so any run of beats can be fitted in constant time.
        var sw = new double[n + 1];
        var si = new double[n + 1];
        var sii = new double[n + 1];
        var st = new double[n + 1];
        var sit = new double[n + 1];
        var stt = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            var w = weights[i];
            sw[i + 1] = sw[i] + w;
            si[i + 1] = si[i] + w * i;
            sii[i + 1] = sii[i] + w * i * i;
            st[i + 1] = st[i] + w * times[i];
            sit[i + 1] = sit[i] + w * i * times[i];
            stt[i + 1] = stt[i] + w * times[i] * times[i];
        }

        (double Intercept, double Slope, double Error) Fit(int from, int to)
        {
            var w = sw[to] - sw[from];
            var i = si[to] - si[from];
            var ii = sii[to] - sii[from];
            var t = st[to] - st[from];
            var it = sit[to] - sit[from];
            var tt = stt[to] - stt[from];
            var det = w * ii - i * i;
            if (w <= 0 || Math.Abs(det) < 1e-12)
                return (0, 0, double.MaxValue);
            var slope = (w * it - i * t) / det;
            var intercept = (t - slope * i) / w;
            var error = tt - 2 * intercept * t - 2 * slope * it + intercept * intercept * w + 2 * intercept * slope * i
                + slope * slope * ii;
            return (intercept, slope, Math.Max(0, error));
        }

        // The noise in the beat times, from second differences, which a steady tempo makes zero: for independent
        // noise of spread σ they have a variance of 6σ², and the median of a squared normal is 0.455 of its mean.
        // Only from beats actually found, since coasted ones are evenly spaced by definition.
        var second = new List<double>();
        for (var i = 1; i + 1 < n; i++)
        {
            if (weights[i - 1] <= 0 || weights[i] <= 0 || weights[i + 1] <= 0)
                continue;
            var d = times[i + 1] - 2 * times[i] + times[i - 1];
            second.Add(d * d);
        }
        if (second.Count == 0)
            return [];
        second.Sort();
        var noise = Math.Max(0.0015 * 0.0015, second[second.Count / 2] / (6 * 0.455));
        var penalty = 4 * Math.Log(n) * noise;

        var cost = new double[n + 1];
        var start = new int[n + 1];
        Array.Fill(cost, double.MaxValue);
        cost[0] = 0;
        for (var end = MIN_SECTION_BEATS; end <= n; end++)
        {
            for (var from = 0; from <= end - MIN_SECTION_BEATS; from++)
            {
                if (cost[from] == double.MaxValue || (from > 0 && from < MIN_SECTION_BEATS))
                    continue;
                var candidate = cost[from] + Fit(from, end).Error + penalty;
                if (candidate < cost[end])
                    (cost[end], start[end]) = (candidate, from);
            }
        }

        var sections = new List<(int, int, double, double)>();
        for (var end = n; end > 0; end = start[end])
        {
            var from = start[end];
            var (intercept, slope, _) = Fit(from, end);
            sections.Add((from, end - from, intercept, slope));
        }
        sections.Reverse();
        return sections;
    }

    /// <summary>
    /// How far, on average, the beats are from the nearest beat of <paramref name="grid"/>, in seconds.
    /// </summary>
    static double MeanOffset(BeatGrid grid, double[] times, double[] weights)
    {
        var gridBeats = grid.Beats().ToArray();
        double sum = 0, weight = 0;
        for (var i = 0; i < times.Length; i++)
        {
            var at = Array.BinarySearch(gridBeats, times[i]);
            if (at < 0)
                at = ~at;
            var nearest = double.MaxValue;
            if (at < gridBeats.Length)
                nearest = Math.Abs(gridBeats[at] - times[i]);
            if (at > 0)
                nearest = Math.Min(nearest, Math.Abs(gridBeats[at - 1] - times[i]));
            if (nearest == double.MaxValue)
                continue;
            sum += weights[i] * nearest;
            weight += weights[i];
        }
        return weight > 0 ? sum / weight : 0;
    }

    /// <summary>
    /// Spectral flux on log magnitudes, less its local average so only peaks stand out: one value per hop.
    /// </summary>
    internal static float[] OnsetEnvelope(ReadOnlySpan<float> mono, CancellationToken cancel = default)
    {
        var frames = (mono.Length - FRAME) / HOP + 1;
        if (frames < 2)
            return [];

        var fft = new Fft(FRAME);
        var previous = new float[FRAME / 2 + 1];
        var current = new float[FRAME / 2 + 1];
        var flux = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            if ((f & 1023) == 0)
                cancel.ThrowIfCancellationRequested();

            fft.Magnitudes(mono.Slice(f * HOP, FRAME), current);
            var sum = 0f;
            for (var k = 1; k < current.Length; k++)
            {
                current[k] = MathF.Log(1 + 100 * current[k]);
                if (f > 0)
                    sum += MathF.Max(0, current[k] - previous[k]);
            }
            flux[f] = sum;
            (previous, current) = (current, previous);
        }

        // Subtract a moving average about a third of a second wide, keeping what rises above it.
        const int HALF_WIDTH = 16;
        var envelope = new float[frames];
        var running = 0.0;
        for (var i = 0; i < Math.Min(frames, HALF_WIDTH); i++)
            running += flux[i];
        for (var f = 0; f < frames; f++)
        {
            var add = f + HALF_WIDTH;
            if (add < frames)
                running += flux[add];
            var remove = f - HALF_WIDTH - 1;
            if (remove >= 0)
                running -= flux[remove];
            var count = Math.Min(frames - 1, f + HALF_WIDTH) - Math.Max(0, f - HALF_WIDTH) + 1;
            envelope[f] = MathF.Max(0, flux[f] - (float)(running / count));
        }
        return envelope;
    }

    /// <summary>
    /// The tempo, to a tenth of a BPM, whose beat lengths (one to four beats) the envelope best matches itself at,
    /// favouring tempos near 120 a little where it's close.
    /// </summary>
    static double RoughTempo(float[] envelope, double framesPerSecond)
    {
        var maxLag = (int)Math.Ceiling(4 * 60 * framesPerSecond / MIN_BPM) + 2;
        var correlation = new double[Math.Min(maxLag, envelope.Length - 1)];
        for (var lag = 1; lag < correlation.Length; lag++)
        {
            var sum = 0.0;
            for (var i = lag; i < envelope.Length; i++)
                sum += envelope[i] * envelope[i - lag];
            correlation[lag] = sum / (envelope.Length - lag);
        }

        double At(double lag)
        {
            var i = (int)lag;
            if (i + 1 >= correlation.Length)
                return 0;
            var t = lag - i;
            return correlation[i] * (1 - t) + correlation[i + 1] * t;
        }

        var best = (Bpm: 120.0, Score: double.MinValue);
        for (var bpm = MIN_BPM; bpm < MAX_BPM; bpm += 0.1)
        {
            var period = 60 * framesPerSecond / bpm;
            var score = 0.0;
            for (var beats = 1; beats <= 4; beats++)
                score += At(beats * period);
            var octavesFrom120 = Math.Log2(bpm / 120);
            score *= Math.Exp(-0.5 * octavesFrom120 * octavesFrom120);
            if (score > best.Score)
                best = (bpm, score);
        }
        return best.Bpm;
    }

    /// <summary>
    /// Folds the envelope at <paramref name="period"/> frames: the height of the tallest peak in the fold, where it is
    /// (in frames into a beat), and the fold's average height.
    /// </summary>
    static (double Score, double Phase, double Mean) Fold(float[] envelope, double period)
    {
        // Quarter-frame bins, about 3 ms.
        const int BINS_PER_FRAME = 4;
        var bins = (int)Math.Ceiling(period * BINS_PER_FRAME);
        Span<double> fold = stackalloc double[bins];
        fold.Clear();
        for (var i = 0; i < envelope.Length; i++)
        {
            var within = i % period;
            fold[Math.Min(bins - 1, (int)(within * BINS_PER_FRAME))] += envelope[i];
        }

        // Smooth a little, round the circle, so a hit that wobbles between neighbouring bins still counts as one.
        var best = (Score: 0.0, Bin: 0);
        var total = 0.0;
        for (var b = 0; b < bins; b++)
        {
            var smoothed = 0.0;
            for (var d = -2; d <= 2; d++)
                smoothed += fold[(b + d + bins) % bins] * (3 - Math.Abs(d));
            total += fold[b];
            if (smoothed > best.Score)
                best = (smoothed, b);
        }
        var mean = total / bins * 9;
        return (best.Score, (best.Bin + 0.5) / BINS_PER_FRAME, mean);
    }
}
