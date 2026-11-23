using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Cuepoint;
using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;
using Cuepoint.Audio.Stems;
using Cuepoint.Midi;
using Cuepoint.Music;
using Cuepoint.Music.Imports;
using Cuepoint.Os2l;
using Cuepoint.UI;

WindowsShell.AttachParentConsole();

// The screenshot tool (--capture <folder> [--look Light|HighContrast]) and the self-test (--self-test [folder]) run on
// a scratch app folder and made-up songs, leaving the user's alone.
var capture = ScreenshotTool.Parse(args);
if (capture is { } scratch)
    ScreenshotTool.UseScratch(scratch.Look, scratch.Tour);
// The tour records the mix itself, so it plays to no sound card, and nothing's heard while it goes.
if (capture is { Tour: true })
    Environment.SetEnvironmentVariable("SDL_AUDIO_DRIVER", "dummy");

// What goes wrong is written down beside the settings, to look into after a set; even on another thread, where it
// still ends the app.
ErrorLog.FilePath = Path.Combine(AppFolder.Path, "errors.log");
AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    if (args.ExceptionObject is Exception e)
        ErrorLog.Write("working in the background", e);
};

var exitCode = 0;
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
        exitCode = Run(capture);
    }
    finally
    {
        Glfw.Terminate();
    }
}
catch (Exception e)
{
    // A windowed app has nowhere to print an unhandled exception, so it would otherwise just vanish.
    ErrorLog.Write("running", e);
    WindowsShell.ShowFatalError(e);
    return 1;
}

return exitCode;

// Runs the app; returns what it exits with: 0, unless the self-test found something wrong.
static int Run(ScreenshotTool.Request? capture)
{
    Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);

    // It's made maximised, as the screen is laid out to fill it, but hidden: Windows would otherwise show it empty
    // while the app gets going, white where it was first sized and black beyond. It's shown once the first frame is
    // drawn.
    Glfw.WindowHint(GlfwWindowHint.Visible, 0);
    Glfw.WindowHint(GlfwWindowHint.Maximized, capture is null ? 1 : 0);
    // The feature tour drives the window itself: the mouse passes through it to whatever's beneath, and it doesn't
    // take the focus from what's being worked in, so nothing typed or clicked meanwhile reaches it.
    if (capture is { Tour: true })
    {
        Glfw.WindowHint(GlfwWindowHint.MousePassthrough, 1);
        Glfw.WindowHint(GlfwWindowHint.FocusOnShow, 0);
        Glfw.WindowHint(GlfwWindowHint.Focused, 0);
    }

    // Declared first so it's disposed last, after the GPU objects that draw to it. It can't be made too small for the
    // decks and mixer to fit side by side.
    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Cuepoint", Size = capture is null ? new int2(1440, 860) : ScreenshotTool.Size });

    // How big everything's drawn: as chosen, or as Windows has it for the screen; the screenshot's always as designed.
    var windowsScale = Glfw.PrimaryMonitor.ContentScale.X;
    // "--scale 1.5" on the command line tries another for one run, leaving the setting as it is.
    var arguments = Environment.GetCommandLineArgs();
    var scaleAt = Array.IndexOf(arguments, "--scale");
    float? tried = scaleAt >= 0 && scaleAt + 1 < arguments.Length && float.TryParse(arguments[scaleAt + 1], System.Globalization.CultureInfo.InvariantCulture, out var given) ? given : null;
    // Never so big that the screen hasn't room for the decks and mixer side by side, which would draw them over each
    // other: on a 1920 by 1080 screen, 150% at most.
    var screenPixels = Glfw.PrimaryMonitor.VideoMode;
    var smallestWindow = new float2(1280, 720);
    var largestScale = ScreenScale.Largest(screenPixels.Width, screenPixels.Height, smallestWindow);
    var screenScale = capture is null ? ScreenScale.Use(tried ?? AppSettings.Load(Path.Combine(AppFolder.Path, "settings.json")).ScreenScale, windowsScale, largestScale) : 1;
    // Never so small that the decks and mixer can't fit side by side, nor bigger than the screen.
    window.SetSizeLimits(
        new int2((int)Math.Min(smallestWindow.X * screenScale, screenPixels.Width), (int)Math.Min(smallestWindow.Y * screenScale, screenPixels.Height)),
        new int2(-1, -1)
    );

#if DEBUG
    const bool ENABLE_VALIDATION = true;
#else
    const bool ENABLE_VALIDATION = false;
