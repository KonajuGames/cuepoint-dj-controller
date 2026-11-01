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

    // Declared first so it's disposed last, after the GPU objects that draw to it. It opens maximised, as the screen
    // is laid out to fill it, and can't be made too small for the decks and mixer to fit side by side.
    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Cuepoint", Size = new int2(1440, 860) });
    window.SetSizeLimits(new int2(1280, 720), new int2(-1, -1));
    window.Maximize();

#if DEBUG
    const bool ENABLE_VALIDATION = true;
#else
    const bool ENABLE_VALIDATION = false;
#endif

    var atlas = new FontAtlas();
    var regular = FindFont(bold: false);
    var bold = FindFont(bold: true);
    var fonts = new Fonts(
        Small: atlas.Add(regular, 12),
        Body: atlas.Add(regular, 14),
        Bold: atlas.Add(bold, 14),
        Heading: atlas.Add(bold, 18),
        Big: atlas.Add(bold, 26)
    );

    using var gpu = new GpuContext(window, ENABLE_VALIDATION);
    using var renderer = new Renderer(gpu, window, atlas);

    const string MUSIC_FOLDER = "F:/Music";

    using var audio = new AudioEngine();
    using var deckA = audio.CreateDeck("A", CrossfaderSide.Left);
    using var deckB = audio.CreateDeck("B", CrossfaderSide.Right);
    Deck[] decks = [deckA, deckB];

    // Settings kept between runs, beside the library's database: for now, the headphones.
    var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuepoint", "settings.json");
    var settings = AppSettings.Load(settingsPath);
    audio.CueMix = settings.CueMix;
    audio.HeadphoneVolume = settings.HeadphoneVolume;
    var headphonesFound = audio.UseHeadphoneDevice(settings.HeadphoneDevice);
    void SaveSettings()
    {
        settings.HeadphoneDevice = audio.HeadphoneDevice;
        settings.CueMix = audio.CueMix;
        settings.HeadphoneVolume = audio.HeadphoneVolume;
        settings.Save(settingsPath);
    }

    // The library scans now and reads tags in the background; songs load onto decks in the background too.
    using var library = new MusicLibrary(MUSIC_FOLDER);

    // Songs are analysed as they come into view in the list, and the results kept between runs.
    var database = LibraryDatabase.Load(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuepoint", "library.json")
    );
    using var analyser = new LibraryAnalyser(library.Songs, database);

    // What's been loaded onto a deck this run, oldest first, for the History folder.
    var history = new List<Song>();
    var folders = LibraryTree.Build(library, decks, history);

    var ui = new Root();
    var screen = ui.Add(new MainScreen(audio, decks, library, analyser, folders, database.FilePath, fonts, HotcueKeys.Labels));
    using var input = new GlfwUiInput(window, ui);
    var browser = screen.Browser;

    // The library's own folder is what shows to begin with, with the lists and filters open below the drives.
    screen.FolderTree.SetExpanded(folders[2], true);
    screen.FolderTree.SetExpanded(folders[3], true);
    screen.FolderTree.Select(folders[0]);

    screen.Log(
        library.Error is { } error ? $"Couldn't read {MUSIC_FOLDER}: {error}"
        : database.LoadError is { } loadError ? $"Couldn't read {database.FilePath}, so starting afresh: {loadError}"
        : $"{library.Songs.Count} songs in {MUSIC_FOLDER}, {library.Songs.Count(song => song.Analysis is not null)} analysed"
    );

    // The window's own buttons, and dragging it by the title bar, which Windows does once told where it is.
    screen.TitleBar.IsMaximized = () => window.IsMaximised;
    screen.TitleBar.MinimizeClicked += window.Iconify;
    screen.TitleBar.MaximizeClicked += () =>
    {
        if (window.IsMaximised)
            window.Restore();
        else
            window.Maximize();
    };
    screen.TitleBar.CloseClicked += () => window.ShouldClose = true;
    screen.TitleBar.OptionsClicked += screen.Options.Open;
    screen.Options.HeadphonesChanged += device =>
    {
        SaveSettings();
        screen.Log(device is null ? "Headphones off" : $"Headphones on {device}");
    };
    if (!headphonesFound)
        screen.Log($"Couldn't find {settings.HeadphoneDevice} for the headphones; choose them again in the options");
    if (OperatingSystem.IsWindows())
        WindowChrome.Install(window, point => screen.TitleBar.IsCaption(new float2(point.X, point.Y)));

    foreach (var deck in decks)
    {
        deck.Loaded += loaded =>
        {
            screen.Log($"Deck {loaded.Name}: loaded {loaded.Artist} - {loaded.Title}");
            loaded.Analysis = loaded.FilePath is { } path ? library.Find(path)?.Analysis : null;
            if (loaded.FilePath is { } file)
                loaded.SetHotcues(database.GetHotcues(file));
        };

        // Hotcues are saved as soon as they're set or cleared, so none is lost if the app closes mid-set.
        deck.HotcuesChanged += changed =>
        {
            if (changed.FilePath is not { } file)
                return;
            database.SetHotcues(file, changed.Hotcues);
            try
            {
                database.Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                screen.Log($"Couldn't save hotcues to {database.FilePath}: {e.Message}");
            }
        };
        // A missing or unsupported file leaves the deck as it was rather than stopping the app.
        deck.LoadFailed += (failed, path, e) =>
            screen.Log($"Deck {failed.Name}: couldn't load {Path.GetFileName(path)}: {e.Message}");
    }

    // A song analysed while it's on a deck takes its gain then. Most finish before the deck has decoded.
    analyser.Analysed += song =>
    {
        if (song.AnalysisError is { } analysisError)
            screen.Log($"Couldn't analyse {Path.GetFileName(song.Path)}: {analysisError}");
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
        history.Add(song);
        screen.Log($"Deck {deck.Name}: loading {song.Artist} - {song.Title}");
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
            screen.Log("Both decks are playing; use Load A or Load B to replace one");
    };

    // App shortcuts go through the UI first, so Escape in the search box only leaves the box, and typing there
    // doesn't work the decks. Cue acts on press and release; held keys' auto-repeat is ignored. Escape no longer
    // quits, now there's a close button, since a stray press mid-set would be a disaster.
    input.UnhandledKey += (key, action, mods) =>
    {
        if (action == GlfwKeyAction.Repeat)
            return;
        var pressed = action == GlfwKeyAction.Press;

        // Each deck's hotcue keys act as its pads do: set or jump, or with Shift, clear.
        if (HotcueKeys.For(key) is { } hotcue)
        {
            if (pressed && hotcue.Deck < decks.Length)
            {
                if ((mods & GlfwModifier.Shift) != 0)
                    decks[hotcue.Deck].DeleteHotcue(hotcue.Hotcue);
                else
                    decks[hotcue.Deck].HotcueDown(hotcue.Hotcue);
            }
            return;
        }

        switch (key)
        {
            case GlfwKey.Escape when pressed:
                screen.Options.Close();
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
            case GlfwKey.C when pressed:
                deckA.Sync();
                break;
            case GlfwKey.Comma when pressed:
                deckB.Sync();
                break;
        }
    };

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
            screen.Log($"Couldn't save {database.FilePath}: {newSaveError}");

        // New tags can change what matches a search.
        if (library.ApplyReadTags() > 0)
            browser.Refilter();

        screen.ScreenSize = new float2(size.X, size.Y);
        screen.Update();
        ui.Layout(screen.ScreenSize);
        input.UpdateCursor();
        renderer.DrawFrame(ui);
    }

    // The headphone blend and volume are set on the mixer as it plays, so they're kept as the app closes.
    if (headphonesFound)
        SaveSettings();

    if (OperatingSystem.IsWindows())
        WindowChrome.Remove();
}

static string FindFont(bool bold) =>
    FontAtlas.FindSystemFont(bold)
    ?? FontAtlas.FindSystemFont()
    ?? throw new FileNotFoundException("No usable system font found; install DejaVu Sans or Liberation Sans");
