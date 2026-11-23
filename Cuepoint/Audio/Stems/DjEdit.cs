using System.Text.RegularExpressions;
using Cuepoint.Audio.Analysis;

namespace Cuepoint.Audio.Stems;

/// <summary>
/// A DJ edit of a song, made: its samples, its beat grid, how far into the edit the song itself was moved, where the
/// beats added at either end meet the song, in seconds through the edit (null for an end left as it was), and where
/// it's cued to start: the intro's first beat, or without one, where the song's first heard, on the nearest beat.
/// </summary>
sealed record DjEditResult(PcmAudio Audio, BeatGrid Grid, double Offset, double? IntroEnd, double? OutroStart, string Summary, double Cue = 0);

/// <summary>
/// What changes halfway through a DJ edit's intro and outro: the song's bass, its instruments or both, from the same bars
/// as the drums, coming in (or in the outro, dropping out), or a bar without the kick, or nothing; or Auto, the bass if
/// those bars have it, or else the instruments, or else the break.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<DjEditChange>))]
enum DjEditChange
{
    Auto,
    Nothing,
    Bass,
    Instruments,
    BassAndInstruments,
    Break,
}

/// <summary>
/// How a DJ edit's beats give way to the song after its intro, take over from it at its outro, or end: Auto, as the
/// song suits; a hit on the downbeat dying away; a fade; a low-pass filter sweep; an echo; or a cut.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<DjEditTransition>))]
enum DjEditTransition
{
    Auto,
    Hit,
    Fade,
    Filter,
    Echo,
    Cut,
}

/// <summary>
/// Choices in how a DJ edit's made: how many bars of beats each end has, what changes halfway through, and how the
/// beats give way to the song, take over from it, and end, over how many beats, and for the intro, whether before the
/// song starts or across its start; and how often an echo repeats. The defaults are what suits most songs.
/// </summary>
sealed record DjEditOptions
{
    public int IntroBars { get; init; } = 16;
    public DjEditChange IntroChange { get; init; } = DjEditChange.Auto;
    public DjEditTransition IntroEnd { get; init; } = DjEditTransition.Auto;
    public int IntroEndBeats { get; init; } = 4;
    public bool IntroEndAcrossSong { get; init; }

    public int OutroBars { get; init; } = 16;
    public DjEditChange OutroChange { get; init; } = DjEditChange.Auto;
    public DjEditTransition OutroStart { get; init; } = DjEditTransition.Auto;
    public int OutroStartBeats { get; init; } = 4;
    public bool OutroStartUnderSong { get; init; }
    public DjEditTransition OutroEnd { get; init; } = DjEditTransition.Auto;
    public int OutroEndBeats { get; init; } = 4;

    public double EchoBeats { get; init; } = 1;

    public static readonly DjEditOptions Default = new();

    /// <summary>
    /// The same, with anything out of range brought into it: ends of 4 to 64 bars, in whole bars of 4, transitions
    /// of 1 to 64 beats, and echoes every eighth note to every bar.
    /// </summary>
    public DjEditOptions Checked() =>
        this with
        {
            IntroBars = Math.Clamp(IntroBars / 4 * 4, 4, 64),
            OutroBars = Math.Clamp(OutroBars / 4 * 4, 4, 64),
            IntroEndBeats = Math.Clamp(IntroEndBeats, 1, 64),
            OutroStartBeats = Math.Clamp(OutroStartBeats, 1, 64),
            OutroEndBeats = Math.Clamp(OutroEndBeats, 1, 64),
            EchoBeats = Math.Clamp(EchoBeats, 0.5, 4),
        };
}

/// <summary>
/// Makes a DJ edit of a song: <see cref="BARS"/> bars of its own drums, from its drum stem, before it starts and after
/// it ends, so it can be mixed in and out over beats alone. A phrase into each something changes: the song's bass,
/// from the same bars as the drums, comes in (or at the end, drops out a phrase before it), or failing that its
/// instruments, or failing those the kick drops out for a bar. The beats before it end on its first downbeat, giving
/// way to any pickup the song starts with, or rolling into it with a fill if it comes straight in on the drums; the
/// beats after it carry on from where its drums stop to the end of a phrase, counted in eight-bar phrases from its
/// first downbeat, and finish on a last hit. An end that has <see cref="READY_BARS"/> bars of beats without vocals
/// already is left as it is, and a song with both has nothing to add.
/// </summary>
static partial class DjEdit
{
    public const int BARS = 16;

    /// <summary>
    /// How many bars of beats without vocals an end needs to be mixable as it is, so it gets none added: fewer than
    /// <see cref="BARS"/>, as an extended mix's may have its hook over some of its beats.
    /// </summary>
    public const int READY_BARS = 8;

    const int BEATS_PER_BAR = BeatGrid.BEATS_PER_BAR;
    const int PHRASE_BARS = 8;

    // Peaks below this are silence.
    const float AUDIBLE = 0.03f;

    // A beat's drums this loud against the song's usual (the 70th percentile of its beats) are playing; vocals this
    // quiet against theirs (the 80th percentile of its bars) aren't.
    const double DRUMS_PLAYING = 0.35;
    const double VOCALS_QUIET = 0.2;

    // Bars of drums alike enough to loop, as the cosine of their sound across the bar, sixteenth by sixteenth.
    const double SAME_GROOVE = 0.9;

    // A bar's drums this loud against the song's usual bar are at full strength, not fading.
    const double FULL_DRUMS = 0.75;

    // How many bars of the song, from where the beats meet it, their loudness is matched to: a phrase.
    const int NEAR_BARS = 8;

    // How long the seams between repeats cross over, just ahead of each downbeat, and how quickly a gap opens and closes.
    const double SEAM_SECONDS = 0.004;
    const double GAP_SECONDS = 0.003;

    // The kick drum, for telling bars apart: below this, in hertz.
    const double KICK_HZ = 150;

    // The bars looped have a part of the song worth bringing in with their drums if it's this loud against its usual.
    const double LAYER_PRESENT = 0.4;

    // A bar of the song this quiet against its usual bar, at its start, is a lead-in to it rather than its first bar,
    // up to this many bars of them; and at its end, it's all but over.
    const double LEAD_IN = 0.35;
    const int MAX_LEAD_IN_BARS = 2;
    const double DYING_AWAY = 0.6;

    /// <summary>
    /// Whether a song's title or file name says it's a DJ edit, an extended or club mix, or a DJ-pool intro version
    /// already: those have beats to mix over at their ends.
    /// </summary>
    public static bool LooksLikeEdit(string title, string path) =>
        EditName().IsMatch(title) || EditName().IsMatch(Path.GetFileNameWithoutExtension(path));

