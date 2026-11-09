using Brutality.Audio;
using Brutality.Audio.Analysis;

namespace Brutality.Music;

/// <summary>
/// What an auto-mix needs of a deck.
/// </summary>
interface IMixDeck
{
    string Name { get; }

    /// <summary>
    /// The crossfader's position for this deck alone: 0 or 1.
    /// </summary>
    float CrossfaderEnd { get; }

    string? FilePath { get; }
    bool IsLoading { get; }
    bool IsPlaying { get; }
    bool IsAudible { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    TrackAnalysis? Analysis { get; }
    TimeSpan CuePoint { get; }
    double Tempo { get; set; }
    float Volume { get; set; }
    bool KeyLocked { get; set; }
    float Low { get; set; }
    float Mid { get; set; }
    float High { get; set; }
    float Filter { get; set; }

    /// <summary>
    /// How loud a stretch of the song on it is.
    /// </summary>
    BandLevels LevelsBetween(double start, double end);

    /// <summary>
    /// Turns its echo on for an echo-out, keeping what it was set to, or off again, putting that back.
    /// </summary>
    void SetEcho(bool on);

    void Play();
    void Pause();
    void Seek(TimeSpan position);

    /// <summary>
    /// Matches its tempo and beat to the other deck's once, the beat <paramref name="beatShift"/> of a beat from it.
    /// </summary>
    bool Sync(double beatShift);

    /// <summary>
    /// Keeps it on the other deck's beat without a jump; call once a frame.
    /// </summary>
    void FollowBeat(double beatShift);

    void StopFollowing();

    /// <summary>
    /// A song couldn't be loaded: its path.
    /// </summary>
    event Action<string>? LoadFailed;
}

/// <summary>
/// A real deck, for an auto-mix.
/// </summary>
sealed class MixDeck : IMixDeck
{
    public Deck Deck { get; }
    (DeckEffect Kind, bool On, float Level, double Beats)? _keptEffect;

    public MixDeck(Deck deck, float crossfaderEnd)
    {
        Deck = deck;
        CrossfaderEnd = crossfaderEnd;
        deck.LoadFailed += (_, path, _) => LoadFailed?.Invoke(path);
    }

    public string Name => Deck.Name;
    public float CrossfaderEnd { get; }
    public string? FilePath => Deck.FilePath;
    public bool IsLoading => Deck.LoadingName is not null;
    public bool IsPlaying => Deck.IsPlaying;
    public bool IsAudible => Deck.IsAudible;
    public TimeSpan Position => Deck.Position;
    public TimeSpan Duration => Deck.Duration;
    public TrackAnalysis? Analysis => Deck.Analysis;
    public TimeSpan CuePoint => Deck.CuePoint;

    public double Tempo
    {
        get => Deck.Tempo;
        set => Deck.Tempo = value;
    }

    public float Volume
    {
        get => Deck.Volume;
        set => Deck.Volume = value;
    }

    public bool KeyLocked
    {
        get => Deck.KeyLocked;
        set => Deck.KeyLocked = value;
    }

    public float Low
    {
        get => Deck.Equalizer.Low;
        set => Deck.Equalizer.Low = value;
    }

    public float Mid
    {
        get => Deck.Equalizer.Mid;
        set => Deck.Equalizer.Mid = value;
    }

    public float High
    {
        get => Deck.Equalizer.High;
        set => Deck.Equalizer.High = value;
    }

    public float Filter
    {
        get => Deck.Equalizer.Filter;
        set => Deck.Equalizer.Filter = value;
    }

    public BandLevels LevelsBetween(double start, double end)
    {
        if (Deck.Detail is not { } detail)
            return default;
        var (peak, bass, mids, treble) = detail.Between(start, end);
        return new BandLevels(peak, bass, mids, treble);
    }

