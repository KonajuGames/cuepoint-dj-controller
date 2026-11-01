namespace Brutality.Audio;

/// <summary>
/// What the deck's platter is doing, when it's taken over from the track: held under a hand, spinning free after
/// one's let go, or spinning back on its own.
/// </summary>
enum PlatterMode
{
    Off,

    /// <summary>The jog wheel's touched, and the song follows it, back or forward.</summary>
    Held,

    /// <summary>Let go: it eases from however it was moving back to the deck's speed (or to a stop, paused).</summary>
    Free,

    /// <summary>Spinning back, slowing to a stop, then paused there.</summary>
    Backspin,
}

/// <summary>
/// The deck as a record on a platter, for scratching and backspins: while the platter's taken over, the deck's sound
/// is rendered here from the decoded song at whatever speed the platter turns, backwards too, instead of from the
/// track. A touched jog wheel holds the record and its turns move it (<see cref="JogTouch"/>, <see cref="Scratch"/>);
/// let go, it carries on and eases back to the deck's speed. <see cref="Backspin"/> spins it back to a stop. Once
/// the platter's back to the deck's own speed, or stopped, the track takes over again from there.
/// </summary>
sealed partial class Deck
{
    /// <summary>
    /// How much of the song a jog wheel step moves a held record, in seconds: a turn of a DDJ-SB3's wheel is about
    /// 720 steps, and a turn of a record at 33⅓ is 1.8 seconds.
    /// </summary>
    public const double SCRATCH_SECONDS_PER_STEP = 0.0025;

    /// <summary>
    /// How fast a backspin starts, in times normal speed, backwards.
    /// </summary>
    public const double BACKSPIN_SPEED = 4;

    // How closely a held record follows the hand, and how quickly a let-go one comes back to speed, in seconds.
    const double HOLD_SECONDS = 0.012;
    const double SMOOTH_SECONDS = 0.004;
    const double RELEASE_SECONDS = 0.08;

    // Shared with the audio thread: the mode, where the record's hand has put it (frames of the song), where it's
    // playing (frames), how fast it's going (times normal, negative backwards), and a backspin's slowing per second.
    int _platterMode;
    double _platterTarget;
    double _platterHead;
    double _platterVelocity;
    double _backspinSlowing;

    // Whether the deck's playing underneath: where it's going back to once the platter lets go. Set on the UI thread.
    bool _motor;

    // Whether the jog wheel's touched, and whether the platter's done and the track should take over again.
    bool _touched;
    bool _platterSettled;

    // The track's own gain (automatic gain and trim), which rendered sound has to have put on it.
    float _inputGain = 1;

    public PlatterMode Platter => (PlatterMode)Volatile.Read(ref _platterMode);

    public bool IsPlatterActive => Platter != PlatterMode.Off;

    /// <summary>
    /// The jog wheel's top touched or let go. Touching holds the record once it's turned (<see cref="Scratch"/>);
    /// letting go sets it free.
    /// </summary>
    public void JogTouch(bool touched)
    {
        _touched = touched;
        if (!touched && Platter == PlatterMode.Held)
            Volatile.Write(ref _platterMode, (int)PlatterMode.Free);
    }

    /// <summary>
    /// A touched jog wheel turned by <paramref name="steps"/>: the record moves that far, the song following it, back
    /// or forward. Untouched, or during a backspin, it does nothing.
    /// </summary>
    public void Scratch(int steps)
    {
        if (!_touched || Platter == PlatterMode.Backspin || steps == 0)
            return;
        if (Platter != PlatterMode.Held && !TakePlatter(PlatterMode.Held, IsPlaying ? _speed : 0))
            return;
        Volatile.Write(ref _platterTarget, Volatile.Read(ref _platterTarget) + steps * SCRATCH_SECONDS_PER_STEP * SampleRate);
    }

    /// <summary>
    /// Spins the record back, from <see cref="BACKSPIN_SPEED"/> to a stop over <paramref name="seconds"/>, then leaves
    /// it paused there: the end of a song, as a transition.
    /// </summary>
    public void Backspin(double seconds)
    {
        if (!IsLoaded || seconds <= 0)
            return;
        if (!TakePlatter(PlatterMode.Backspin, -BACKSPIN_SPEED))
            return;
        _motor = false;
        Volatile.Write(ref _backspinSlowing, BACKSPIN_SPEED / seconds);
    }