    [GeneratedRegex(
        @"(?:[\(\[]|\s-\s)[^\)\]]*\b(?:dj[\s-]*edit|extended|club\s+(?:mix|edit|version)|intro|outro|dj\s+tool|mixshow|12\s*(?:""|inch|in\b)|long\s+version)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex EditName();

    /// <summary>
    /// What a DJ edit says it is, on the end of its title and file name, so it's clear where it came from.
    /// </summary>
    public const string SUFFIX = "(Cuepoint DJ Edit)";

    /// <summary>
    /// What a DJ edit of a song called <paramref name="title"/> is called: <see cref="SUFFIX"/> on the end, in place of
    /// a "(Radio Edit)", "(Original Mix)" or the like, which it no longer is.
    /// </summary>
    public static string EditTitle(string title) => $"{Version().Replace(title, "").TrimEnd()} {SUFFIX}";

    // A version on the end of a title, in brackets or after a dash, that a DJ edit takes the place of: the radio,
    // single, album, short or clean edit, mix or version, the original (mix or version), or just "(Edit)".
    [GeneratedRegex(
        @"\s*(?:[\(\[]\s*(?:original(?:\s+(?:mix|version))?|(?:radio|single|album|short|clean)\s*(?:edit|mix|version)|edit)\s*[\)\]]|\s-\s+(?:original(?:\s+(?:mix|version))?|(?:radio|single|album|short|clean)\s+(?:edit|mix|version)))\s*$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex Version();

    /// <summary>
    /// Where a DJ edit of the song at <paramref name="path"/> is kept: beside it, as a FLAC named from its file name as
    /// its title is, by <see cref="EditTitle"/>.
    /// </summary>
    public static string EditPath(string path) => Path.ChangeExtension(OldEditPath(path), ".flac");

    /// <summary>
    /// Where a DJ edit of the song at <paramref name="path"/> was kept before they were FLACs: beside it, as a WAV.
    /// </summary>
    public static string OldEditPath(string path) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", $"{EditTitle(Path.GetFileNameWithoutExtension(path))}.wav");

    /// <summary>
    /// Makes a DJ edit of <paramref name="song"/>, separated into <paramref name="stems"/>, its beats where
    /// <paramref name="grid"/> says. Returns why not instead, if there's nothing to add or nothing to make the beats
    /// from.
    /// </summary>
    public static (DjEditResult? Edit, string? Why) Make(PcmAudio song, SongStems stems, BeatGrid grid, DjEditOptions? options = null)
    {
        if (song.Channels != stems.Channels || song.Frames != stems.Frames)
            return (null, "its stems aren't the same length as it");
        var look = new Look(song, stems, grid);
        if (look.Why is { } why)
            return (null, why);
        if (look.HasIntro && look.HasOutro)
            return (null, $"it has {READY_BARS} bars of beats at each end already");

        Loop? intro = null, outro = null;
        if (!look.HasIntro && (intro = look.ChooseLoop(look.FirstPlaying, 1)) is null)
            return (null, "it hasn't a steady bar of drums to loop for an intro");
        if (!look.HasOutro && (outro = look.ChooseLoop(look.OutroBar - 1, -1)) is null)
            return (null, "it hasn't a steady bar of drums to loop for an outro");
        return (Render(look, intro, outro, (options ?? DjEditOptions.Default).Checked()), null);
    }

    /// <summary>
    /// A stretch of the song's drums to loop: <paramref name="Bars"/> bars from beat <paramref name="Beat"/>, played at
    /// <paramref name="Gain"/>, to be no louder than the song's drums where they meet; and what of the song from the
    /// same bars changes a phrase in.
    /// </summary>
    sealed record Loop(double Beat, int Bars, float Gain, Layer Layer);

    /// <summary>
    /// What changes a phrase into an end's beats: the song's bass or instruments, looped with the drums, or with
    /// neither to hand, a bar without the kick.
    /// </summary>
    enum Layer
    {
        Break,
        Bass,
        Instruments,
        BassAndInstruments,
    }

    /// <summary>
    /// Samples to loop, a stem or a stretch of one, starting from frame <paramref name="First"/> of the song.
    /// </summary>
    sealed record Source(float[] Samples, long First);

    /// <summary>
    /// How the song goes into its intro's beats: giving way to its own pickup, with a fill, or with a hit on its first
    /// downbeat.
    /// </summary>
    enum Entry
    {
        Gap,
        Fill,
        Hit,
    }

    // How faint an echo's last repeat is.
    const float ECHO_LAST = 0.03f;

    // Each echo loses more of its lows and highs than the one before, below and above these, in hertz.
    const double ECHO_LOW_HZ = 250;
    const double ECHO_HIGH_HZ = 7000;

    /// <summary>
    /// The song looked over bar by bar from its first downbeat: where it's audible, where its drums play, where it
    /// has vocals, and how its drums sound across each bar, for finding bars alike enough to loop.
    /// </summary>
    sealed class Look
    {
        public readonly PcmAudio Song;
        public readonly SongStems Stems;
        public readonly BeatGrid Grid;
        public readonly int Rate;
        public readonly int Channels;

        /// <summary>
        /// The song's first downbeat, as a beat number of the grid, where its phrases are counted from.
        /// </summary>
        public readonly double Downbeat;

        public readonly double FirstAudible;
        public readonly double LastAudible;

        public readonly int BarCount;
        readonly bool[] _playing = [];
        readonly bool[] _vocalsQuiet = [];
        readonly double[][] _profiles = [];
        readonly double[] _energy = [];
        readonly double[] _vocals = [];
        readonly double[] _bass = [];
        readonly double[] _others = [];
        readonly double[] _mix = [];
        readonly double _bassLevel;
        readonly double _othersLevel;
        public readonly double DrumLevel;
        readonly double _vocalLevel;

        // The drums of a usual bar with them playing.
        readonly double _fullBar;

        public readonly int FirstPlaying;
        public readonly int LastPlaying;

        /// <summary>
        /// The bar the song's beats stop at, where the outro's take over.
        /// </summary>
        public readonly int OutroBar;

        /// <summary>
        /// Whether the song fades out, so its fade gives way to the outro's beats.
        /// </summary>
        public readonly bool FadesOut;
        public readonly bool HasIntro;
        public readonly bool HasOutro;

        public string? Why { get; }

        public Look(PcmAudio song, SongStems stems, BeatGrid grid)
        {
            (Song, Stems, Grid, Rate, Channels) = (song, stems, grid, song.SampleRate, song.Channels);
            var duration = (double)song.Frames / Rate;

            (FirstAudible, LastAudible) = (Audible(song.Samples, Channels, Rate, fromEnd: false), Audible(song.Samples, Channels, Rate, fromEnd: true));
            if (LastAudible <= FirstAudible)
            {
                Why = "it's silent";
                return;
            }

            // The first downbeat once it's audible, a little before allowed, as a hit can lead its beat; one cut off
            // at the very start of the file doesn't count.
            var beat = Math.Ceiling(grid.BeatNumberAt(FirstAudible) - 0.25);
            while (!grid.IsBarStart(beat) || grid.TimeOfBeat(beat) < -0.1 * 60 / grid.BpmAt(0))
                beat++;
            Downbeat = beat;

            // A bar or two much quieter than the song, a riser or a sound leading in, come before its first downbeat,
            // as a pickup does, so its phrases are counted from where it really starts.
            var mixes = new List<double>();
            for (var bar = 0; grid.TimeOfBeat(BarBeat(bar + 1)) <= duration; bar++)
                mixes.Add(Rms(song.Samples, Channels, Frame(BarBeat(bar)), Frame(BarBeat(bar + 1))));
            var usual = Percentile([.. mixes], 0.5);
            var leadIn = 0;
            while (leadIn < MAX_LEAD_IN_BARS && leadIn < mixes.Count && mixes[leadIn] < LEAD_IN * usual)
                leadIn++;
            Downbeat += leadIn * BEATS_PER_BAR;

            while (grid.TimeOfBeat(BarBeat(BarCount + 1)) <= duration)
                BarCount++;
            if (BarCount < BARS)
            {
                Why = "it's too short";
                return;
            }

            // The drums, the kick and the rest apart, a sixteenth at a time.
            var (kick, rest) = Split(stems.Drums, Channels, Rate);
            _playing = new bool[BarCount];
            _vocalsQuiet = new bool[BarCount];
            _profiles = new double[BarCount][];
            _energy = new double[BarCount];
            _vocals = new double[BarCount];
            _bass = new double[BarCount];
            _others = new double[BarCount];
            _mix = new double[BarCount];
            var beats = new double[BarCount * BEATS_PER_BAR];
            for (var bar = 0; bar < BarCount; bar++)
            {
                var profile = new double[32];
                for (var sixteenth = 0; sixteenth < 16; sixteenth++)
                {
                    var (from, to) = (Frame(BarBeat(bar) + sixteenth / 4.0), Frame(BarBeat(bar) + (sixteenth + 1) / 4.0));
                    profile[sixteenth] = Rms(kick, 1, from, to);
                    profile[16 + sixteenth] = Rms(rest, 1, from, to);
                }
                _profiles[bar] = profile;
                for (var b = 0; b < BEATS_PER_BAR; b++)
                    beats[bar * BEATS_PER_BAR + b] = Rms(stems.Drums, Channels, Frame(BarBeat(bar) + b), Frame(BarBeat(bar) + b + 1));
                _energy[bar] = Rms(stems.Drums, Channels, Frame(BarBeat(bar)), Frame(BarBeat(bar + 1)));
                _vocals[bar] = Rms(stems.Vocals, Channels, Frame(BarBeat(bar)), Frame(BarBeat(bar + 1)));
                _bass[bar] = Rms(stems.Bass, Channels, Frame(BarBeat(bar)), Frame(BarBeat(bar + 1)));
                _others[bar] = OthersRms(Frame(BarBeat(bar)), Frame(BarBeat(bar + 1)));
                _mix[bar] = Rms(song.Samples, Channels, Frame(BarBeat(bar)), Frame(BarBeat(bar + 1)));
            }
            _bassLevel = Percentile(_bass, 0.7);
            _othersLevel = Percentile(_others, 0.7);

            DrumLevel = Percentile(beats, 0.7);
            _vocalLevel = Percentile(_vocals, 0.8);
            if (DrumLevel < 1e-3)
            {
                Why = "it has no drums to make beats from";
                return;
            }
            for (var bar = 0; bar < BarCount; bar++)
            {
                _playing[bar] = Enumerable.Range(0, BEATS_PER_BAR).All(b => beats[bar * BEATS_PER_BAR + b] >= DRUMS_PLAYING * DrumLevel);
                _vocalsQuiet[bar] = _vocals[bar] <= Math.Max(VOCALS_QUIET * _vocalLevel, 1e-3);
            }
            if (_playing.Count(playing => playing) < BarCount / 5)
            {
                Why = "it hasn't a steady beat";
                return;
            }
            FirstPlaying = Array.IndexOf(_playing, true);
            LastPlaying = Array.LastIndexOf(_playing, true);

            // A song that fades out has its drums dying away over its last bars rather than stopping: the outro's
            // beats take over where the fade starts, on the four-bar line before it, rather than once they're faint.
            OutroBar = LastPlaying + 1;
            _fullBar = Percentile(Enumerable.Range(0, BarCount).Where(bar => _playing[bar]).Select(bar => _energy[bar]).ToArray(), 0.5);
            var lastFull = Array.FindLastIndex(_energy, LastPlaying, energy => energy >= FULL_DRUMS * _fullBar);
            if (lastFull >= 0 && LastPlaying - lastFull >= 2 && Enumerable.Range(lastFull + 1, LastPlaying - lastFull).All(bar => _energy[bar] <= _energy[bar - 1] * 1.1))
            {
                FadesOut = true;
                OutroBar = Math.Max(FirstPlaying + 1, (lastFull + 1) / 4 * 4);
            }
            else
            {
                // A song that carries on past its drums, with an ending of its own, has it heard: the outro's beats
                // come in on the phrase line (or failing that the four-bar line) where it's dying away, overlapping no
                // more than its last bar or two.
                var usualBar = Percentile(_mix, 0.5);
                var over = BarCount;
                while (over > OutroBar && _mix[over - 1] < DYING_AWAY * usualBar)
                    over--;
                if (over - OutroBar > 1)
                {
                    var phrase = over / PHRASE_BARS * PHRASE_BARS;
                    OutroBar = Math.Max(OutroBar, phrase >= over - 2 ? phrase : over / 4 * 4);
                }
            }

            // Beats to mix over already: eight bars of drums without vocals from its start, give or take a bar or two
            // of something else first; or to its end, with no more than a few bars of something else after.
            HasIntro = FirstPlaying <= 2 && Run(FirstPlaying, 1) >= READY_BARS;
            HasOutro = Run(LastPlaying, -1) >= READY_BARS && LastAudible <= Grid.TimeOfBeat(BarBeat(LastPlaying + 1 + 4));
        }

        // How loud the song's instruments are from one frame to another: what's left of it once its vocals, drums and
        // bass are taken out.
        double OthersRms(long fromFrame, long toFrame)
        {
            var (from, to) = (fromFrame * Channels, Math.Min(toFrame * Channels, Song.Samples.Length));
            if (to <= from)
                return 0;
            var sum = 0.0;
            for (var i = from; i < to; i++)
            {
                var other = Song.Samples[i] - Stems.Vocals[i] - Stems.Drums[i] - Stems.Bass[i];
                sum += other * other;
            }
            return Math.Sqrt(sum / (to - from));
        }

        // How many bars in a row from bar `from`, going `step` at a time, have drums playing and no vocals.
        int Run(int from, int step)
        {
            var count = 0;
            for (var bar = from; bar >= 0 && bar < BarCount && _playing[bar] && _vocalsQuiet[bar]; bar += step)
                count++;
            return count;
        }

        /// <summary>
        /// The beat number bar <paramref name="bar"/> starts on, counting from the first downbeat.
        /// </summary>
        public double BarBeat(int bar) => Downbeat + bar * BEATS_PER_BAR;

        public double Time(double beat) => Grid.TimeOfBeat(beat);

        long Frame(double beat) => Math.Clamp((long)Math.Round(Grid.TimeOfBeat(beat) * Rate), 0, Song.Frames);

        /// <summary>
        /// Whether the song has drums playing in bar <paramref name="bar"/>.
        /// </summary>
        public bool Playing(int bar) => bar >= 0 && bar < BarCount && _playing[bar];

        /// <summary>
        /// The bars to loop, near bar <paramref name="near"/>, where the beats meet the song: four bars alike, or
        /// failing that two, or failing that the steadiest bar there is, each with drums playing in the bar before and
        /// after, so the seams cross over cleanly, and at full strength, so a quiet stretch or a fade isn't looped. Full
        /// strength is against the drums of the <see cref="NEAR_BARS"/> bars from <paramref name="near"/>, going
        /// <paramref name="step"/> at a time, and the loop's turned down to them if it's louder. Bars with vocals, which
        /// bleed into the drums, are passed over where there are others; bars with bass, to bring in with the drums,
        /// and bars starting on a four-bar line, where a bass line's likelier to loop round, are chosen over others.
        /// </summary>
        public Loop? ChooseLoop(int near, int step)
        {
            var nearby = Enumerable.Range(0, NEAR_BARS).Select(n => near + n * step).Where(Playing).Select(bar => _energy[bar]).ToArray();
            var level = nearby.Length > 0 ? Percentile(nearby, 0.5) : _fullBar;
            foreach (var bars in new[] { 4, 2, 1 })
            {
                (int Start, double Score)? best = null;
                for (var start = 1; start + bars < BarCount; start++)
                {
                    if (!Enumerable.Range(start - 1, bars + 2).All(Playing) || !Enumerable.Range(start, bars).All(bar => _energy[bar] >= FULL_DRUMS * level))
                        continue;
                    // Each bar like the next, and the bar after like the first, as it'll be heard looping round.
                    var alike = Enumerable.Range(start, bars).Select(bar => Similarity(bar, bar + 1)).Append(Similarity(start + bars, start)).Min();
                    if (alike < SAME_GROOVE && bars > 1)
                        continue;
                    var vocals = Enumerable.Range(start, bars).Max(bar => _vocalLevel > 1e-6 ? Math.Min(1, _vocals[bar] / _vocalLevel) : 0);
                    var bass = _bassLevel > 1e-6 ? Math.Min(1, Enumerable.Range(start, bars).Average(bar => _bass[bar]) / _bassLevel) : 0;
                    var score = alike - 0.3 * vocals + 0.1 * bass + (start % 4 == 0 ? 0.05 : 0) - 0.004 * Math.Abs(start - near);
                    if (best is null || score > best.Value.Score)
                        best = (start, score);
                }
                if (best is { } found)
                {
                    var loudness = Enumerable.Range(found.Start, bars).Average(bar => _energy[bar]);
                    return new Loop(BarBeat(found.Start), bars, (float)Math.Clamp(level / loudness, 0.5, 1), ChooseLayer(found.Start, bars));
                }
            }
            return null;
        }

        /// <summary>
        /// What changes a phrase into the beats looped from <paramref name="bars"/> bars at bar
        /// <paramref name="start"/>: their bass if they have it, or else their instruments, or else a break.
        /// </summary>
        Layer ChooseLayer(int start, int bars)
        {
            var range = Enumerable.Range(start, bars).ToArray();
            if (_bassLevel > 1e-3 && range.Average(bar => _bass[bar]) >= LAYER_PRESENT * _bassLevel)
                return Layer.Bass;
            if (_othersLevel > 1e-3 && range.Average(bar => _others[bar]) >= LAYER_PRESENT * _othersLevel)
                return Layer.Instruments;
            return Layer.Break;
        }

        /// <summary>
        /// What's looped with the drums of <paramref name="loop"/> for its change: the song's bass stem; its
        /// instruments, around the loop's bars; or for a break, its drums there without the kick.
        /// </summary>
        public Source LayerSource(Loop loop, Layer layer)
        {
            if (layer == Layer.Bass)
                return new Source(Stems.Bass, 0);
            var (from, to) = (Frame(loop.Beat - BEATS_PER_BAR), Frame(loop.Beat + (loop.Bars + 1) * BEATS_PER_BAR));
            var samples = new float[(to - from) * Channels];
            if (layer is Layer.Instruments or Layer.BassAndInstruments)
            {
                // The song less its vocals and drums, and its bass too unless it's wanted.
                var bass = layer == Layer.BassAndInstruments ? 0 : 1;
                for (var i = 0; i < samples.Length; i++)
                {
                    var at = from * Channels + i;
                    samples[i] = Song.Samples[at] - Stems.Vocals[at] - Stems.Drums[at] - bass * Stems.Bass[at];
                }
                return new Source(samples, from);
            }

            // The kick taken out: the drums above it, through a fourth-order Butterworth high-pass (two biquads, from
            // the Audio EQ Cookbook), channel by channel.
            var w = 2 * Math.PI * KICK_HZ / Rate;
            var alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
            var a0 = 1 + alpha;
            var (b0, b1, b2) = ((1 + Math.Cos(w)) / 2 / a0, -(1 + Math.Cos(w)) / a0, (1 + Math.Cos(w)) / 2 / a0);
            var (a1, a2) = (-2 * Math.Cos(w) / a0, (1 - alpha) / a0);
            for (var c = 0; c < Channels; c++)
            {
                for (var frame = 0; frame < to - from; frame++)
                    samples[frame * Channels + c] = Stems.Drums[(from + frame) * Channels + c];
                for (var pass = 0; pass < 2; pass++)
                {
                    double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
                    for (var frame = 0; frame < to - from; frame++)
                    {
                        var x = (double)samples[frame * Channels + c];
                        var y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                        (x2, x1, y2, y1) = (x1, x, y1, y);
                        samples[frame * Channels + c] = (float)y;
                    }
                }
            }
            return new Source(samples, from);
        }

        double Similarity(int a, int b)
        {
            var ratio = _energy[b] > 0 ? _energy[a] / _energy[b] : 0;
            if (ratio is < 0.6 or > 1.6)
                return 0;
            double dot = 0, aa = 0, bb = 0;
            for (var i = 0; i < 32; i++)
                (dot, aa, bb) = (dot + _profiles[a][i] * _profiles[b][i], aa + _profiles[a][i] * _profiles[a][i], bb + _profiles[b][i] * _profiles[b][i]);
            return aa > 0 && bb > 0 ? dot / Math.Sqrt(aa * bb) : 0;
        }

        /// <summary>
        /// How the song should come in after the intro's beats: if it has a drum fill before its first downbeat, the
        /// beats leave a gap for it from where it starts (to the eighth note), so it takes over from them; otherwise
        /// they carry on under any pickup it has (a voice, say, which they'd otherwise stop dead for), and if it comes
        /// straight in on its drums, a fill rolls into it, or if it starts without drums, they land on its first
        /// downbeat with a hit.
        /// </summary>
        public (Entry Entry, double GapFrom) ChooseEntry()
        {
            var downbeat = Time(Downbeat);
            var leadIn = LeadInStart();
            if (leadIn < downbeat && Rms(Stems.Drums, Channels, (long)(FirstAudible * Rate), (long)(downbeat * Rate)) >= DRUMS_PLAYING * DrumLevel)
                return (Entry.Gap, leadIn);
            return (Playing(0) ? Entry.Fill : Entry.Hit, downbeat);
        }

        /// <summary>
        /// Where the song's first heard, if that's before its first downbeat (a pickup, or a lead-in, up to a bar of
        /// it), moved back to the eighth note; its first downbeat, if not.
        /// </summary>
        public double LeadInStart()
        {
            var downbeat = Time(Downbeat);
            var eighth = 30 / Grid.BpmAt(downbeat);
            return FirstAudible < downbeat - eighth / 2
                ? Math.Max(downbeat - BEATS_PER_BAR * 2 * eighth, downbeat - Math.Ceiling((downbeat - FirstAudible) / eighth - 0.25) * eighth)
                : downbeat;
        }
    }

    /// <summary>
    /// The edit: the intro's beats, the song moved along to follow them, and the outro's after it.
    /// </summary>
    static DjEditResult Render(Look look, Loop? intro, Loop? outro, DjEditOptions options)
    {
        var (song, grid, rate, channels) = (look.Song, look.Grid, look.Rate, look.Channels);
        var downbeat = look.Time(look.Downbeat);
        var (introBars, outroBars) = (options.IntroBars, options.OutroBars);

        // How far along the song moves: the intro's bars before its first downbeat, to the nearest frame. A song
        // that's silent for longer than that before its first downbeat loses the start of its silence.
        var introBeat = 60 / grid.BpmAt(downbeat);
        var shift = intro is null ? 0 : (long)Math.Round((introBars * BEATS_PER_BAR * introBeat - downbeat) * rate);
        var offset = (double)shift / rate;

        // Where the outro's beats start and finish, in the song's time: from the bar its drums stop at, for its bars
        // or more, to the end of a phrase, or of half a phrase if the whole one would be more than four over.
        var outroBar = look.OutroBar;
        var endBar = outroBar;
        double outroBeat = 0, outroStart = 0, outroEnd = 0, tail = 0;
        if (outro is not null)
        {
            endBar = (outroBar + outroBars + PHRASE_BARS - 1) / PHRASE_BARS * PHRASE_BARS;
            if (endBar - outroBar > outroBars + 4)
                endBar = (outroBar + outroBars + 3) / 4 * 4;
            outroStart = look.Time(look.BarBeat(outroBar));
            outroBeat = 60 / grid.BpmAt(outroStart - 0.001);
            outroEnd = outroStart + (endBar - outroBar) * BEATS_PER_BAR * outroBeat;
            // A last hit, or echoes, ring on past the end of the phrase.
            tail = options.OutroEnd switch
            {
                DjEditTransition.Auto => outroBeat,
                DjEditTransition.Hit or DjEditTransition.Echo => options.OutroEndBeats * outroBeat,
                _ => 0.01,
            };
        }

        var frames = outro is not null ? (long)Math.Ceiling((outroEnd + offset + tail) * rate) : song.Frames + shift;
        var output = new float[frames * channels];

        // The song, faded out over the outro's last two bars if it's still going then, or over its first two in
        // place of a fade-out of its own.
        var fadeStart = outro is null ? double.MaxValue : look.FadesOut ? outroStart : outroEnd - 2 * BEATS_PER_BAR * outroBeat;
        var (fadeFrom, fadeTo) = (fadeStart * rate, (fadeStart + 2 * BEATS_PER_BAR * outroBeat) * rate);
        for (long frame = Math.Max(0, -shift); frame < song.Frames; frame++)
        {
            var to = frame + shift;
            if (to >= frames || frame >= fadeTo)
                break;
            var gain = frame <= fadeFrom ? 1f : (float)((fadeTo - frame) / (fadeTo - fadeFrom));
            for (var c = 0; c < channels; c++)
                output[to * channels + c] = song.Samples[frame * channels + c] * gain;
        }

        var summary = new List<string>();
        double? introEnd = null;
        if (intro is not null)
        {
            introEnd = downbeat + offset;
            summary.Add(RenderIntro(output, look, intro, options, introEnd.Value, introBeat, offset));
            FadeIn(output, channels, rate);
        }

        double? outroAt = null;
        if (outro is not null)
        {
            outroAt = outroStart + offset;
            summary.Add(RenderOutro(output, look, outro, options, outroAt.Value, outroEnd + offset, outroBeat, endBar - outroBar));
        }

        // Cued to the intro's first beat, at the very start; or without one, to where the song's first heard, moved
        // onto the nearest beat if it's within half of one, as a deck cues a song it's given.
        var cue = 0.0;
        if (intro is null)
        {
            var heard = look.FirstAudible;
            var beat = grid.TimeOfBeat(Math.Round(grid.BeatNumberAt(heard)));
            cue = beat >= 0 && Math.Abs(beat - heard) <= 30 / grid.BpmAt(heard) ? beat : heard;
        }

        var audio = new PcmAudio(output, channels, rate);
        var editGrid = EditGrid(look, offset, intro is not null ? introBars : null, outro is not null ? (outroStart, 60 / outroBeat) : null, (double)frames / rate);
        return new DjEditResult(audio, editGrid, offset, introEnd, outroAt, string.Join("; ", summary), cue < 0.005 ? 0 : cue);
    }

    /// <summary>
    /// What's brought in with the beats of <paramref name="loop"/> for its change, as <paramref name="change"/> asks:
    /// for Auto, what the loop's bars have; null for nothing.
    /// </summary>
    static Layer? ChangeLayer(DjEditChange change, Loop loop) =>
        change switch
        {
            DjEditChange.Auto => loop.Layer,
            DjEditChange.Bass => Layer.Bass,
            DjEditChange.Instruments => Layer.Instruments,
            DjEditChange.BassAndInstruments => Layer.BassAndInstruments,
            DjEditChange.Break => Layer.Break,
            _ => null,
        };

    static string Changing(Layer? layer, int bars, bool intro) =>
        layer switch
        {
            Layer.Bass => intro ? $"its bass coming in after {bars}" : $"its bass dropping out for the last {bars}",
            Layer.Instruments => intro ? $"its instruments coming in after {bars}" : $"its instruments dropping out for the last {bars}",
            Layer.BassAndInstruments => intro ? $"its bass and instruments coming in after {bars}" : $"its bass and instruments dropping out for the last {bars}",
            Layer.Break => intro ? $"the kick dropping out for bar {bars}" : $"the kick dropping out for a bar before the last {bars}",
            _ => "beats alone",
        };

    static string Span(double seconds, double beat)
    {
        var beats = (int)Math.Round(seconds / beat);
        return beats % BEATS_PER_BAR == 0 ? (beats == BEATS_PER_BAR ? "a bar" : $"{beats / BEATS_PER_BAR} bars") : beats == 1 ? "a beat" : $"{beats} beats";
    }

    /// <summary>
    /// The intro's beats, laid over the edit: <paramref name="end"/> is the song's first downbeat in it. Returns what
    /// was done, to say.
    /// </summary>
    static string RenderIntro(float[] output, Look look, Loop intro, DjEditOptions options, double end, double beat, double offset)
    {
        var (grid, rate, channels) = (look.Grid, look.Rate, look.Channels);
        var bars = options.IntroBars;
        var bar = BEATS_PER_BAR * beat;
        var start = end - bars * bar;
        var half = bars / 2;
        var change = start + half * bar;
        var drums = new Source(look.Stems.Drums, 0);
        var kind = ChangeLayer(options.IntroChange, intro);
        var layer = kind is { } k ? look.LayerSource(intro, k) : null;
        var changing = Changing(kind, half, intro: true);
        var gain = intro.Gain;
        var loopStart = grid.TimeOfBeat(intro.Beat);

        // The drums all through, but for a gap, and a break, if that's the change, in the bar before it; or what's
        // brought in, from the change on.
        void LayBeats(float[] into, double to, Func<double, double> open)
        {
            if (kind == Layer.Break)
            {
                Lay(into, channels, rate, drums, grid, intro, start, to, beat, time => open(time) * (1 - Within(time, change - bar, change)));
                Lay(into, channels, rate, layer!, grid, intro, start, to, beat, time => open(time) * Within(time, change - bar, change));
            }
            else
            {
                Lay(into, channels, rate, drums, grid, intro, start, to, beat, open);
                if (layer is not null)
                    Lay(into, channels, rate, layer, grid, intro, start, to, beat, time => open(time) * Within(time, change, double.MaxValue));
            }
        }

        if (options.IntroEnd == DjEditTransition.Auto)
        {
            var (entry, gapFrom) = look.ChooseEntry();
            var gapStart = entry switch
            {
                Entry.Gap => gapFrom + offset,
                Entry.Fill => end - beat,
                _ => end,
            };
            LayBeats(output, end, time => 1 - Within(time, gapStart, double.MaxValue));
            switch (entry)
            {
                case Entry.Fill:
                    // The loop's backbeat, the snare or clap, on each sixteenth of the last beat, getting louder.
                    var backbeat = grid.TimeOfBeat(intro.Beat + 1);
                    for (var i = 0; i < 4; i++)
                        Hit(output, channels, rate, drums, backbeat, end - beat + i * beat / 4, beat / 4, gain * (0.55f + 0.15f * i), decay: false);
                    return $"{bars} bars of beats before it, {changing}, with a fill into its first beat";
                case Entry.Gap:
                    return $"{bars} bars of beats before it, {changing}, leaving a gap for its pickup";
                default:
                    // The drums, and what came in with them, die away from the song's first downbeat rather than
                    // stopping dead on it.
                    Hit(output, channels, rate, drums, loopStart, end, beat, gain, decay: true);
                    if (layer is not null && kind != Layer.Break)
                        Hit(output, channels, rate, layer, loopStart, end, beat, gain, decay: true);
                    return $"{bars} bars of beats before it, {changing}, landing on its first beat";
            }
        }

        // The way out as chosen, over the stretch before the song's first downbeat, or after it, under the song's
        // start: the beats stop at its end (cut, faded or filtered away), or at its start (a hit or echoes ringing on
        // over it). A hit lands on the downbeat, whichever.
        var length = options.IntroEndBeats * beat;
        var transition = options.IntroEnd;
        var (from, to) = transition == DjEditTransition.Hit || options.IntroEndAcrossSong ? (end, end + length) : (end - length, end);
        var stop = transition is DjEditTransition.Hit or DjEditTransition.Echo ? from : to;
        var beats = new float[(long)Math.Ceiling((to + length + bar) * rate) * channels];
        LayBeats(beats, stop, _ => 1);
        string ending;
        switch (transition)
        {
            case DjEditTransition.Fade:
                Shape(beats, channels, rate, from, to, comingIn: false, filter: false);
                ending = $"fading out over {Span(length, beat)}";
                break;
            case DjEditTransition.Filter:
                Shape(beats, channels, rate, from, to, comingIn: false, filter: true);
                ending = $"filtering out over {Span(length, beat)}";
                break;
            case DjEditTransition.Echo:
                Echo(beats, channels, rate, from, options.EchoBeats * beat, length);
                ending = $"echoing out over {Span(length, beat)}";
                break;
            case DjEditTransition.Hit:
                Hit(beats, channels, rate, drums, loopStart, from, length, gain, decay: true);
                if (layer is not null && kind != Layer.Break)
                    Hit(beats, channels, rate, layer, loopStart, from, length, gain, decay: true);
                ending = $"landing on its first beat and dying away over {Span(length, beat)}";
                break;
            default:
                ending = "stopping";
                break;
        }
        Mix(output, beats, 0, channels);
        var where = transition == DjEditTransition.Hit ? "" : options.IntroEndAcrossSong ? " into its start" : " before it starts";
        return $"{bars} bars of beats before it, {changing}, {ending}{where}";
    }

    /// <summary>
    /// The outro's beats, laid over the edit from <paramref name="start"/>, where the song's beats stop, to
    /// <paramref name="end"/>, the end of the phrase, <paramref name="bars"/> bars on. Returns what was done, to say.
    /// </summary>
    static string RenderOutro(float[] output, Look look, Loop outro, DjEditOptions options, double start, double end, double beat, int bars)
    {
        var (grid, rate, channels) = (look.Grid, look.Rate, look.Channels);
        var bar = BEATS_PER_BAR * beat;
        var half = options.OutroBars / 2;
        var change = end - half * bar;
        var drums = new Source(look.Stems.Drums, 0);
        var kind = ChangeLayer(options.OutroChange, outro);
        var layer = kind is { } k ? look.LayerSource(outro, k) : null;
        var changing = Changing(kind, half, intro: false);

        // Taking over from the song: straight away (cut), or faded or filtered in, over the stretch after the song's
        // beats stop, or before, under its last bars.
        var coming = options.OutroStart is DjEditTransition.Fade or DjEditTransition.Filter;
        var inLength = options.OutroStartBeats * beat;
        var (inFrom, inTo) = !coming ? (start, start) : options.OutroStartUnderSong ? (start - inLength, start) : (start, start + inLength);
        // Laid from a bar line, so the loop's bars fall where the song's do.
        var layFrom = start - Math.Ceiling((start - inFrom) / bar - 1e-9) * bar;

        // Going out: faded or filtered away over the end of the phrase, or stopped at it, with a hit or echoes ringing
        // on after it.
        var going = options.OutroEnd == DjEditTransition.Auto ? DjEditTransition.Hit : options.OutroEnd;
        var outLength = options.OutroEnd == DjEditTransition.Auto ? beat : options.OutroEndBeats * beat;
        var origin = Math.Max(0, layFrom - bar);
        var beats = new float[(long)Math.Ceiling((end + outLength + bar - origin) * rate) * channels];
        var (from, to) = (layFrom - origin, end - origin);
        double Fading(double time) => look.FadesOut ? Math.Min(1, (time + origin - start) / (2 * bar)) : 1;
        if (kind == Layer.Break)
        {
            Lay(beats, channels, rate, drums, grid, outro, from, to, beat, time => 1 - Within(time + origin, change - bar, change));
            Lay(beats, channels, rate, layer!, grid, outro, from, to, beat, time => Within(time + origin, change - bar, change));
        }
        else
        {
            Lay(beats, channels, rate, drums, grid, outro, from, to, beat, _ => 1);
            if (layer is not null)
                Lay(beats, channels, rate, layer, grid, outro, from, to, beat, time => Within(time + origin, double.MinValue, change) * Fading(time));
        }

        // Nothing before the beats come in, then in as chosen.
        Shape(beats, channels, rate, inFrom - origin, inTo - origin, comingIn: true, filter: options.OutroStart == DjEditTransition.Filter);

        string ending;
        switch (going)
        {
            case DjEditTransition.Fade:
                Shape(beats, channels, rate, end - outLength - origin, end - origin, comingIn: false, filter: false);
                ending = $"fading out over the last {Span(outLength, beat)}";
                break;
            case DjEditTransition.Filter:
                Shape(beats, channels, rate, end - outLength - origin, end - origin, comingIn: false, filter: true);
                ending = $"filtering out over the last {Span(outLength, beat)}";
                break;
            case DjEditTransition.Echo:
                Echo(beats, channels, rate, end - origin, options.EchoBeats * beat, outLength);
                ending = $"echoing out over {Span(outLength, beat)} after it";
                break;
            case DjEditTransition.Cut:
                ending = "stopping at the end of the phrase";
                break;
            default:
                // A last hit on the one after the phrase.
                Hit(beats, channels, rate, drums, grid.TimeOfBeat(outro.Beat), end - origin, outLength, outro.Gain, decay: true);
                ending = options.OutroEnd == DjEditTransition.Auto ? "to the end of the phrase" : $"landing on the end of the phrase and dying away over {Span(outLength, beat)}";
                break;
        }
        Mix(output, beats, (long)Math.Round(origin * rate), channels);

        var takingOver = !coming ? "" : $", {(options.OutroStart == DjEditTransition.Filter ? "filtering" : "fading")} in over {Span(inLength, beat)}{(options.OutroStartUnderSong ? " under its end" : "")}";
        return $"{bars} bars after it{(look.FadesOut ? " in place of its fade-out" : "")}{takingOver}, {changing}, {ending}";
    }

    // Adds what's in `beats` into `output`, from frame `at` of it.
    static void Mix(float[] output, float[] beats, long at, int channels)
    {
        var from = at * channels;
        for (long i = 0; i < beats.Length && from + i < output.Length; i++)
        {
            if (from + i >= 0)
                output[from + i] += beats[i];
        }
    }

    /// <summary>
    /// Brings <paramref name="beats"/> in from nothing, or with <paramref name="comingIn"/> false, takes them away to
    /// nothing, from <paramref name="from"/> to <paramref name="to"/> seconds: by their level, on a curve even to the
    /// ear, or with <paramref name="filter"/>, a low-pass sweeping between the whole sound and the deepest bass, its
    /// level following over the quarter nearest nothing. Silent before it coming in, and after it going out.
    /// </summary>
    static void Shape(float[] beats, int channels, int rate, double from, double to, bool comingIn, bool filter)
    {
        var frames = beats.Length / channels;
        var (first, last) = (from * rate, to * rate);
        var state = new double[channels * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var along = last > first ? Math.Clamp((frame - first) / (last - first), 0, 1) : frame >= first ? 1 : 0;
            var presence = comingIn ? along : 1 - along;
            if (presence >= 1 && !filter)
                continue;
            double gain;
            var cutoff = 0.0;
            if (filter)
            {
                cutoff = FILTER_LOW_HZ * Math.Pow(FILTER_HIGH_HZ / FILTER_LOW_HZ, presence);
                gain = Math.Min(1, presence / 0.25);
            }
            else
                gain = presence <= 0 ? 0 : Math.Pow(10, -FADE_DB * (1 - presence) / 20);
            var a = filter ? 1 - Math.Exp(-2 * Math.PI * cutoff / rate) : 1;
            for (var c = 0; c < channels; c++)
            {
                var x = (double)beats[frame * channels + c];
                if (filter && presence < 1)
                {
                    state[c * 2] += a * (x - state[c * 2]);
                    state[c * 2 + 1] += a * (state[c * 2] - state[c * 2 + 1]);
                    x = state[c * 2 + 1];
                }
                else if (filter)
                    state[c * 2] = state[c * 2 + 1] = x;
                beats[frame * channels + c] = (float)(x * gain);
            }
        }
    }

    // A fade's far end, this far down, in decibels, before nothing; and a filter sweep's ends, in hertz.
    const double FADE_DB = 48;
    const double FILTER_LOW_HZ = 40;
    const double FILTER_HIGH_HZ = 18000;

    /// <summary>
    /// An echo of the last <paramref name="delay"/> seconds of <paramref name="beats"/> before <paramref name="at"/>,
    /// repeating every <paramref name="delay"/> seconds from there for <paramref name="length"/> seconds, each repeat
    /// quieter than the one before, down to a whisper by the end, and thinner, through one more pass of a high-pass at
    /// <see cref="ECHO_LOW_HZ"/> and a low-pass at <see cref="ECHO_HIGH_HZ"/>.
    /// </summary>
    static void Echo(float[] beats, int channels, int rate, double at, double delay, double length)
    {
        var size = (int)Math.Round(delay * rate);
        var start = (int)Math.Round(at * rate) - size;
        if (size <= 0 || start < 0 || (start + size) * channels > beats.Length)
            return;
        var echo = beats.AsSpan(start * channels, size * channels).ToArray();

        // Eased in and out over a moment, so its repeats don't click where they join.
        var ease = Math.Max(1, (int)(GAP_SECONDS * rate));
        for (var i = 0; i < Math.Min(ease, size); i++)
        {
            for (var c = 0; c < channels; c++)
            {
                echo[i * channels + c] *= (float)i / ease;
                echo[(size - 1 - i) * channels + c] *= (float)i / ease;
            }
        }

        var repeats = Math.Max(1, (int)Math.Round(length / delay));
        var feedback = (float)Math.Clamp(Math.Pow(ECHO_LAST, 1.0 / repeats), 0.3, 0.85);
        var low = (float)(1 - Math.Exp(-2 * Math.PI * ECHO_LOW_HZ / rate));
        var high = (float)(1 - Math.Exp(-2 * Math.PI * ECHO_HIGH_HZ / rate));
        var frames = beats.Length / channels;
        var gain = 1f;
        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            gain *= feedback;
            for (var c = 0; c < channels; c++)
            {
                float below = 0, smooth = 0;
                for (var i = 0; i < size; i++)
                {
                    var x = echo[i * channels + c];
                    below += low * (x - below);
                    smooth += high * (x - below - smooth);
                    echo[i * channels + c] = smooth;
                }
            }
            var to = start + repeat * size;
            for (var i = 0; i < size && to + i < frames; i++)
            {
                for (var c = 0; c < channels; c++)
                    beats[(to + i) * channels + c] += echo[i * channels + c] * gain;
            }
        }
    }

    /// <summary>
    /// How far <paramref name="time"/> is within <paramref name="from"/> to <paramref name="to"/>, in seconds: 1
    /// inside, 0 outside, and in between across the moment either side of each end, so nothing clicks.
    /// </summary>
    static double Within(double time, double from, double to) =>
        time < from - GAP_SECONDS || time >= to ? 0
        : time < from ? (time - (from - GAP_SECONDS)) / GAP_SECONDS
        : time >= to - GAP_SECONDS ? (to - time) / GAP_SECONDS
        : 1;

    /// <summary>
    /// The edit's beat grid: the song's, moved along with it, the intro's beats at the tempo of its first downbeat,
    /// and the outro's at the tempo its drums stop at; its bars starting where the song's do. The intro's marker is on
    /// its first beat, at the very start, so a grid kept to a few decimal places still has its first beat there rather
    /// than a moment after, counted back from the song.
    /// </summary>
    static BeatGrid EditGrid(Look look, double offset, int? introBars, (double At, double Bpm)? outro, double duration)
    {
        var grid = look.Grid;
        var downbeat = look.Time(look.Downbeat);
        var markers = new List<GridMarker>();
        var intro = introBars is not null;
        if (introBars is { } bars)
        {
            var bpm = grid.BpmAt(downbeat);
            var start = downbeat + offset - bars * BEATS_PER_BAR * 60 / bpm;
            markers.Add(new GridMarker(Math.Abs(start) < 1e-3 ? 0 : start, bpm));
        }
        foreach (var marker in grid.Markers)
        {
            if (intro && marker.Time <= downbeat + 1e-3 || outro is { } o && marker.Time >= o.At - 1e-3)
                continue;
            markers.Add(marker with { Time = marker.Time + offset });
        }
        if (outro is { } last && (markers.Count == 0 || Math.Abs(markers[^1].Bpm - last.Bpm) > 1e-9))
            markers.Add(new GridMarker(last.At + offset, last.Bpm));
        if (markers.Count == 0)
            markers.Add(new GridMarker(downbeat + offset, grid.BpmAt(downbeat)));
        markers.Sort((a, b) => a.Time.CompareTo(b.Time));

        var moved = new BeatGrid(markers, duration);
        return moved.WithDownbeat((int)Math.Round(moved.BeatNumberAt(downbeat + offset)));
    }

    /// <summary>
    /// Lays <paramref name="loop"/>, from <paramref name="source"/>, over the edit from <paramref name="from"/> to
    /// <paramref name="to"/> seconds, again and again, at beats <paramref name="beat"/> seconds long, stretched to fit
    /// if the song's beats there are another length. Each repeat crosses over into the next just ahead of its downbeat,
    /// and the whole is at <paramref name="gain"/> for the time through the edit.
    /// </summary>
    static void Lay(float[] output, int channels, int rate, Source source, BeatGrid grid, Loop loop, double from, double to, double beat, Func<double, double> gain)
    {
        var sourceStart = grid.TimeOfBeat(loop.Beat) * rate;
        var sourceLength = grid.TimeOfBeat(loop.Beat + loop.Bars * BEATS_PER_BAR) * rate - sourceStart;
        var length = loop.Bars * BEATS_PER_BAR * beat * rate;
        var ratio = sourceLength / length;
        var exact = Math.Abs(ratio - 1) < 1e-4;
        var seam = SEAM_SECONDS * rate;
        var (start, end) = (from * rate, to * rate);
        var frames = output.Length / channels;
        var samples = source.Samples;
        var sourceFrames = samples.Length / channels;

        for (var repeat = start; repeat < end - 1; repeat += length)
        {
            for (var frame = (long)Math.Ceiling(repeat - seam); frame < Math.Min(repeat + length, end); frame++)
            {
                if (frame < 0 || frame >= frames)
                    continue;
                var along = frame - repeat;
                var level = Math.Min(1, Math.Min((along + seam) / seam, Math.Min((repeat + length - frame) / seam, (end - frame) / seam)));
                level *= loop.Gain * gain((double)frame / rate);
                if (level <= 0)
                    continue;
                var at = sourceStart + along * ratio - source.First;
                if (exact)
                {
                    var read = (long)Math.Round(at);
                    if (read < 0 || read >= sourceFrames)
                        continue;
                    for (var c = 0; c < channels; c++)
                        output[frame * channels + c] += (float)(samples[read * channels + c] * level);
                }
                else
                {
                    var read = (long)Math.Floor(at);
                    if (read < 0 || read + 1 >= sourceFrames)
                        continue;
                    var t = (float)(at - read);
                    for (var c = 0; c < channels; c++)
                        output[frame * channels + c] += (float)((samples[read * channels + c] * (1 - t) + samples[(read + 1) * channels + c] * t) * level);
                }
            }
        }
    }

    /// <summary>
    /// One hit from <paramref name="source"/>, <paramref name="length"/> seconds of it from <paramref name="from"/>
    /// seconds through the song, at <paramref name="at"/> seconds through the edit: dying away across it, or cut off
    /// cleanly at its end.
    /// </summary>
    static void Hit(float[] output, int channels, int rate, Source source, double from, double at, double length, float gain, bool decay)
    {
        var (first, to, frames) = ((long)Math.Round(from * rate) - source.First, (long)Math.Round(at * rate), (long)(length * rate));
        var samples = source.Samples;
        var fade = Math.Max(1, (long)(GAP_SECONDS * rate));
        for (long i = 0; i < frames; i++)
        {
            var (s, o) = (first + i, to + i);
            if (s < 0 || s >= samples.Length / channels || o < 0 || o >= output.Length / channels)
                continue;
            var x = (float)i / frames;
            var level = gain * (decay ? (1 - x) * (1 - x) : Math.Min(1f, (float)(frames - i) / fade));
            for (var c = 0; c < channels; c++)
                output[o * channels + c] += samples[s * channels + c] * level;
        }
    }

    // The edit's very first frames brought up from nothing, so a hit cut into at the start doesn't click.
    static void FadeIn(float[] output, int channels, int rate)
    {
        var frames = Math.Min(output.Length / channels, rate / 500);
        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < channels; c++)
                output[i * channels + c] *= (float)i / frames;
        }
    }

    /// <summary>
    /// When the song's first audible, or with <paramref name="fromEnd"/>, last audible, in seconds.
    /// </summary>
    static double Audible(float[] samples, int channels, int rate, bool fromEnd)
    {
        var frames = samples.Length / channels;
        for (var n = 0; n < frames; n++)
        {
            var frame = fromEnd ? frames - 1 - n : n;
            for (var c = 0; c < channels; c++)
            {
                if (MathF.Abs(samples[frame * channels + c]) > AUDIBLE)
                    return (double)frame / rate;
            }
        }
        return fromEnd ? 0 : (double)frames / rate;
    }

    /// <summary>
    /// The drums in mono, split into the kick, below <see cref="KICK_HZ"/>, and the rest.
    /// </summary>
    static (float[] Kick, float[] Others) Split(float[] drums, int channels, int rate)
    {
        var frames = drums.Length / channels;
        var (kick, rest) = (new float[frames], new float[frames]);
        var a = (float)(1 - Math.Exp(-2 * Math.PI * KICK_HZ / rate));
        float low1 = 0, low2 = 0;
        for (var i = 0; i < frames; i++)
        {
            var mono = 0f;
            for (var c = 0; c < channels; c++)
                mono += drums[i * channels + c];
            mono /= channels;
            low1 += a * (mono - low1);
            low2 += a * (low1 - low2);
            (kick[i], rest[i]) = (low2, mono - low2);
        }
        return (kick, rest);
    }

    static double Rms(float[] samples, int channels, long fromFrame, long toFrame)
    {
        var (from, to) = (fromFrame * channels, Math.Min(toFrame * channels, samples.Length));
        if (to <= from)
            return 0;
        var sum = 0.0;
        for (var i = from; i < to; i++)
            sum += samples[i] * samples[i];
        return Math.Sqrt(sum / (to - from));
    }

    static double Percentile(double[] values, double fraction)
    {
        var sorted = values.Where(value => value > 1e-6).Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * fraction))];
    }
}
