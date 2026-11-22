using Brutal.GlfwApi;
using Brutal.Numerics;
using Cue.Framework.Rendering;
using Cue.Framework.UI;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cuepoint.Audio;
using Cuepoint.Music;
using Cuepoint.Streaming;

namespace Cuepoint;

/// <summary>
/// Takes the README's screenshot from the running app, repeatably: run as <c>Cuepoint --capture &lt;folder&gt;
/// [--look Light|HighContrast]</c>. Nothing of the user's is read or touched: the app's folder is a scratch one for the
/// run (<see cref="UseScratch"/>), its music a dozen songs made up for it (a beat and a chord at a tempo and key each,
/// under made-up names), and the master volume's down, so nothing's heard. Once they're analysed, two are loaded and
/// played in step, and the frame the GPU drew is saved as a PNG: only the window's own picture, never the screen's.
/// Run as <c>Cuepoint --self-test [folder]</c>, on the same scratch folder and songs, it's a check before a gig: it
/// loads, plays, syncs, records, edits the recording's tracklist, streams to a pretend server of its own and panics,
/// says what passed and what didn't, writes that to <c>self-test.txt</c>, and quits, with an exit code of 1 if
/// anything failed.
/// </summary>
sealed class ScreenshotTool
{
    /// <summary>
    /// The window's size for the shot: a 1080p screen with the taskbar taken off, as the README's has always been.
    /// </summary>
    public static readonly int2 Size = new(1920, 1050);

    /// <summary>
    /// What the tool drives: the app as <c>Program</c> built it.
    /// </summary>
    public sealed record App(
        GlfwWindow Window,
        Renderer Renderer,
        Func<IReadOnlyList<Song>> Listed,
        IReadOnlyList<Deck> Decks,
        AudioEngine Audio,
        Func<Song, Deck, bool> Load,
        Action ListEverySong,
        Func<Song, bool> IsAnalysed,
        Action StartRecording,
        Func<string?> StopRecording
    );

    /// <summary>
    /// What the command line asked for: where to put what's made, the look, and whether it's the self-test rather
    /// than the screenshot.
    /// </summary>
    public sealed record Request(string Folder, string? Look, bool SelfTest);

    abstract record Step;

    sealed record Wait(int Frames) : Step;

    sealed record Until(Func<bool> Ready, int MaxFrames, string What) : Step;

    sealed record Shot(string Name) : Step;

    /// <summary>
    /// A check of the self-test, passed or not, and what was found.
    /// </summary>
    sealed record Check(string Name, bool Passed, string Found) : Step;

    /// <summary>
    /// The made-up songs: artist, album, title, tempo and key (its tonic as a semitone from C, and whether it's
    /// minor). The first two go on the decks.
    /// </summary>
    static readonly (string Artist, string Album, string Title, double Bpm, int Tonic, bool Minor)[] Songs =
    [
        ("Glass Horizon", "Northern Static", "Northern Static", 124, 9, true),
        ("Mira Vale", "Paper Satellites", "Afterglow Avenue", 124, 4, true),
        ("Glass Horizon", "Northern Static", "Low Tide Signals", 122, 2, true),
        ("Kites & Copper", "Velvet Circuit", "Velvet Circuit", 126, 0, false),
        ("Kites & Copper", "Velvet Circuit", "Copper Skies", 125, 7, false),
        ("The Lantern Club", "Night Ferry", "Night Ferry", 120, 11, true),
        ("The Lantern Club", "Night Ferry", "Halcyon", 118, 5, false),
        ("Solenne", "Glasshouse", "Glasshouse", 128, 6, true),
        ("Solenne", "Glasshouse", "Slow Orbit", 127, 1, true),
        ("Northbound Echo", "Golden Hour Drive", "Golden Hour Drive", 123, 10, false),
        ("Northbound Echo", "Golden Hour Drive", "Neon Harbour", 126, 3, true),
        ("Mira Vale", "Paper Satellites", "Paper Satellites", 121, 8, false),
    ];