#endif

    // Text's rasterised at the size it's drawn, so it's sharp.
    var atlas = new FontAtlas { Scale = screenScale };
    var regular = FindFont(bold: false);
    var bold = FindFont(bold: true);
    var fonts = new Fonts(
        Small: atlas.Add(regular, 12),
        Body: atlas.Add(regular, 14),
        Bold: atlas.Add(bold, 14),
        Heading: atlas.Add(bold, 18),
        Big: atlas.Add(bold, 26),
        Large: atlas.Add(regular, 20),
        SmallBold: atlas.Add(bold, 12),
        Badge: atlas.Add(FindHeavyFont() ?? bold, 26)
    );
    var images = Images.Load(atlas);

    // The logo on the taskbar and in Alt+Tab too. GLFW copies the pixels, so they needn't outlive the call.
    unsafe
    {
        var icon = Images.Read("logo-32.png");
        fixed (byte* pixels = icon.Pixels)
            window.SetIcon(1, new GlfwImage { Width = icon.Width, Height = icon.Height, Pixels = pixels });
    }

    using var gpu = new GpuContext(window, ENABLE_VALIDATION);
    using var renderer = new Renderer(gpu, window, atlas);

    // Settings kept between runs, beside the library's database; read before the audio starts, which opens its
    // devices with the buffer size kept.
    var settingsPath = Path.Combine(AppFolder.Path, "settings.json");

    // The keys for the decks, their pads and the crossfader, as changed from the usual ones.
    var keysPath = Path.Combine(AppFolder.Path, "keys.json");
    var keyMap = KeyMap.Load(keysPath);
    keyMap.Apply();
    var settings = AppSettings.Load(settingsPath);
    // Before any song's looked up, as its tempo's shown in this range.
    TempoRange.Min = settings.TempoRangeMin;

    // The look, before anything's made, as elements take their colours as they're made.
    Theme.Use(Enum.TryParse<ThemeKind>(settings.Theme, out var look) ? look : ThemeKind.Dark);

    using var audio = new AudioEngine(settings.BufferFrames is { } frames && AudioEngine.BufferSizes.Contains(frames) ? frames : null);
    using var deckA = audio.CreateDeck("A", CrossfaderSide.Left);
    using var deckB = audio.CreateDeck("B", CrossfaderSide.Right);
    Deck[] decks = [deckA, deckB];

    // Hearing a song from the list in the headphones, without a deck.
    using var preview = new SongPreview(audio);

    // Eight one-shot samples, played into the mix from either deck's pads in SAMPLER mode, kept between runs.
    using var sampler = new Sampler(audio);
    for (var slot = 0; slot < Math.Min(Sampler.SLOTS, settings.SamplerSlots?.Count ?? 0); slot++)
    {
        if (settings.SamplerSlots![slot] is { } samplePath && File.Exists(samplePath))
            sampler.Load(slot, samplePath, Path.GetFileNameWithoutExtension(samplePath));
    }

    // The output goes first, as headphones on its channels 3 and 4 need it there.
    audio.CueMix = settings.CueMix;
    audio.HeadphoneVolume = settings.HeadphoneVolume;
    audio.Limiter.Enabled = settings.MasterLimiter;
    audio.CrossfaderCurve = Enum.TryParse<CrossfaderCurve>(settings.CrossfaderCurve, out var curve) ? curve : CrossfaderCurve.Smooth;
    var outputFound = audio.UseOutputDevice(settings.OutputDevice);
    var headphonesFound = audio.UseHeadphoneDevice(settings.HeadphoneDevice);
    // Those not plugged in are still the ones chosen, gone to as soon as they are.
    audio.Choose(settings.OutputDevice, settings.HeadphoneDevice);
    audio.BoothVolume = settings.BoothVolume;
    var boothFound = settings.BoothDevice is null || audio.UseBoothDevice(settings.BoothDevice);
    // Once settings have been restored from a backup, nothing's saved over them as the app closes to take them in.
    var settingsRestored = false;

    void SaveSettings()
    {
        if (settingsRestored)
            return;
        // The devices chosen, even while one's missing and the mix is on the default instead.
        settings.OutputDevice = audio.ChosenOutputDevice;
        settings.HeadphoneDevice = audio.ChosenHeadphoneDevice;
        settings.CueMix = audio.CueMix;
        settings.HeadphoneVolume = audio.HeadphoneVolume;
        settings.BoothDevice = audio.ChosenBoothDevice;
        settings.BoothVolume = audio.BoothVolume;
        settings.Effects = decks.ToDictionary(
            deck => deck.Name,
            deck => new AppSettings.DeckEffectSettings
            {
                Kind = deck.Effects.Kind,
                Level = deck.Effects.Level,
                EchoBeats = deck.Effects.BeatsOf(DeckEffect.Echo),
                FlangerBeats = deck.Effects.BeatsOf(DeckEffect.Flanger),
                GateBeats = deck.Effects.BeatsOf(DeckEffect.Gate),
            }
        );
        settings.MasterEffect = new AppSettings.DeckEffectSettings
        {
            Kind = audio.MasterEffects.Kind,
            Level = audio.MasterEffects.Level,
            EchoBeats = audio.MasterEffects.BeatsOf(DeckEffect.Echo),
            FlangerBeats = audio.MasterEffects.BeatsOf(DeckEffect.Flanger),
            GateBeats = audio.MasterEffects.BeatsOf(DeckEffect.Gate),
        };
        settings.SamplerSlots = sampler.Paths.Any(path => path is not null) ? [.. sampler.Paths] : null;
        settings.UnquantizedDecks = decks.Where(deck => !deck.Quantize).Select(deck => deck.Name).ToList() is { Count: > 0 } off ? off : null;
        settings.Save(settingsPath);
    }
    sampler.Changed += SaveSettings;

    static string FolderKey(string path) => Path.TrimEndingDirectorySeparator(path);

    // A folder lists only the songs directly in it. The one chosen can be made recursive, to list every song under it,
    // for as long as it stays chosen. The music library made recursive stays so, between runs too (see SetRecursive).
    LibraryNode? recursiveNode = null;
    bool IsRecursive(string path) => recursiveNode?.Path == path;

    // The library scans now and reads tags in the background; songs load onto decks in the background too. It's the
    // folder last chosen as the library, or Windows' own Music folder to begin with.
    // It's looked through in the background once the screen's up, so a big one doesn't keep the window waiting.
    using var library = new MusicLibrary(settings.MusicFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), settings.LibraryRecursive, scan: false);

    // The library's folder is watched, so songs added, changed or taken away show up without starting again; watched
    // afresh whenever the library moves or goes recursive. Paths notWatched says are passed over (recordings being
    // written, once their folder's known).
    LibraryWatcher? watcher = null;
    Func<string, bool> notWatched = _ => false;
    void WatchLibrary()
    {
        watcher?.Dispose();
        watcher = LibraryWatcher.Start(library.Root, library.Recursive, path => notWatched(path));
    }

    // Songs are analysed as they come into view in the list, and the results kept between runs.
    var database = LibraryDatabase.Load(
        Path.Combine(AppFolder.Path, "library.json")
    );
    using var analyser = new LibraryAnalyser(library.Songs, database);

    // Each song's title, artist and album go into the library with its analysis, as its tags are read.
    library.Tagged += database.SetNames;

    // What's been loaded onto a deck this run, oldest first, for the History folder.
    var history = new List<Song>();

    // Songs put together by hand, a playlist file each, in the app's folder or on the drive of the user's choosing:
    // Cuepoint\VirtualFolders either way. Deleting one sends its file to the Recycle Bin, unless Windows is set to
    // delete straight away on that drive.
    var virtualFolders = new VirtualFolders(
        library,
        Path.Combine(AppFolder.Path, "VirtualFolders"),
        () => VirtualFolders.ReadyDrives(WindowsShell.IsReadOnlyVolume)
    );
    if (OperatingSystem.IsWindows())
        virtualFolders.RemoveFile = WindowsShell.MoveToRecycleBin;

    // Other DJ software's playlists and hot cues: Rekordbox collections exported as XML, and Serato's crates. Each
    // collection's hot cues, memory cues and beat grids come into the library once (and again when it's exported
    // afresh), each for songs with none of their own here; a record of what's come in keeps what's cleared here from
    // coming back.
    // They're looked for in the background once the screen's up, as a big collection takes a while to read.
    var imports = new ImportedLibraries();
    var importRecordPath = Path.Combine(AppFolder.Path, "imports.json");
    var importRecord = ImportRecord.Load(importRecordPath);
    // A collection's hot cues, memory cues and beat grids, each for its songs here with none of their own: a
    // Rekordbox grid takes the analysed one's place, as one fixed by hand here would. What came in, as "hot cues for
    // 3 songs, ...", or null if nothing did; and why it couldn't be saved, if it couldn't.
    (string? News, string? Error) ImportCollection(RekordboxCollection collection, HashSet<string> filesThere)
    {
        int hotcues = 0, memoryCues = 0, grids = 0;
        var gridded = new List<string>();
        foreach (var path in collection.Songs)
        {
            if (!filesThere.Contains(path))
                continue;
            if (collection.Hotcues.TryGetValue(path, out var cues) && database.GetHotcues(path).All(cue => cue is null))
            {
                database.SetHotcues(path, cues);
                if (collection.HotcueDetails.TryGetValue(path, out var details))
                    database.SetHotcueDetails(path, details);
                hotcues++;
            }
            if (collection.MemoryCues.TryGetValue(path, out var memory) && database.GetMemoryCues(path).Count == 0)
            {
                database.SetMemoryCues(path, memory);
                if (collection.MemoryLoops.TryGetValue(path, out var loops))
                    database.SetMemoryLoops(path, loops);
                memoryCues++;
            }
            if (collection.Grids.TryGetValue(path, out var grid) && !database.HasGridEdit(path))
            {
                database.SetGridEdit(path, SeratoImport.AsEdit(grid));
                gridded.Add(path);
                grids++;
            }
        }
        UseGrids(gridded);

        static string Songs(int count) => count == 1 ? "1 song" : $"{count} songs";
        var news = new List<string>();
        if (hotcues > 0)
            news.Add($"hot cues for {Songs(hotcues)}");
        if (memoryCues > 0)
            news.Add($"memory cues for {Songs(memoryCues)}");
        if (grids > 0)
            news.Add($"beat grids for {Songs(grids)}");
        if (news.Count == 0)
            return (null, null);
        var said = news.Count == 1 ? news[0] : $"{string.Join(", ", news.Take(news.Count - 1))} and {news[^1]}";
        try
        {
            database.Save();
            return (said, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (said, e.Message);
        }
    }

    // Songs given a beat grid from elsewhere use it straight away: in the library, and on a deck.
    void UseGrids(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
            return;
        foreach (var path in paths)
        {
            if (library.Find(path) is { Analysis: { } analysis } song)
                song.Analysis = database.WithEdits(path, analysis);
        }
        foreach (var deck in decks)
        {
            if (deck.FilePath is { } path && paths.Contains(path, StringComparer.OrdinalIgnoreCase) && deck.Analysis is { } analysis)
                deck.Analysis = database.WithEdits(path, analysis);
        }
    }

    importRecord.Save(importRecordPath);

    // Songs found to be exact copies of each other, for the list of them, and whether a look for those or for moved
    // songs is going on.
    IReadOnlyList<Song> duplicates = [];
    // The songs in Duplicates by their paths, in their groups of copies.
    List<IReadOnlyList<string>> duplicateGroups = [];
    var repairing = false;
    // The screenshot tool, self-test and tour see only their own made-up songs, never those in other drives' files.
    var smartPlaylists = new SmartPlaylists(settings.SmartPlaylists, library, database, analyser, capture is null ? null : () => []);
    // Every set played, kept between runs, this one as it goes.
    var playLog = new PlayLog(Path.Combine(AppFolder.Path, "history"));
    var sessionStarted = DateTime.Now;
    var keptPlays = 0;
    SongQuery.LastPlayed = playLog.LastPlayed;

    // What would mix well next, set once the session's plays are kept: best first, and what it's suggesting for.
    Func<(IReadOnlyList<Song>, string)> suggested = () => ([], "Suggested next");
    // Why each song's suggested, and the key shift it'd load with, by path, as last suggested.
    var suggestedWhy = new Dictionary<string, Suggestion>(StringComparer.OrdinalIgnoreCase);
    var shiftOnLoad = new Dictionary<Deck, int>();
    var folders = LibraryTree.Build(library, decks, history, virtualFolders, IsRecursive, imports, smartPlaylists, () => duplicates, () => suggested(), song => suggestedWhy.GetValueOrDefault(song.Path)?.Why, playLog.Past);
    var smartNode = folders.Single(node => node.Kind == LibraryNodeKind.SmartPlaylists);
    _ = smartNode.Children;
    var virtualFoldersNode = folders.Single(node => node.Kind == LibraryNodeKind.VirtualFolders);

    // Each deck's beat grid can be fixed by hand, with its pads in GRID mode.
    var gridEditors = decks.Select(deck => new GridEditor(deck, library, database)).ToArray();
    GridEditor? GridEditorFor(Deck deck) => gridEditors.FirstOrDefault(editor => editor.Deck == deck);

    // Hot cue and grid changes, to take back one at a time with Ctrl + Z.
    var edits = new EditHistory(database, library, decks);
    foreach (var editor in gridEditors)
        editor.Edited += edits.RecordGrid;

    // And each deck's pads can roll loops instead, with them in ROLL mode.
    var rollPads = gridEditors.Select(editor => new RollPads(editor.Deck, editor)).ToArray();
    RollPads? RollPadsFor(Deck deck) => rollPads.FirstOrDefault(roll => roll.Deck == deck);

    // Or beat jumps, with them in JUMP mode.
    var jumpPads = gridEditors.Select((editor, i) => new JumpPads(editor.Deck, editor, rollPads[i])).ToArray();
    JumpPads? JumpPadsFor(Deck deck) => jumpPads.FirstOrDefault(jump => jump.Deck == deck);

    // Or samples, with them in SAMPLER mode.
    var samplerPads = gridEditors.Select((editor, i) => new SamplerPads(editor.Deck, sampler, editor, rollPads[i], jumpPads[i])).ToArray();
    SamplerPads? SamplerPadsFor(Deck deck) => samplerPads.FirstOrDefault(pads => pads.Deck == deck);

    // Or the song's stems, with them in STEMS mode: separated with Demucs, and kept on disk once they have been.
    var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuepoint");
    using var separator = new StemSeparator(new StemCache(Path.Combine(appData, "stems")), Path.Combine(appData, "models", "htdemucs.onnx"));
    var stemPads = gridEditors.Select((editor, i) => new StemPads(editor.Deck, separator, editor, rollPads[i], jumpPads[i], samplerPads[i])).ToArray();
    // DJ edits of songs, made from their stems: see DjEdit.
    var djEdits = new DjEdits(separator);
    StemPads? StemPadsFor(Deck deck) => stemPads.FirstOrDefault(pads => pads.Deck == deck);

    // Quantise is on for each deck unless it was turned off.
    audio.MicrophoneLevel = settings.MicrophoneLevel;
    audio.Talkover = settings.Talkover;
    if (settings.MicrophoneDevice is { } micDevice && !audio.UseMicrophoneDevice(micDevice))
        Console.Error.WriteLine($"Couldn't open {micDevice} for the microphone");
    if (settings.MasterEffect is { } master)
    {
        audio.MasterEffects.Kind = master.Kind;
        audio.MasterEffects.Level = master.Level;
        audio.MasterEffects.SetBeats(DeckEffect.Echo, master.EchoBeats);
        audio.MasterEffects.SetBeats(DeckEffect.Flanger, master.FlangerBeats);
        audio.MasterEffects.SetBeats(DeckEffect.Gate, master.GateBeats);
    }
    foreach (var deck in decks)
    {
        deck.Quantize = settings.UnquantizedDecks?.Contains(deck.Name) != true;
        if (settings.Effects?.GetValueOrDefault(deck.Name) is { } effects)
        {
            deck.Effects.Kind = effects.Kind;
            deck.Effects.Level = effects.Level;
            deck.Effects.SetBeats(DeckEffect.Echo, effects.EchoBeats);
            deck.Effects.SetBeats(DeckEffect.Flanger, effects.FlangerBeats);
            deck.Effects.SetBeats(DeckEffect.Gate, effects.GateBeats);
        }
    }

    var ui = new Root { TooltipFont = fonts.Body, Scale = screenScale };
    var screen = ui.Add(new MainScreen(audio, decks, library, analyser, folders, $"{database.FilePath}, or \\{LibraryDatabase.DRIVE_FOLDER} on other drives", fonts, images, HotcueKeys.Labels, KeyboardControls.HintsFor, GridEditorFor, RollPadsFor, JumpPadsFor, SamplerPadsFor, StemPadsFor));
    sampler.LoadFailed += (slot, error) => screen.Log($"Couldn't put that on sampler pad {slot + 1}: {error}");
    foreach (var editor in gridEditors)
        editor.Log += screen.Log;
    using var input = new GlfwUiInput(window, ui);
    var browser = screen.Browser;

    // Something going wrong in the app's own code mustn't stop a set: the decks play on their own thread regardless.
    // So it's written to the error log and said on the status line, and the app carries on. The same thing going
    // wrong frame after frame is said once a minute, not sixty times a second.
    var lastFailure = "";
    var lastFailureAt = double.NegativeInfinity;
    void Failed(string doing, Exception e)
    {
        var what = $"{doing}: {e.GetType().Name}: {e.Message}";
        var at = Glfw.Time;
        if (what == lastFailure && at - lastFailureAt < 60)
            return;
        (lastFailure, lastFailureAt) = (what, at);
        ErrorLog.Write(doing, e);
        try
        {
            screen.Log($"Something went wrong {doing} ({e.Message}), but the decks play on. It's written down in {ErrorLog.FilePath}");
        }
        catch (Exception)
        {
            // Even the status line's failing; the log has it.
        }
    }
    input.Failed += e => Failed("handling the mouse or keyboard", e);

    // The virtual folders, lists and filters are open below the drives. What shows is what was chosen last time, if
    // it's still there, opened out to; otherwise the library's own folder. The list is sorted and its columns laid out
    // as they were left.
    foreach (var heading in folders.Where(node => node.Kind is LibraryNodeKind.VirtualFolders || node.Name is "Lists" or "Filters"))
        screen.FolderTree.SetExpanded(heading, true);
    browser.Layout = settings.Browser;
    void ShowFolder(LibraryNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            screen.FolderTree.SetExpanded(parent, true);
        screen.FolderTree.Select(node);
        screen.FolderTree.ScrollIntoView(node);
    }
    var lastFolder = settings.BrowserFolder is { } lastPath ? LibraryTree.Find(folders, lastPath) : null;
    // One in what's imported isn't known of yet, and is gone back to once it is.
    var lastFolderToRestore = lastFolder is null && settings.BrowserFolder is ["Rekordbox" or "Serato", ..] ? settings.BrowserFolder : null;
    if (lastFolder is { HasSongs: true })
        ShowFolder(lastFolder);
    else
        screen.FolderTree.Select(folders[0]);

    // How many songs the library has, or why it couldn't be read, with anything else to say after it.
    void LogLibrary(string? also = null) =>
        screen.Log(
            (
                library.Error is { } error ? $"Couldn't read {library.Root}: {error}"
                : $"{library.Songs.Count} songs in {library.Root}, {library.Songs.Count(song => song.Analysis is not null)} analysed"
            ) + (also is null ? "" : $"; {also}")
        );

    // Once the library's been looked through: what the database knows put on its songs, and its lists, the folders in
    // it already looked in, and the songs listed, all as they are now.
    void AfterLibraryScan()
    {
        analyser.LookUp(library.Songs);
        foreach (var node in folders)
            node.RefreshSongsUnder(LibraryFile.WithSeparator(library.Root));
        if (browser.Source?.Kind is LibraryNodeKind.Library or LibraryNodeKind.List or LibraryNodeKind.Filter or LibraryNodeKind.Folder or LibraryNodeKind.Drive or LibraryNodeKind.VirtualFolder)
            browser.Reload();
    }

    // What other DJ software has, read in the background; its nodes join the folder list when it's in, what came in
    // from it is said, and a playlist or crate chosen last time is gone back to.
    var (importRoot, importChosen) = (library.Root, settings.RekordboxXmlFiles?.ToList());
    library.Background.Start(
        () =>
        {
            var found = ImportedLibraries.Find(importRoot, VirtualFolders.ReadyDrives().Select(drive => drive.Root), importChosen);
            return (Found: found, Files: found.Rekordbox.ToDictionary(collection => collection, FilesThere));
        },
        read => TakeImports(read.Found, read.Files)
    );

    // Recordings a crash left unfinished are mended once the first look through the library's done (set later).
    Action mendRecordings = () => { };

    // The library's looked through in the background, its songs coming in once it's done.
    screen.Log($"Looking through {library.Root}");
    library.BeginScan(
        library.Root,
        library.Recursive,
        () =>
        {
            AfterLibraryScan();
            mendRecordings();
            if (library.Error is null && database.LoadError is { } loadError)
                screen.Log($"Couldn't read {loadError}; it starts afresh");
            else
                LogLibrary();
        }
    );

    // What's chosen in the folder list is kept as it's chosen, not only as the app closes, so it's back next time
    // even if the app doesn't close properly.
    void RememberFolder()
    {
        settings.BrowserFolder = browser.Source is { } shown && shown != folders[0] ? [.. LibraryTree.PathOf(shown)] : null;
        settings.Save(settingsPath);
    }
    screen.FolderTree.SelectionChanged += node =>
    {
        // Another folder chosen, the one made recursive goes back to its own songs; the library stays as it's set.
        if (recursiveNode is { } recursive && recursive != node)
            SetRecursive(recursive, false);
        if (node.HasSongs)
            RememberFolder();
    };

    static HashSet<string> FilesThere(RekordboxCollection collection) => collection.Songs.Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);

    void TakeImports(ImportedLibraries found, Dictionary<RekordboxCollection, HashSet<string>> files)
    {
        imports = found;
        var news = new List<string>();
        foreach (var collection in imports.Rekordbox)
        {
            if (!ImportRecord.IsNew(importRecord.Rekordbox, collection.FilePath))
                continue;
            var (brought, error) = ImportCollection(collection, files[collection]);
            if (brought is not null)
                news.Add($"brought in {brought} from {collection.FilePath}");
            if (error is not null)
                news.Add($"couldn't save them: {error}");
        }
        importRecord.Save(importRecordPath);

        folders.RemoveAll(node => node.Kind == LibraryNodeKind.Group && node.Name is "Rekordbox" or "Serato");
        folders.InsertRange(folders.IndexOf(virtualFoldersNode) + 1, LibraryTree.ImportedNodes(library, imports));
        screen.FolderTree.Roots = folders;
        if (lastFolderToRestore is { } last && browser.Source == folders[0] && LibraryTree.Find(folders, last) is { HasSongs: true } node)
            ShowFolder(node);
        lastFolderToRestore = null;

        if (imports.IsEmpty)
            return;
        var kinds = new List<string>();
        if (imports.Rekordbox.Count > 0)
            kinds.Add(imports.Rekordbox.Count == 1 ? "a Rekordbox collection" : $"{imports.Rekordbox.Count} Rekordbox collections");
        var crates = imports.Serato.Sum(drive => drive.Crates.Count);
        if (crates > 0)
            kinds.Add(crates == 1 ? "a Serato crate" : $"{crates} Serato crates");
        screen.Log(string.Join("; ", [$"Found {string.Join(" and ", kinds)}, in the folder list", .. news]));
    }

    // The folder list's "Rekordbox" node, made if there wasn't one, after the virtual folders; or opened afresh to
    // show what's changed.
    void ShowRekordbox()
    {
        if (folders.FirstOrDefault(node => node.Name == "Rekordbox" && node.Parent is null) is { } existing)
        {
            existing.Refresh();
            screen.FolderTree.Roots = folders;
            return;
        }
        folders.Insert(folders.IndexOf(virtualFoldersNode) + 1, LibraryTree.RekordboxRoot(library, imports));
        screen.FolderTree.Roots = folders;
    }

    // What's to be done once the window's events have been handled, rather than in the middle of one: a dialog of
    // Windows' own, say, which runs its own events while it's open.
    var afterEvents = new Queue<Action>();

    // A Rekordbox collection chosen to import: its playlists in the folder list, and hot cues, memory cues and beat
    // grids for its songs with none here. It's remembered, so it's found again next time, and read again whenever it changes.
    void ImportRekordbox()
    {
        var owner = OperatingSystem.IsWindows() ? WindowChrome.Handle : 0;
        if (WindowsShell.AskForFile(owner, "Import a Rekordbox collection", ("Rekordbox XML", "*.xml"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) is not { } path)
            return;
        if (RekordboxXml.Load(path) is not { } collection)
        {
            screen.Log($"{Path.GetFileName(path)} isn't a Rekordbox collection: export one from Rekordbox with File > Export Collection in xml format");
            return;
        }
        path = collection.FilePath;
        if (settings.RekordboxXmlFiles?.Contains(path, StringComparer.OrdinalIgnoreCase) != true)
        {
            settings.RekordboxXmlFiles = [.. settings.RekordboxXmlFiles ?? [], path];
            settings.Save(settingsPath);
        }
        imports.Put(collection);
        ImportRecord.IsNew(importRecord.Rekordbox, path);
        importRecord.Save(importRecordPath);
        ShowRekordbox();
        if (folders.FirstOrDefault(node => node.Name == "Rekordbox" && node.Parent is null) is { } rekordbox)
            screen.FolderTree.SetExpanded(rekordbox, true);

        var (news, error) = ImportCollection(collection, FilesThere(collection));
        var playlists = Count(collection.Playlists);
        screen.Log(
            $"Imported {Path.GetFileName(path)}: {playlists} {(playlists == 1 ? "playlist" : "playlists")} under Rekordbox, "
                + (news ?? "no cues or grids needed")
                + (error is null ? "" : $"; couldn't save them: {error}")
        );

        static int Count(IEnumerable<RekordboxNode> nodes) => nodes.Sum(node => node.IsFolder ? Count(node.Children) : 1);
    }

    // Any folder can be made the music library. The library's own lists and filters follow it straight away.
    void UseAsMusicLibrary(string path)
    {
        library.BeginScan(
            path,
            library.Recursive,
            scanned: () =>
            {
                AfterLibraryScan();
                LogLibrary();
            }
        );
        WatchLibrary();
        settings.MusicFolder = library.Root;
        settings.Save(settingsPath);
        screen.Options.MusicFolder = library.Root;
        screen.Log($"Looking through {path}");
    }

    // Making a folder recursive lists every song under it, until another is chosen. For the library, it rescans, its
    // lists and filters follow, and it's kept so until it's turned off, between runs too.
    void SetRecursive(LibraryNode node, bool recursive)
    {
        if (node.Kind != LibraryNodeKind.Library)
            recursiveNode = recursive ? node : null;
        if (node.Kind == LibraryNodeKind.Library)
        {
            settings.LibraryRecursive = recursive;
            settings.Save(settingsPath);
            // Said again once it's looked through, as how many songs there are has changed.
            library.BeginScan(
                library.Root,
                recursive,
                () =>
                {
                    AfterLibraryScan();
                    LogLibrary(recursive ? "every song under it listed" : null);
                }
            );
            WatchLibrary();
        }
        else
            node.RefreshSongs();
        if (browser.Source == node || node.Kind == LibraryNodeKind.Library && browser.Source?.Kind is LibraryNodeKind.List or LibraryNodeKind.Filter)
            browser.Reload();
    }

    void ToggleRecursive(LibraryNode node)
    {
        var isLibrary = node.Kind == LibraryNodeKind.Library;
        var recursive = isLibrary ? !library.Recursive : recursiveNode != node;
        SetRecursive(node, recursive);
        var where = isLibrary ? library.Root : node.Path;
        screen.Log(recursive ? $"Listing every song under {where}" : $"Listing only the songs directly in {where}");
    }

    // Songs played out for ten seconds or more get a tick in the list, and make up the tracklist of a recording.
    var plays = new SessionPlays();

    // Suggestions leave out what's been played and what's on a deck.
    suggested = () =>
    {
        if (Suggestions.From(decks) is not { } playing)
            return ([], "Suggested next: load an analysed song to suggest what follows it");
        var onDecks = decks.Select(deck => deck.FilePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggestions = Suggestions.For(playing, smartPlaylists.Everything(), song => plays.Contains(song.Path) || onDecks.Contains(song.Path));
        suggestedWhy.Clear();
        foreach (var suggestion in suggestions)
            suggestedWhy[suggestion.Song.Path] = suggestion;
        var songs = suggestions.Select(suggestion => suggestion.Song).ToList();
        var after = library.SongAt(playing.Path) is { } song ? $"{song.Artist} - {song.Title}" : Path.GetFileNameWithoutExtension(playing.Path);
        return (songs, $"Suggested after {after} ({playing.Key.Camelot}, {playing.Bpm:F1} BPM)");
    };

    // The mix can be recorded, as the room hears it, with the title bar's record button: into a WAV file named for
    // when it started, in the recordings folder.
    var recordingsFolder =
        settings.RecordingsFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Cuepoint Recordings");
    screen.Options.RecordingsFolder = recordingsFolder;
    notWatched = path => path.StartsWith(LibraryFile.WithSeparator(recordingsFolder), StringComparison.OrdinalIgnoreCase);
    var tracklistDialog = new TracklistDialog(ui, fonts.Body, fonts.Bold, fonts.Heading);
    MixRecorder? recorder = null;
    // The last recording made this run, for editing its tracklist.
    string? lastRecording = null;
    void StartRecording()
    {
        var path = Path.Combine(recordingsFolder, $"Mix {DateTime.Now:yyyy-MM-dd HH-mm-ss}.wav");
        try
        {
            recorder = new MixRecorder(path);
            audio.Recorder = recorder;
            screen.Log($"Recording the mix to {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            screen.Log($"Couldn't record the mix in {recordingsFolder}: {e.Message}");
        }
    }
    void StopRecording()
    {
        if (recorder is not { } stopping)
            return;
        // The audio thread lets go of it first, so nothing more comes in as it finishes the file.
        audio.Recorder = null;
        recorder = null;
        stopping.Dispose();
        var files = stopping.Paths;
        lastRecording = stopping.Path;
        var saved = files.Count > 1 ? $"{stopping.Path} and {files.Count - 1} more" : stopping.Path;
        var length = stopping.Duration;
        var time = length.TotalHours >= 1 ? $"{(int)length.TotalHours}:{length.Minutes:00}:{length.Seconds:00}" : $"{length.Minutes}:{length.Seconds:00}";
        var tracklist = SaveTracklist(stopping, files);
        screen.Log(
            stopping.Error is { } error
                ? $"Recording stopped: {error}. What was recorded is in {saved}"
                : $"Saved {time} of the mix in {saved}{(tracklist ? ", with its tracklist and cue sheet" : "")}"
        );
    }

    // Beside each of a recording's files, a cue sheet marking where each song played starts in it; and beside the
    // first, the whole recording's tracklist as text. False if no song was played for long enough to count, or they
    // couldn't be written.
    bool SaveTracklist(MixRecorder recording, IReadOnlyList<string> files) =>
        WriteTracklist(recording.Path, recording.Started, recording.FileDuration, files, plays.History);

    bool WriteTracklist(string first, DateTime started, TimeSpan fileDuration, IReadOnlyList<string> files, IReadOnlyList<(DateTime Started, string Path)> history)
    {
        var all = PlayHistory.Recorded(history, started, TimeSpan.Zero, TimeSpan.MaxValue, library.SongAt);
        if (all.Count == 0)
            return false;
        var failed = PlayHistory.Save(
            Path.ChangeExtension(first, ".txt"),
            PlayHistory.Tracklist(all, Path.GetFileNameWithoutExtension(first), library.SongAt),
            HistoryFormat.Text
        );
        for (var i = 0; i < files.Count; i++)
        {
            var from = fileDuration * i;
            var to = i + 1 < files.Count ? fileDuration * (i + 1) : TimeSpan.MaxValue;
            var tracks = PlayHistory.Recorded(history, started, from, to, library.SongAt);
            if (tracks.Count > 0)
            {
                failed ??= PlayHistory.SaveCueSheet(
                    Path.ChangeExtension(files[i], ".cue"),
                    PlayHistory.CueSheet(tracks, Path.GetFileName(files[i]), started, library.SongAt)
                );
            }
        }
        if (failed is not null)
            screen.Log($"Couldn't save the recording's tracklist: {failed}");
        return failed is null;
    }

    // A recording a crash left unfinished is mended as the app starts, once the library's names are in: its sizes
    // written into its header, and its tracklist, if it hadn't one, from the sets kept as they were played.
    mendRecordings = () =>
    {
        mendRecordings = () => { };
        var mended = MixRecorder.MendAll(recordingsFolder);
        var history = playLog.Past.SelectMany(set => set.Songs).Select(song => (song.Started, song.Path)).ToList();
        foreach (var first in mended.Select(RecordingTracklist.FirstFile).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(first);
            var withTracklist = !File.Exists(Path.ChangeExtension(first, ".cue"))
                && MixRecorder.StartedFrom(first) is { } started
                && WriteTracklist(first, started, MixRecorder.FileLength, RecordingTracklist.Files(first), history);
            screen.Log(withTracklist ? $"Mended {name}, left unfinished by a crash, and wrote its tracklist" : $"Mended {name}, left unfinished by a crash");
        }
    };
    // A recording's tracklist, read back from its cue sheets to change and write again.
    void ChooseTracklist()
    {
        var owner = OperatingSystem.IsWindows() ? WindowChrome.Handle : 0;
        if (WindowsShell.AskForFile(owner, "Edit a recording's tracklist", ("Recordings", "*.wav;*.cue"), recordingsFolder) is { } chosen)
            EditTracklist(chosen);
    }
    void EditTracklist(string path)
    {
        var first = RecordingTracklist.FirstFile(path);
        if (!File.Exists(first))
        {
            screen.Log($"Couldn't find the recording {first}");
            return;
        }
        if (recorder is not null && string.Equals(recorder.Path, first, StringComparison.OrdinalIgnoreCase))
        {
            screen.Log("That's still recording: stop it first, and its tracklist's written");
            return;
        }
        var name = Path.GetFileNameWithoutExtension(first);
        IReadOnlyList<RecordedTrack> tracks;
        DateTime date;
        try
        {
            (tracks, date) = RecordingTracklist.Read(first, MixRecorder.FileLength);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            screen.Log($"Couldn't read the tracklist of {name}: {e.Message}");
            return;
        }
        tracklistDialog.Show(
            name,
            tracks,
            edited =>
            {
                if (RecordingTracklist.Write(first, edited, date, MixRecorder.FileLength) is { } error)
                    return $"Couldn't save it: {error}";
                screen.Log($"Saved the tracklist of {name}: {edited.Count} {(edited.Count == 1 ? "song" : "songs")}");
                return null;
            }
        );
    }

    screen.TitleItems.Recording = () => recorder?.Duration;
    screen.TitleItems.RecordClicked += () =>
    {
        if (recorder is null)
            StartRecording();
        else
            StopRecording();
    };

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
        screen.Log(
            device is null ? "Headphones off"
            : audio.HeadphonesOnOutput ? $"Headphones on {device}, channels 3 and 4"
            : $"Headphones on {device}"
        );
    };
    screen.Options.OutputChanged += device =>
    {
        SaveSettings();
        screen.Log($"Main output on {device ?? "the system default"}{(settings.HeadphoneDevice is null && audio.OutputCanCarryHeadphones ? "; headphones can go on its channels 3 and 4" : "")}");
    };
    if (!outputFound)
        screen.Log($"Couldn't find {settings.OutputDevice} for the main output, so it's on the system default until it's plugged in");
    if (!headphonesFound)
        screen.Log($"Couldn't find {settings.HeadphoneDevice} for the headphones; they'll go on it when it's plugged in");
    if (!boothFound)
        screen.Log($"Couldn't find {settings.BoothDevice} for the booth; it'll go on it when it's plugged in");
    screen.Options.BoothChanged += device =>
    {
        SaveSettings();
        screen.Log(device is null ? "Booth output off" : audio.BoothDevice is null ? $"Couldn't open {device} for the booth; it'll go on it when it can" : $"Booth output on {device}");
    };
    screen.TitleItems.AudioWarning = () => audio.Missing;
    if (OperatingSystem.IsWindows())
        WindowChrome.Install(window, point => screen.TitleBar.IsCaption(new float2(point.X, point.Y) / screenScale));

    // The songs played this session, in order, saved where the DJ says: as an M3U playlist or a plain-text set list,
    // by the type chosen in the Save dialog.
    void ExportHistory() => ExportSet(plays.History, "Nothing's been played yet this session, so there's no history to export");

    // A set, this session's or one before, as a playlist or a set list.
    void ExportSet(IReadOnlyList<(DateTime Started, string Path)> history, string empty)
    {
        if (history.Count == 0)
        {
            screen.Log(empty);
            return;
        }
        (string Description, string Pattern)[] types = [("M3U playlist", "*.m3u8"), ("Plain text", "*.txt")];
        var owner = OperatingSystem.IsWindows() ? WindowChrome.Handle : 0;
        var name = $"Play history {history[0].Started:yyyy-MM-dd}";
        if (WindowsShell.AskWhereToSave(owner, "Export play history", types, name, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) is not { } chosen)
            return;
        var format = chosen.Type == 0 ? HistoryFormat.M3u : HistoryFormat.Text;
        var error = PlayHistory.Save(chosen.Path, PlayHistory.Write(history, format, library.SongAt), format);
        var count = history.Count;
        screen.Log(
            error is not null ? $"Couldn't export the play history: {error}"
            : $"Exported {count} {(count == 1 ? "song" : "songs")} played to {chosen.Path}"
        );
    }
    browser.IsPlayed = song => plays.Contains(song.Path);


    // Decks being put back after the app didn't close properly, and where each goes once its song is in.
    var restoring = new Dictionary<Deck, (double Position, bool Play, int KeyShift)>();

    // Decks to lock to the other again once they've their songs back, analysed, and the other has its: by the song
    // each was locked with, so a song loaded by hand meanwhile isn't locked.
    var relocking = new Dictionary<Deck, string>();
    void Relock()
    {
        foreach (var (deck, path) in relocking.ToList())
        {
            if (!string.Equals(deck.FilePath, path, StringComparison.OrdinalIgnoreCase) && !deck.IsLoading)
                relocking.Remove(deck);
            else if (!deck.IsLoading && deck.CanSync)
            {
                deck.SyncLocked = true;
                relocking.Remove(deck);
            }
        }
    }

    // Each deck's cover, as the renderer's texture of it and its size.
    var covers = new Dictionary<Deck, (int Texture, int Width, int Height)>();

    foreach (var deck in decks)
    {
        deck.Loaded += loaded =>
        {
            // The new song's cover made a texture for the deck to show, and the last song's given back.
            if (covers.Remove(loaded, out var shown))
                renderer.RemoveImage(shown.Texture);
            if (loaded.Cover is { } picture)
                covers[loaded] = (renderer.AddImage(picture.Width, picture.Height, picture.Rgba), picture.Width, picture.Height);
            screen.ShowCover(loaded, covers.TryGetValue(loaded, out var cover) ? cover : null);

            if (restoring.Remove(loaded, out var restore))
            {
                loaded.Seek(TimeSpan.FromSeconds(restore.Position));
                loaded.KeyShift = restore.KeyShift;
                // Not if it would only reach the end at once.
                if (restore.Play && restore.Position < loaded.Duration.TotalSeconds - 1)
                    loaded.Play();
            }
            plays.Restart(loaded);
            var shifted = shiftOnLoad.Remove(loaded, out var shift);
            if (shifted)
                loaded.KeyShift = shift;
            // A file without tags is named as the library knows it, from its folders.
            if (loaded.FilePath is { } named && library.Find(named) is { } known)
                loaded.NameUntagged(known.Title, known.Artist);
            screen.Log(
                (loaded.Artist.Length > 0 ? $"Deck {loaded.Name}: loaded {loaded.Artist} - {loaded.Title}" : $"Deck {loaded.Name}: loaded {loaded.Title}")
                + (shifted ? $", its key shifted {shift:+0;-0} to mix, as suggested" : "")
            );

            // Hot cues and a beat grid Serato kept in the file (an MP3, M4A, FLAC or AIFF) come in the first time it's loaded
            // (and again if it changes), if it has none of its own here.
            if (loaded.FilePath is { } loadedPath && ImportRecord.IsNew(importRecord.Serato, loadedPath))
            {
                var taken = SeratoImport.Take(database, SeratoImport.Read([loadedPath]));
                UseGrids(taken.Gridded);
                if (taken.Hotcues + taken.Grids > 0)
                {
                    var what = taken is { Hotcues: > 0, Grids: > 0 } ? "hot cues and beat grid" : taken.Hotcues > 0 ? "hot cues" : "beat grid";
                    screen.Log($"Deck {loaded.Name}: brought in its {what} from Serato");
                    try
                    {
                        database.Save();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        screen.Log($"Couldn't save its {what} to {database.FilePath}: {e.Message}");
                    }
                }
                importRecord.Save(importRecordPath);
            }
            loaded.Analysis = loaded.FilePath is { } path ? library.Find(path)?.Analysis : null;
            if (loaded.FilePath is { } file)
            {
                loaded.SetHotcues(database.GetHotcues(file));
                loaded.SetHotcueDetails(database.GetHotcueDetails(file));
                loaded.SetMemoryCues(database.GetMemoryCues(file), database.GetCuePoint(file), database.GetMemoryLoops(file));
            }
        };

        // Memory cues are saved as soon as one's added or taken away, and can be undone.
        deck.MemoryCuesChanged += changed =>
        {
            if (changed.FilePath is not { } file)
                return;
            edits.RecordMemoryCues(file, database.GetMemoryCues(file), changed.MemoryCues);
            database.SetMemoryCues(file, changed.MemoryCues);
            try
            {
                database.Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                screen.Log($"Couldn't save the memory cues to {database.FilePath}: {e.Message}");
            }
        };

        // A cue point is saved as soon as it's moved, so it's there next time the song's loaded.
        deck.CuePointChanged += changed =>
        {
            if (changed.FilePath is not { } file)
                return;
            database.SetCuePoint(file, changed.CuePoint);
            try
            {
                database.Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                screen.Log($"Couldn't save the cue point to {database.FilePath}: {e.Message}");
            }
        };

        // A hotcue's name, colour and loop are saved as soon as they're changed, and can be undone.
        deck.HotcueDetailsChanged += changed =>
        {
            if (changed.FilePath is not { } file)
                return;
            database.SetHotcueDetails(file, changed.HotcueDetails);
            try
            {
                database.Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                screen.Log($"Couldn't save the hot cue to {database.FilePath}: {e.Message}");
            }
        };

        // Hotcues are saved as soon as they're set or cleared, so none is lost if the app closes mid-set.
        deck.HotcuesChanged += changed =>
        {
            if (changed.FilePath is not { } file)
                return;
            edits.RecordHotcues(file, database.GetHotcues(file), changed.Hotcues);
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
        // The library's own copy of the song too, if the list had another, as it can after a rescan: a deck loads
        // its analysis from there.
        if (library.Find(song.Path) is { } known && !ReferenceEquals(known, song))
            (known.Analysis, known.AnalysisError) = (song.Analysis, song.AnalysisError);
        // A search by tempo or key finds the song now it has them.
        if (browser.SearchesAnalysis)
            browser.Refilter();
        foreach (var deck in decks)
        {
            if (string.Equals(deck.FilePath, song.Path, StringComparison.OrdinalIgnoreCase))
                deck.Analysis = song.Analysis;
        }
    };

    // A song loaded by hand comes off Up next, if it's waiting there: set once Auto-Mix is made.
    Action<Song>? loadedByHand = null;

    // Shift + 1 or 2: the first song still to come in Up next, onto that deck. Set once Auto-Mix is made.
    Action<Deck>? loadNextUp = null;

    // Returns whether it's loading, as a playing deck can be protected.
    bool Load(Song song, Deck deck, bool byHand = true)
    {
        // One stray key press or drag mustn't stop the music.
        if (settings.ProtectPlayingDecks && deck.IsAudible)
        {
            screen.Log($"Deck {deck.Name} is playing, so {song.Title} wasn't loaded: pause it or turn it down first");
            return false;
        }
        // Analysis goes to the front of the queue, so the deck has its gain and tempo as soon as possible.
        analyser.Request(song);
        // Loaded from Suggested next, a song that only mixes a semitone up or down gets that key shift once it's in.
        if (browser.Source is { Name: "Suggested next" } && suggestedWhy.GetValueOrDefault(song.Path) is { Shift: not 0 } shifted)
            shiftOnLoad[deck] = shifted.Shift;
        else
            shiftOnLoad.Remove(deck);
        deck.BeginLoad(song.Path, $"{song.Artist} - {song.Title}");
        history.Add(song);
        screen.Log($"Deck {deck.Name}: loading {song.Artist} - {song.Title}");
        if (byHand)
            loadedByHand?.Invoke(song);
        return true;
    }

    browser.LoadRequested += (song, deckName) => Load(song, decks.First(deck => deck.Name == deckName));
    screen.SongDropped += (deck, song) => Load(song, deck);


    // The song at a path: a file outside the library becomes one by browsing its folder, as the folder tree would; a
    // folder gives its first song.
    Song? SongFor(string path)
    {
        if (Directory.Exists(path))
            return library.SongsIn(path).FirstOrDefault();
        if (!File.Exists(path) || !MusicLibrary.Extensions.Contains(Path.GetExtension(path)))
            return null;
        if (library.Find(path) is null && Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder)
            library.SongsIn(folder);
        return library.Find(path);
    }

    // Files dragged in from Explorer load on the deck they're dropped on: the first audio file among them, or the
    // first in the first folder dropped.
    input.FilesDropped += (point, paths) =>
    {
        if (screen.DeckAt(point) is not { } deck)
        {
            screen.Log("Drop songs on a deck to load them");
            return;
        }

        var song = paths.Select(SongFor).FirstOrDefault(found => found is not null);
        if (song is null)
            screen.Log(paths.Length == 1 ? $"{Path.GetFileName(paths[0])} isn't a song Cuepoint can play" : "None of those are songs Cuepoint can play");
        else
            Load(song, deck);
    };

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
    // Lighting software, told the beat and what each deck is doing.
    using var lighting = new Os2lLink(decks, audio);
    lighting.Log += screen.Log;
    lighting.Enabled = settings.Os2lEnabled;
    lighting.DirectAddress = settings.Os2lAddress;
    // The waveforms as they were left: zoom, colours, beat grid and height, kept as they're changed.
    screen.Options.WaveformSeconds.Value = settings.WaveformSeconds;
    screen.Options.WaveformColours.Selected = Enum.TryParse<WaveformColours>(settings.WaveformColours, out var colours) ? colours : WaveformColours.Deck;
    screen.Options.WaveformBeatGrid.Checked = settings.WaveformBeatGrid;
    screen.Options.TallWaveforms.Checked = settings.TallWaveforms;
    void KeepWaveformSettings()
    {
        settings.WaveformSeconds = screen.Options.WaveformSeconds.Value;
        settings.WaveformColours = screen.Options.WaveformColours.Selected.ToString();
        settings.WaveformBeatGrid = screen.Options.WaveformBeatGrid.Checked;
        settings.TallWaveforms = screen.Options.TallWaveforms.Checked;
    }
    screen.Options.WaveformsChanged += () =>
    {
        KeepWaveformSettings();
        SaveSettings();
    };

    screen.LargeListText = settings.LargeListText;
    screen.Options.LargeListText = () => screen.LargeListText;
    screen.Options.LargeListTextToggled += () =>
    {
        screen.LargeListText = settings.LargeListText = !screen.LargeListText;
        settings.Save(settingsPath);
    };

    screen.Options.ProtectPlayingDecks.Checked = settings.ProtectPlayingDecks;
    screen.Options.ProtectPlayingDecks.Changed += box =>
    {
        settings.ProtectPlayingDecks = box.Checked;
        SaveSettings();
    };
    // Another tempo range: every song shown afresh in it, on the decks too, and the lists sorted and searched again.
    screen.Options.TempoRangeChanged += min =>
    {
        TempoRange.Min = settings.TempoRangeMin = min;
        SaveSettings();
        analyser.LookAgainAll();
        foreach (var deck in decks)
        {
            if (deck.FilePath is { } path && library.Find(path)?.Analysis is { } shown)
                deck.Analysis = shown;
        }
        browser.Refilter();
        screen.Log($"Tempos shown from {TempoRange.Name(TempoRange.Min)}");
    };
    // A new screen size, taken on the next time the app starts, as the look is.
    (screen.Options.WindowsScale, screen.Options.RunningScale, screen.Options.LargestScale) = (windowsScale, screenScale, largestScale);
    screen.Options.ChosenScreenSize = settings.ScreenScale;
    screen.Options.ScreenSizeChosen += scale =>
    {
        settings.ScreenScale = scale;
        SaveSettings();
        var drawn = screen.Options.Drawn(scale);
        screen.Log(drawn == screenScale
            ? $"The screen will stay at {ScreenScale.Name(drawn, windowsScale)}"
            : $"Restart Cuepoint to draw the screen at {ScreenScale.Name(drawn, windowsScale)}");
    };
    screen.Options.AutoCue.Checked = settings.AutoCue;
    foreach (var deck in decks)
        deck.AutoCue = settings.AutoCue;
    screen.Options.AutoCue.Changed += box =>
    {
        settings.AutoCue = box.Checked;
        foreach (var deck in decks)
            deck.AutoCue = box.Checked;
        SaveSettings();
    };
    screen.Options.AnalyseWhenIdle.Checked = settings.AnalyseWhenIdle;
    screen.Options.AnalyseWhenIdle.Changed += box =>
    {
        settings.AnalyseWhenIdle = box.Checked;
        SaveSettings();
    };
    screen.Options.MasterLimiterToggled += () =>
    {
        settings.MasterLimiter = audio.Limiter.Enabled;
        settings.Save(settingsPath);
    };
    screen.Options.BufferSizeChanged += () =>
    {
        settings.BufferFrames = audio.BufferFrames;
        settings.Save(settingsPath);
    };
    screen.Options.CrossfaderCurveChanged += () =>
    {
        settings.CrossfaderCurve = audio.CrossfaderCurve.ToString();
        settings.Save(settingsPath);
    };

    screen.Options.LightingEnabled = () => lighting.Enabled;
    screen.Options.LightingStatus = () => lighting.Status;
    screen.TitleItems.Os2lConnected = () => lighting.Enabled ? lighting.IsConnected : null;
    screen.Options.LightingAddress.Text = settings.Os2lAddress ?? "";
    screen.Options.LightingToggled += () =>
    {
        lighting.Enabled = settings.Os2lEnabled = !lighting.Enabled;
        SaveSettings();
    };
    screen.Options.LightingAddressChanged += address =>
    {
        settings.Os2lAddress = address.Length == 0 ? null : address;
        lighting.DirectAddress = settings.Os2lAddress;
        SaveSettings();
        screen.Log(settings.Os2lAddress is null ? "Lighting: no address given; finding it by itself" : $"Lighting: connecting to {settings.Os2lAddress} too");
    };

    var menu = new ContextMenu(fonts.Body);
    screen.Options.Menu = menu;
    screen.Menu = menu;

    // The headphones' settings drop down from their button on the title bar, which is lit while a deck's cued.
    var headphones = new HeadphonesPanel(audio, fonts.Small, fonts.Body, fonts.Bold, images);
    screen.TitleItems.HeadphonesClicked += () => headphones.Toggle(screen.TitleItems.HeadphonesAnchor);
    // The effect on the whole mix drops down from its button beside the master meter.
    var masterFx = new MasterEffectsPanel(audio.MasterEffects, fonts.Small, fonts.Body, fonts.Bold, images) { Menu = menu };
    // Not left with the button that opened it focused, so Escape puts it away.
    screen.TitleItems.MasterFxClicked += () =>
    {
        masterFx.Toggle(screen.TitleItems.MasterFxAnchor);
        ui.Focus(null);
    };
    // Live streaming drops down from LIVE beside the record button: the main mix, as it's recorded, to an Icecast
    // server, titled with the song playing.
    var liveStream = new LiveStreamPanel(
        ui,
        new Cuepoint.Streaming.StreamServer(
            settings.StreamHost ?? "",
            settings.StreamPort,
            settings.StreamMount,
            Cuepoint.Streaming.SavedSecret.Unprotect(settings.StreamPassword),
            settings.StreamName ?? "Cuepoint",
            Cuepoint.Streaming.StreamServer.Bitrates.Contains(settings.StreamKbps) ? settings.StreamKbps : 128
        ),
        fonts.Small,
        fonts.Body,
        fonts.Bold
    )
    {
        Menu = menu,
    };
    screen.TitleItems.LiveClicked += () => liveStream.Toggle(screen.TitleItems.LiveAnchor);
    screen.TitleItems.LiveStream = () => liveStream.Stream;
    // What's on air: the last song to count as played, as in the recording's tracklist.
    string OnAir() =>
        plays.History.Count > 0 && library.SongAt(plays.History[^1].Path) is { } song ? (song.Artist.Length > 0 ? $"{song.Artist} - {song.Title}" : song.Title)
        : plays.History.Count > 0 ? Path.GetFileNameWithoutExtension(plays.History[^1].Path)
        : "";
    liveStream.GoLive += server =>
    {
        (settings.StreamHost, settings.StreamPort, settings.StreamMount, settings.StreamName, settings.StreamKbps) = (server.Host, server.Port, server.Mount, server.Name, server.Kbps);
        settings.StreamPassword = Cuepoint.Streaming.SavedSecret.Protect(server.Password) ?? settings.StreamPassword;
        SaveSettings();
        var stream = new Cuepoint.Streaming.LiveStream(server) { Title = OnAir() };
        (liveStream.Stream, audio.Stream) = (stream, stream);
        screen.Log($"Going live to {server.ListenAt}");
    };
    void StopStreaming()
    {
        if (liveStream.Stream is not { } stream)
            return;
        // The audio thread lets go of it first.
        audio.Stream = null;
        liveStream.Stream = null;
        stream.Dispose();
        screen.Log($"Stopped streaming, after {stream.BytesSent / 1024 / 1024.0:F1} MB");
    }
    liveStream.StopRequested += StopStreaming;

    // How the stream's getting on is said as it changes, so the status line never still says it's going live once it
    // is, or isn't. Turned down for good, it's let go, so LIVE offers to go live again.
    Cuepoint.Streaming.StreamState? saidStream = null;
    void FollowStream()
    {
        var stream = liveStream.Stream;
        if (stream?.State == saidStream)
            return;
        saidStream = stream?.State;
        switch (stream)
        {
            case { State: Cuepoint.Streaming.StreamState.Live }:
                screen.Log($"Live at {stream.Server.ListenAt}");
                break;
            case { State: Cuepoint.Streaming.StreamState.Retrying, Problem: { } retrying }:
                screen.Log($"Stream: {retrying}");
                break;
            case { State: Cuepoint.Streaming.StreamState.Stopped }:
                audio.Stream = null;
                liveStream.Stream = null;
                stream.Dispose();
                saidStream = null;
                liveStream.Stopped(stream.Problem ?? "The server let it go");
                screen.Log($"Stopped streaming: {stream.Problem ?? "the server let it go"}");
                break;
        }
    }

    // The microphone's on, level and talkover drop down from MIC beside it.
    var microphone = new MicrophonePanel(audio, fonts.Small, fonts.Body, fonts.Bold);
    screen.TitleItems.MicClicked += () =>
    {
        microphone.Toggle(screen.TitleItems.MicAnchor);
        ui.Focus(null);
    };
    microphone.LevelChanged += level =>
    {
        settings.MicrophoneLevel = level;
        SaveSettings();
    };
    microphone.TalkoverChanged += on =>
    {
        settings.Talkover = on;
        SaveSettings();
    };
    screen.Options.MicrophoneChanged += device =>
    {
        settings.MicrophoneDevice = device;
        SaveSettings();
        screen.Log(device is null ? "Microphone off" : audio.MicrophoneDevice is null ? $"Couldn't open {device} for the microphone" : $"Microphone on {device}: MIC on the title bar puts it in the mix");
    };
    screen.TitleItems.Cueing = () => decks.Any(deck => deck.Headphones);
    browser.ColumnMenuRequested += at => menu.Show(at, browser.ColumnMenu());
    var prompt = new NamePrompt(ui, fonts.Body, fonts.Bold, fonts.Heading);
    var details = new DetailsDialog(ui, fonts.Body, fonts.Bold, fonts.Heading);
    var confirmation = new ConfirmDialog(ui, fonts.Body, fonts.Bold, fonts.Heading);

    // A set hotcue's menu: name it, colour it, make it a hot loop of so many beats, or clear it.
    void ChangeHotcue(Deck deck, int index, string? name, uint? color, double? loopEnd)
    {
        if (deck.FilePath is { } file)
            edits.RecordHotcueDetails(file, deck.HotcueDetails, index);
        deck.SetHotcueDetail(index, name, color, loopEnd);
    }
    // A sampler pad's menu: a deck's loop (or its last bar) sampled into it, kept as a WAV in the app's folder, or it
    // cleared.
    void SampleInto(Deck deck, int slot)
    {
        if (deck.SampleRange() is not { } range || deck.AudioBetween(range.From, range.To) is not { } audio)
        {
            screen.Log($"Deck {deck.Name} has nothing to sample");
            return;
        }
        var what = deck.IsLooping ? "loop" : "bar";
        var name = $"{deck.Title} {what}";
        var file = string.Concat($"{name} {DateTime.Now:yyyy-MM-dd HH-mm-ss}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".wav";
        var path = Path.Combine(AppFolder.Path, "Samples", file);
        if (Sampler.WriteWav(path, audio) is { } error)
        {
            screen.Log($"Couldn't keep the sample: {error}");
            return;
        }
        sampler.Load(slot, path, name);
        screen.Log($"Deck {deck.Name}'s {what} ({(range.To - range.From).TotalSeconds:F1} seconds) is on sampler pad {Sampler.SlotName(slot)}");
    }
    screen.SamplerPadMenuRequested += (slot, at) =>
        menu.Show(
            at,
            [
                .. decks.Select(deck => new MenuItem(
                    $"Sample deck {deck.Name}'s {(deck.IsLooping ? "loop" : "last bar")} here",
                    () => SampleInto(deck, slot),
                    Enabled: deck.IsLoaded
                )),
                MenuItem.Separator,
                new MenuItem($"Take sample {Sampler.SlotName(slot)} off", () => sampler.Clear(slot), Enabled: sampler.NameOf(slot) is not null),
            ]
        );

    // Effects that build to the next phrase: one press, from a deck's FX button's right-click or a controller.
    var phraseEffects = decks.ToDictionary(deck => deck, deck => new PhraseEffects(deck));
    void StartPhraseEffect(Deck deck, PhraseEffect kind) => screen.Log(phraseEffects[deck].Start(kind));
    screen.FxMenuRequested += (deck, at) =>
    {
        var going = phraseEffects[deck].Kind;
        menu.Show(
            at,
            [
                new MenuItem("Echo out to the next phrase", () => StartPhraseEffect(deck, PhraseEffect.EchoOut), Checked: going == PhraseEffect.EchoOut),
                new MenuItem("Filter sweep to the next phrase", () => StartPhraseEffect(deck, PhraseEffect.FilterSweep), Checked: going == PhraseEffect.FilterSweep),
            ]
        );
    };

    // Hot cues suggested for the song on a deck, at its start, drop, breakdown, second drop and outro, from its bass:
    // on the pads that are empty, named and coloured, leaving any set alone; Ctrl+Z takes them back.
    void SuggestHotcues(Deck deck)
    {
        if (deck.FilePath is not { } file || deck.Analysis is not { } analysis || deck.Detail is not { } detail)
        {
            screen.Log($"Deck {deck.Name}: hot cues can be suggested once the song's analysed");
            return;
        }
        var song = new MixSong(analysis, deck.Duration.TotalSeconds, 0, (start, end) =>
        {
            var (peak, bass, mids, treble) = detail.Between(start, end);
            return new BandLevels(peak, bass, mids, treble);
        });
        var bar = 4 * 60 / analysis.Grid.MainBpm;
        var before = deck.Hotcues.ToArray();
        var after = before.ToArray();
        var details = deck.HotcueDetails.ToArray();
        var added = new List<string>();
        foreach (var (name, at) in TransitionPlanner.CuePoints(song))
        {
            // Not one within a bar of a hot cue there already.
            if (after.Any(cue => cue is { } there && Math.Abs(there.TotalSeconds - at) < bar))
                continue;
            if (Array.IndexOf(after, null) is not (>= 0 and var pad))
                break;
            after[pad] = TimeSpan.FromSeconds(at);
            details[pad] = new HotcueDetail(Math.Round(at, 3), name, AppTheme.HotcuePalette[added.Count % AppTheme.HotcuePalette.Count].Hex, null);
            added.Add(name);
        }
        if (added.Count == 0)
        {
            screen.Log($"Deck {deck.Name}: no hot cues to suggest that aren't there already, or the pads are full");
            return;
        }
        edits.RecordHotcues(file, before, after, deck.HotcueDetails.ToArray(), "suggesting hot cues");
        // Kept to the millisecond, as they're saved, so each one's name and colour stays with it.
        var kept = after.Select(cue => cue is { } at ? TimeSpan.FromSeconds(Math.Round(at.TotalSeconds, 3)) : (TimeSpan?)null).ToArray();
        database.SetHotcues(file, kept);
        database.SetHotcueDetails(file, details);
        deck.SetHotcues(kept);
        deck.SetHotcueDetails(details);
        try
        {
            database.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            screen.Log($"Couldn't save the hot cues to {database.FilePath}: {e.Message}");
            return;
        }
        screen.Log($"Deck {deck.Name}: suggested {string.Join(", ", added)}; Ctrl+Z takes them back");
    }

    screen.HotcueMenuRequested += (deck, index, at) =>
    {
        // An empty pad: suggestions for the song's hot cues.
        if (deck.Hotcues[index] is null)
        {
            menu.Show(at, [new MenuItem("Suggest hot cues for this song", () => SuggestHotcues(deck), Enabled: deck.Analysis is not null)]);
            return;
        }
        var detail = deck.DetailOf(index);
        var (name, color, loopEnd) = (detail?.Name, detail?.Color, detail?.LoopEnd);
        var number = index + 1;
        var loopBeats = loopEnd is { } end && deck.Hotcues[index] is { } start && deck.Analysis?.Grid is { } grid
            ? grid.BeatNumberAt(end) - grid.BeatNumberAt(start.TotalSeconds)
            : (double?)null;
        double[] lengths = [1, 2, 4, 8, 16, 32];
        menu.Show(
            at,
            [
                new MenuItem(
                    "Rename...",
                    () => prompt.Show($"Name hot cue {number}", name ?? "", _ => null, given => ChangeHotcue(deck, index, given, color, loopEnd))
                ),
                new MenuItem(
                    "Colour",
                    [
                        new MenuItem("Its pad's own", () => ChangeHotcue(deck, index, name, null, loopEnd), Checked: color is null),
                        MenuItem.Separator,
                        .. AppTheme.HotcuePalette.Select(colour =>
                            new MenuItem(colour.Name, () => ChangeHotcue(deck, index, name, colour.Hex, loopEnd), Checked: color == colour.Hex)
                        ),
                    ]
                ),
                new MenuItem(
                    "Hot loop",
                    [
                        new MenuItem("Not a loop", () => ChangeHotcue(deck, index, name, color, null), Checked: loopEnd is null),
                        MenuItem.Separator,
                        .. lengths.Select(beats =>
                            new MenuItem(
                                beats == 1 ? "1 beat" : $"{beats} beats",
                                () => ChangeHotcue(deck, index, name, color, deck.HotLoopEnd(index, beats)),
                                Enabled: deck.HotLoopEnd(index, beats) is not null,
                                Checked: loopBeats is { } length && Math.Abs(length - beats) < 0.01
                            )
                        ),
                    ]
                ),
                MenuItem.Separator,
                new MenuItem($"Clear hot cue {number}", () => deck.DeleteHotcue(index)),
            ]
        );
    };

    // Everything set up, in one file to keep or take to another computer, and back again.
    var appFolder = AppFolder.Path;
    void BackUpSettings()
    {
        SaveSettings();
        var owner = OperatingSystem.IsWindows() ? WindowChrome.Handle : 0;
        if (WindowsShell.AskWhereToSave(owner, "Back up Cuepoint's settings", [("Cuepoint settings", "*.zip")], $"Cuepoint settings {DateTime.Now:yyyy-MM-dd}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) is not { } chosen)
            return;
        var (count, error) = SettingsBackup.Write(chosen.Path, appFolder);
        screen.Log(error is not null ? $"Couldn't back up the settings: {error}" : $"Backed up {count} files of settings to {chosen.Path}");
    }
    void RestoreSettings()
    {
        var owner = OperatingSystem.IsWindows() ? WindowChrome.Handle : 0;
        if (WindowsShell.AskForFile(owner, "Restore Cuepoint's settings", ("Cuepoint settings", "*.zip"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) is not { } path)
            return;
        confirmation.Show(
            "Restore these settings?",
            $"Everything set up now is replaced by what's in {Path.GetFileName(path)}. Cuepoint closes to take it in; start it again after. Your songs, their analysis and cues aren't touched.",
            "Restore and close",
            () =>
            {
                var (count, error) = SettingsBackup.Restore(path, appFolder);
                if (error is not null)
                {
                    screen.Log($"Couldn't restore the settings: {error}");
                    return;
                }
                settingsRestored = true;
                screen.Log($"Restored {count} files of settings; closing to take them in");
                window.ShouldClose = true;
            }
        );
    }
    screen.Options.LookChosen += kind =>
    {
        settings.Theme = kind == ThemeKind.Dark ? null : kind.ToString();
        SaveSettings();
        screen.Log(kind == Theme.Kind ? $"{Theme.NameOf(kind)} it is" : $"Cuepoint will look {Theme.NameOf(kind).ToLowerInvariant()} the next time it starts");
    };
    screen.Options.BackUpClicked += () =>
    {
        screen.Options.Close();
        afterEvents.Enqueue(BackUpSettings);
    };
    screen.Options.RestoreClicked += () =>
    {
        screen.Options.Close();
        afterEvents.Enqueue(RestoreSettings);
    };

    var controls = new KeyboardControls(decks, audio);

    // Panic: every deck's EQ, filter, trim, key, effect and loop, and the master effect, back to neutral at once, the
    // music playing on and the faders where they are; Ctrl+Z puts the settings back.
    void PanicNow()
    {
        // An echo out or sweep going stops first, putting back what it changed, so it doesn't undo the panic.
        foreach (var phrase in phraseEffects.Values)
            phrase.Stop();
        var panic = Panic.Reset(decks, audio.MasterEffects);
        foreach (var deck in decks)
            controls.ResetEq(deck);
        if (panic.Changed == 0)
        {
            screen.Log("Panic: everything's at neutral already");
            return;
        }
        edits.RecordLibrary("the panic", panic.Undo);
        screen.Log($"Panic: {panic.Changed} {(panic.Changed == 1 ? "thing" : "things")} back to neutral; Ctrl+Z puts them back");
    }


    // DJ controllers, found as they're plugged in. The load buttons take the selected song; the browse knob works
    // the list (and with Shift, the folders) as the arrow keys do.
    // Not for the screenshot tool, which leaves the user's controllers alone.
    using var midiDevices = OperatingSystem.IsWindows() && capture is null ? new MidiDevices(new WinMidi()) : null;
    ControllerDialog? controllerDialog = null;
    // The tempo sent out as MIDI clock, on the output chosen in the options.
    using var midiClock = OperatingSystem.IsWindows() && capture is null ? new MidiClockPort(new WinMidi(), midiDevices) : null;
    if (midiClock is not null)
    {
        if (settings.MidiClockOutput is { } clockOut && !midiClock.Use(clockOut))
            screen.Log($"Couldn't open {clockOut} for MIDI clock; is it plugged in, or open in something else?");
        screen.Options.MidiClockOutputs = midiClock.Outputs;
        screen.Options.MidiClockOutput = () => midiClock.Name;
        screen.Options.MidiClockChanged += name =>
        {
            var opened = midiClock.Use(name);
            settings.MidiClockOutput = opened ? name : null;
            SaveSettings();
            screen.Log(
                name is null ? "MIDI clock off"
                : opened ? $"MIDI clock out of {name}: the leading deck's tempo, started on its next downbeat"
                : $"Couldn't open {name} for MIDI clock; is it open in something else?"
            );
        };
    }
    using var controllers = midiDevices is null
        ? null
        : new MidiControls(midiDevices, decks, audio, Path.Combine(AppFolder.Path, "controllers"));
    if (controllers is not null)
    {
        controllers.Log += screen.Log;
        controllers.LoadRequested += deck =>
        {
            if (browser.SelectedSong is { } song)
                Load(song, deck);
            else
                screen.Log("Pick a song in the list to load");
        };
        void Step(Element element, int steps)
        {
            for (var i = 0; i < Math.Abs(steps); i++)
                element.KeyDown(steps > 0 ? Key.Down : Key.Up, Modifiers.None);
        }
        controllers.Browse += steps => Step(browser.List, steps);
        controllers.BrowseFolders += steps => Step(screen.FolderTree, steps);
        controllers.BrowsePressed += () => screen.FolderTree.KeyDown(Key.Enter, Modifiers.None);
        controllers.PanicPressed += PanicNow;
        controllers.PhraseEffectPressed += (deck, kind) => StartPhraseEffect(deck, kind);

        // In GRID mode the controller's pads are the grid tools too, Shift + pad making a nudge a fine one.
        controllers.PadPressed = (deck, pad, shifted) =>
        {
            if (RollPadsFor(deck) is { Active: true } roll && !shifted)
            {
                roll.Down(pad);
                return true;
            }
            if (JumpPadsFor(deck) is { Active: true } jump && !shifted)
            {
                jump.Down(pad);
                return true;
            }
            if (SamplerPadsFor(deck) is { Active: true } samples)
            {
                if (shifted)
                    samples.Clear(pad);
                else
                    samples.Down(pad);
                return true;
            }
            if (GridEditorFor(deck) is not { Active: true } editor)
                return false;
            editor.Pad(pad, fine: shifted);
            return true;
        };
        controllers.PadReleased = (deck, pad) => RollPadsFor(deck)?.Up(pad);

        // The controller's roll mode button turns ROLL mode on and off, its pads then rolling loops; HOT CUE puts
        // them back to hotcues, from ROLL or GRID.
        controllers.RollModePressed += deck =>
        {
            if (RollPadsFor(deck) is { } roll)
                roll.Active = !roll.Active;
        };
        controllers.HotcueModePressed += deck =>
        {
            if (RollPadsFor(deck) is { } roll)
                roll.Active = false;
            if (GridEditorFor(deck) is { } editor)
                editor.Active = false;
            if (JumpPadsFor(deck) is { } jump)
                jump.Active = false;
            if (SamplerPadsFor(deck) is { } samples)
                samples.Active = false;
        };
        controllers.IsRollMode = deck => RollPadsFor(deck)?.Active == true;

        // Shift + HOT CUE puts the controller's pads in its beat jump mode, and the deck's in JUMP mode to match.
        controllers.JumpModePressed += deck =>
        {
            if (JumpPadsFor(deck) is { } jump)
                jump.Active = true;
        };
        controllers.IsJumpMode = deck => JumpPadsFor(deck)?.Active == true;

        // The controller's SAMPLER button puts the deck's pads in SAMPLER mode to match its own.
        // SAMPLER puts the pads in sampler mode; pressed again, it flips to the other bank of eight.
        controllers.SamplerModePressed += deck =>
        {
            if (SamplerPadsFor(deck) is not { } samples)
                return;
            if (samples.Active)
            {
                samples.Bank = 1 - samples.Bank;
                screen.Log($"Deck {deck.Name}'s pads play sampler bank {samples.Bank + 1}");
            }
            samples.Active = true;
        };
        controllers.IsSamplerMode = deck => SamplerPadsFor(deck)?.Active == true;
        controllers.GridModePressed += deck =>
        {
            if (GridEditorFor(deck) is { } editor)
                editor.Active = !editor.Active;
        };
        controllers.IsGridMode = deck => GridEditorFor(deck)?.Active == true;

        // Mapping, opened from the options, over everything but menus.
        controllerDialog = ui.Add(new ControllerDialog(controllers, midiDevices!, fonts.Body, fonts.Bold, fonts.Heading));
        screen.Options.ControllerStatus = () => midiDevices!.Devices.Count == 0 ? null : string.Join(", ", midiDevices.Devices.Select(device => device.Name));
        screen.Options.MapControllerClicked += () =>
        {
            screen.Options.Close();
            controllerDialog.Open();
        };
    }
    // Enter in the search box goes on to the list, on its first song if none's chosen, so 1 and 2 load from it and the
    // arrow keys move through it, rather than typing into the box.
    browser.SearchBox.Submitted += _ =>
    {
        if (browser.List.SelectedItem is null && browser.List.Items.Count > 0)
            browser.List.SelectedIndex = 0;
        ui.Focus(browser.List);
    };

    // Changing keys: the next key pressed goes to the one listening for it, and no key works anything meanwhile.
    screen.Keys.Bindings = () => keyMap.Bindings;
    screen.Keys.Chosen = (id, key) =>
    {
        KeyBinding? swapped;
        try
        {
            swapped = keyMap.Set(id, key);
        }
        catch (ArgumentException e)
        {
            return e.ParamName is null ? e.Message : e.Message[..e.Message.LastIndexOf(" (Parameter", StringComparison.Ordinal)];
        }
        keyMap.Apply();
        screen.RefreshKeys();
        if (!settingsRestored && keyMap.Save(keysPath) is { } error)
            return $"Couldn't keep the keys: {error}";
        return swapped is null ? $"{DeckKeys.Label(key)} it is" : $"{DeckKeys.Label(key)} it is; {swapped.Name.ToLowerInvariant()}{(swapped.Deck >= 0 ? $" on deck {(char)('A' + swapped.Deck)}" : "")} is on {DeckKeys.Label(swapped.Key)} now";
    };
    screen.Keys.ResetClicked += () =>
    {
        keyMap = KeyMap.Usual();
        keyMap.Apply();
        screen.RefreshKeys();
        if (!settingsRestored)
            keyMap.Save(keysPath);
    };

    input.UnhandledKey += OnUnhandledKey;

    // A key the UI didn't use; the feature tour presses keys through here too.
    void OnUnhandledKey(GlfwKey key, GlfwKeyAction action, GlfwModifier mods)
    {
        if (screen.Keys.Visible)
        {
            if (action == GlfwKeyAction.Press)
                screen.Keys.Learn(key);
            return;
        }

        // The arrow keys work the lists wherever the focus is, as a controller's browse knob does, repeating as
        // they're held.
        if (!screen.Options.Visible && !screen.Shortcuts.Visible && BrowseKey(key, mods) is var (target, browseKey))
        {
            if (action != GlfwKeyAction.Release)
                target.KeyDown(browseKey, Modifiers.None);
            return;
        }

        if (action == GlfwKeyAction.Repeat)
            return;
        var pressed = action == GlfwKeyAction.Press;

        // ? shows every shortcut, or hides them again.
        if (key == KeyboardShortcuts.SHOW && (mods & GlfwModifier.Shift) != 0)
        {
            if (pressed)
            {
                screen.Options.Close();
                screen.Shortcuts.Toggle();
            }
            return;
        }

        if (key == GlfwKey.F && (mods & GlfwModifier.Control) != 0)
        {
            if (pressed)
            {
                ui.Focus(browser.SearchBox);
                browser.SearchBox.SelectAll();
            }
            return;
        }

        // Panic: everything colouring the sound back to neutral.
        if (key == GlfwKey.Backspace && (mods & GlfwModifier.Control) != 0)
        {
            if (pressed)
                PanicNow();
            return;
        }

        // Before the decks' keys, as Z on its own is deck A's cue.
        if (key == GlfwKey.Z && (mods & GlfwModifier.Control) != 0)
        {
            if (pressed)
                screen.Log(edits.Undo());
            return;
        }

        if (key == GlfwKey.P && (mods & GlfwModifier.Control) != 0)
        {
            if (pressed && browser.SelectedSong is { } selected)
                TogglePreview(selected);
            return;
        }

        // Each deck's hotcue keys act as its pads do: set or jump, or with Shift, clear; or in GRID mode, its tools,
        // Shift making a nudge a fine one; or in ROLL mode, loop rolls while they're held; or in JUMP mode, beat jumps.
        if (HotcueKeys.For(key) is { } hotcue)
        {
            // A roll lasts while its key's held, whatever mode the pads are in by the time it's let go, as does a hotcue
            // held under slip.
            if (!pressed && hotcue.Deck < decks.Length)
            {
                rollPads[hotcue.Deck].Up(hotcue.Hotcue);
                decks[hotcue.Deck].HotcueUp(hotcue.Hotcue);
            }
            else if (pressed && hotcue.Deck < decks.Length && rollPads[hotcue.Deck].Active)
                rollPads[hotcue.Deck].Down(hotcue.Hotcue);
            else if (pressed && hotcue.Deck < decks.Length && gridEditors[hotcue.Deck].Active)
                gridEditors[hotcue.Deck].Pad(hotcue.Hotcue, fine: (mods & GlfwModifier.Shift) != 0);
            else if (pressed && hotcue.Deck < decks.Length && jumpPads[hotcue.Deck].Active)
                jumpPads[hotcue.Deck].Down(hotcue.Hotcue);
            else if (pressed && hotcue.Deck < decks.Length && samplerPads[hotcue.Deck].Active)
            {
                if ((mods & GlfwModifier.Shift) != 0)
                    samplerPads[hotcue.Deck].Clear(hotcue.Hotcue);
                else
                    samplerPads[hotcue.Deck].Down(hotcue.Hotcue);
            }
            else if (pressed && hotcue.Deck < decks.Length)
            {
                if ((mods & GlfwModifier.Shift) != 0)
                    decks[hotcue.Deck].DeleteHotcue(hotcue.Hotcue);
                else
                    decks[hotcue.Deck].HotcueDown(hotcue.Hotcue);
            }
            return;
        }

        controls.Key(key, action, mods);

        switch (key)
        {
            case GlfwKey.Escape when pressed:
                if (menu.Visible)
                    menu.Close();
                else if (headphones.Visible)
                    headphones.Close();
                else if (masterFx.Visible)
                    masterFx.Close();
                else if (microphone.Visible)
                    microphone.Close();
                else if (liveStream.Visible)
                    liveStream.Close();
                else if (prompt.Visible)
                    prompt.Close();
                else if (confirmation.Visible)
                    confirmation.Close();
                else if (tracklistDialog.Visible)
                    tracklistDialog.Close();
                else if (screen.Shortcuts.Visible)
                    screen.Shortcuts.Close();
                else if (controllerDialog?.Escape() != true)
                    screen.Options.Close();
                break;
            case GlfwKey.Number1 when pressed && (mods & GlfwModifier.Shift) != 0:
                loadNextUp?.Invoke(deckA);
                break;
            case GlfwKey.Number2 when pressed && (mods & GlfwModifier.Shift) != 0:
                loadNextUp?.Invoke(deckB);
                break;
            case GlfwKey.Number1 when pressed && browser.SelectedSong is { } song:
                Load(song, deckA);
                break;
            case GlfwKey.Number2 when pressed && browser.SelectedSong is { } song:
                Load(song, deckB);
                break;
        }
    }

    // Where a key moves through the lists, as KeyboardShortcuts lists them: the song list's on its own, the folder
    // list's with Shift. Enter loads the chosen song onto a free deck, or opens out the chosen folder.
    (Element Target, Key Key)? BrowseKey(GlfwKey key, GlfwModifier mods)
    {
        if ((mods & (GlfwModifier.Control | GlfwModifier.Alt | GlfwModifier.Super)) != 0)
            return null;
        Key? browse = key switch
        {
            GlfwKey.Up => Key.Up,
            GlfwKey.Down => Key.Down,
            GlfwKey.Left => Key.Left,
            GlfwKey.Right => Key.Right,
            GlfwKey.PageUp => Key.PageUp,
            GlfwKey.PageDown => Key.PageDown,
            GlfwKey.Home => Key.Home,
            GlfwKey.End => Key.End,
            GlfwKey.Enter or GlfwKey.KpEnter => Key.Enter,
            _ => null,
        };
        if (browse is not { } k)
            return null;
        if ((mods & GlfwModifier.Shift) != 0)
            return k is Key.PageUp or Key.PageDown ? null : (screen.FolderTree, k);
        return k is Key.Left or Key.Right ? null : (browser.List, k);
    }

    // Right-click menus and the name prompt go over everything, so they're added last.
    ui.Add(headphones);
    ui.Add(masterFx);
    ui.Add(microphone);
    ui.Add(liveStream);
    ui.Add(menu);
    ui.Add(prompt);
    ui.Add(details);
    ui.Add(confirmation);
    ui.Add(tracklistDialog);

    // Virtual folders: made from the heading's menu, renamed and deleted from their own, filled by dragging songs
    // onto them, and edited in the list. The list follows whatever changes.
    LibraryNode NodeFor(VirtualFolder folder) => virtualFoldersNode.Children.First(node => node.Folder == folder);
    void FoldersChanged()
    {
        virtualFoldersNode.Refresh();
        screen.FolderTree.Refresh();
    }
    virtualFolders.Changed += _ =>
    {
        if (browser.Source?.Kind == LibraryNodeKind.VirtualFolder)
            browser.Reload();
    };

    smartPlaylists.Changed += () =>
    {
        smartNode.Refresh();
        _ = smartNode.Children;
        screen.FolderTree.Refresh();
        SaveSettings();
    };

    // What to find, then what to call it, starting from whatever's in the search box.
    void NewSmartPlaylist() =>
        prompt.Show(
            "What should the smart playlist find?",
            browser.SearchText,
            SmartPlaylists.CheckSearch,
            search =>
                afterEvents.Enqueue(() =>
                    prompt.Show("Name the smart playlist", search, name => smartPlaylists.CheckName(name), name => screen.FolderTree.Select(SmartNodeFor(smartPlaylists.Add(name, search))))
                )
        );

    LibraryNode SmartNodeFor(SmartPlaylist list)
    {
        smartNode.Refresh();
        screen.FolderTree.SetExpanded(smartNode, true);
        return smartNode.Children.Single(node => node.Smart == list);
    }

    void NewVirtualFolder() =>
        prompt.Show(
            "New virtual folder",
            virtualFolders.UnusedName(),
            name => virtualFolders.CheckName(name),
            name =>
            {
                var folder = virtualFolders.Create(name);
                FoldersChanged();
                screen.FolderTree.SetExpanded(virtualFoldersNode, true);
                // It's shown, not opened, so the songs to put in it are still listed to drag across.
                screen.FolderTree.ScrollIntoView(NodeFor(folder));
                screen.Log($"Made {folder.Name}; drag songs onto it to add them");
            }
        );

    void RenameVirtualFolder(VirtualFolder folder) =>
        prompt.Show(
            $"Rename {folder.Name}",
            folder.Name,
            name => virtualFolders.CheckName(name, folder),
            name =>
            {
                var old = folder.Name;
                virtualFolders.Rename(folder, name);
                FoldersChanged();
                screen.Log($"Renamed {old} to {folder.Name}");
            }
        );

    void DeleteVirtualFolder(VirtualFolder folder)
    {
        try
        {
            var showing = browser.Source?.Folder == folder;
            var (name, drive, paths) = (folder.Name, folder.Drive, folder.Paths.ToList());
            virtualFolders.Delete(folder);
            FoldersChanged();
            if (showing || screen.FolderTree.SelectedItem?.Folder == folder)
                screen.FolderTree.Select(virtualFoldersNode);
            if (showing)
                browser.Source = folders[0];
            edits.RecordLibrary(
                $"deleting {name}",
                () =>
                {
                    virtualFolders.Recreate(name, drive, paths);
                    FoldersChanged();
                }
            );
            screen.Log($"Deleted {folder.Name}; its songs are untouched, and Ctrl + Z brings it back");
        }
        catch (InvalidOperationException e)
        {
            screen.Log($"Couldn't delete {folder.Name}: {e.Message}");
        }
    }

    void MoveVirtualFolder(VirtualFolder folder, StorageDrive drive)
    {
        try
        {
            virtualFolders.MoveToDrive(folder, drive.Root);
            FoldersChanged();
            screen.Log(folder.Drive is null ? $"Moved {folder.Name} to the app's folder" : $"Moved {folder.Name} to {Path.GetDirectoryName(folder.FilePath)}");
        }
        catch (InvalidOperationException e)
        {
            screen.Log($"Couldn't move {folder.Name} to {drive.Label}: {e.Message}");
        }
    }

    // Drives come and go: a virtual folder kept on one shows up when it's plugged in, and goes when it's taken out.
    var nextDriveScan = 0.0;
    void ScanDrives(double now)
    {
        if (now < nextDriveScan)
            return;
        nextDriveScan = now + 3;
        if (!virtualFolders.Rescan())
            return;
        var showing = browser.Source?.Folder;
        FoldersChanged();
        if (showing is not null && !virtualFolders.All.Contains(showing))
        {
            screen.FolderTree.Select(virtualFoldersNode);
            browser.Source = folders[0];
        }
    }

    // Serato's hot cues and beat grids for many songs at once, from a menu: the files are looked in for them in the
    // background, as a big folder's would take a while, then each song gets what it has none of here. Songs keep
    // their own hot cues, and a grid fixed here. What it's for is named in what's said: a folder or list ("Disco"), the songs chosen ("3 songs"), or
    // one song by its title, with oneSong.
    void ImportSeratoCues(string what, Func<IReadOnlyList<string>> paths, bool oneSong = false)
    {
        screen.Log(oneSong ? $"Looking for Serato cues and a grid in {what}..." : $"Looking for Serato cues and grids in the songs in {what}...");
        library.Background.Start(
            () =>
            {
                var all = paths();
                return (Count: all.Count, Found: SeratoImport.Read(all));
            },
            result =>
            {
                var taken = SeratoImport.Take(database, result.Found);
                UseGrids(taken.Gridded);
                foreach (var path in result.Found.Select(song => song.Path))
                {
                    // Looked in now, so not again when it's next loaded.
                    ImportRecord.IsNew(importRecord.Serato, path);
                    // A song on a deck shows what it has now straight away.
                    foreach (var deck in decks)
                    {
                        if (string.Equals(deck.FilePath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            deck.SetHotcues(database.GetHotcues(path));
                            deck.SetHotcueDetails(database.GetHotcueDetails(path));
                        }
                    }
                }
                importRecord.Save(importRecordPath);
                string? error = null;
                if (taken.Hotcues + taken.Grids > 0)
                {
                    try
                    {
                        database.Save();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        error = e.Message;
                    }
                }
                static string Songs(int count) => count == 1 ? "1 song" : $"{count} songs";
                var saving = error is null ? "" : $"; couldn't save them: {error}";
                var news = taken.News(Songs);
                screen.Log(
                    oneSong ? (result.Found.Count == 0 ? $"{what} has no Serato cues or grid"
                        : taken.Hotcues + taken.Grids > 0
                            ? $"Brought in Serato's {(taken is { Hotcues: > 0, Grids: > 0 } ? "hot cues and beat grid" : taken.Hotcues > 0 ? "hot cues" : "beat grid")} for {what}"
                                + (taken.Kept > 0 ? "; kept its own of the rest" : "")
                                + saving
                        : $"{what} has its own here already, so kept them rather than Serato's")
                    : result.Count == 0 ? $"There are no songs in {what} to look in"
                    : result.Found.Count == 0 ? $"None of the {Songs(result.Count)} in {what} has Serato cues or a grid"
                    : (news is null ? $"Nothing new from Serato for the {Songs(result.Count)} in {what}" : $"Brought in Serato's {news}, of the {Songs(result.Count)} in {what}")
                        + (taken.Kept > 0 ? $"; {Songs(taken.Kept)} had their own here already, so kept them" : "")
                        + saving
                );
            }
        );
    }

    // What a node in the folder list imports Serato's cues for: a folder or drive, every song in it and its folders
    // (looked for in the background); anything else with songs, those it lists now.
    MenuItem SeratoCuesFor(LibraryNode node, string text = "Import Serato cues and grids")
    {
        if (node is { Kind: LibraryNodeKind.Folder or LibraryNodeKind.Drive, Path: { } path })
            return new MenuItem(text, () => ImportSeratoCues(node.Name, () => MusicLibrary.ListFolder(path, recursive: true).Select(file => file.FullName).ToList()));
        var listed = node.SongPaths?.Invoke().ToList() ?? node.Songs.Select(song => song.Path).ToList();
        return new MenuItem(text, () => ImportSeratoCues(node.Name, () => listed), Enabled: listed.Count > 0);
    }

    // A list copied to a removable drive to take away, one at a time, in the background. The bar and what it's doing
    // show under the song list, with a cross to stop it. Once the songs are there, the drive gets what's known about
    // them in its library file and a playlist of them as a virtual folder.
    DriveExport? export = null;
    browser.ExportProgress = () => export is { } running ? (running.Fraction, running.Status) : null;
    browser.ExportCancelled += () => export?.Cancel();

    void StartExport(string name, IReadOnlyList<Song> songs, StorageDrive drive)
    {
        var job = new DriveExport(name, songs, drive.Root);
        export = job;
        library.Background.Start(job.Run, result => FinishExport(job, drive, result));
    }

    void FinishExport(DriveExport job, StorageDrive drive, DriveExport.Result result)
    {
        export = null;
        static string Songs(int count) => count == 1 ? "1 song" : $"{count} songs";
        if (result.Cancelled)
        {
            screen.Log($"Stopped exporting {job.FileName}; what's been copied stays on {drive.Label}, and exporting again carries on from there");
            return;
        }
        if (result.Error is { } problem)
        {
            screen.Log($"Couldn't export {job.FileName} to {drive.Label}: {problem}");
            return;
        }
        if (result.Songs.Count == 0)
        {
            screen.Log($"Couldn't copy any of {job.FileName}'s songs to {drive.Label}");
            return;
        }

        DriveExport.Record(database, result.Songs);
        string? saving = null;
        try
        {
            database.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            saving = e.Message;
        }
        if (!database.EnsureDriveFile(drive.Root))
            saving ??= "its library file couldn't be written";
        if (virtualFolders.WriteToDrive(job.FileName, drive.Root, result.Songs.Select(song => song.Path).ToList()) is { } playlistError)
            saving ??= playlistError;
        FoldersChanged();
        screen.Log(
            $"Exported {Songs(result.Songs.Count)} to {job.Folder}"
                + (result.Failed.Count > 0 ? $"; {Songs(result.Failed.Count)} couldn't be copied" : "")
                + (saving is null ? "" : $"; {saving}")
        );
    }

    // The menu item for a list: a drive to export it to, from those it isn't kept on. Exporting over a playlist the
    // drive has of that name asks first.
    // Plays a list by itself on the two decks, in its own panel beside the song list.
    var autoMix = new AutoMix(
        [new MixDeck(deckA, 0), new MixDeck(deckB, 1)],
        () => audio.Crossfader,
        value => audio.Crossfader = value,
        (song, deck) => Load(song, ((MixDeck)deck).Deck, byHand: false)
    );
    loadedByHand = song => autoMix.TakeLoaded(song.Path);

    // Songs put on Up next: on the end of whatever list Auto-Mix has, analysed first, as it'll want them.
    void AddToUpNext(IReadOnlyList<Song> songs)
    {
        if (songs.Count == 0)
            return;
        analyser.LookUp(songs);
        analyser.Request(songs);
        autoMix.Add(songs);
        screen.ShowAutoMix();
        screen.Log(songs.Count == 1 ? $"Added {songs[0].Title} to {autoMix.Name}" : $"Added {songs.Count} songs to {autoMix.Name}");
    }
    screen.AutoMix.SongsDropped += AddToUpNext;
    // When the set should end, for Auto-Mix to pace itself to.
    screen.AutoMix.EndRequested += () =>
        prompt.Show(
            "When should the set end? (23:30, or +45 for 45 minutes from now; blank for no end)",
            autoMix.EndAt?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            text => AutoMix.TryParseEnd(text, DateTime.Now, out _) ? null : "Give a time, as 23:30, or minutes from now, as +45",
            text =>
            {
                AutoMix.TryParseEnd(text, DateTime.Now, out var end);
                autoMix.EndAt = end;
                screen.Log(
                    end is not { } ending ? $"{autoMix.Name} has no end time"
                    : autoMix.Over > 0 ? $"{autoMix.Name} ends at {ending:HH:mm}: it would run {Math.Ceiling(autoMix.Over / 60):0} min over, so it'll mix out of each song sooner"
                    : $"{autoMix.Name} ends at {ending:HH:mm}; the list finishes at {autoMix.FinishesAt:HH:mm}"
                );
            }
        );
    screen.AutoMix.OrderRequested += how =>
    {
        if (autoMix.Order(how) is not { } before)
        {
            screen.Log("There aren't two songs still to come to put in order");
            return;
        }
        var shuffled = how == SongOrder.Shuffle;
        var what = how == SongOrder.Mixing ? "for mixing" : MixOrder.Name(how).ToLowerInvariant();
        edits.RecordLibrary(shuffled ? $"shuffling {autoMix.Name}" : $"ordering {autoMix.Name} {what}", () => autoMix.PutBack(before));
        screen.Log(shuffled ? $"Shuffled {autoMix.Name}" : $"Put {autoMix.Name} in order {what}");
    };

    // It comes off the list as it loads.
    loadNextUp = deck =>
    {
        if (autoMix.NextWaiting is { } song)
            Load(song, deck);
        else
            screen.Log(autoMix.Songs.Count == 0 ? "Up next is empty: right-click songs and choose Add to Up next" : "Nothing's waiting in Up next");
    };
    autoMix.Log += screen.Log;
    screen.AutoMix.Mix = autoMix;
    screen.AutoMix.PlayPressed += () =>
    {
        if (autoMix.IsRunning)
            autoMix.TogglePause();
        else if (autoMix.Start() is { } why)
            screen.Log($"Auto-Mix: {why}");
    };
    screen.AutoMix.CloseRequested += () =>
    {
        if (autoMix.IsRunning)
            confirmation.Show(
                "Stop Auto-Mix?",
                "The music keeps playing, but nothing more will be mixed.",
                "Stop",
                () =>
                {
                    autoMix.Stop("Stopped");
                    screen.HideAutoMix();
                }
            );
        else
            screen.HideAutoMix();
    };

    void OpenInAutoMix(LibraryNode node)
    {
        var paths = node.SongPaths?.Invoke() ?? [];
        void Open()
        {
            library.LoadSongsAt(
                paths,
                songs =>
                {
                    if (songs.Count == 0)
                    {
                        screen.Log($"None of {node.Name}'s songs are here to mix");
                        return;
                    }
                    autoMix.Stop("Replaced");
                    analyser.LookUp(songs);
                    // What could trip the set up, said before it starts: those not analysed yet are on their way.
                    var check = SetCheck.Of(paths, songs, SetCheck.DriveThere);
                    analyser.Request(songs);
                    autoMix.Open(node.Name, songs);
                    screen.ShowAutoMix();
                    if (!check.AllGood)
                        screen.Log($"{node.Name}: {check.Summary}{(check.NotAnalysed > 0 ? " They're being analysed now." : "")}");
                }
            );
        }
        if (autoMix.IsRunning)
            confirmation.Show($"Mix {node.Name} instead?", "Auto-Mix stops what it's mixing. The music keeps playing.", "Mix it", Open);
        else
            Open();
    }

    MenuItem AutoMixFor(LibraryNode node) => new("Open in Auto-Mix", () => OpenInAutoMix(node), Enabled: (node.SongPaths?.Invoke().Count ?? 0) > 0);

    // Before a set: whether a list's songs are all there and analysed, with the chance to analyse those that aren't.
    MenuItem CheckFor(LibraryNode node) =>
        new(
            "Check for a set",
            () =>
            {
                var paths = node.SongPaths?.Invoke() ?? [];
                library.LoadSongsAt(
                    paths,
                    songs =>
                    {
                        analyser.LookUp(songs);
                        var check = SetCheck.Of(paths, songs, SetCheck.DriveThere);
                        var unanalysed = songs.Where(song => song.Analysis is null).ToList();
                        if (check.AllGood)
                            screen.Log($"{node.Name}: {check.Summary}");
                        else if (unanalysed.Count > 0)
                            confirmation.Show($"{node.Name} isn't quite ready", check.Summary, "Analyse them", () => screen.Log($"Analysing {analyser.QueueAll(unanalysed)} songs from {node.Name}"), "Not now", () => { });
                        else
                            confirmation.Show($"{node.Name} isn't quite ready", check.Summary, "OK", () => { });
                    }
                );
            },
            Enabled: (node.SongPaths?.Invoke().Count ?? 0) > 0
        );

    // Looks under a folder for exact copies, in the background, and lists them.
    MenuItem FindDuplicatesIn(string folder) =>
        new(
            "Find duplicates",
            () =>
            {
                repairing = true;
                screen.Log($"Looking for exact copies under {folder}...");
                library.Background.Start(
                    () => LibraryRepair.FindCopies(MusicLibrary.ListFolder(folder, recursive: true)),
                    copies =>
                        library.LoadSongsAt(
                            copies.SelectMany(same => same).ToList(),
                            songs =>
                            {
                                repairing = false;
                                duplicates = songs;
                                duplicateGroups = copies.ToList();
                                screen.Log(copies.Count == 0 ? $"No exact copies under {folder}" : $"{songs.Count} songs are exact copies of each other, in {copies.Count} {(copies.Count == 1 ? "group" : "groups")}; see Lists > Duplicates");
                                if (copies.Count == 0)
                                    return;
                                browser.ShowColumn("Folder");
                                var lists = folders.Single(node => node.Name == "Lists");
                                screen.FolderTree.SetExpanded(lists, true);
                                var listed = lists.Children.Single(node => node.Name == "Duplicates");
                                if (browser.Source == listed)
                                    browser.Reload();
                                else
                                    screen.FolderTree.Select(listed);
                            }
                        ),
                    () =>
                    {
                        repairing = false;
                        screen.Log($"Couldn't look for copies under {folder}");
                    }
                );
            },
            Enabled: !repairing
        );

    // Looks under a folder for songs that look like the same recording, tagged or encoded differently, and lists them
    // where exact copies go.
    MenuItem FindSimilarIn(string folder) =>
        new(
            "Find similar songs",
            () =>
            {
                repairing = true;
                screen.Log($"Looking for songs alike under {folder}...");
                library.Background.Start(
                    () => MusicLibrary.ListFolder(folder, recursive: true).Select(file => file.FullName).ToList(),
                    paths =>
                        library.LoadSongsAt(
                            paths,
                            songs =>
                            {
                                repairing = false;
                                var groups = LibraryRepair.FindSimilar(songs);
                                duplicates = groups.SelectMany(group => group).ToList();
                                duplicateGroups = groups.Select(group => (IReadOnlyList<string>)group.Select(song => song.Path).ToList()).ToList();
                                screen.Log(
                                    groups.Count == 0
                                        ? $"No songs alike under {folder}"
                                        : $"{duplicates.Count} songs look like the same recording as another, in {groups.Count} {(groups.Count == 1 ? "group" : "groups")}; see Lists > Duplicates"
                                );
                                if (groups.Count == 0)
                                    return;
                                browser.ShowColumn("Folder");
                                var lists = folders.Single(node => node.Name == "Lists");
                                screen.FolderTree.SetExpanded(lists, true);
                                var listed = lists.Children.Single(node => node.Name == "Duplicates");
                                if (browser.Source == listed)
                                    browser.Reload();
                                else
                                    screen.FolderTree.Select(listed);
                            }
                        ),
                    () =>
                    {
                        repairing = false;
                        screen.Log($"Couldn't look for songs alike under {folder}");
                    }
                );
            },
            Enabled: !repairing
        );

    // Looks under a folder for songs the database has whose files have gone, and offers to take what's known to them.
    MenuItem FindMovedSongsIn(string folder) =>
        new(
            "Find moved songs here",
            () =>
            {
                repairing = true;
                database.OpenDriveFiles(VirtualFolders.ReadyDrives().Select(drive => drive.Root));
                var known = database.KnownSizes();
                screen.Log($"Looking under {folder} for songs that have moved...");
                library.Background.Start(
                    () => LibraryRepair.FindMoved(known, MusicLibrary.ListFolder(folder, recursive: true)),
                    found =>
                    {
                        repairing = false;
                        var left = found.Ambiguous > 0 ? $" {found.Ambiguous} could be more than one file, so are left." : "";
                        if (found.Moves.Count == 0)
                        {
                            screen.Log(found.Missing == 0 ? "No songs known to the database are missing" : $"None of the {found.Missing} missing songs are under {folder}.{left}");
                            return;
                        }
                        confirmation.Show(
                            $"Relocate {found.Moves.Count} of {found.Missing} missing songs?",
                            $"Matched by file name and size. Their analyses and cues follow.{left}",
                            "Relocate",
                            () =>
                            {
                                var error = LibraryRepair.Apply(found.Moves, database, library, analyser, virtualFolders);
                                analyser.SaveSoon();
                                browser.Reload();
                                screen.Log(error is not null ? $"Relocated {found.Moves.Count} songs, but couldn't save a virtual folder: {error}" : $"Relocated {found.Moves.Count} songs");
                            }
                        );
                    },
                    () =>
                    {
                        repairing = false;
                        screen.Log("Couldn't look for moved songs");
                    }
                );
            },
            Enabled: !repairing
        );

    MenuItem ExportFor(LibraryNode node)
    {
        var paths = node.SongPaths?.Invoke() ?? [];
        void Start(StorageDrive drive) =>
            library.LoadSongsAt(paths, songs =>
            {
                if (export is not null)
                    return;
                if (songs.Count > 0)
                    StartExport(node.Name, songs, drive);
                else
                    screen.Log($"None of {node.Name}'s songs are here to export");
            });
        var drives = RemovableDrives.Writable().Where(drive => node.Folder is not { } folder || !virtualFolders.IsOn(folder, drive.Root));
        return new MenuItem(
            "Export to drive",
            drives
                .Select(drive => new MenuItem(
                    drive.Label,
                    () =>
                    {
                        var name = DriveExport.SafeName(node.Name);
                        if (virtualFolders.IsOnDrive(name, drive.Root))
                            confirmation.Show($"Replace {name} on {drive.Label}?", "The drive already has a playlist of that name. Its list of songs is replaced.", "Replace", () => Start(drive));
                        else
                            Start(drive);
                    }
                ))
                .ToList(),
            enabled: export is null && paths.Count > 0
        );
    }

    screen.FolderTree.ItemSecondaryClicked += (node, point) =>
    {
        if (node.Kind == LibraryNodeKind.VirtualFolders)
            menu.Show(point, [new MenuItem("New virtual folder...", NewVirtualFolder)]);
        else if (node.Kind == LibraryNodeKind.SmartPlaylists)
            menu.Show(point, [new MenuItem("New smart playlist...", NewSmartPlaylist)]);
        else if (node.Smart is { } smart)
            menu.Show(
                point,
                [
                    new MenuItem("Rename...", () => prompt.Show("Rename smart playlist", smart.Name, name => smartPlaylists.CheckName(name, smart), name => smartPlaylists.Rename(smart, name))),
                    new MenuItem("Edit search...", () => prompt.Show("What should it find?", smart.Search, SmartPlaylists.CheckSearch, search => smartPlaylists.SetSearch(smart, search))),
                    ExportFor(node),
                    AutoMixFor(node),
                    CheckFor(node),
                    new MenuItem(
                        "Delete...",
                        () =>
                            confirmation.Show(
                                $"Delete {smart.Name}?",
                                "Only the saved search goes. The songs aren't touched.",
                                "Delete",
                                () =>
                                {
                                    if (browser.Source == node)
                                        screen.FolderTree.Select(smartNode.Parent ?? folders[0]);
                                    var (name, search) = (smart.Name, smart.Search);
                                    smartPlaylists.Delete(smart);
                                    edits.RecordLibrary($"deleting {name}", () => smartPlaylists.Add(smartPlaylists.CheckName(name) is null ? name : $"{name} (2)", search));
                                    screen.Log($"Deleted {name}; Ctrl + Z brings it back");
                                }
                            )
                    ),
                ]
            );
        else if (node.Kind == LibraryNodeKind.Library)
            menu.Show(
                point,
                [
                    new MenuItem("Recursive", () => ToggleRecursive(node), Checked: library.Recursive),
                    // Other DJ software's libraries, one format to a line, more to come.
                    new MenuItem("Import", [new MenuItem("Rekordbox XML...", () => afterEvents.Enqueue(ImportRekordbox)), SeratoCuesFor(node, "Serato cues and grids")]),
                    new MenuItem("Export", [new MenuItem("Play history...", () => afterEvents.Enqueue(ExportHistory))]),
                    new MenuItem(
                        "Recording's tracklist",
                        [
                            new MenuItem(
                                lastRecording is null ? "The last recording's" : $"{Path.GetFileNameWithoutExtension(lastRecording)}'s",
                                () => EditTracklist(lastRecording!),
                                Enabled: lastRecording is not null
                            ),
                            new MenuItem("Another recording's...", () => afterEvents.Enqueue(ChooseTracklist)),
                        ]
                    ),
                    FindDuplicatesIn(library.Root),
                    FindSimilarIn(library.Root),
                    FindMovedSongsIn(library.Root),
                ]
            );
        else if (node is { Kind: LibraryNodeKind.Folder or LibraryNodeKind.Drive, Path: { } path })
            menu.Show(
                point,
                [
                    new MenuItem(
                        "Use as music library",
                        () => UseAsMusicLibrary(path),
                        Checked: string.Equals(FolderKey(path), FolderKey(library.Root), StringComparison.OrdinalIgnoreCase)
                    ),
                    new MenuItem("Recursive", () => ToggleRecursive(node), Checked: recursiveNode == node),
                    SeratoCuesFor(node),
                    FindDuplicatesIn(path),
                    FindSimilarIn(path),
                    FindMovedSongsIn(path),
                ]
            );
        else if (node.Folder is { } folder)
            menu.Show(
                point,
                [
                    new MenuItem("Rename...", () => RenameVirtualFolder(folder)),
                    AutoMixFor(node),
                    CheckFor(node),
                    SeratoCuesFor(node),
                    ExportFor(node),
                    new MenuItem(
                        "Store on drive",
                        virtualFolders
                            .WritableDrives()
                            .Select(drive => new MenuItem(
                                drive.Label,
                                () => MoveVirtualFolder(folder, drive),
                                Checked: virtualFolders.IsOn(folder, drive.Root)
                            ))
                            .ToList()
                    ),
                    new MenuItem(
                        "Delete...",
                        () =>
                            confirmation.Show(
                                $"Delete {folder.Name}?",
                                folder.Paths.Count switch
                                {
                                    0 => "It's empty, so nothing else is lost.",
                                    1 => "Its list of 1 song is deleted. The song itself isn't touched.",
                                    var count => $"Its list of {count} songs is deleted. The songs themselves aren't touched.",
                                },
                                "Delete",
                                () => DeleteVirtualFolder(folder)
                            )
                    ),
                ]
            );
        // Lists, filters, and Rekordbox's playlists and Serato's crates: the songs they list.
        // A set played before: to mix again, check, or write out.
        else if (node.Set is { } set)
            menu.Show(
                point,
                [
                    AutoMixFor(node),
                    CheckFor(node),
                    new MenuItem("Export...", () => afterEvents.Enqueue(() => ExportSet(set.Songs.Select(song => (song.Started, song.Path)).ToList(), "That set has nothing in it"))),
                ]
            );
        else if (node.HasSongs)
            menu.Show(point, node.Kind == LibraryNodeKind.Imported ? [AutoMixFor(node), CheckFor(node), SeratoCuesFor(node), ExportFor(node)] : [SeratoCuesFor(node)]);
    };

    screen.FolderTree.AcceptsDrop = (node, payload) =>
        payload is IReadOnlyList<Song> { Count: > 0 } && (node.Folder is not null || node is { Kind: LibraryNodeKind.Folder or LibraryNodeKind.Drive or LibraryNodeKind.Library, Path: not null } && !repairing);
    screen.FolderTree.ItemDropped += (node, payload) =>
    {
        // Onto a folder or drive: the songs moved there.
        if (node is { Folder: null, Path: { } there } && payload is IReadOnlyList<Song> moved)
        {
            MoveSongs(moved, there);
            return;
        }
        if (node.Folder is not { } folder || payload is not IReadOnlyList<Song> songs)
            return;
        var (added, error) = virtualFolders.Add(folder, songs);
        screen.Log(
            error is not null ? $"Couldn't save {folder.Name}: {error}"
            : added == 0 ? (songs.Count == 1 ? $"{songs[0].Title} is already in {folder.Name}" : $"Those are already in {folder.Name}")
            : added == 1 ? $"Added {songs.First(song => folder.Contains(song.Path)).Title} to {folder.Name}"
            : $"Added {added} songs to {folder.Name}{(added < songs.Count ? $" ({songs.Count - added} were there already)" : "")}"
        );
    };

    void TogglePreview(Song song)
    {
        if (preview.Song == song)
            preview.Stop();
        else if (audio.HasHeadphones)
            preview.Start(song);
        else
            screen.Log("Set up headphones to preview a song: the headphones button along the top");
    }

    browser.PreviewProgress = () => preview.Song is { } previewing ? (previewing.Title, preview.Fraction) : null;
    browser.PreviewSought += preview.Seek;
    browser.PreviewStopped += preview.Stop;
    preview.Failed += (song, error) => screen.Log($"Couldn't preview {song.Title}: {error}");

    // A rating, comment or colour changed on the songs chosen, and the column for it shown so it can be seen.
    void ChangeNotes(IReadOnlyList<Song> songs, string column, Func<SongNotes, SongNotes> change)
    {
        foreach (var song in songs)
        {
            song.Notes = change(song.Notes);
            database.SetNotes(song.Path, song.Notes);
        }
        analyser.SaveSoon();
        browser.ShowColumn(column);
        browser.Refilter();
    }

    // A song onto one of the sampler's pads, in place of whatever was there.
    MenuItem SamplerMenu(IReadOnlyList<Song> songs) =>
        new(
            "Put on sampler pad",
            Enumerable
                .Range(0, Sampler.SLOTS)
                .Select(slot => new MenuItem(
                    sampler.NameOf(slot) is { } there ? $"{Sampler.SlotName(slot)}: {there}" : Sampler.SlotName(slot),
                    () =>
                    {
                        sampler.Load(slot, songs[0].Path, songs[0].Title);
                        screen.Log($"{songs[0].Title} is on sampler pad {Sampler.SlotName(slot)}: play it with the pads in {(slot < Sampler.PADS ? "SAMPLER" : "SAMPLER 2")} mode");
                    }
                ))
                .ToList(),
            enabled: songs.Count == 1
        );

    // A song's title, artist and album, written into its file's tags. Its analysis, cues and notes stay with it, as
    // what's known is moved on to the file as it now is, rather than it counting as a new song to analyse.
    void EditDetails(Song song)
    {
        if (!OperatingSystem.IsWindows())
            return;
        details.Show(
            song.Title,
            song.Artist,
            song.Album,
            song.Genre,
            (title, artist, album, genre) =>
            {
                if (!OperatingSystem.IsWindows())
                    throw new InvalidOperationException("Details can only be written on Windows");
                if (WindowsTags.Write(song.Path, title, artist, album, genre) is { } error)
                    throw new InvalidOperationException(error);
                var file = new FileInfo(song.Path);
                (song.Title, song.Artist, song.Album) = (title, artist.Length > 0 ? artist : song.Artist, album.Length > 0 ? album : song.Album);
                if (genre.Length > 0)
                    song.Genre = genre;
                database.Retag(song, file.Length, file.LastWriteTimeUtc);
                (song.Size, song.Modified) = (file.Length, file.LastWriteTimeUtc);
                browser.Refilter();
                screen.Log($"Saved the details of {song.Artist} - {song.Title}");
            }
        );
    }

    // Several songs' artist or album, written into each file's tags in the background, then what's known of each
    // moved on to its file as it now is.
    void EditManyDetails(IReadOnlyList<Song> songs)
    {
        if (!OperatingSystem.IsWindows())
            return;
        string? Shared(Func<Song, string> of) => songs.Select(of).Distinct(StringComparer.Ordinal).Count() == 1 ? of(songs[0]) : null;
        details.ShowMany(
            songs.Count,
            Shared(song => song.Artist),
            Shared(song => song.Album),
            Shared(song => song.Genre),
            (artist, album, genre) =>
            {
                var paths = songs.Select(song => song.Path).ToList();
                screen.Log($"Saving the details of {songs.Count} songs...");
                library.Background.Start(
                    () => paths.Select(path => OperatingSystem.IsWindows() ? WindowsTags.Write(path, null, artist, album, genre) : "Not on Windows").ToList(),
                    errors =>
                    {
                        var saved = 0;
                        string? firstError = null;
                        for (var i = 0; i < songs.Count; i++)
                        {
                            if (errors[i] is { } error)
                            {
                                firstError ??= $"{Path.GetFileName(songs[i].Path)}: {error}";
                                continue;
                            }
                            var song = songs[i];
                            var file = new FileInfo(song.Path);
                            if (artist.Length > 0)
                                song.Artist = artist;
                            if (album.Length > 0)
                                song.Album = album;
                            if (genre.Length > 0)
                                song.Genre = genre;
                            database.Retag(song, file.Length, file.LastWriteTimeUtc);
                            (song.Size, song.Modified) = (file.Length, file.LastWriteTimeUtc);
                            saved++;
                        }
                        browser.Refilter();
                        screen.Log(
                            firstError is null
                                ? $"Saved the details of {saved} songs"
                                : $"Saved the details of {saved} of {songs.Count} songs; couldn't save the rest, as {firstError}"
                        );
                    },
                    () => screen.Log("Couldn't save the songs' details")
                );
            }
        );
    }

    MenuItem RatingMenu(IReadOnlyList<Song> songs) =>
        new(
            "Rating",
            Enumerable
                .Range(0, SongNotes.MAX_RATING + 1)
                .Select(rating => new MenuItem(
                    rating == 0 ? "None" : new string('*', rating),
                    () => ChangeNotes(songs, "Rating", notes => notes with { Rating = rating }),
                    Checked: songs.All(song => song.Notes.Rating == rating)
                ))
                .ToList(),
            enabled: songs.Count > 0
        );

    MenuItem ColourMenu(IReadOnlyList<Song> songs) =>
        new(
            "Colour",
            SongNotes
                .Colors.Prepend(null)
                .Select(colour => new MenuItem(
                    colour is null ? "None" : char.ToUpperInvariant(colour[0]) + colour[1..],
                    () => ChangeNotes(songs, "Colour", notes => notes with { Color = colour }),
                    Checked: songs.All(song => song.Notes.Color == colour)
                ))
                .ToList(),
            enabled: songs.Count > 0
        );

    // The other copies of a song in Duplicates.
    List<string> OtherCopies(Song song) =>
        duplicateGroups.FirstOrDefault(group => group.Contains(song.Path, StringComparer.OrdinalIgnoreCase))?.Where(path => !string.Equals(path, song.Path, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];

    // One copy in Duplicates kept, and the others recycled, what's known about them coming onto it, where it hasn't
    // its own: cues, notes, places in virtual folders. A copy on a deck is left.
    void KeepCopy(Song kept)
    {
        var onDecks = OtherCopies(kept).Where(path => decks.Any(deck => string.Equals(deck.FilePath, path, StringComparison.OrdinalIgnoreCase))).ToList();
        var others = OtherCopies(kept).Except(onDecks, StringComparer.OrdinalIgnoreCase).ToList();
        if (others.Count == 0)
        {
            screen.Log(onDecks.Count > 0 ? "The other copies are on a deck, so they're left" : $"{kept.Title} has no other copies");
            return;
        }
        var where = string.Join(", ", others.Take(3).Select(path => Path.GetDirectoryName(path))) + (others.Count > 3 ? $" and {others.Count - 3} more" : "");
        var recycling = others.ToDictionary(path => path, FileDeletion.WouldRecycle, StringComparer.OrdinalIgnoreCase);
        var forGood = recycling.Count(pair => !pair.Value);
        var going =
            forGood == 0 ? $"{(others.Count == 1 ? "goes" : "go")} to the Recycle Bin"
            : forGood == others.Count ? $"{(others.Count == 1 ? "is" : "are")} deleted for good, as there's no Recycle Bin for {(others.Count == 1 ? "it" : "them")}"
            : $"{(others.Count - forGood == 1 ? "goes" : "go")} to the Recycle Bin but for {forGood}, deleted for good as there's no Recycle Bin for {(forGood == 1 ? "it" : "them")}";
        confirmation.Show(
            $"Keep the copy in {Path.GetFileName(Path.GetDirectoryName(kept.Path))}?",
            $"This one, {kept.Path}, is kept. {(others.Count == 1 ? "The other copy, in" : $"The {others.Count} other copies, in")} {where}, {going}. "
                + $"Their hot cues, notes and places in virtual folders come onto this one, where it hasn't its own.{(forGood > 0 ? " That can't be undone." : "")}{(onDecks.Count > 0 ? " A copy on a deck is left." : "")}",
            forGood == 0 ? "Recycle" : "Delete",
            () =>
            {
                var recycled = new List<string>();
                string? problem = null;
                foreach (var other in others)
                {
                    if (FileDeletion.Delete(other, recycling[other]) is { } error)
                        problem ??= $"{Path.GetFileName(other)}: {error}";
                    else
                        recycled.Add(other);
                }
                var saving = LibraryRepair.Apply(LibraryRepair.OntoKept(kept, recycled), database, library, analyser, virtualFolders);
                analyser.SaveSoon();
                duplicateGroups = duplicateGroups
                    .Select(group => (IReadOnlyList<string>)group.Except(recycled, StringComparer.OrdinalIgnoreCase).ToList())
                    .Where(group => group.Count > 1)
                    .ToList();
                var left = duplicateGroups.SelectMany(group => group).ToHashSet(StringComparer.OrdinalIgnoreCase);
                duplicates = duplicates.Where(song => left.Contains(song.Path)).ToList();
                browser.Reload();
                screen.Log(
                    (recycled.Count == 1 ? $"Kept {kept.Title}; deleted its other copy" : $"Kept {kept.Title}; deleted {recycled.Count} other copies")
                        + (problem is not null ? $"; couldn't delete {problem}" : "")
                        + (saving is not null ? $"; couldn't save a virtual folder: {saving}" : "")
                );
            }
        );
    }

    // Songs dragged onto a folder or drive moved there, what's known about them following: cues, notes, analysis,
    // places in virtual folders. A song on a deck is left.
    void MoveSongs(IReadOnlyList<Song> songs, string folder)
    {
        var playing = songs.Where(song => decks.Any(deck => string.Equals(deck.FilePath, song.Path, StringComparison.OrdinalIgnoreCase))).ToList();
        var moving = songs.Except(playing).Where(song => !string.Equals(Path.GetDirectoryName(song.Path), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)).ToList();
        if (moving.Count == 0)
        {
            screen.Log(playing.Count > 0 ? "A song on a deck can't be moved" : $"Those are in {folder} already");
            return;
        }
        confirmation.Show(
            moving.Count == 1 ? $"Move {moving[0].Title}?" : $"Move {moving.Count} songs?",
            $"{(moving.Count == 1 ? "Its file is" : "Their files are")} moved to {folder}, and {(moving.Count == 1 ? "its" : "their")} hot cues, notes, analysis and places in virtual folders follow. "
                + $"A file there by the same name isn't replaced.{(playing.Count > 0 ? " A song on a deck is left." : "")}",
            "Move",
            () =>
            {
                // Not one loaded onto a deck since.
                var paths = moving.Select(song => song.Path).Where(path => !decks.Any(deck => string.Equals(deck.FilePath, path, StringComparison.OrdinalIgnoreCase))).ToList();
                if (paths.Count == 0)
                {
                    screen.Log("A song on a deck can't be moved");
                    return;
                }
                repairing = true;
                screen.Log(moving.Count == 1 ? $"Moving {moving[0].Title} to {folder}..." : $"Moving {moving.Count} songs to {folder}...");
                library.Background.Start(
                    () => LibraryRepair.MoveFiles(paths, folder),
                    moved =>
                    {
                        repairing = false;
                        var saving = LibraryRepair.Apply(moved.Moves, database, library, analyser, virtualFolders);
                        analyser.SaveSoon();
                        browser.Reload();
                        screen.Log(
                            $"Moved {moved.Moves.Count} {(moved.Moves.Count == 1 ? "song" : "songs")} to {folder}"
                                + (moved.Problems.Count > 0 ? $"; couldn't move {moved.Problems.Count}, as {moved.Problems[0]}" : "")
                                + (saving is not null ? $"; couldn't save a virtual folder: {saving}" : "")
                        );
                    },
                    () =>
                    {
                        repairing = false;
                        screen.Log($"Couldn't move the songs to {folder}");
                    }
                );
            }
        );
    }

    // A menu for songs in the list, acting on the selection.
    browser.SongsSecondaryClicked += (songs, at) =>
    {
        var played = songs.Where(song => plays.Contains(song.Path)).ToList();
        var listed = browser.List.Items;
        menu.Show(
            at,
            [
                .. browser.Source is { Name: "Duplicates" } && songs.Count == 1 && OtherCopies(songs[0]).Count > 0
                    ? [new MenuItem("Keep this copy, delete the others...", () => KeepCopy(songs[0]))]
                    : Array.Empty<MenuItem>(),
                new MenuItem(songs.Count > 1 ? $"Add {songs.Count} songs to Up next" : "Add to Up next", () => AddToUpNext(songs), Enabled: songs.Count > 0),
                new MenuItem(
                    preview.Song is { } hearing && songs.Contains(hearing) ? "Stop preview" : "Preview in headphones",
                    () => TogglePreview(songs[0]),
                    Enabled: songs.Count > 0 && audio.HasHeadphones
                ),
                SamplerMenu(songs),
                new MenuItem(
                    "Edit details...",
                    () =>
                    {
                        if (songs.Count == 1)
                            EditDetails(songs[0]);
                        else
                            EditManyDetails(songs);
                    },
                    Enabled: songs.Count > 0 && OperatingSystem.IsWindows()
                ),
                RatingMenu(songs),
                ColourMenu(songs),
                new MenuItem(
                    "Comment...",
                    () =>
                        prompt.Show(
                            songs.Count > 1 ? $"Comment on {songs.Count} songs" : $"Comment on {songs[0].Title}",
                            songs.Select(song => song.Notes.Comment).Distinct().Count() == 1 ? songs[0].Notes.Comment ?? "" : "",
                            _ => null,
                            text => ChangeNotes(songs, "Comment", notes => notes with { Comment = text.Trim() is { Length: > 0 } comment ? comment : null })
                        ),
                    Enabled: songs.Count > 0
                ),
                // A DJ edit of the song: 16 bars of its own drums before and after it, saved beside it as a song of its
                // own, or made again in place of the one made before. Not for one that's an edit or extended mix
                // already.
                new MenuItem(
                    songs.Count != 1 ? "Make a DJ edit"
                    : djEdits.WhyNot(songs[0]) is { } noEdit ? $"Make a DJ edit ({noEdit})"
                    : DjEdits.HasEdit(songs[0]) ? "Remake the DJ edit..."
                    : "Make a DJ edit",
                    () => MakeDjEdit(songs[0]),
                    Enabled: songs.Count == 1 && djEdits.WhyNot(songs[0]) is null
                ),
                // The songs chosen, analysed again from scratch: after a file's been edited, say, or to try a bad grid
                // again. A grid fixed by hand stays as it was.
                new MenuItem(
                    songs.Count > 1 ? $"Re-analyse {songs.Count} songs" : "Re-analyse",
                    () =>
                    {
                        var queued = analyser.Reanalyse(songs);
                        screen.Log(
                            queued == 0 ? (songs.Count == 1 ? $"{songs[0].Title} is being analysed already" : "Those are being analysed already")
                            : queued == 1 && songs.Count == 1 ? $"Analysing {songs[0].Title} again"
                            : $"Analysing {queued} songs again"
                        );
                    },
                    Enabled: songs.Count > 0
                ),
                // Every song in the list that hasn't been analysed yet, with a bar across the top while it goes.
                new MenuItem(
                    "Analyse All",
                    () =>
                    {
                        var added = analyser.QueueAll(listed);
                        screen.Log(
                            added == 0 ? "Every song in the list is analysed already"
                            : $"Analysing {added} {(added == 1 ? "song" : "songs")}{(analyser.BulkTotal > added ? $", {analyser.BulkTotal - analyser.BulkDone} in all" : "")}"
                        );
                    },
                    Enabled: listed.Any(song => song.Analysis is null && song.AnalysisError is null)
                ),
                // Serato's hot cues and beat grids for the songs chosen, for those without their own here.
                new MenuItem(
                    songs.Count > 1 ? $"Import Serato cues and grids for {songs.Count} songs" : "Import Serato cues and grids",
                    () =>
                    {
                        var paths = songs.Select(song => song.Path).ToList();
                        if (songs.Count == 1)
                            ImportSeratoCues(songs[0].Title, () => paths, oneSong: true);
                        else
                            ImportSeratoCues("the selection", () => paths);
                    },
                    Enabled: songs.Count > 0
                ),
                new MenuItem(
                    played.Count > 1 ? $"Mark {played.Count} songs as not played" : "Mark as not played",
                    () =>
                    {
                        plays.Forget(played.Select(song => song.Path));
                        screen.Log(played.Count == 1 ? $"{played[0].Title} is marked as not played" : $"{played.Count} songs are marked as not played");
                    },
                    Enabled: played.Count > 0
                ),
                new MenuItem(
                    songs.Count > 1 ? $"Delete {songs.Count} songs..." : "Delete...",
                    () => DeleteSongs(songs),
                    Enabled: songs.Count > 0
                ),
            ]
        );
    };

    // Songs' files deleted, once it's agreed, and taken out of the virtual folders: to the Recycle Bin where their
    // drive has one, for good where it hasn't. A song on a deck is left.
    void DeleteSongs(IReadOnlyList<Song> songs)
    {
        var onDecks = songs.Where(song => decks.Any(deck => string.Equals(deck.FilePath, song.Path, StringComparison.OrdinalIgnoreCase))).ToList();
        var deleting = songs.Except(onDecks).ToList();
        if (deleting.Count == 0)
        {
            screen.Log(songs.Count == 1 ? $"{songs[0].Title} is on a deck, so it can't be deleted" : "Those are on decks, so they can't be deleted");
            return;
        }
        var recycling = deleting.ToDictionary(song => song, song => FileDeletion.WouldRecycle(song.Path));
        var forGood = recycling.Count(pair => !pair.Value);
        var where =
            deleting.Count == 1 ? (forGood == 0 ? $"{deleting[0].Path} goes to the Recycle Bin" : $"{deleting[0].Path} is deleted for good, as there's no Recycle Bin for it")
            : forGood == 0 ? "Their files go to the Recycle Bin"
            : forGood == deleting.Count ? "Their files are deleted for good, as there's no Recycle Bin for them"
            : $"{deleting.Count - forGood} of their files go to the Recycle Bin, and {forGood} {(forGood == 1 ? "is" : "are")} deleted for good, as there's no Recycle Bin for {(forGood == 1 ? "it" : "them")}";
        confirmation.Show(
            deleting.Count == 1 ? $"Delete {deleting[0].Title}?" : $"Delete {deleting.Count} songs?",
            $"{where}, and {(deleting.Count == 1 ? "it comes out of any virtual folders it's in" : "they come out of any virtual folders they're in")}."
                + (forGood > 0 ? " That can't be undone." : "")
                + (onDecks.Count > 0 ? $" {(onDecks.Count == 1 ? "The one" : $"The {onDecks.Count}")} on a deck {(onDecks.Count == 1 ? "is" : "are")} left." : ""),
            "Delete",
            () =>
            {
                var deleted = new List<Song>();
                string? problem = null;
                foreach (var song in deleting)
                {
                    if (preview.Song == song)
                        preview.Stop();
                    if (FileDeletion.Delete(song.Path, recycling[song]) is { } error)
                        problem ??= $"{Path.GetFileName(song.Path)}: {error}";
                    else
                        deleted.Add(song);
                }
                string? saving = null;
                foreach (var folder in virtualFolders.All)
                    saving ??= virtualFolders.Remove(folder, deleted);
                browser.Reload();
                screen.Log(
                    (deleted.Count == 0 ? "Nothing was deleted"
                    : deleted.Count == 1 ? $"Deleted {deleted[0].Title}"
                    : $"Deleted {deleted.Count} songs")
                        + (problem is not null ? $"; couldn't delete {problem}" : "")
                        + (saving is not null ? $"; couldn't save a virtual folder: {saving}" : "")
                );
            }
        );
    }

    // A DJ edit made before is only made again once it's agreed to replace it.
    void MakeDjEdit(Song song)
    {
        if (!DjEdits.HasEdit(song))
        {
            StartDjEdit(song, replace: false);
            return;
        }
        var (flac, wav) = (DjEdit.EditPath(song.Path), DjEdit.OldEditPath(song.Path));
        var wavGoes = !File.Exists(wav) ? ""
            : $" The WAV it was before, {Path.GetFileName(wav)}, {(FileDeletion.WouldRecycle(wav) ? "goes to the Recycle Bin" : "is deleted, as its drive has no Recycle Bin")}.";
        confirmation.Show(
            $"Replace the DJ edit of {song.Title}?",
            (File.Exists(flac) ? $"{Path.GetFileName(flac)} is made again from the song, in place of the one there." : $"It's made again from the song as {Path.GetFileName(flac)}, a FLAC about half the size.")
                + wavGoes
                + " Its hot cues and memory cues are the song's again, so any set on the edit itself are lost.",
            "Replace",
            () => StartDjEdit(song, replace: true)
        );
    }

    void StartDjEdit(Song song, bool replace)
    {
        string[] editPaths = [DjEdit.EditPath(song.Path), DjEdit.OldEditPath(song.Path)];
        if (replace && decks.FirstOrDefault(deck => editPaths.Contains(deck.FilePath, StringComparer.OrdinalIgnoreCase)) is { } loaded)
            screen.Log($"Can't make the DJ edit of {song.Title} again while it's on deck {loaded.Name}");
        else if (djEdits.Start(song, replace) is { } why)
            screen.Log($"Can't make a DJ edit of {song.Title}: {why}");
        else
            screen.Log(
                $"{(replace ? "Making the DJ edit" : "Making a DJ edit")} of {song.Title}{(separator.HasStems(song.Path) ? "..." : ", once it's separated into its stems...")}"
            );
    }

    // A DJ edit made becomes a song in its own right, with the original's analysis and cues moved along with it,
    // memory cues where the song starts and where the outro's beats take over, and cued to its very start. One made
    // again takes the old one's place. The original notes that it has one.
    void TakeInDjEdit(DjEditDone done)
    {
        if (done.Separated)
            OfferToDeleteStems(done.Song);
        if (done.Edit is not { } edit || done.EditPath is not { } path)
        {
            screen.Log($"No DJ edit of {done.Song.Title}: {done.Why}");
            return;
        }
        var original = done.Song;
        var offset = edit.Offset;
        var duration = edit.Grid.Duration;
        TimeSpan? Moved(TimeSpan? at) => at is { } time && time.TotalSeconds + offset is var moved and >= 0 && moved < duration ? TimeSpan.FromSeconds(moved) : null;

        var hotcues = database.GetHotcues(original.Path).Select(Moved).ToList();
        database.SetHotcues(path, hotcues);
        database.SetHotcueDetails(
            path,
            database.GetHotcueDetails(original.Path)
                .Select((detail, i) => detail is not null && i < hotcues.Count && hotcues[i] is not null ? detail with { At = detail.At + offset, LoopEnd = detail.LoopEnd + offset } : null)
                .ToList()
        );
        database.SetMemoryCues(
            path,
            [
                .. database.GetMemoryCues(original.Path).Select(cue => Moved(cue)).OfType<TimeSpan>(),
                .. new[] { edit.IntroEnd, edit.OutroStart }.OfType<double>().Select(TimeSpan.FromSeconds),
            ]
        );
        database.SetMemoryLoops(
            path,
            database.GetMemoryLoops(original.Path)
                .Select(loop => (Start: Moved(loop.Start), End: Moved(loop.End)))
                .Where(loop => loop.Start is not null && loop.End is not null)
                .Select(loop => (loop.Start!.Value, loop.End!.Value))
                .ToList()
        );
        // Always cued somewhere, rather than to its first memory cue, which can be where the song starts or the outro:
        // to the intro's first beat at the very start; or with no intro, where the song was cued, if it was, or where
        // it's first heard.
        database.SetCuePoint(path, edit.IntroEnd is null && Moved(database.GetCuePoint(original.Path)) is { } cuePoint ? cuePoint : TimeSpan.FromSeconds(edit.Cue));
        // An edit made again in place of a WAV from before they were FLACs sends the WAV to the Recycle Bin, or where
        // the drive hasn't one, deletes it, as replacing it said.
        var replaced = library.Find(path) is not null;
        var oldWav = DjEdit.OldEditPath(original.Path);
        string? oldWavNews = null;
        if (!string.Equals(oldWav, path, StringComparison.OrdinalIgnoreCase) && File.Exists(oldWav))
        {
            var recycle = FileDeletion.WouldRecycle(oldWav);
            oldWavNews = FileDeletion.Delete(oldWav, recycle) is { } error ? $"; couldn't delete the old WAV: {error}"
                : recycle ? "; the old WAV's in the Recycle Bin"
                : "; the old WAV's deleted";
            replaced = true;
        }
        if (library.SongAt(path) is { } song)
        {
            // A song known already is the old edit: it's this file now.
            var file = new FileInfo(path);
            (song.Size, song.Modified, song.Title) = (file.Length, file.LastWriteTimeUtc, done.Title);
            if (original.Analysis is { } analysis)
            {
                // The beats are where the edit put them, so it needn't be analysed.
                var moved = analysis with { Grid = edit.Grid };
                database.Set(song, moved);
                (song.Analysis, song.AnalysisError) = (moved, null);
            }
        }
        var notes = original.Notes with { DjEdit = Path.GetFileName(path) };
        database.SetNotes(original.Path, notes);
        original.Notes = notes;
        analyser.SaveSoon();
        if (Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(library.Root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            TakeInLibraryChanges();
        // The folders that list it, wherever they are, in the music folder or not, read again: those in the folder list,
        // and the one listed now, so it's there straight away.
        var editFolder = LibraryFile.WithSeparator(Path.GetDirectoryName(Path.GetFullPath(path))!);
        foreach (var node in folders)
            node.RefreshSongsHolding(editFolder);
        if (browser.Source is { Path: { } shown } source && editFolder.StartsWith(LibraryFile.WithSeparator(shown), StringComparison.OrdinalIgnoreCase))
            source.RefreshSongs();
        browser.Reload();
        screen.Log($"{(replaced ? "Made again" : "Made")} {Path.GetFileName(path)}: {edit.Summary}{oldWavNews}");
    }

    // A song separated just to make its DJ edit has its stems taking up room on disk, which it's offered back, unless
    // a deck has the song loaded, so it's likely they're wanted.
    void OfferToDeleteStems(Song song)
    {
        if (separator.StemsSize(song.Path) is not { } bytes || decks.Any(deck => string.Equals(deck.FilePath, song.Path, StringComparison.OrdinalIgnoreCase)))
            return;
        confirmation.Show(
            $"Delete the stems of {song.Title}?",
            $"It was separated into its stems to make its DJ edit, and they take {bytes / (1024.0 * 1024):0} MB. Keep them if you'll play it in STEMS mode; "
                + "otherwise they can go, and it's separated again if they're ever wanted.",
            "Delete",
            () =>
            {
                try
                {
                    separator.DeleteStems(song.Path);
                    screen.Log($"Deleted the stems of {song.Title}, freeing {bytes / (1024.0 * 1024):0} MB");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    screen.Log($"Couldn't delete the stems of {song.Title}: {e.Message}");
                }
            }
        );
    }

    browser.AnalyseAllCancelled += left => screen.Log($"Stopped analysing all; {left} {(left == 1 ? "song was" : "songs were")} left unanalysed");

    // Taking songs out of a virtual folder, or putting them in another order, can be undone with Ctrl + Z.
    browser.RemoveRequested += (folder, songs) =>
    {
        var before = folder.Paths.ToList();
        var error = virtualFolders.Remove(folder, songs);
        var what = songs.Count == 1 ? songs[0].Title : $"{songs.Count} songs";
        if (error is null)
            edits.RecordLibrary($"taking {what} out of {folder.Name}", () => virtualFolders.Restore(folder, before));
        screen.Log(error is not null ? $"Couldn't save {folder.Name}: {error}" : $"Took {what} out of {folder.Name}; Ctrl + Z puts it back");
    };
    browser.MoveRequested += (folder, songs, before) =>
    {
        var order = folder.Paths.ToList();
        if (virtualFolders.Move(folder, songs, before) is { } error)
            screen.Log($"Couldn't save {folder.Name}: {error}");
        else if (!order.SequenceEqual(folder.Paths))
            edits.RecordLibrary($"reordering {folder.Name}", () => virtualFolders.Restore(folder, order));
    };

    // What the decks are doing is written down every couple of seconds, and the record deleted as the app closes. One
    // left from last time means the app didn't close properly, so each deck gets its song back where it was, with its
    // tempo, key lock, key shift and slip. A deck that was playing a moment ago plays on from where it would have got to; after longer,
    // it waits where it was.
    const double SESSION_SAVE_SECONDS = 2;
    var sessionPath = Path.Combine(AppFolder.Path, "session.json");
    if (SessionState.Load(sessionPath) is { } left)
    {
        var resume = DateTime.UtcNow - left.SavedAt < TimeSpan.FromMinutes(2);
        foreach (var state in left.Decks)
        {
            if (decks.FirstOrDefault(deck => deck.Name == state.Name) is not { } deck || state.Path is null || SongFor(state.Path) is not { } song)
                continue;
            deck.Tempo = state.Tempo;
            deck.KeyLocked = state.KeyLocked;
            deck.Slip = state.Slip;
            restoring[deck] = (resume ? left.PositionAt(state, DateTime.UtcNow) : state.Position, resume && state.Playing, state.KeyShift);
            if (state.SyncLocked)
                relocking[deck] = song.Path;
            Load(song, deck, byHand: false);
        }
        if (restoring.Count > 0)
            screen.Log(
                resume
                    ? "Cuepoint didn't close properly, so the decks are picking up where they were"
                    : "Cuepoint didn't close properly last time, so the decks have their songs back where they were"
            );
    }
    var nextSessionSave = 0.0;
    string? djEditStatus = null;

    // Sound cards come and go, a controller's unplugged by mistake, say: they're looked at every second, so the music
    // carries on somewhere within a second or two, and goes back as soon as they're back.
    const double DEVICE_CHECK_SECONDS = 1;
    var nextDeviceCheck = 0.0;

    // The Rekordbox collections are looked at every few seconds, and one that's been exported afresh is read again:
    // its playlists, and hot cues for songs with none here.
    const double IDLE_ANALYSIS_SECONDS = 1;
    var nextIdleAnalysis = IDLE_ANALYSIS_SECONDS;
    const double REKORDBOX_CHECK_SECONDS = 5;
    var nextRekordboxCheck = REKORDBOX_CHECK_SECONDS;
    void CheckRekordbox()
    {
        foreach (var known in imports.Rekordbox.ToList())
        {
            if (!File.Exists(known.FilePath) || !ImportRecord.IsNew(importRecord.Rekordbox, known.FilePath))
                continue;
            importRecord.Save(importRecordPath);
            var path = known.FilePath;
            library.Background.Start(
                () => RekordboxXml.Load(path) is { } collection ? (Collection: collection, Files: FilesThere(collection)) : default,
                read =>
                {
                    if (read.Collection is not { } changed)
                        return;
                    imports.Put(changed);
                    ShowRekordbox();
                    var (news, error) = ImportCollection(changed, read.Files);
                    screen.Log(
                        error is not null ? $"{Path.GetFileName(changed.FilePath)} changed; couldn't save what came in: {error}"
                        : news is not null ? $"{Path.GetFileName(changed.FilePath)} changed, so it's read again; {news} brought in"
                        : $"{Path.GetFileName(changed.FilePath)} changed, so its playlists are read again"
                    );
                }
            );
        }
    }

    // What's changed in the library's folder is scanned in once it's been still for a second, so an album copied in
    // is taken in one go. The library's own lists follow, and the folder being shown if it's in the library.
    var libraryQuiet = TimeSpan.FromSeconds(1);
    void TakeInLibraryChanges()
    {
        var before = library.Songs.ToDictionary(song => song.Path, StringComparer.OrdinalIgnoreCase);
        library.BeginScan(
            library.Root,
            library.Recursive,
            () =>
            {
                var added = library.Songs.Count(song => !before.ContainsKey(song.Path));
                var changed = library.Songs.Count(song => before.TryGetValue(song.Path, out var was) && was != song);
                var gone = before.Count - (library.Songs.Count - added);
                AfterLibraryScan();
                if (added + changed + gone == 0)
                    return;
                var news = new List<string>();
                if (added > 0)
                    news.Add($"{added} added");
                if (changed > 0)
                    news.Add($"{changed} changed");
                if (gone > 0)
                    news.Add($"{gone} gone");
                screen.Log($"Music library: {string.Join(", ", news)}; {library.Songs.Count} songs now");
            }
        );
    }
    WatchLibrary();

    // Each part of a frame is kept apart from the rest, so one going wrong doesn't stop the others: drawing failing
    // still leaves the decks, the keyboard, a controller and the lighting working.
    void Guarded(string doing, Action step)
    {
        try
        {
            step();
        }
        catch (Exception e)
        {
            Failed(doing, e);
        }
    }

    var screenshots = capture is { } shots
        ? new ScreenshotTool(
            new(
                window,
                renderer,
                () => browser.ListedSongs,
                decks,
                audio,
                (song, deck) => Load(song, deck),
                () => SetRecursive(folders.First(node => node.Kind == LibraryNodeKind.Library), true),
                song => song.Analysis is not null || song.AnalysisError is not null,
                StartRecording,
                () =>
                {
                    StopRecording();
                    return lastRecording;
                },
                ui,
                screen,
                OnUnhandledKey
            ),
            shots
        )
        : null;
    var lastFrame = Glfw.Time;
    var windowShown = false;
    while (!window.ShouldClose && screenshots?.Done != true)
    {
        // Minimised, there's nothing to draw, but the decks, a controller and lighting software still need seeing to:
        // so everything carries on but the drawing, waking often enough to send lighting software every beat on time.
        // (With the app's own title bar, a minimised window still has a framebuffer, so its size doesn't say.)
        //
        // Whether it's minimised is asked after handling events, as one of them may be the minimise itself: drawing
        // into a minimised window leaves it black for good once it's restored.
        if (window.IsIconified)
            Glfw.WaitEventsTimeout(0.005);
        else
            Glfw.PollEvents();
        var size = window.FramebufferSize;
        var minimised = window.IsIconified || size.X == 0 || size.Y == 0;
        while (afterEvents.TryDequeue(out var afterward))
            Guarded("doing what was asked", afterward);

        // A set mustn't stop for a screen saver or sleep, so the computer stays awake while the window can be seen,
        // and is let go while it's minimised.
        WindowsShell.KeepAwake(!minimised);

        // Finishes any background loads, raising their Loaded events here on the UI thread.
        Guarded(
            "loading a song",
            () =>
            {
                foreach (var deck in decks)
                    deck.Update();
                foreach (var phrase in phraseEffects.Values)
                {
                    if (phrase.Update() is { } finished)
                        screen.Log(finished);
                }
                MasterEffectsTiming.Follow(audio.MasterEffects, decks);
                midiClock?.Follow(decks);
                if (relocking.Count > 0)
                    Relock();
                preview.Update();
                sampler.Update();
            }
        );

        // Applies finished analyses and saves the database when any came in.
        Guarded(
            "taking in analyses",
            () =>
            {
                var saveError = analyser.SaveError;
                analyser.Update();
                if (analyser.SaveError is { } newSaveError && newSaveError != saveError)
                    screen.Log($"Couldn't save {database.FilePath}: {newSaveError}");
            }
        );

        // DJ edits being made move along, saying how they're getting on, and become songs once they're made.
        Guarded(
            "making DJ edits",
            () =>
            {
                if (djEdits.Update() is { } done)
                    TakeInDjEdit(done);
                if (djEdits.Status is { } status && status != djEditStatus)
                    screen.Log(status);
                djEditStatus = djEdits.Status;
            }
        );

        // A recording that can't carry on (a full disk, say) is stopped, keeping what it has.
        Guarded(
            "recording the mix",
            () =>
            {
                if (recorder?.Error is not null)
                    StopRecording();
            }
        );

        // New tags can change what matches a search.
        // Takes in the library's songs once it's been looked through in the background, and the folders the folder
        // list's looking through, laying the list out afresh to show what's come in.
        Guarded("taking in the library's songs", () => library.ApplyScan());
        Guarded(
            "taking in a folder",
            () =>
            {
                if (library.Background.Apply() > 0)
                    screen.FolderTree.Refresh();
            }
        );

        Guarded(
            "reading tags",
            () =>
            {
                if (library.ApplyReadTags() > 0)
                    browser.Refilter();
            }
        );

        // Faders whose keys are held move by the time since the last frame.
        var now = Glfw.Time;
        var elapsed = now - lastFrame;
        lastFrame = now;
        Guarded(
            "working the decks from the keyboard",
            () =>
            {
                controls.Update(elapsed);
                plays.Update(decks, elapsed);
                // The set kept as it grows (or shrinks, a song marked as not played).
                // The stream's title follows the song playing, and how it's getting on is said.
                if (liveStream.Stream is { } live)
                    live.Title = OnAir();
                FollowStream();
                if (plays.History.Count != keptPlays)
                {
                    keptPlays = plays.History.Count;
                    if (playLog.Keep(sessionStarted, plays.History) is { } historyError)
                        screen.Log($"Couldn't keep the play history: {historyError}");
                }
            }
        );
        Guarded(
            "running Auto-Mix",
            () =>
            {
                autoMix.Update(elapsed);
                // Its blends are planned as gradual fades, so it mixes on the smooth curve whatever's chosen. Both
                // curves agree at the ends, where it starts and stops, so nothing jumps when it takes the curve over or
                // gives it back there.
                audio.CrossfaderCurveOverride = autoMix.IsRunning ? CrossfaderCurve.Smooth : null;
            }
        );
        Guarded("talking to the lighting software", () => lighting.Update(now));
        Guarded("working the decks from the controller", () => controllers?.Update(now, elapsed));
        Guarded("looking for drives", () => ScanDrives(now));
        if (now >= nextSessionSave)
        {
            nextSessionSave = now + SESSION_SAVE_SECONDS;
            Guarded("writing down where the decks are", () => SessionState.Of(decks).Save(sessionPath));
        }
        Guarded(
            "taking in changes to the library",
            () =>
            {
                if (watcher?.TakeChanges(libraryQuiet) == true)
                    TakeInLibraryChanges();
            }
        );

        // Separating stems keeps off the graphics card, which drawing the screen needs, while anything's playing.
        separator.Busy = export is not null || preview.IsPlaying || decks.Any(deck => deck.IsPlaying);

        // With nothing playing, songs not analysed yet are done one at a time: those listed first, then the library.
        if (now >= nextIdleAnalysis)
        {
            nextIdleAnalysis = now + IDLE_ANALYSIS_SECONDS;
            Guarded(
                "analysing while idle",
                () =>
                {
                    if (settings.AnalyseWhenIdle && export is null && !preview.IsPlaying && !decks.Any(deck => deck.IsPlaying))
                        analyser.RequestIdle(browser.ListedSongs.Concat(library.Songs));
                }
            );
        }
        if (now >= nextRekordboxCheck)
        {
            nextRekordboxCheck = now + REKORDBOX_CHECK_SECONDS;
            Guarded("looking at the Rekordbox collections", CheckRekordbox);
        }
        if (now >= nextDeviceCheck)
        {
            nextDeviceCheck = now + DEVICE_CHECK_SECONDS;
            Guarded(
                "looking after the sound cards",
                () =>
                {
                    foreach (var news in audio.CheckDevices())
                        screen.Log(news);
                }
            );
        }
        if (minimised)
            continue;

        Guarded(
            "drawing the screen",
            () =>
            {
                // In the UI's units: the screen's pixels over the size it's drawn at.
                screen.ScreenSize = new float2(size.X, size.Y) / screenScale;
                if (controllerDialog is not null)
                    controllerDialog.ScreenSize = screen.ScreenSize;
                headphones.ScreenSize = masterFx.ScreenSize = microphone.ScreenSize = liveStream.ScreenSize = menu.ScreenSize = prompt.ScreenSize = details.ScreenSize = confirmation.ScreenSize = tracklistDialog.ScreenSize = screen.ScreenSize;
                screen.Update();
                headphones.Refresh();
                masterFx.Refresh();
                microphone.Refresh();
                liveStream.Refresh();
                ui.Layout(screen.ScreenSize);
                input.UpdateCursor();
                renderer.DrawFrame(ui);
                screenshots?.Frame();
            }
        );
        if (!windowShown)
            window.Visible = windowShown = true;
    }

    // Closing properly, nothing needs picking up next time.
    // An export still going stops, so it doesn't leave half a file behind.
    export?.Cancel();
    export?.Wait(TimeSpan.FromSeconds(5));
    SessionState.Delete(sessionPath);
    watcher?.Dispose();

    // A recording still going is finished as the app closes, so its file is whole, and a stream's let go.
    StopRecording();
    StopStreaming();

    // Anything about the library not yet saved (a drive's file to write again with relative paths, say) is saved as
    // the app closes.
    if (database.Dirty)
    {
        try
        {
            database.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Couldn't save the library: {e.Message}");
        }
    }

    // The headphone blend and volume are set on the mixer as it plays, so they're kept as the app closes, with how
    // the browser was left, and the devices chosen, whether or not they're plugged in.
    settings.Browser = browser.Layout;
    settings.BrowserFolder = browser.Source is { } shown && shown != folders[0] ? [.. LibraryTree.PathOf(shown)] : null;
    KeepWaveformSettings();
    SaveSettings();

    WindowsShell.KeepAwake(false);
    if (OperatingSystem.IsWindows())
        WindowChrome.Remove();
    return screenshots?.ExitCode ?? 0;
}

// The heaviest weight of the system font, for a letter or two that must stand out; null if there's none, to fall
// back on bold.
static string? FindHeavyFont()
{
    if (!OperatingSystem.IsWindows())
        return null;
    var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    return new[] { Path.Combine(fonts, "seguibl.ttf"), Path.Combine(fonts, "ariblk.ttf") }.FirstOrDefault(File.Exists);
}

static string FindFont(bool bold) =>
    FontAtlas.FindSystemFont(bold)
    ?? FontAtlas.FindSystemFont()
    ?? throw new FileNotFoundException("No usable system font found; install DejaVu Sans or Liberation Sans");
