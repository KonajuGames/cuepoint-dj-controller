using Cuepoint.Midi;

namespace Cuepoint.Tests.Midi;

public sealed class MidiClockTests
{
    readonly MidiClock _clock = new();
    readonly List<(double At, MidiMessage Message)> _sent = [];
    double _now;

    // The clock ticked every millisecond for a while, following what the music does at each moment.
    void Run(double seconds, Func<double, ClockSource> source)
    {
        var end = _now + seconds;
        for (; _now < end; _now += 0.001)
        {
            var at = _now;
            _clock.Tick(source(_now), _now, message => _sent.Add((at, message)));
        }
    }

    // Playing at a steady tempo from a beat at a time, as a deck does.
    static Func<double, ClockSource> Playing(double bpm, double beat = 0, double from = 0) =>
        now => new ClockSource(true, bpm, beat + (now - from) * bpm / 60, now);

    IEnumerable<double> Pulses => _sent.Where(sent => sent.Message == MidiClock.Pulse).Select(sent => sent.At);

    [Fact]
    public void It_sends_twenty_four_pulses_a_beat_evenly()
    {
        Run(4, Playing(125));

        // 125 BPM: 50 pulses a second, one every 20 ms.
        var pulses = Pulses.ToList();
        Assert.InRange(pulses.Count, 199, 201);
        var gaps = pulses.Zip(pulses.Skip(1), (a, b) => b - a).ToList();
        Assert.All(gaps, gap => Assert.InRange(gap, 0.019, 0.0211));
    }

    [Fact]
    public void Start_is_sent_once_on_the_first_downbeat_then_a_pulse()
    {
        // Half way through a bar: the next downbeat's two beats away.
        Run(3, Playing(120, beat: 2));

        var start = _sent.FindIndex(sent => sent.Message == MidiClock.Start);
        Assert.True(start >= 0);
        Assert.Single(_sent, sent => sent.Message == MidiClock.Start);
        Assert.InRange(_sent[start].At, 0.99, 1.01);
        Assert.Equal(MidiClock.Pulse, _sent[start + 1].Message);
        Assert.True(_clock.Running);
    }

    [Fact]
    public void Music_starting_on_a_downbeat_starts_the_clock_at_once()
    {
        Run(0.1, Playing(120, beat: 8));

        Assert.Equal(MidiClock.Start, _sent[0].Message);
        Assert.Equal(MidiClock.Pulse, _sent[1].Message);
    }

    sealed class Output : IMidiOutput
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<MidiMessage> Sent = new();
        public bool Closed;

        public void Send(MidiMessage message) => Sent.Enqueue(message);

        public void Dispose() => Closed = true;
    }

    sealed class Ports(Output output) : IMidiPorts
    {
        public IReadOnlyList<string> InputNames() => [];

        public IReadOnlyList<string> OutputNames() => ["", "loopMIDI Port"];

        public IDisposable? OpenInput(int index, Action<MidiMessage> received) => null;

        public IMidiOutput? OpenOutput(int index) => index == 1 ? output : null;
    }

    [Fact]
    public void The_port_chosen_gets_clock_until_it_s_closed()
    {
        var output = new Output();
        using var port = new MidiClockPort(new Ports(output), null);

        Assert.Equal(["loopMIDI Port"], port.Outputs());
        Assert.False(port.Use("Not there"));
        Assert.True(port.Use("loopMIDI Port"));
        Assert.Equal("loopMIDI Port", port.Name);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (output.Sent.Count < 5 && watch.Elapsed.TotalSeconds < 2)
            Thread.Sleep(10);
        Assert.Contains(MidiClock.Pulse, output.Sent);

        port.Use(null);
        Assert.True(output.Closed);
        Assert.Null(port.Name);
    }

    [Fact]
    public void Without_a_beat_grid_it_starts_at_once()
    {
        Run(0.1, now => new ClockSource(true, 120, null, now));

        Assert.Equal(MidiClock.Start, _sent[0].Message);
    }

    [Fact]
    public void Stop_is_sent_when_the_music_stops_and_the_pulses_go_on_at_its_tempo()
    {
        Run(2, Playing(120));
        Run(2, now => new ClockSource(false, 120, 4, now));

        Assert.Single(_sent, sent => sent.Message == MidiClock.Stop);
        Assert.False(_clock.Running);
        Assert.InRange(Pulses.Count(at => at >= 2), 95, 97);
    }

    [Fact]
    public void A_jump_in_the_music_is_caught_up_with_at_once_without_a_burst()
    {
        Run(2, Playing(120));
        // Seeked on by a beat and a half.
        Run(2, Playing(120, beat: 5.5, from: 2));

        var pulses = Pulses.ToList();
        var gaps = pulses.Zip(pulses.Skip(1), (a, b) => b - a).ToList();
        Assert.DoesNotContain(gaps, gap => gap < 0.015);
        // Back in step: a pulse on each beat of the music.
        var lastBeat = pulses.Last(at => at > 3.5);
        var beat = 5.5 + (lastBeat - 2) * 2;
        Assert.InRange(beat * 24 - Math.Round(beat * 24), -0.2, 0.2);
    }

    [Fact]
    public void It_follows_a_change_of_tempo()
    {
        Run(2, Playing(120));
        Run(2, Playing(140, beat: 4, from: 2));

        Assert.InRange(Pulses.Count(at => at >= 3), 55, 57);
    }

    [Fact]
    public void A_tempo_it_can_t_use_reads_as_120()
    {
        Run(1, now => new ClockSource(false, 0, null, now));

        Assert.InRange(Pulses.Count(), 47, 49);
    }
}