    /// <summary>
    /// Takes the sound over from the track, from where it is, in <paramref name="mode"/>, moving at
    /// <paramref name="velocity"/>. The track's kept running, even paused, so the sound goes on being mixed.
    /// </summary>
    bool TakePlatter(PlatterMode mode, double velocity)
    {
        if (_pcm is null || !IsLoaded)
            return false;
        if (!IsPlatterActive)
        {
            ExitLoop();
            _previewing = false;
            _motor = IsPlaying;
            var at = (double)SongFrame;
            Volatile.Write(ref _platterHead, at);
            Volatile.Write(ref _platterTarget, at);
            Volatile.Write(ref _platterVelocity, velocity);
            Volatile.Write(ref _platterSettled, false);
            if (!_motor)
            {
                if (_track.Paused())
                    _track.Resume();
                else if (!_track.Playing())
                {
                    _track.Play(0);
                    _track.SetPlaybackPosition((long)at);
                }
            }
        }
        else
            Volatile.Write(ref _platterVelocity, velocity);
        Volatile.Write(ref _platterMode, (int)mode);
        return true;
    }

    /// <summary>
    /// Hands the sound back to the track once the platter's done, from where it got to. Call once a frame.
    /// </summary>
    void UpdatePlatter()
    {
        if (IsPlatterActive && Volatile.Read(ref _platterSettled))
            ReleasePlatter();
    }

    /// <summary>
    /// The track takes over again from where the record is: playing on, or paused there.
    /// </summary>
    void ReleasePlatter()
    {
        if (!IsPlatterActive)
            return;
        Volatile.Write(ref _platterMode, (int)PlatterMode.Off);
        if (_pcm is not { } pcm)
            return;
        var at = (long)Math.Clamp(Volatile.Read(ref _platterHead), 0, pcm.Frames - 1);
        _engine.Mixer.Lock();
        try
        {
            if (!_track.Playing() && !_track.Paused())
                _track.Play(0);
            _track.SetPlaybackPosition(at);
            if (_motor)
                _track.Resume();
            else
                _track.Pause();
        }
        finally
        {
            _engine.Mixer.Unlock();
        }
    }

    /// <summary>
    /// The block of sound from the platter, on the audio thread, in place of the track's: the song read at the
    /// record's position as it moves. Returns false, leaving the block alone, when the platter's off.
    /// </summary>
    unsafe bool RenderPlatter(float* samples, int count, int channels, int rate)
    {
        var mode = (PlatterMode)Volatile.Read(ref _platterMode);
        if (mode == PlatterMode.Off || _pcm is not { } pcm || channels <= 0 || rate <= 0)
            return false;

        var source = pcm.Samples;
        var sourceChannels = pcm.Channels;
        var lastFrame = pcm.Frames - 1;
        var framesPerFrame = (double)pcm.SampleRate / rate;
        var gain = Volatile.Read(ref _inputGain);
        var head = Volatile.Read(ref _platterHead);
        var velocity = Volatile.Read(ref _platterVelocity);
        var motorSpeed = _motor ? Volatile.Read(ref _speed) : 0;
        var settled = false;

        // The speed's worked out a few milliseconds at a time, the sound read sample by sample in between.
        const int CHUNK = 32;
        var frames = count / channels;
        var chunkSeconds = (double)CHUNK / rate;
        for (var start = 0; start < frames; start += CHUNK)
        {
            switch (mode)
            {
                case PlatterMode.Held:
                    var wanted = (Volatile.Read(ref _platterTarget) - head) / pcm.SampleRate / HOLD_SECONDS;
                    velocity += (Math.Clamp(wanted, -16, 16) - velocity) * (1 - Math.Exp(-chunkSeconds / SMOOTH_SECONDS));
                    break;
                case PlatterMode.Free:
                    velocity += (motorSpeed - velocity) * (1 - Math.Exp(-chunkSeconds / RELEASE_SECONDS));
                    if (Math.Abs(velocity - motorSpeed) < 0.01)
                        (velocity, settled) = (motorSpeed, true);
                    break;
                case PlatterMode.Backspin:
                    velocity += Volatile.Read(ref _backspinSlowing) * chunkSeconds;
                    if (velocity >= 0)
                        (velocity, settled) = (0, true);
                    break;
            }

            var end = Math.Min(frames, start + CHUNK);
            for (var f = start; f < end; f++)
            {
                // Between two of the song's frames, by how far; nothing before the start or past the end.
                var whole = (long)Math.Floor(head);
                var into = (float)(head - whole);
                for (var c = 0; c < channels; c++)
                {
                    var channel = Math.Min(c, sourceChannels - 1);
                    var a = whole >= 0 && whole <= lastFrame ? source[whole * sourceChannels + channel] : 0f;
                    var b = whole + 1 >= 0 && whole + 1 <= lastFrame ? source[(whole + 1) * sourceChannels + channel] : 0f;
                    samples[f * channels + c] = (a + (b - a) * into) * gain;
                }
                head = Math.Clamp(head + velocity * framesPerFrame, 0, lastFrame);
            }
        }

        Volatile.Write(ref _platterHead, head);
        Volatile.Write(ref _platterVelocity, velocity);
        if (settled)
            Volatile.Write(ref _platterSettled, true);
        return true;
    }
}
