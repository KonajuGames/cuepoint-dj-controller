using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutality;
using Brutality.Audio;
using Brutality.Input;
using Brutality.Midi;
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

    // Songs put together by hand, a playlist file each. Deleting one sends its file to the Recycle Bin, unless
    // Windows is set to delete straight away on that drive.
    var virtualFolders = new VirtualFolders(
        library,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuepoint", "Virtual folders")
    );
    if (OperatingSystem.IsWindows())
        virtualFolders.RemoveFile = WindowsShell.MoveToRecycleBin;

    var folders = LibraryTree.Build(library, decks, history, virtualFolders);
    var virtualFoldersNode = folders.Single(node => node.Kind == LibraryNodeKind.VirtualFolders);

    var ui = new Root();
    var screen = ui.Add(new MainScreen(audio, decks, library, analyser, folders, database.FilePath, fonts, images, HotcueKeys.Labels, KeyboardControls.HintsFor));
    using var input = new GlfwUiInput(window, ui);
    var browser = screen.Browser;

    // The library's own folder is what shows to begin with, with the virtual folders, lists and filters open below
    // the drives.
    foreach (var heading in folders.Where(node => node.Kind is LibraryNodeKind.VirtualFolders || node.Name is "Lists" or "Filters"))
        screen.FolderTree.SetExpanded(heading, true);
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

    // Songs played out for ten seconds or more get a tick in the list.
    var plays = new SessionPlays();
    browser.IsPlayed = song => plays.Contains(song.Path);

    foreach (var deck in decks)
    {
        deck.Loaded += loaded =>
        {
            plays.Restart(loaded);
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
    screen.SongDropped += (deck, song) => Load(song, deck);

    // Files dragged in from Explorer load on the deck they're dropped on: the first audio file among them, or the
    // first in the first folder dropped.
    input.FilesDropped += (point, paths) =>
    {
        if (screen.DeckAt(point) is not { } deck)
        {
            screen.Log("Drop songs on a deck to load them");
            return;
        }

        // A file outside the library becomes a song by browsing its folder, as the folder tree would.
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
    var menu = new ContextMenu(fonts.Body);
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

        controls.Key(key, action, mods);

        switch (key)
        {
            case GlfwKey.Escape when pressed:
                if (menu.Visible)
                    menu.Close();
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

    screen.FolderTree.ItemSecondaryClicked += (node, point) =>
    {
        if (node.Kind == LibraryNodeKind.VirtualFolders)
            menu.Show(point, [new MenuItem("New virtual folder...", NewVirtualFolder)]);
        else if (node.Folder is { } folder)
            menu.Show(
                point,
                [
                    new MenuItem("Rename...", () => RenameVirtualFolder(folder)),
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
        menu.Show(
            at,
            [
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

    var lastFrame = Glfw.Time;
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

        // Faders whose keys are held move by the time since the last frame.
        var now = Glfw.Time;
        controls.Update(now - lastFrame);
        plays.Update(decks, now - lastFrame);
        controllers?.Update(now, now - lastFrame);
        lastFrame = now;

        screen.ScreenSize = new float2(size.X, size.Y);
        if (controllerDialog is not null)
            controllerDialog.ScreenSize = screen.ScreenSize;
        menu.ScreenSize = prompt.ScreenSize = confirmation.ScreenSize = screen.ScreenSize;
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