    readonly App _app;
    readonly string _out;
    readonly string _name;
    readonly IEnumerator<Step> _script;
    int _waiting;
    Until? _until;
    Shot? _taking;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    readonly bool _selfTest;
    readonly List<Check> _checks = [];

    /// <summary>
    /// What the process should exit with: 1 if the self-test found anything wrong, else 0.
    /// </summary>
    public int ExitCode => _checks.Any(check => !check.Passed) ? 1 : 0;

    /// <summary>
    /// What the command line asks for; null without <c>--capture</c> or <c>--self-test</c>.
    /// </summary>
    public static Request? Parse(string[] args)
    {
        var testAt = Array.IndexOf(args, "--self-test");
        var selfTest = testAt >= 0;
        var at = selfTest ? testAt : Array.IndexOf(args, "--capture");
        if (at < 0)
            return null;
        var folder = at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(args[at + 1])
            : Path.Combine(Environment.CurrentDirectory, selfTest ? "." : "screenshots");
        var look = Array.IndexOf(args, "--look") is var lookAt and >= 0 && lookAt + 1 < args.Length ? args[lookAt + 1] : null;
        return new Request(Path.GetFullPath(folder), look, selfTest);
    }

    /// <summary>
    /// Points the app's folder at an empty scratch one, with the made-up songs as its music and
    /// <paramref name="look"/> as its look, so the run starts from nothing and leaves the user's settings, library and
    /// music alone. Call before anything reads <see cref="AppFolder.Path"/>.
    /// </summary>
    public static void UseScratch(string? look)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "cuepoint-capture");
        try
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
        catch (IOException) { }
        var app = Path.Combine(scratch, "App");
        var music = Path.Combine(scratch, "Music");
        Directory.CreateDirectory(app);
        AppFolder.Use(app);
        for (var i = 0; i < Songs.Length; i++)
        {
            var song = Songs[i];
            var folder = Path.Combine(music, song.Artist, song.Album);
            Directory.CreateDirectory(folder);
            WriteSong(Path.Combine(folder, $"{i % 4 + 1:00} {song.Title}.wav"), song.Bpm, song.Tonic, song.Minor, seed: i);
        }
        new AppSettings
        {
            MusicFolder = music,
            Theme = look is "Light" or "HighContrast" ? look : null,
            RecordingsFolder = Path.Combine(scratch, "Recordings"),
        }.Save(Path.Combine(app, "settings.json"));
    }

    public ScreenshotTool(App app, Request request)
    {
        _app = app;
        _out = request.Folder;
        _selfTest = request.SelfTest;
        _name = request.Look is "Light" ? "screenshot-light" : request.Look is "HighContrast" ? "screenshot-high-contrast" : "screenshot";
        Directory.CreateDirectory(_out);
        _script = (_selfTest ? SelfTest() : Run()).GetEnumerator();
    }

    /// <summary>
    /// Whether the shot's been taken, or given up on.
    /// </summary>
    public bool Done { get; private set; }

    /// <summary>
    /// Each frame, after it's drawn: the script moved on, or the shot it asked for saved.
    /// </summary>
    public void Frame()
    {
        if (Done)
            return;
        if (_taking is { } shot)
        {
            if (_app.Renderer.TakeWindowCapture() is not { } picture)
            {
                _app.Renderer.CaptureWindow = true;
                return;
            }
            var path = Path.Combine(_out, shot.Name + ".png");
            File.WriteAllBytes(path, Png.Encode(new Png.Image(picture.Width, picture.Height, picture.Rgba)));
            Console.WriteLine($"saved {path} ({picture.Width}x{picture.Height})");
            _taking = null;
        }
        if (_until is { } until)
        {
            if (!until.Ready() && _waiting-- > 0)
                return;
            if (!until.Ready())
                Console.Error.WriteLine($"gave up waiting for {until.What}; taking the shot anyway");
            else
                Console.WriteLine($"{_clock.Elapsed.TotalSeconds:F1}s: {until.What} done");
            // What's left of its time limit isn't waited out.
            (_until, _waiting) = (null, 0);
        }
        if (_waiting > 0)
        {
            _waiting--;
            return;
        }
        if (!_script.MoveNext())
        {
            if (_selfTest)
                Report();
            Done = true;
            return;
        }
        switch (_script.Current)
        {
            case Wait wait:
                _waiting = wait.Frames;
                break;
            case Until next:
                (_until, _waiting) = (next, next.MaxFrames);
                break;
            case Shot take:
                _taking = take;
                _app.Renderer.CaptureWindow = true;
                break;
            case Check check:
                _checks.Add(check);
                Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL")}  {check.Name}: {check.Found}");
                break;
        }
    }

    IEnumerable<Step> Run()
    {
        // The picture's exactly its size. The app's own title bar draws over the window's frame, so the frame's in the
        // picture, while the size asked of the window has it added on: it's asked for that much less.
        for (var i = 0; i < 5 && _app.Window.FramebufferSize != Size; i++)
        {
            _app.Window.Size = Size - (_app.Window.FramebufferSize - _app.Window.Size) - (_app.Window.Size - Size);
            yield return new Wait(10);
        }
        // Nothing's heard: the meters still move, as they're before the master volume.
        _app.Audio.MasterVolume = 0;
        // The songs as the song list has them, the Music library chosen: those are the ones analysed and loaded.
        var listed = _app.Listed;
        // The songs are in artist and album folders, as most people's are: the library's made recursive, as it
        // would be from its right-click menu.
        _app.ListEverySong();
        yield return new Until(() => listed().Count == Songs.Length, 30 * 60, "the songs to be found");
        yield return new Until(() => listed().All(_app.IsAnalysed), 120 * 60, "the songs to be analysed");

        var (a, b) = (_app.Decks[0], _app.Decks[1]);
        if (listed().FirstOrDefault(song => song.Title == Songs[0].Title) is not { } first
            || listed().FirstOrDefault(song => song.Title == Songs[1].Title) is not { } second)
        {
            Console.Error.WriteLine("the made-up songs weren't found; nothing to load");
            yield break;
        }
        _app.Load(first, a);
        _app.Load(second, b);
        yield return new Until(() => a.IsLoaded && b.IsLoaded && a.Analysis is not null && b.Analysis is not null, 30 * 60, "the decks to load");

        // Well into both, B following A's tempo and beat, as in the middle of a mix.
        a.Seek(TimeSpan.FromSeconds(30));
        b.Seek(TimeSpan.FromSeconds(16));
        _app.Audio.Crossfader = 0.5f;
        b.SyncLocked = true;
        a.Play();
        b.Play();
        yield return new Wait(5 * 60);
        yield return new Shot(_name);
    }

    // ------------------------------------------------------------------------------------------------ the self-test

    IEnumerable<Step> SelfTest()
    {
        _app.Audio.MasterVolume = 0;
        var listed = _app.Listed;
        _app.ListEverySong();
        yield return new Until(() => listed().Count == Songs.Length, 30 * 60, "the songs to be found");
        yield return new Check("Finding songs", listed().Count == Songs.Length, $"{listed().Count} of {Songs.Length}");
        yield return new Until(() => listed().All(_app.IsAnalysed), 120 * 60, "the songs to be analysed");
        var analysed = listed().Where(song => song.Analysis is not null).ToList();
        var temposRight = analysed.Count(song => Songs.FirstOrDefault(made => made.Title == song.Title) is var made && Math.Abs(song.Analysis!.Bpm - made.Bpm) < 0.6);
        yield return new Check("Analysing", analysed.Count == Songs.Length && temposRight == Songs.Length, $"{analysed.Count} analysed, {temposRight} at the tempo they were made at");

        var (a, b) = (_app.Decks[0], _app.Decks[1]);
        if (listed().FirstOrDefault(song => song.Title == Songs[0].Title) is not { } first
            || listed().FirstOrDefault(song => song.Title == Songs[1].Title) is not { } second)
        {
            yield return new Check("Loading", false, "the made-up songs weren't there to load");
            yield break;
        }
        _app.Load(first, a);
        _app.Load(second, b);
        yield return new Until(() => a.IsLoaded && b.IsLoaded && a.Analysis is not null && b.Analysis is not null, 30 * 60, "the decks to load");
        yield return new Check("Loading both decks", a.IsLoaded && b.IsLoaded, $"A: {a.Title}, B: {b.Title}");

        // Playing: the decks move on, and B locked to A takes its tempo and beat.
        b.Tempo = 0.03;
        b.SyncLocked = true;
        a.Play();
        b.Play();
        var started = a.Position;
        yield return new Wait(3 * 60);
        yield return new Check("Playing", a.IsPlaying && a.Position - started > TimeSpan.FromSeconds(2), $"deck A moved on {(a.Position - started).TotalSeconds:F1} seconds in 3");
        yield return new Check("Sync lock", a.Bpm is { } tempoA && b.Bpm is { } tempoB && Math.Abs(tempoA - tempoB) < 0.05 && b.IsInSync, $"A at {a.Bpm:F2} BPM, B at {b.Bpm:F2}");

        // Recording, long enough for the song playing to count as played, so it has a tracklist.
        _app.StartRecording();
        yield return new Wait(12 * 60);
        var recording = _app.StopRecording();
        var wav = recording is not null && File.Exists(recording) ? new FileInfo(recording).Length : 0;
        var cue = recording is not null ? Path.ChangeExtension(recording, ".cue") : "";
        yield return new Check("Recording", wav > 48000 * 6 * 10 && File.Exists(cue), $"{wav / 1024} KB recorded, {(File.Exists(cue) ? "with" : "without")} its cue sheet");

        // The tracklist, edited and read back.
        if (recording is not null && File.Exists(cue))
        {
            var (tracks, date) = RecordingTracklist.Read(recording, MixRecorder.FileLength);
            RecordedTrack[] edited = [.. tracks.Select(track => track with { Title = track.Title + " (checked)" })];
            var error = RecordingTracklist.Write(recording, edited, date, MixRecorder.FileLength);
            var (again, _) = RecordingTracklist.Read(recording, MixRecorder.FileLength);
            yield return new Check("Editing the tracklist", error is null && tracks.Count > 0 && again.SequenceEqual(edited), $"{tracks.Count} {(tracks.Count == 1 ? "song" : "songs")}, edited and read back");
        }
        else
            yield return new Check("Editing the tracklist", false, "no cue sheet to edit");

        // Streaming, to a pretend server of its own on this machine.
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var received = 0L;
        var listening = Task.Run(() =>
        {
            try
            {
                using var client = server.AcceptTcpClient();
                var network = client.GetStream();
                var request = new StringBuilder();
                while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && network.ReadByte() is var c and >= 0)
                    request.Append((char)c);
                network.Write("HTTP/1.1 100 Continue\r\n\r\n"u8);
                var buffer = new byte[8192];
                int read;
                while ((read = network.Read(buffer)) > 0)
                    Interlocked.Add(ref received, read);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidOperationException) { }
        });
        var stream = new LiveStream(new StreamServer("127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port, "self-test", "self-test", "Cuepoint self-test", 128)) { Title = $"{a.Artist} - {a.Title}" };
        _app.Audio.Stream = stream;
        yield return new Wait(4 * 60);
        var live = stream.State == StreamState.Live;
        _app.Audio.Stream = null;
        stream.Dispose();
        server.Stop();
        listening.Wait(TimeSpan.FromSeconds(2));
        yield return new Check("Streaming", live && Interlocked.Read(ref received) > 1000, $"{(live ? "live" : stream.Problem ?? "not live")}, {Interlocked.Read(ref received) / 1024} KB received");

        // The panic, and putting it back.
        (a.Equalizer.Low, a.Effects.On) = (-1, true);
        var panic = Panic.Reset(_app.Decks, _app.Audio.MasterEffects);
        var neutral = a.Equalizer.Low == 0 && !a.Effects.On && a.IsPlaying;
        panic.Undo();
        yield return new Check("Panic", neutral && a.Equalizer.Low == -1 && a.Effects.On, neutral ? "back to neutral, playing on, and put back" : "not back to neutral");
        (a.Equalizer.Low, a.Effects.On) = (0, false);
        a.Pause();
        b.Pause();
    }

    /// <summary>
    /// Says how the self-test went, and writes it beside where it was asked to.
    /// </summary>
    void Report()
    {
        var failed = _checks.Count(check => !check.Passed);
        var summary = failed == 0 ? $"All {_checks.Count} checks passed" : $"{failed} of {_checks.Count} checks failed";
        var text = new StringBuilder($"Cuepoint self-test, {DateTime.Now:yyyy-MM-dd HH:mm}\n\n");
        foreach (var check in _checks)
            text.Append($"{(check.Passed ? "PASS" : "FAIL")}  {check.Name}: {check.Found}\n");
        text.Append($"\n{summary}, in {_clock.Elapsed.TotalSeconds:F0} seconds.\n");
        var path = Path.Combine(_out, "self-test.txt");
        try
        {
            File.WriteAllText(path, text.ToString().Replace("\n", "\r\n"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Couldn't write {path}: {e.Message}");
        }
        Console.WriteLine($"{summary}; written to {path}");
    }

    // ------------------------------------------------------------------------------------------- the made-up songs

    const int RATE = 22050;
    // A minute each: as much as the screen shows, and quick to analyse, as all of them are before the shot.
    const double SECONDS = 60;

    /// <summary>
    /// A song of <see cref="SECONDS"/> as a mono WAV: a chord in its key throughout, a bass note and hi-hats, and a
    /// kick on every beat but in the intro, a breakdown and the outro, so the analysis finds its tempo, key and shape.
    /// </summary>
    static void WriteSong(string path, double bpm, int tonic, bool minor, int seed)
    {
        var random = new Random(seed);
        var frames = (int)(SECONDS * RATE);
        var samples = new float[frames];
        var beat = 60 / bpm;
        double Note(int semitone, int octave) => 440 * Math.Pow(2, (semitone - 9) / 12.0 + octave - 4);
        double[] chord = [Note(tonic, 3), Note(tonic + (minor ? 3 : 4), 3), Note(tonic + 7, 3), Note(tonic, 4)];
        var bass = Note(tonic, 2);
        var beats = (int)(SECONDS / beat);
        for (var n = 0; n < beats; n++)
        {
            var start = (int)(n * beat * RATE);
            var fraction = (double)n / beats;
            var kick = n >= 16 && n < beats - 16 && !(fraction > 0.55 && fraction < 0.65);
            var length = (int)(beat * RATE);
            for (var i = 0; i < length && start + i < frames; i++)
            {
                var t = (double)i / RATE;
                var sample = 0.0;
                if (kick)
                {
                    // A sine falling from 120 Hz to 50, dying away over a sixth of a second.
                    var phase = 2 * Math.PI * (50 * t + 70 * 0.04 * (1 - Math.Exp(-t / 0.04)));
                    sample += 0.8 * Math.Sin(phase) * Math.Exp(-t / 0.16);
                    sample += 0.25 * Math.Sin(2 * Math.PI * bass * t) * Math.Exp(-t / 0.3);
                }
                // An open hi-hat on the off-beat.
                var off = t - beat / 2;
                if (off >= 0)
                    sample += 0.08 * (random.NextDouble() * 2 - 1) * Math.Exp(-off / 0.03);
                samples[start + i] += (float)sample;
            }
        }
        // The chord all through, swelling in and out over a bar or two.
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / RATE;
            var pad = 0.0;
            foreach (var frequency in chord)
                pad += Math.Sin(2 * Math.PI * frequency * t) + 0.3 * Math.Sin(4 * Math.PI * frequency * t);
            var swell = 0.75 + 0.25 * Math.Sin(2 * Math.PI * t / (8 * beat));
            samples[i] += (float)(0.06 * pad * swell);
        }
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(RATE);
        writer.Write(RATE * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 2);
        foreach (var sample in samples)
            writer.Write((short)Math.Clamp(sample * 26000, short.MinValue, short.MaxValue));
    }
}