    public void SetEcho(bool on)
    {
        var effects = Deck.Effects;
        if (on && _keptEffect is null)
        {
            _keptEffect = (effects.Kind, effects.On, effects.Level, effects.BeatsOf(DeckEffect.Echo));
            effects.Kind = DeckEffect.Echo;
            effects.SetBeats(DeckEffect.Echo, 0.5);
            effects.Level = 0.8f;
            effects.On = true;
        }
        else if (!on && _keptEffect is { } kept)
        {
            effects.On = kept.On;
            effects.Kind = kept.Kind;
            effects.Level = kept.Level;
            effects.SetBeats(DeckEffect.Echo, kept.Beats);
            _keptEffect = null;
        }
    }

    public void Play() => Deck.Play();
    public void Pause() => Deck.Pause();
    public void Seek(TimeSpan position) => Deck.Seek(position);
    public bool Sync(double beatShift) => Deck.Sync(beatShift);
    public void FollowBeat(double beatShift) => Deck.FollowBeat(beatShift);
    public void StopFollowing() => Deck.StopFollowing();

    public event Action<string>? LoadFailed;
}

/// <summary>
/// Plays a list of songs by itself, on two decks: the first on the first deck, then each next one loaded onto the
/// other as soon as that's free, and at a good moment the mixer's worked by hand as a DJ would, from one to the other.
/// <see cref="TransitionPlanner"/> chooses the moment and the kind of transition. Anything moved by anyone else, by
/// mouse, keyboard or controller, stops it where it is, the music playing on: it's noticed by each control not being
/// where it was left. UI thread only; call <see cref="Update"/> once a frame.
/// </summary>
sealed class AutoMix
{
    // How long before the end of a song to settle for what's known, how much notice a transition needs, how long the
    // new song takes to come back to its own tempo, and how close to the end a stopped song counts as having finished.
    const double PLAN_DEADLINE_SECONDS = 25;
    const double NOTICE_SECONDS = 2;
    const int GLIDE_BARS = 16;
    const double NEAR_END_SECONDS = 1;

    // How far off a control or a song's position can be and still be as it was left, and how long after a song
    // starts before its position can be judged.
    const float TOLERANCE = 0.002f;
    const double SEEK_TOLERANCE = 0.3;
    const double SEEK_GRACE_SECONDS = 1;
    const double LONGEST_FRAME_SECONDS = 0.5;

    enum Phase
    {
        Stopped,
        LoadingFirst,
        Playing,
        Mixing,
    }

    readonly record struct LeftAs(float Volume, float Low, float Mid, float High, float Filter, double Tempo, string? File, bool Playing, double Position);

    readonly IReadOnlyList<IMixDeck> _decks;
    readonly Func<float> _getCrossfader;
    readonly Action<float> _setCrossfader;
    readonly Action<Song, IMixDeck> _load;
    readonly Dictionary<IMixDeck, LeftAs> _left = [];
    readonly Dictionary<IMixDeck, bool> _keyLocks = [];
    readonly Dictionary<IMixDeck, string> _loading = [];
    readonly Queue<string> _failed = [];
    readonly HashSet<IMixDeck> _resume = [];

    float _crossfaderLeft;
    Phase _phase;
    IMixDeck _current;
    IMixDeck _other;
    int _next = -1;
    int _nextToTry;
    bool _nextAsked;
    MixPlan? _plan;
    bool _incomingPlaying;
    bool _resynced;
    bool _currentEnded;
    double _sinceStart;
    double _played;
    double _glideLeft;
    double _glidePerSecond;

    public AutoMix(IReadOnlyList<IMixDeck> decks, Func<float> getCrossfader, Action<float> setCrossfader, Action<Song, IMixDeck> load)
    {
        _decks = decks;
        _getCrossfader = getCrossfader;
        _setCrossfader = setCrossfader;
        _load = load;
        _current = decks[0];
        _other = decks[1];
        foreach (var deck in decks)
            deck.LoadFailed += _failed.Enqueue;
    }

