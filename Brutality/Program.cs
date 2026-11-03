using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutality;
using Brutality.Audio;
using Brutality.Input;
using Brutality.Midi;
using Brutality.Music;
using Brutality.Music.Imports;
using Brutality.Os2l;
using Brutality.Rendering;
using Brutality.UI;

WindowsShell.AttachParentConsole();

// What goes wrong is written down beside the settings, to look into after a set; even on another thread, where it
// still ends the app.
ErrorLog.FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuepoint", "errors.log");
AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    if (args.ExceptionObject is Exception e)
        ErrorLog.Write("working in the background", e);
};

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
    ErrorLog.Write("running", e);
    WindowsShell.ShowFatalError(e);
    return 1;
}

return 0;

static void Run()
{
    Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);

    // It's made maximised, as the screen is laid out to fill it, but hidden: Windows would otherwise show it empty
    // while the app gets going, white where it was first sized and black beyond. It's shown once the first frame is
    // drawn.
    Glfw.WindowHint(GlfwWindowHint.Visible, 0);
    Glfw.WindowHint(GlfwWindowHint.Maximized, 1);

    // Declared first so it's disposed last, after the GPU objects that draw to it. It can't be made too small for the
    // decks and mixer to fit side by side.
    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Cuepoint", Size = new int2(1440, 860) });
    window.SetSizeLimits(new int2(1280, 720), new int2(-1, -1));

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
        Big: atlas.Add(bold, 26),
        Large: atlas.Add(regular, 20)
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

    using var audio = new AudioEngine();
    using var deckA = audio.CreateDeck("A", CrossfaderSide.Left);
    using var deckB = audio.CreateDeck("B", CrossfaderSide.Right);
    Deck[] decks = [deckA, deckB];

    // Settings kept between runs, beside the library's database. The output goes first, as headphones on its channels
    // 3 and 4 need it there.
    var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuepoint", "settings.json");
    var settings = AppSettings.Load(settingsPath);
    audio.CueMix = settings.CueMix;
    audio.HeadphoneVolume = settings.HeadphoneVolume;
    audio.Limiter.Enabled = settings.MasterLimiter;
    var outputFound = audio.UseOutputDevice(settings.OutputDevice);
    var headphonesFound = audio.UseHeadphoneDevice(settings.HeadphoneDevice);
    // Those not plugged in are still the ones chosen, gone to as soon as they are.
    audio.Choose(settings.OutputDevice, settings.HeadphoneDevice);
    void SaveSettings()
    {
        // The devices chosen, even while one's missing and the mix is on the default instead.
        settings.OutputDevice = audio.ChosenOutputDevice;
        settings.HeadphoneDevice = audio.ChosenHeadphoneDevice;
        settings.CueMix = audio.CueMix;
        settings.HeadphoneVolume = audio.HeadphoneVolume;
        settings.UnquantizedDecks = decks.Where(deck => !deck.Quantize).Select(deck => deck.Name).ToList() is { Count: > 0 } off ? off : null;
        settings.Save(settingsPath);
    }

    static string FolderKey(string path) => Path.TrimEndingDirectorySeparator(path);

    // A folder lists only the songs directly in it. The one chosen can be made recursive, to list every song under it,
    // for as long as it stays chosen.
    LibraryNode? recursiveNode = null;
    bool IsRecursive(string path) => recursiveNode?.Path == path;

    // The library scans now and reads tags in the background; songs load onto decks in the background too. It's the
    // folder last chosen as the library, or Windows' own Music folder to begin with.
    // It's looked through in the background once the screen's up, so a big one doesn't keep the window waiting.
    using var library = new MusicLibrary(settings.MusicFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), scan: false);

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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuepoint", "library.json")
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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), VirtualFolders.DriveFolder),
        () => VirtualFolders.ReadyDrives(WindowsShell.IsReadOnlyVolume)
    );
    if (OperatingSystem.IsWindows())
        virtualFolders.RemoveFile = WindowsShell.MoveToRecycleBin;

    // Other DJ software's playlists and hot cues: Rekordbox collections exported as XML, and Serato's crates. Each
    // collection's hot cues come into the library once (and again when it's exported afresh), for songs with none
    // of their own here; a record of what's come in keeps hot cues cleared here from coming back.
    var imports = ImportedLibraries.Find(library.Root, VirtualFolders.ReadyDrives().Select(drive => drive.Root), settings.RekordboxXmlFiles);
    var importRecordPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuepoint", "imports.json");
    var importRecord = ImportRecord.Load(importRecordPath);
    // A collection's hot cues, for each of its songs here with none of their own; how many songs got them, and why
    // they couldn't be saved, if they couldn't.
    (int Songs, string? Error) ImportHotcues(RekordboxCollection collection)
    {
        var songs = 0;
        foreach (var (path, cues) in collection.Hotcues)
        {
            if (!File.Exists(path) || database.GetHotcues(path).Any(cue => cue is not null))
                continue;
            database.SetHotcues(path, cues);
            songs++;
        }
        if (songs == 0)
            return (0, null);
        try
        {
            database.Save();
            return (songs, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (songs, e.Message);
        }
    }

    var importNews = new List<string>();
    foreach (var collection in imports.Rekordbox)
    {
        if (!ImportRecord.IsNew(importRecord.Rekordbox, collection.FilePath))
            continue;
        var (songs, error) = ImportHotcues(collection);
        if (songs > 0)
            importNews.Add($"Brought in hot cues for {songs} {(songs == 1 ? "song" : "songs")} from {collection.FilePath}");
        if (error is not null)
            importNews.Add($"Couldn't save them: {error}");
    }
    importRecord.Save(importRecordPath);

    var folders = LibraryTree.Build(library, decks, history, virtualFolders, IsRecursive, imports);
    var virtualFoldersNode = folders.Single(node => node.Kind == LibraryNodeKind.VirtualFolders);

    // Each deck's beat grid can be fixed by hand, with its pads in GRID mode.
    var gridEditors = decks.Select(deck => new GridEditor(deck, library, database)).ToArray();
    GridEditor? GridEditorFor(Deck deck) => gridEditors.FirstOrDefault(editor => editor.Deck == deck);

    // And each deck's pads can roll loops instead, with them in ROLL mode.
    var rollPads = gridEditors.Select(editor => new RollPads(editor.Deck, editor)).ToArray();
    RollPads? RollPadsFor(Deck deck) => rollPads.FirstOrDefault(roll => roll.Deck == deck);

    // Or beat jumps, with them in JUMP mode.
    var jumpPads = gridEditors.Select((editor, i) => new JumpPads(editor.Deck, editor, rollPads[i])).ToArray();
    JumpPads? JumpPadsFor(Deck deck) => jumpPads.FirstOrDefault(jump => jump.Deck == deck);

    // Quantise is on for each deck unless it was turned off.
    foreach (var deck in decks)
        deck.Quantize = settings.UnquantizedDecks?.Contains(deck.Name) != true;

    var ui = new Root { TooltipFont = fonts.Body };
    var screen = ui.Add(new MainScreen(audio, decks, library, analyser, folders, $"{database.FilePath}, or \\{LibraryDatabase.DRIVE_FOLDER} on other drives", fonts, images, HotcueKeys.Labels, KeyboardControls.HintsFor, GridEditorFor, RollPadsFor, JumpPadsFor));
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
    var lastFolder = settings.BrowserFolder is { } lastPath ? LibraryTree.Find(folders, lastPath) : null;
    if (lastFolder is { HasSongs: true })
    {
        for (var parent = lastFolder.Parent; parent is not null; parent = parent.Parent)
            screen.FolderTree.SetExpanded(parent, true);
        screen.FolderTree.Select(lastFolder);
        screen.FolderTree.ScrollIntoView(lastFolder);
    }
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

    // What other DJ software there was, and what came in from it, said with the library's songs, as the status line
    // shows only the last thing said.
    string? importSummary = null;
    if (!imports.IsEmpty)
    {
        var found = new List<string>();
        if (imports.Rekordbox.Count > 0)
            found.Add(imports.Rekordbox.Count == 1 ? "a Rekordbox collection" : $"{imports.Rekordbox.Count} Rekordbox collections");
        var crates = imports.Serato.Sum(drive => drive.Crates.Count);
        if (crates > 0)
            found.Add(crates == 1 ? "a Serato crate" : $"{crates} Serato crates");
        importSummary = string.Join("; ", [$"found {string.Join(" and ", found)}, in the folder list", .. importNews]);
    }

    // The library's looked through in the background, its songs coming in once it's done.
    screen.Log($"Looking through {library.Root}" + (importSummary is null ? "" : $"; {importSummary}"));
    library.BeginScan(
        library.Root,
        library.Recursive,
        () =>
        {
            AfterLibraryScan();
            if (library.Error is null && database.LoadError is { } loadError)
                screen.Log($"Couldn't read {loadError}; it starts afresh");
            else
                LogLibrary(importSummary);
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
        if (recursiveNode is { } recursive && recursive != node)
            SetRecursive(recursive, false);
        if (node.HasSongs)
            RememberFolder();
    };

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

    // A Rekordbox collection chosen to import: its playlists in the folder list, and hot cues for its songs with none
    // here. It's remembered, so it's found again next time, and read again whenever it changes.
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

        var (songs, error) = ImportHotcues(collection);
        var playlists = Count(collection.Playlists);
        screen.Log(
            $"Imported {Path.GetFileName(path)}: {playlists} {(playlists == 1 ? "playlist" : "playlists")} under Rekordbox, "
                + (songs > 0 ? $"hot cues for {songs} {(songs == 1 ? "song" : "songs")}" : "no hot cues needed")
                + (error is null ? "" : $"; couldn't save them: {error}")
        );

        static int Count(IEnumerable<RekordboxNode> nodes) => nodes.Sum(node => node.IsFolder ? Count(node.Children) : 1);
    }

    // Any folder can be made the music library. The library's own lists and filters follow it straight away.
    void UseAsMusicLibrary(string path)
    {
        library.BeginScan(
            path,
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

    // Making a folder recursive lists every song under it, until another is chosen. For the library, it rescans, and
    // its lists and filters follow.
    void SetRecursive(LibraryNode node, bool recursive)
    {
        recursiveNode = recursive ? node : null;
        if (node.Kind == LibraryNodeKind.Library)
        {
            library.BeginScan(library.Root, recursive, AfterLibraryScan);
            WatchLibrary();
        }
        else
            node.RefreshSongs();
        if (browser.Source == node || node.Kind == LibraryNodeKind.Library && browser.Source?.Kind is LibraryNodeKind.List or LibraryNodeKind.Filter)
            browser.Reload();
    }

    void ToggleRecursive(LibraryNode node)
    {
        SetRecursive(node, recursiveNode != node);
        var where = node.Kind == LibraryNodeKind.Library ? library.Root : node.Path;
        screen.Log(recursiveNode == node ? $"Listing every song under {where}" : $"Listing only the songs directly in {where}");
    }

    // The mix can be recorded, as the room hears it, with the title bar's record button: into a WAV file named for
    // when it started, in the recordings folder.
    var recordingsFolder =
        settings.RecordingsFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Cuepoint Recordings");
    screen.Options.RecordingsFolder = recordingsFolder;
    notWatched = path => path.StartsWith(LibraryFile.WithSeparator(recordingsFolder), StringComparison.OrdinalIgnoreCase);
    MixRecorder? recorder = null;
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
        var saved = files.Count > 1 ? $"{stopping.Path} and {files.Count - 1} more" : stopping.Path;
        var length = stopping.Duration;
        var time = length.TotalHours >= 1 ? $"{(int)length.TotalHours}:{length.Minutes:00}:{length.Seconds:00}" : $"{length.Minutes}:{length.Seconds:00}";
        screen.Log(
            stopping.Error is { } error
                ? $"Recording stopped: {error}. What was recorded is in {saved}"
                : $"Saved {time} of the mix in {saved}"
        );
    }
    screen.TitleBar.Recording = () => recorder?.Duration;
    screen.TitleBar.RecordClicked += () =>
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
    screen.TitleBar.AudioWarning = () => audio.Missing;
    if (OperatingSystem.IsWindows())
        WindowChrome.Install(window, point => screen.TitleBar.IsCaption(new float2(point.X, point.Y)));

    // Songs played out for ten seconds or more get a tick in the list.
    var plays = new SessionPlays();

    // The songs played this session, in order, saved where the DJ says: as an M3U playlist or a plain-text set list,
    // by the type chosen in the Save dialog.
    void ExportHistory()
    {
        if (plays.History.Count == 0)
        {
            screen.Log("Nothing's been played yet this session, so there's no history to export");
            return;
        }
        (string Description, string Pattern)[] types = [("M3U playlist", "*.m3u8"), ("Plain text", "*.txt")];
        var owner = OperatingSystem.IsWindows() ? WindowChrome.Handle : 0;
        var name = $"Play history {plays.History[0].Started:yyyy-MM-dd}";
        if (WindowsShell.AskWhereToSave(owner, "Export play history", types, name, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) is not { } chosen)
            return;
        var format = chosen.Type == 0 ? HistoryFormat.M3u : HistoryFormat.Text;
        var error = PlayHistory.Save(chosen.Path, PlayHistory.Write(plays.History, format, library.SongAt), format);
        var count = plays.History.Count;
        screen.Log(
            error is not null ? $"Couldn't export the play history: {error}"
            : $"Exported {count} {(count == 1 ? "song" : "songs")} played to {chosen.Path}"
        );
    }
    browser.IsPlayed = song => plays.Contains(song.Path);

    // Decks being put back after the app didn't close properly, and where each goes once its song is in.
    var restoring = new Dictionary<Deck, (double Position, bool Play, int KeyShift)>();

    foreach (var deck in decks)
    {
        deck.Loaded += loaded =>
        {
            if (restoring.Remove(loaded, out var restore))
            {
                loaded.Seek(TimeSpan.FromSeconds(restore.Position));
                loaded.KeyShift = restore.KeyShift;
                // Not if it would only reach the end at once.
                if (restore.Play && restore.Position < loaded.Duration.TotalSeconds - 1)
                    loaded.Play();
            }
            plays.Restart(loaded);
            screen.Log($"Deck {loaded.Name}: loaded {loaded.Artist} - {loaded.Title}");

            // Hot cues Serato kept in the file (an MP3, M4A or FLAC) come in the first time it's loaded (and again if it
            // changes), if it has none here.
            if (loaded.FilePath is { } loadedPath && ImportRecord.IsNew(importRecord.Serato, loadedPath))
            {
                if (database.GetHotcues(loadedPath).All(cue => cue is null) && SeratoMarkers.ReadHotcues(loadedPath) is { } serato)
                {
                    database.SetHotcues(loadedPath, serato);
                    var count = serato.Count(cue => cue is not null);
                    screen.Log($"Deck {loaded.Name}: brought in {count} {(count == 1 ? "hot cue" : "hot cues")} from Serato");
                    try
                    {
                        database.Save();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        screen.Log($"Couldn't save hot cues to {database.FilePath}: {e.Message}");
                    }
                }
                importRecord.Save(importRecordPath);
            }
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

    screen.Options.MasterLimiterToggled += () =>
    {
        settings.MasterLimiter = audio.Limiter.Enabled;
        settings.Save(settingsPath);
    };

    screen.Options.LightingEnabled = () => lighting.Enabled;
    screen.Options.LightingStatus = () => lighting.Status;
    screen.TitleBar.Os2lConnected = () => lighting.Enabled ? lighting.IsConnected : null;
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
    screen.TitleBar.HeadphonesClicked += () => headphones.Toggle(screen.TitleBar.HeadphonesAnchor);
    screen.TitleBar.Cueing = () => decks.Any(deck => deck.Headphones);
    browser.ColumnMenuRequested += at => menu.Show(at, browser.ColumnMenu());
    var prompt = new NamePrompt(ui, fonts.Body, fonts.Bold, fonts.Heading);
    var confirmation = new ConfirmDialog(ui, fonts.Body, fonts.Bold, fonts.Heading);

    var controls = new KeyboardControls(decks, audio);

    // DJ controllers, found as they're plugged in. The load buttons take the selected song; the browse knob works
    // the list (and with Shift, the folders) as the arrow keys do.
    using var midiDevices = OperatingSystem.IsWindows() ? new MidiDevices(new WinMidi()) : null;
    ControllerDialog? controllerDialog = null;
    using var controllers = midiDevices is null
        ? null
        : new MidiControls(midiDevices, decks, audio, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuepoint", "controllers"));
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
        };
        controllers.IsRollMode = deck => RollPadsFor(deck)?.Active == true;

        // Shift + HOT CUE puts the controller's pads in its beat jump mode, and the deck's in JUMP mode to match.
        controllers.JumpModePressed += deck =>
        {
            if (JumpPadsFor(deck) is { } jump)
                jump.Active = true;
        };
        controllers.IsJumpMode = deck => JumpPadsFor(deck)?.Active == true;
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
    input.UnhandledKey += (key, action, mods) =>
    {
        if (action == GlfwKeyAction.Repeat)
            return;
        var pressed = action == GlfwKeyAction.Press;

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
                else if (prompt.Visible)
                    prompt.Close();
                else if (confirmation.Visible)
                    confirmation.Close();
                else if (controllerDialog?.Escape() != true)
                    screen.Options.Close();
                break;
            case GlfwKey.Number1 when pressed && browser.SelectedSong is { } song:
                Load(song, deckA);
                break;
            case GlfwKey.Number2 when pressed && browser.SelectedSong is { } song:
                Load(song, deckB);
                break;
        }
    };

    // Right-click menus and the name prompt go over everything, so they're added last.
    ui.Add(headphones);
    ui.Add(menu);
    ui.Add(prompt);
    ui.Add(confirmation);

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
            virtualFolders.Delete(folder);
            FoldersChanged();
            if (showing || screen.FolderTree.SelectedItem?.Folder == folder)
                screen.FolderTree.Select(virtualFoldersNode);
            if (showing)
                browser.Source = folders[0];
            screen.Log($"Deleted {folder.Name}; its songs are untouched");
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

    screen.FolderTree.ItemSecondaryClicked += (node, point) =>
    {
        if (node.Kind == LibraryNodeKind.VirtualFolders)
            menu.Show(point, [new MenuItem("New virtual folder...", NewVirtualFolder)]);
        else if (node.Kind == LibraryNodeKind.Library)
            menu.Show(
                point,
                [
                    new MenuItem("Recursive", () => ToggleRecursive(node), Checked: recursiveNode == node),
                    // Other DJ software's libraries, one format to a line, more to come.
                    new MenuItem("Import", [new MenuItem("Rekordbox XML...", () => afterEvents.Enqueue(ImportRekordbox))]),
                    new MenuItem("Export", [new MenuItem("Play history...", () => afterEvents.Enqueue(ExportHistory))]),
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
                ]
            );
        else if (node.Folder is { } folder)
            menu.Show(
                point,
                [
                    new MenuItem("Rename...", () => RenameVirtualFolder(folder)),
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
    };

    screen.FolderTree.AcceptsDrop = (node, payload) => node.Folder is not null && payload is IReadOnlyList<Song> { Count: > 0 };
    screen.FolderTree.ItemDropped += (node, payload) =>
    {
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

    // A menu for songs in the list, acting on the selection.
    browser.SongsSecondaryClicked += (songs, at) =>
    {
        var played = songs.Where(song => plays.Contains(song.Path)).ToList();
        var listed = browser.List.Items;
        menu.Show(
            at,
            [
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
                new MenuItem(
                    played.Count > 1 ? $"Mark {played.Count} songs as not played" : "Mark as not played",
                    () =>
                    {
                        plays.Forget(played.Select(song => song.Path));
                        screen.Log(played.Count == 1 ? $"{played[0].Title} is marked as not played" : $"{played.Count} songs are marked as not played");
                    },
                    Enabled: played.Count > 0
                ),
            ]
        );
    };

    browser.AnalyseAllCancelled += left => screen.Log($"Stopped analysing all; {left} {(left == 1 ? "song was" : "songs were")} left unanalysed");

    browser.RemoveRequested += (folder, songs) =>
    {
        var error = virtualFolders.Remove(folder, songs);
        screen.Log(error is not null ? $"Couldn't save {folder.Name}: {error}" : $"Took {(songs.Count == 1 ? songs[0].Title : $"{songs.Count} songs")} out of {folder.Name}");
    };
    browser.MoveRequested += (folder, songs, before) =>
    {
        if (virtualFolders.Move(folder, songs, before) is { } error)
            screen.Log($"Couldn't save {folder.Name}: {error}");
    };

    // What the decks are doing is written down every couple of seconds, and the record deleted as the app closes. One
    // left from last time means the app didn't close properly, so each deck gets its song back where it was, with its
    // tempo, key lock, key shift and slip. A deck that was playing a moment ago plays on from where it would have got to; after longer,
    // it waits where it was.
    const double SESSION_SAVE_SECONDS = 2;
    var sessionPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuepoint", "session.json");
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
            Load(song, deck);
        }
        if (restoring.Count > 0)
            screen.Log(
                resume
                    ? "Cuepoint didn't close properly, so the decks are picking up where they were"
                    : "Cuepoint didn't close properly last time, so the decks have their songs back where they were"
            );
    }
    var nextSessionSave = 0.0;

    // Sound cards come and go, a controller's unplugged by mistake, say: they're looked at every second, so the music
    // carries on somewhere within a second or two, and goes back as soon as they're back.
    const double DEVICE_CHECK_SECONDS = 1;
    var nextDeviceCheck = 0.0;

    // The Rekordbox collections are looked at every few seconds, and one that's been exported afresh is read again:
    // its playlists, and hot cues for songs with none here.
    const double REKORDBOX_CHECK_SECONDS = 5;
    var nextRekordboxCheck = REKORDBOX_CHECK_SECONDS;
    void CheckRekordbox()
    {
        foreach (var known in imports.Rekordbox.ToList())
        {
            if (!File.Exists(known.FilePath) || !ImportRecord.IsNew(importRecord.Rekordbox, known.FilePath))
                continue;
            importRecord.Save(importRecordPath);
            if (RekordboxXml.Load(known.FilePath) is not { } changed)
                continue;
            imports.Put(changed);
            ShowRekordbox();
            var (songs, error) = ImportHotcues(changed);
            screen.Log(
                error is not null ? $"{Path.GetFileName(changed.FilePath)} changed; couldn't save its hot cues: {error}"
                : songs > 0 ? $"{Path.GetFileName(changed.FilePath)} changed, so it's read again; hot cues for {songs} {(songs == 1 ? "song" : "songs")} brought in"
                : $"{Path.GetFileName(changed.FilePath)} changed, so its playlists are read again"
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

    var lastFrame = Glfw.Time;
    var windowShown = false;
    while (!window.ShouldClose)
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
                screen.ScreenSize = new float2(size.X, size.Y);
                if (controllerDialog is not null)
                    controllerDialog.ScreenSize = screen.ScreenSize;
                headphones.ScreenSize = menu.ScreenSize = prompt.ScreenSize = confirmation.ScreenSize = screen.ScreenSize;
                screen.Update();
                headphones.Refresh();
                ui.Layout(screen.ScreenSize);
                input.UpdateCursor();
                renderer.DrawFrame(ui);
            }
        );
        if (!windowShown)
            window.Visible = windowShown = true;
    }

    // Closing properly, nothing needs picking up next time.
    SessionState.Delete(sessionPath);
    watcher?.Dispose();

    // A recording still going is finished as the app closes, so its file is whole.
    StopRecording();

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
}

static string FindFont(bool bold) =>
    FontAtlas.FindSystemFont(bold)
    ?? FontAtlas.FindSystemFont()
    ?? throw new FileNotFoundException("No usable system font found; install DejaVu Sans or Liberation Sans");
