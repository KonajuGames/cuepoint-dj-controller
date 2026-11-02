using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutality;
using Brutality.Audio;
using Brutality.Input;
using Brutality.Midi;
using Brutality.Music;
using Brutality.Os2l;
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
    var outputFound = audio.UseOutputDevice(settings.OutputDevice);
    var headphonesFound = audio.UseHeadphoneDevice(settings.HeadphoneDevice);
    void SaveSettings()
    {
        settings.OutputDevice = audio.OutputDevice;
        settings.HeadphoneDevice = audio.HeadphoneDevice;
        settings.CueMix = audio.CueMix;
        settings.HeadphoneVolume = audio.HeadphoneVolume;
        settings.Save(settingsPath);
    }

    static string FolderKey(string path) => Path.TrimEndingDirectorySeparator(path);

    // A folder lists only the songs directly in it. The one chosen can be made recursive, to list every song under it,
    // for as long as it stays chosen.
    LibraryNode? recursiveNode = null;
    bool IsRecursive(string path) => recursiveNode?.Path == path;

    // The library scans now and reads tags in the background; songs load onto decks in the background too. It's the
    // folder last chosen as the library, or Windows' own Music folder to begin with.
    using var library = new MusicLibrary(settings.MusicFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));

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

    var folders = LibraryTree.Build(library, decks, history, virtualFolders, IsRecursive);
    var virtualFoldersNode = folders.Single(node => node.Kind == LibraryNodeKind.VirtualFolders);

    // Each deck's beat grid can be fixed by hand, with its pads in GRID mode.
    var gridEditors = decks.Select(deck => new GridEditor(deck, library, database)).ToArray();
    GridEditor? GridEditorFor(Deck deck) => gridEditors.FirstOrDefault(editor => editor.Deck == deck);

    // And each deck's pads can roll loops instead, with them in ROLL mode.
    var rollPads = gridEditors.Select(editor => new RollPads(editor.Deck, editor)).ToArray();
    RollPads? RollPadsFor(Deck deck) => rollPads.FirstOrDefault(roll => roll.Deck == deck);

    var ui = new Root();
    var screen = ui.Add(new MainScreen(audio, decks, library, analyser, folders, $"{database.FilePath}, or \\{LibraryDatabase.DRIVE_FOLDER} on other drives", fonts, images, HotcueKeys.Labels, KeyboardControls.HintsFor, GridEditorFor, RollPadsFor));
    foreach (var editor in gridEditors)
        editor.Log += screen.Log;
    using var input = new GlfwUiInput(window, ui);
    var browser = screen.Browser;

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

    void LogLibrary() =>
        screen.Log(
            library.Error is { } error ? $"Couldn't read {library.Root}: {error}"
            : $"{library.Songs.Count} songs in {library.Root}, {library.Songs.Count(song => song.Analysis is not null)} analysed"
        );
    if (library.Error is null && database.LoadError is { } loadError)
        screen.Log($"Couldn't read {loadError}; it starts afresh");
    else
        LogLibrary();

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

    // Any folder can be made the music library. The library's own lists and filters follow it straight away.
    void UseAsMusicLibrary(string path)
    {
        library.Scan(path);
        analyser.LookUp(library.Songs);
        settings.MusicFolder = library.Root;
        settings.Save(settingsPath);
        screen.Options.MusicFolder = library.Root;
        if (browser.Source?.Kind is LibraryNodeKind.Library or LibraryNodeKind.List or LibraryNodeKind.Filter)
            browser.Reload();
        LogLibrary();
    }

    // Making a folder recursive lists every song under it, until another is chosen. For the library, it rescans, and
    // its lists and filters follow.
    void SetRecursive(LibraryNode node, bool recursive)
    {
        recursiveNode = recursive ? node : null;
        if (node.Kind == LibraryNodeKind.Library)
        {
            library.Scan(library.Root, recursive);
            analyser.LookUp(library.Songs);
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
        screen.Log($"Couldn't find {settings.OutputDevice} for the main output, so it's on the system default; choose it again in the options");
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
    // Lighting software, told the beat and what each deck is doing.
    using var lighting = new Os2lLink(decks, audio);
    lighting.Log += screen.Log;
    lighting.Enabled = settings.Os2lEnabled;
    lighting.DirectAddress = settings.Os2lAddress;
    screen.LargeListText = settings.LargeListText;
    screen.Options.LargeListText = () => screen.LargeListText;
    screen.Options.LargeListTextToggled += () =>
    {
        screen.LargeListText = settings.LargeListText = !screen.LargeListText;
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
        };
        controllers.IsRollMode = deck => RollPadsFor(deck)?.Active == true;
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
        // Shift making a nudge a fine one; or in ROLL mode, loop rolls while they're held.
        if (HotcueKeys.For(key) is { } hotcue)
        {
            // A roll lasts while its key's held, whatever mode the pads are in by the time it's let go.
            if (!pressed && hotcue.Deck < decks.Length)
                rollPads[hotcue.Deck].Up(hotcue.Hotcue);
            else if (pressed && hotcue.Deck < decks.Length && rollPads[hotcue.Deck].Active)
                rollPads[hotcue.Deck].Down(hotcue.Hotcue);
            else if (pressed && hotcue.Deck < decks.Length && gridEditors[hotcue.Deck].Active)
                gridEditors[hotcue.Deck].Pad(hotcue.Hotcue, fine: (mods & GlfwModifier.Shift) != 0);
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
            menu.Show(point, [new MenuItem("Recursive", () => ToggleRecursive(node), Checked: recursiveNode == node)]);
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

        // A set mustn't stop for a screen saver or sleep, so the computer stays awake while the window can be seen,
        // and is let go while it's minimised.
        WindowsShell.KeepAwake(!minimised);

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
        lighting.Update(now);
        controllers?.Update(now, now - lastFrame);
        ScanDrives(now);
        lastFrame = now;
        if (minimised)
            continue;

        screen.ScreenSize = new float2(size.X, size.Y);
        if (controllerDialog is not null)
            controllerDialog.ScreenSize = screen.ScreenSize;
        menu.ScreenSize = prompt.ScreenSize = confirmation.ScreenSize = screen.ScreenSize;
        screen.Update();
        ui.Layout(screen.ScreenSize);
        input.UpdateCursor();
        renderer.DrawFrame(ui);
    }

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
    // the browser was left. (Not the devices, if one chosen before wasn't found, so it's still chosen once it's
    // plugged in again.)
    settings.Browser = browser.Layout;
    settings.BrowserFolder = browser.Source is { } shown && shown != folders[0] ? [.. LibraryTree.PathOf(shown)] : null;
    if (headphonesFound && outputFound)
        SaveSettings();
    else
        settings.Save(settingsPath);

    WindowsShell.KeepAwake(false);
    if (OperatingSystem.IsWindows())
        WindowChrome.Remove();
}

static string FindFont(bool bold) =>
    FontAtlas.FindSystemFont(bold)
    ?? FontAtlas.FindSystemFont()
    ?? throw new FileNotFoundException("No usable system font found; install DejaVu Sans or Liberation Sans");