    /// <summary>
    /// What's being mixed, to show.
    /// </summary>
    public string Name { get; private set; } = "";

    public IReadOnlyList<Song> Songs { get; private set; } = [];

    /// <summary>
    /// Which of <see cref="Songs"/> is the main one playing; -1 before it's started.
    /// </summary>
    public int CurrentIndex { get; private set; } = -1;

    public bool IsRunning => _phase != Phase.Stopped;
    public bool IsPaused { get; private set; }

    /// <summary>
    /// What's happening, to show: the transition under way or coming up, or why it stopped.
    /// </summary>
    public string Status { get; private set; } = "";

    /// <summary>
    /// News for the status line.
    /// </summary>
    public event Action<string>? Log;

    /// <summary>
    /// How long it's been playing.
    /// </summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds(_played);

    /// <summary>
    /// How long the whole thing will have taken: what's played and what's left, knowing each transition's overlap once
    /// it's been planned.
    /// </summary>
    public TimeSpan Total
    {
        get
        {
            if (CurrentIndex < 0)
                return TimeSpan.FromSeconds(Songs.Sum(LengthOf));
            var rest = Songs.Skip(CurrentIndex + 1).Sum(LengthOf);
            var position = _current.Position.TotalSeconds;
            var left = Math.Max(0, (_plan?.EndAt ?? _current.Duration.TotalSeconds) - position);
            if (_plan is { } plan)
                rest -= Math.Min(rest, plan.IncomingFrom + (plan.EndAt - plan.IncomingAt));
            return TimeSpan.FromSeconds(_played + left + rest);
        }
    }

    static double LengthOf(Song song) => (song.Duration ?? TimeSpan.FromSeconds(song.Analysis?.Grid.Duration ?? 0)).TotalSeconds;

    /// <summary>
    /// Makes <paramref name="songs"/> what's to be mixed, from the top. Not while one's running.
    /// </summary>
    public void Open(string name, IReadOnlyList<Song> songs)
    {
        if (IsRunning)
            throw new InvalidOperationException("Auto-Mix is running");
        (Name, Songs, CurrentIndex, Status, _played) = (name, songs, -1, "", 0);
    }

    /// <summary>
    /// Starts from the top: the mixer set straight and the first song on the first deck. Returns why it can't, if it
    /// can't, leaving everything as it was.
    /// </summary>
    public string? Start()
    {
        if (IsRunning)
            return null;
        if (Songs.Count == 0)
            return "There are no songs to mix";
        if (_decks.FirstOrDefault(deck => deck.IsAudible) is { } playing)
            return $"Deck {playing.Name} is playing. Stop it or turn it down first";

        foreach (var deck in _decks)
        {
            deck.Pause();
            _keyLocks[deck] = deck.KeyLocked;
            deck.KeyLocked = true;
            deck.Volume = 1;
            deck.Tempo = 0;
            FlatEq(deck);
        }
        (_current, _other) = (_decks[0], _decks[1]);
        _setCrossfader(_current.CrossfaderEnd);
        (CurrentIndex, _next, _nextToTry, _nextAsked, _plan, _incomingPlaying, _resynced, _currentEnded, _played, _glideLeft, IsPaused) = (0, -1, 1, false, null, false, false, false, 0, 0, false);
        _failed.Clear();
        _loading.Clear();
        Status = "Loading the first song";
        _phase = Phase.LoadingFirst;
        LoadOnto(_current, Songs[0]);
        LeaveAsIs();
        return null;
    }

    /// <summary>
    /// Pauses both decks and the transition with them, or carries on.
    /// </summary>
    public void TogglePause()
    {
        if (!IsRunning)
            return;
        if (!IsPaused)
        {
            _resume.Clear();
            foreach (var deck in _decks.Where(deck => deck.IsPlaying))
            {
                _resume.Add(deck);
                deck.Pause();
            }
            IsPaused = true;
        }
        else
        {
            foreach (var deck in _resume)
                deck.Play();
            // Started again a moment apart, so the beats are lined up again.
            if (_incomingPlaying && _plan is { BeatMatched: true } plan)
                _other.Sync(plan.BeatShift);
            IsPaused = false;
            _sinceStart = 0;
        }
        LeaveAsIs();
    }

