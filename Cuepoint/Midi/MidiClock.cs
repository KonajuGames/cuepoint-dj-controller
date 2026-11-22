using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cuepoint.Midi;

/// <summary>
/// What MIDI clock follows, as it was at <paramref name="At"/> (in seconds, on <see cref="MidiClockOutput.Now"/>'s
/// clock): whether the music's playing, its tempo, and where it was in its beats (beat 0 on a downbeat, as a beat grid
/// counts them), if it has a beat grid.
/// </summary>
sealed record ClockSource(bool Playing, double Bpm, double? Beat, double At)
{
    public static readonly ClockSource Silent = new(false, MidiClock.DEFAULT_BPM, null, 0);
}

/// <summary>
/// MIDI clock: 24 pulses a beat at the tempo of the music, kept in step with its beats, for drum machines, effects
/// units and lighting desks to follow. Start is sent on the first downbeat after the music starts, so what follows
/// counts its bars from there, and Stop when it stops; the pulses go on between, so what's following keeps the tempo.
/// A jump in the music (a seek, a loop, another deck taking the lead) is caught up with at once, never with a burst of
/// pulses, which would read as a sudden tempo. Has no thread or timer of its own: <see cref="Tick"/> is called every
/// millisecond or so.
/// </summary>
sealed class MidiClock
{
    public const int PULSES_PER_BEAT = 24;
    public const double DEFAULT_BPM = 120;
    const int BEATS_PER_BAR = 4;

    public static readonly MidiMessage Pulse = new(0xF8, 0, 0);
    public static readonly MidiMessage Start = new(0xFA, 0, 0);
    public static readonly MidiMessage Stop = new(0xFC, 0, 0);

    /// <summary>
    /// How far behind or ahead of the beat, in pulses, it can be and only be nudged back into step; further and it's
    /// put straight there.
    /// </summary>
    const double SNAP_PULSES = PULSES_PER_BEAT / 4.0;

    /// <summary>
    /// How much of the way back into step it's nudged each second: small, so the pulses stay steady.
    /// </summary>
    const double NUDGE_PER_SECOND = 2;

    double _phase;
    long _sent;
    double _last = double.NaN;
    bool _playing;
    bool _startOnBar;

    /// <summary>
    /// Whether Start has been sent and not Stop since.
    /// </summary>
    public bool Running => _playing && !_startOnBar;

    /// <summary>
    /// Sends what's due by <paramref name="now"/> (in seconds), following <paramref name="source"/>.
    /// </summary>
    public void Tick(ClockSource source, double now, Action<MidiMessage> send)
    {
        var bpm = source.Bpm is > 0 and < 1000 ? source.Bpm : DEFAULT_BPM;
        var pulsesPerSecond = bpm / 60 * PULSES_PER_BEAT;
        double? target = source.Beat is { } beat ? (beat + (now - source.At) * bpm / 60) * PULSES_PER_BEAT : null;

        if (double.IsNaN(_last))
        {
            _phase = target ?? 0;
            // A pulse right on where it starts is sent now.
            _sent = (long)Math.Ceiling(_phase) - 1;
        }
        else
        {
            var elapsed = Math.Clamp(now - _last, 0, 0.25);
            _phase += elapsed * pulsesPerSecond;
            if (source.Playing && target is { } there)
            {
                var off = there - _phase;
                if (Math.Abs(off) > SNAP_PULSES)
                {
                    // Somewhere else in the music: straight there, sending nothing for the gap.
                    _phase = there;
                    _sent = (long)Math.Ceiling(_phase) - 1;
                }
                else
                    _phase += off * Math.Min(1, NUDGE_PER_SECOND * elapsed);
            }
        }
        _last = now;

        if (source.Playing && !_playing)
        {
            _playing = true;
            _startOnBar = true;
        }
        else if (!source.Playing && _playing)
        {
            _playing = false;
            if (!_startOnBar)
                send(Stop);
            _startOnBar = false;
        }

        var due = (long)Math.Floor(_phase);
        // Never more than a pulse or two at once; if it's somehow further behind, it starts again from where it is.
        if (due - _sent > 2)
            _sent = due - 1;
        while (_sent < due)
        {
            _sent++;
            if (_startOnBar && (source.Beat is null || IsOnBar(_sent)))
            {
                send(Start);
                _startOnBar = false;
            }
            send(Pulse);
        }
    }

