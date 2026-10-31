using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutality;
using Brutality.Audio;
using Brutality.Input;
using Brutality.Music;
using Brutality.Rendering;
using Brutality.UI;

WindowsShell.AttachParentConsole();

try
{
    if (!Glfw.Init())
    {
        var errorCode = Glfw.GetError(out var errorMessage);
        throw new InvalidOperationException($"Failed to initialize GLFW: {errorCode}, {errorMessage}");
    }

    // GLFW can't find Brutal's Vulkan loader by itself, so touching Vulkan first loads it.
    _ = Vulkan.GetInstanceProcAddr;

    try
    {
        Run();
    }
    finally
    {
        Glfw.Terminate();
    }
}
catch (Exception e)
{
    // A windowed app has nowhere to print an unhandled exception, so it would otherwise just vanish.
    WindowsShell.ShowFatalError(e);
    return 1;
}

return 0;

static void Run()
{
    Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);

    // Declared first so it's disposed last, after the GPU objects that draw to it.
    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Cuepoint", Size = new int2(1280, 720) });

#if DEBUG
    const bool ENABLE_VALIDATION = true;
#else
    const bool ENABLE_VALIDATION = false;
#endif

    var fonts = new FontAtlas();
    var bodyFont = fonts.Add(FindFont(bold: false), 16);
    var titleFont = fonts.Add(FindFont(bold: true), 22);

    using var gpu = new GpuContext(window, ENABLE_VALIDATION);
    using var renderer = new Renderer(gpu, window, fonts);

    var hud = new Hud(bodyFont, titleFont, gpu.DeviceName);
    using var input = new GlfwUiInput(window, hud.Ui);

    const string MUSIC_FOLDER = "F:/Music";

    using var audio = new AudioEngine();
    using var deckA = audio.CreateDeck("A", CrossfaderSide.Left);
    using var deckB = audio.CreateDeck("B", CrossfaderSide.Right);
    Deck[] decks = [deckA, deckB];
    hud.Log($"Audio decoders: {string.Join(", ", audio.Decoders)}");

    // The library scans now and reads tags in the background; songs load onto decks in the background too.
    using var library = new MusicLibrary(MUSIC_FOLDER);
    hud.Log(library.Error is { } error ? $"Library: {error}" : $"Library: {library.Songs.Count} songs in {MUSIC_FOLDER}");

    // Songs are analysed as they come into view in the list, and the results kept between runs.
    var database = LibraryDatabase.Load(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuepoint", "library.json")
    );
    using var analyser = new LibraryAnalyser(library.Songs, database);
    hud.Log(
        database.LoadError is { } loadError
            ? $"Database: couldn't read {database.FilePath}, starting afresh: {loadError}"
            : $"Database: {library.Songs.Count(song => song.Analysis is not null)} songs already analysed"
    );
    var browser = hud.AddBrowser(library, analyser, decks.Select(deck => deck.Name));

    // Left to right: deck A, the crossfader, deck B.
    hud.AddDeck(deckA);
    hud.AddCrossfader(audio);
    hud.AddDeck(deckB);

    foreach (var deck in decks)
    {
        deck.Loaded += loaded =>
        {
            hud.Log($"Deck {loaded.Name}: loaded {loaded.Artist} - {loaded.Title}");
            loaded.Analysis = loaded.FilePath is { } path ? library.Find(path)?.Analysis : null;
        };
        // A missing or unsupported file leaves the deck as it was rather than stopping the app.
        deck.LoadFailed += (failed, path, e) =>
            hud.Log($"Deck {failed.Name}: couldn't load {Path.GetFileName(path)}: {e.Message}");
    }

    // A song analysed while it's on a deck takes its gain then. Most finish before the deck has decoded.
    analyser.Analysed += song =>
    {
        if (song.AnalysisError is { } analysisError)
            hud.Log($"Couldn't analyse {Path.GetFileName(song.Path)}: {analysisError}");
        foreach (var deck in decks)
        {
            if (string.Equals(deck.FilePath, song.Path, StringComparison.OrdinalIgnoreCase))
                deck.Analysis = song.Analysis;
        }
    };

    void Load(Song song, Deck deck)
    {
        // Analysis goes to the front of the queue, so the deck has its gain and tempo as soon as possible.
        analyser.Request(song);
        deck.BeginLoad(song.Path, $"{song.Artist} - {song.Title}");
        hud.Log($"Deck {deck.Name}: loading {song.Artist} - {song.Title}");
    }

    browser.LoadRequested += (song, deckName) => Load(song, decks.First(deck => deck.Name == deckName));

    // Double-click or Enter picks a deck for you: an empty one if there is one, otherwise one that's paused. Never a
    // playing one, so a load can't cut off the song you're playing out.
    browser.SongActivated += song =>
    {
        var target =
            decks.FirstOrDefault(deck => !deck.IsLoaded && !deck.IsLoading)
            ?? decks.FirstOrDefault(deck => !deck.IsPlaying);
        if (target is not null)
            Load(song, target);
        else
            hud.Log("Both decks are playing; use Load to A or B to replace one");
    };

    // App shortcuts go through the UI first, so Escape in the note box only leaves the box, and typing there
    // doesn't work the decks. Cue acts on press and release; held keys' auto-repeat is ignored.
    input.UnhandledKey += (key, action, _) =>
    {
        if (action == GlfwKeyAction.Repeat)
            return;
        var pressed = action == GlfwKeyAction.Press;

        switch (key)
        {
            case GlfwKey.Escape when pressed:
                window.ShouldClose = true;
                break;
            case GlfwKey.Number1 when pressed && browser.SelectedSong is { } song:
                Load(song, deckA);
                break;
            case GlfwKey.Number2 when pressed && browser.SelectedSong is { } song:
                Load(song, deckB);
                break;
            case GlfwKey.Z:
                if (pressed)
                    deckA.CueDown();
                else
                    deckA.CueUp();
                break;
            case GlfwKey.X when pressed:
                deckA.TogglePlay();
                break;
            case GlfwKey.N:
                if (pressed)
                    deckB.CueDown();
                else
                    deckB.CueUp();
                break;
            case GlfwKey.M when pressed:
                deckB.TogglePlay();
                break;
        }
    };

    var clock = Stopwatch.StartNew();
    while (!window.ShouldClose)
    {
        Glfw.PollEvents();

        // Minimised: there's no framebuffer to draw to, so sleep until something happens.
        var size = window.FramebufferSize;
        if (size.X == 0 || size.Y == 0)
        {
            Glfw.WaitEvents();
            continue;
        }

        // Finishes any background loads, raising their Loaded events here on the UI thread.
        foreach (var deck in decks)
            deck.Update();

        // Applies finished analyses and saves the database when any came in.
        var saveError = analyser.SaveError;
        analyser.Update();
        if (analyser.SaveError is { } newSaveError && newSaveError != saveError)
            hud.Log($"Database: couldn't save {database.FilePath}: {newSaveError}");

        hud.Update(clock.Elapsed.TotalSeconds, size);

        hud.Ui.Layout(new float2(size.X, size.Y));
        input.UpdateCursor();
        renderer.DrawFrame(hud.Ui);
    }
}

static string FindFont(bool bold) =>
    FontAtlas.FindSystemFont(bold)
    ?? FontAtlas.FindSystemFont()
    ?? throw new FileNotFoundException("No usable system font found; install DejaVu Sans or Liberation Sans");
