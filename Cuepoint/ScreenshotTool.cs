using Brutal.GlfwApi;
using Brutal.Numerics;
using Cue.Framework.Rendering;
using Cue.Framework.UI;
using Cuepoint.Audio;
using Cuepoint.Music;

namespace Cuepoint;

/// <summary>
/// Takes the README's screenshot from the running app, repeatably: run as <c>Cuepoint --capture &lt;folder&gt;
/// [--look Light|HighContrast]</c>. Nothing of the user's is read or touched: the app's folder is a scratch one for the
/// run (<see cref="UseScratch"/>), its music a dozen songs made up for it (a beat and a chord at a tempo and key each,
/// under made-up names), and the master volume's down, so nothing's heard. Once they're analysed, two are loaded and
/// played in step, and the frame the GPU drew is saved as a PNG: only the window's own picture, never the screen's.
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
    public sealed record App(GlfwWindow Window, Renderer Renderer, Func<IReadOnlyList<Song>> Listed, IReadOnlyList<Deck> Decks, AudioEngine Audio, Func<Song, Deck, bool> Load, Action ListEverySong, Func<Song, bool> IsAnalysed);

    abstract record Step;

    sealed record Wait(int Frames) : Step;

    sealed record Until(Func<bool> Ready, int MaxFrames, string What) : Step;

    sealed record Shot(string Name) : Step;

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

    /// <summary>
    /// The folder and look from the command line; null without <c>--capture</c>.
    /// </summary>
    public static (string Folder, string? Look)? Parse(string[] args)
    {
        var at = Array.IndexOf(args, "--capture");
        if (at < 0)
            return null;
        var folder = at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(args[at + 1])
            : Path.Combine(Environment.CurrentDirectory, "screenshots");
        var look = Array.IndexOf(args, "--look") is var lookAt and >= 0 && lookAt + 1 < args.Length ? args[lookAt + 1] : null;
        return (folder, look);
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

    public ScreenshotTool(App app, string folder, string? look)
    {
        _app = app;
        _out = folder;
        _name = look is "Light" ? "screenshot-light" : look is "HighContrast" ? "screenshot-high-contrast" : "screenshot";
        Directory.CreateDirectory(folder);
        _script = Run().GetEnumerator();
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