    /// <summary>
    /// Stops mixing where it is, whatever's playing playing on, saying <paramref name="why"/>.
    /// </summary>
    public void Stop(string why)
    {
        if (!IsRunning)
            return;
        _phase = Phase.Stopped;
        IsPaused = false;
        foreach (var deck in _decks)
        {
            deck.StopFollowing();
            deck.SetEcho(false);
            if (_keyLocks.TryGetValue(deck, out var locked))
                deck.KeyLocked = locked;
        }
        Status = why;
        Log?.Invoke($"Auto-Mix: {why}");
    }

    public void Update(double elapsed)
    {
        if (!IsRunning)
            return;
        if (TakenOver(elapsed))
        {
            Stop("You took over");
            return;
        }
        if (!IsPaused)
        {
            HandleFailures();
            if (IsRunning)
                Step(elapsed);
        }
        if (IsRunning)
            LeaveAsIs();
    }

    void Step(double elapsed)
    {
        _sinceStart += elapsed;
        if (_decks.Any(deck => deck.IsPlaying))
            _played += elapsed;
        switch (_phase)
        {
            case Phase.LoadingFirst:
                if (IsLoaded(_current, Songs[CurrentIndex]))
                {
                    _current.Play();
                    _sinceStart = 0;
                    _phase = Phase.Playing;
                    Status = "";
                }
                break;
            case Phase.Playing:
                StepPlaying(elapsed);
                break;
            case Phase.Mixing:
                StepMixing();
                break;
        }
    }

    bool IsLoaded(IMixDeck deck, Song song) => !deck.IsLoading && string.Equals(deck.FilePath, song.Path, StringComparison.OrdinalIgnoreCase);

    bool NextIsReady => _nextAsked && IsLoaded(_other, Songs[_next]);

    bool HasNext => _nextAsked || _nextToTry < Songs.Count;

    void LoadOnto(IMixDeck deck, Song song)
    {
        _loading[deck] = song.Path;
        _load(song, deck);
    }

    void StepPlaying(double elapsed)
    {
        var position = _current.Position.TotalSeconds;
        Glide(elapsed);

        if (!_nextAsked && _nextToTry < Songs.Count)
        {
            (_next, _nextAsked) = (_nextToTry, true);
            LoadOnto(_other, Songs[_next]);
        }

        if (_plan is null && NextIsReady && (_current.Analysis is not null && _other.Analysis is not null || _current.Duration.TotalSeconds - position <= PLAN_DEADLINE_SECONDS || _currentEnded))
            MakePlan(position);

        if (_currentEnded)
        {
            // It's run out with nothing lined up: straight on to the next, once there is one.
            if (NextIsReady)
                Cut();
            else if (!HasNext)
                Stop("Finished");
            return;
        }

        if (_plan is not { } plan)
            return;
        Status = $"Next: {plan.Description} in {Clock(Math.Max(0, plan.StartAt - position))}";

        StartIncomingIfDue(plan, position);

        // Once more just before it's heard, as the deck going out may have been gliding when it started.
        if (_incomingPlaying && plan.BeatMatched && !_resynced && position >= plan.StartAt - 60 / (_current.Analysis?.Grid.MainBpm ?? 120))
        {
            _other.Sync(plan.BeatShift);
            _resynced = true;
        }
        Follow(plan);

        if (position >= plan.StartAt)
        {
            _phase = Phase.Mixing;
            Status = plan.Description;
            if (TransitionPlanner.At(plan.Kind, 0).Echo)
                _current.SetEcho(true);
        }
    }