    static bool IsOnBar(long pulse) => pulse % (PULSES_PER_BEAT * BEATS_PER_BAR) == 0;
}

/// <summary>
/// Sends <see cref="MidiClock"/> out of a MIDI output on a thread of its own, every millisecond, following whatever
/// <see cref="Source"/> was last set to (once a frame, from the UI thread).
/// </summary>
sealed partial class MidiClockOutput : IDisposable
{
    readonly IMidiOutput _output;
    readonly bool _owned;
    readonly Thread _thread;
    volatile ClockSource _source = ClockSource.Silent;
    volatile bool _stopping;

    /// <summary>
    /// The output's name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The time on the clock's own clock, in seconds, for <see cref="ClockSource.At"/>.
    /// </summary>
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>
    /// What it follows, from any thread.
    /// </summary>
    public ClockSource Source
    {
        get => _source;
        set => _source = value;
    }

    /// <param name="owned">Whether it's to close the output when it's done, rather than leave that to whatever
    /// opened it (a controller that's sending its lights through it too).</param>
    public MidiClockOutput(string name, IMidiOutput output, bool owned)
    {
        Name = name;
        _output = output;
        _owned = owned;
        _thread = new Thread(Run) { IsBackground = true, Name = "MIDI clock", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    void Run()
    {
        // Sleeps of a millisecond, not the system's usual sixteen, so each pulse goes on time.
        var fine = OperatingSystem.IsWindows() && timeBeginPeriod(1) == 0;
        try
        {
            var clock = new MidiClock();
            while (!_stopping)
            {
                clock.Tick(_source, Now, _output.Send);
                Thread.Sleep(1);
            }
            if (clock.Running)
                _output.Send(MidiClock.Stop);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"MIDI clock: {e}");
        }
        finally
        {
            if (fine)
                timeEndPeriod(1);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(500);
        if (_owned)
            _output.Dispose();
    }

    [LibraryImport("winmm.dll")]
    [SupportedOSPlatform("windows")]
    private static partial uint timeBeginPeriod(uint period);

    [LibraryImport("winmm.dll")]
    [SupportedOSPlatform("windows")]
    private static partial uint timeEndPeriod(uint period);
}

/// <summary>
/// The MIDI output chosen for clock, if one is, following the deck leading the mix. A controller's own output, open
/// already for its lights, is shared rather than opened again.
/// </summary>
sealed class MidiClockPort(IMidiPorts ports, MidiDevices? devices) : IDisposable
{
    MidiClockOutput? _output;

    /// <summary>
    /// The output clock's going out of; null for none.
    /// </summary>
    public string? Name => _output?.Name;

    /// <summary>
    /// The outputs there are to choose from.
    /// </summary>
    public IReadOnlyList<string> Outputs() => ports.OutputNames().Where(name => name != "").Distinct().ToList();

    /// <summary>
    /// Sends clock out of the output named <paramref name="name"/>, or none if null. Returns false if it couldn't be
    /// opened, as it's not there or something else has it.
    /// </summary>
    public bool Use(string? name)
    {
        _output?.Dispose();
        _output = null;
        if (name is null)
            return true;
        if (devices?.Devices.FirstOrDefault(device => device.Name == name && device.Output is not null) is { Output: { } shared })
        {
            _output = new MidiClockOutput(name, shared, owned: false);
            return true;
        }
        var index = ports.OutputNames().ToList().IndexOf(name);
        if (index < 0 || ports.OpenOutput(index) is not { } output)
            return false;
        _output = new MidiClockOutput(name, output, owned: true);
        return true;
    }

    /// <summary>
    /// Has the clock follow the deck leading the mix: the one playing that isn't following the other with sync lock,
    /// or else one loaded. Once a frame, after the decks update.
    /// </summary>
    public void Follow(IReadOnlyList<Audio.Deck> decks)
    {
        if (_output is null)
            return;
        var leader = Audio.MasterEffectsTiming.Leader(decks);
        _output.Source = new ClockSource(
            leader?.IsPlaying ?? false,
            leader?.Bpm ?? MidiClock.DEFAULT_BPM,
            leader is { IsLoaded: true, Analysis.Grid: { } grid } ? grid.BeatNumberAt(leader.Position.TotalSeconds) : null,
            MidiClockOutput.Now
        );
    }

    public void Dispose()
    {
        _output?.Dispose();
        _output = null;
    }
}