    /// <summary>
    /// Starts the new song when it's time: before the transition if its beats are to be lined up, partway through for
    /// an echo-out.
    /// </summary>
    void StartIncomingIfDue(MixPlan plan, double position)
    {
        if (_incomingPlaying || position < plan.IncomingAt)
            return;
        _glideLeft = 0;
        _other.Play();
        _incomingPlaying = true;
        _sinceStart = 0;
        if (plan.BeatMatched)
            _other.Sync(plan.BeatShift);
    }

    /// <summary>
    /// Holds the new song on the old one's beat, from when it starts until it's taken over.
    /// </summary>
    void Follow(MixPlan plan)
    {
        if (_incomingPlaying && plan.BeatMatched)
            _other.FollowBeat(plan.BeatShift);
    }

    void MakePlan(double position)
    {
        var plan = TransitionPlanner.Plan(Describe(_current), Describe(_other), position + NOTICE_SECONDS);
        _plan = plan;
        _other.Seek(TimeSpan.FromSeconds(plan.IncomingFrom));
        if (plan.BeatMatched)
            _other.Sync(plan.BeatShift);
        else
            _other.Tempo = 0;
        Apply(plan.Kind, 0);
        _setCrossfader(_current.CrossfaderEnd);
    }

    static MixSong Describe(IMixDeck deck) => new(deck.Analysis, deck.Duration.TotalSeconds, deck.CuePoint.TotalSeconds, deck.LevelsBetween);

    void StepMixing()
    {
        var plan = _plan!;
        var position = _current.Position.TotalSeconds;
        StartIncomingIfDue(plan, position);
        Follow(plan);
        var progress = (position - plan.StartAt) / (plan.EndAt - plan.StartAt);
        Status = $"{plan.Description}, {Math.Clamp(progress, 0, 1) * 100:0}%";
        Apply(plan.Kind, progress);
        if (progress >= 1 || _currentEnded)
            Finish();
    }

    void Apply(TransitionKind kind, double progress)
    {
        var moves = TransitionPlanner.At(kind, progress);
        _setCrossfader(_current.CrossfaderEnd + (_other.CrossfaderEnd - _current.CrossfaderEnd) * moves.Crossfade);
        (_current.Low, _current.Mid, _current.High, _current.Filter) = (moves.OutLow, moves.OutMid, moves.OutHigh, moves.OutFilter);
        (_other.Low, _other.Filter) = (moves.InLow, moves.InFilter);
    }

    /// <summary>
    /// The new song has taken over: the old one stopped and set straight for its next turn, the new one eased back to
    /// its own tempo.
    /// </summary>
    void Finish()
    {
        var old = _current;
        if (!_incomingPlaying)
            _other.Play();
        _other.StopFollowing();
        _sinceStart = 0;
        old.Pause();
        old.SetEcho(false);
        old.Tempo = 0;
        FlatEq(old);
        FlatEq(_other);
        _setCrossfader(_other.CrossfaderEnd);
        SwapDecks();
        if (_current.Analysis?.Grid is { } grid && Math.Abs(_current.Tempo) > 1e-4)
        {
            _glideLeft = GLIDE_BARS * BeatGrid.BEATS_PER_BAR * 60 / grid.MainBpm;
            _glidePerSecond = _current.Tempo / _glideLeft;
        }
    }

    /// <summary>
    /// Straight to the next song, without a transition, as the last ran out before one could be begun.
    /// </summary>
    void Cut()
    {
        _other.StopFollowing();
        _current.Pause();
        FlatEq(_current);
        FlatEq(_other);
        if (!_incomingPlaying)
            _other.Play();
        _setCrossfader(_other.CrossfaderEnd);
        _sinceStart = 0;
        SwapDecks();
    }

    void SwapDecks()
    {
        (_current, _other) = (_other, _current);
        CurrentIndex = _next;
        (_next, _nextToTry, _nextAsked, _plan, _incomingPlaying, _resynced, _currentEnded) = (-1, CurrentIndex + 1, false, null, false, false, false);
        _phase = Phase.Playing;
        Status = "";
        Log?.Invoke($"Auto-Mix: playing {Songs[CurrentIndex].Title} ({CurrentIndex + 1} of {Songs.Count})");
    }

    void Glide(double elapsed)
    {
        if (_glideLeft <= 0)
            return;
        var step = Math.Min(elapsed, _glideLeft);
        _glideLeft -= step;
        _current.Tempo = _glideLeft <= 0 ? 0 : _current.Tempo - _glidePerSecond * step;
    }

    /// <summary>
    /// A song that wouldn't load is passed over.
    /// </summary>
    void HandleFailures()
    {
        while (_failed.TryDequeue(out var path))
        {
            var which = _phase == Phase.LoadingFirst ? CurrentIndex : _next;
            if (which < 0 || !string.Equals(Songs[which].Path, path, StringComparison.OrdinalIgnoreCase))
                continue;
            Log?.Invoke($"Auto-Mix: passed over {Songs[which].Title}, which wouldn't load");
            if (_phase != Phase.LoadingFirst)
                (_next, _nextToTry, _nextAsked) = (-1, which + 1, false);
            else if (which + 1 < Songs.Count)
            {
                (CurrentIndex, _nextToTry) = (which + 1, which + 2);
                LoadOnto(_current, Songs[CurrentIndex]);
            }
            else
            {
                Stop("None of the songs would load");
                return;
            }
        }
    }

    /// <summary>
    /// Whether anything isn't as it was left at the end of the last frame, so someone else has moved it. Notes whether
    /// the main song has simply come to its end.
    /// </summary>
    bool TakenOver(double elapsed)
    {
        if (MathF.Abs(_getCrossfader() - _crossfaderLeft) > TOLERANCE)
            return true;
        foreach (var deck in _decks)
        {
            if (!_left.TryGetValue(deck, out var left))
                continue;
            if (
                MathF.Abs(deck.Volume - left.Volume) > TOLERANCE
                || MathF.Abs(deck.Low - left.Low) > TOLERANCE
                || MathF.Abs(deck.Mid - left.Mid) > TOLERANCE
                || MathF.Abs(deck.High - left.High) > TOLERANCE
                || MathF.Abs(deck.Filter - left.Filter) > TOLERANCE
                || Math.Abs(deck.Tempo - left.Tempo) > TOLERANCE
            )
                return true;

            var loadedOurs = _loading.TryGetValue(deck, out var ours) && string.Equals(deck.FilePath, ours, StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(deck.FilePath, left.File, StringComparison.OrdinalIgnoreCase) && !loadedOurs)
                return true;
            if (deck.IsPlaying && !left.Playing)
                return true;
            if (!deck.IsPlaying && left.Playing)
            {
                if (left.Position < deck.Duration.TotalSeconds - NEAR_END_SECONDS)
                    return true;
                if (deck == _current)
                    _currentEnded = true;
            }

            // Moved to somewhere else in the song: not as far on as the time gone by says.
            if (deck.IsPlaying && left.Playing && _sinceStart >= SEEK_GRACE_SECONDS && elapsed < LONGEST_FRAME_SECONDS)
            {
                var moved = deck.Position.TotalSeconds - left.Position;
                if (Math.Abs(moved - elapsed * (1 + deck.Tempo)) > SEEK_TOLERANCE)
                    return true;
            }
        }
        return false;
    }

    void LeaveAsIs()
    {
        _crossfaderLeft = _getCrossfader();
        foreach (var deck in _decks)
            _left[deck] = new LeftAs(deck.Volume, deck.Low, deck.Mid, deck.High, deck.Filter, deck.Tempo, deck.FilePath, deck.IsPlaying, deck.Position.TotalSeconds);
    }

    static void FlatEq(IMixDeck deck) => (deck.Low, deck.Mid, deck.High, deck.Filter) = (0, 0, 0, 0);

    static string Clock(double seconds) => $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";
}
