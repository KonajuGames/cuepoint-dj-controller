using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Cue.Framework.UI;
using Cue.Framework.Window;
using Cuepoint.Audio;
using Cuepoint.Music;
using Cuepoint.UI;

namespace Cuepoint;

/// <summary>
/// The feature tour: <c>Cuepoint --tour [folder] [--ffmpeg path]</c>. On the same scratch folder and made-up songs as
/// the screenshot (longer ones, with a little more to them), it plays through the app's main features by itself,
/// pressing its keys and moving and clicking a pretend mouse, while every frame the GPU draws goes to ffmpeg as
/// <c>tour-raw.mp4</c> and the mix to <c>tour-audio.wav</c>. Nothing's played aloud, and the window lets the real mouse
/// through and doesn't take the focus, so the computer can be used meanwhile. What was said and shown when — the
/// captions, the boxes round what's being shown, the keys pressed and where the mouse was — goes in
/// <c>tour-timeline.json</c>, for <c>docs/tour/annotate.py</c> to draw over the video.
/// </summary>
sealed partial class ScreenshotTool
{
    const double TOUR_SECONDS = 150;
    const int FPS = 60;

    VideoWriter? _video;
    MixRecorder? _soundtrack;

    // The video's own clock: how long has been recorded, which stops while the tour skips ahead.
    double _recordedBefore;
    double _recordingSince = -1;

    double VideoTime => _recordedBefore + (_recordingSince >= 0 ? _clock.Elapsed.TotalSeconds - _recordingSince : 0);

    readonly List<string> _events = [];
    readonly StringBuilder _cursorTrack = new();
    float2 _mouse = new(1500, 760);
    bool _cursorShown;
    bool _mouseDown;

    /// <summary>
    /// The songs Auto-Mix plays at the end of the tour, from a virtual folder made for it.
    /// </summary>
    static readonly string[] TourPlaylist = ["Copper Skies", "Velvet Circuit", "Neon Harbour"];

    static void WriteTourPlaylist(string app, string music)
    {
        var folder = Path.Combine(app, "VirtualFolders");
        Directory.CreateDirectory(folder);
        var text = new StringBuilder("#EXTM3U\n");
        foreach (var title in TourPlaylist)
            if (Directory.EnumerateFiles(music, $"* {title}.wav", SearchOption.AllDirectories).FirstOrDefault() is { } path)
                text.Append(path).Append('\n');
        File.WriteAllText(Path.Combine(folder, "Warm-up set.m3u8"), text.ToString(), new UTF8Encoding(false));
    }

    // -------------------------------------------------------------------------------------------- recording frames

    /// <summary>
    /// Each frame while the tour records: the frame just drawn goes to the video, the next is asked for, and where
    /// the pretend mouse is goes down.
    /// </summary>
    void TourFrame()
    {
        if (_video is null || _recordingSince < 0)
            return;
        var t = VideoTime;
        if (_app.Renderer.TakeWindowCapture() is { } picture)
            _video.Add(picture.Rgba, t);
        _app.Renderer.CaptureWindow = true;
        if (_cursorShown)
            _cursorTrack.Append(CultureInfo.InvariantCulture, $"[{t:F3},{_mouse.X:F1},{_mouse.Y:F1},{(_mouseDown ? 1 : 0)}],");
    }

    void StartRecordingVideo()
    {
        var size = _app.Window.FramebufferSize;
        _video = new VideoWriter(_ffmpeg, Path.Combine(_out, "tour-raw.mp4"), size.X, size.Y, FPS, Path.Combine(_out, "tour-ffmpeg.log"));
        _soundtrack = new MixRecorder(Path.Combine(_out, "tour-audio.wav"));
        _app.Audio.Tap = _soundtrack;
        _recordingSince = _clock.Elapsed.TotalSeconds;
        _app.Renderer.CaptureWindow = true;
    }

    /// <summary>
    /// Stops the video and its sound together, to skip a wait; <see cref="ResumeVideo"/> carries on from there.
    /// </summary>
    void PauseVideo()
    {
        _recordedBefore = VideoTime;
        _recordingSince = -1;
        _app.Audio.Tap = null;
        _app.Renderer.TakeWindowCapture();
    }

    void ResumeVideo()
    {
        _app.Audio.Tap = _soundtrack;
        _recordingSince = _clock.Elapsed.TotalSeconds;
        _app.Renderer.CaptureWindow = true;
    }

    void FinishTour()
    {
        var end = VideoTime;
        _recordingSince = -1;
        _app.Audio.Tap = null;
        _soundtrack?.Dispose();
        _video?.Finish(end);
        Console.WriteLine($"{_video?.Captured} frames captured for {_video?.Written} written ({end:F1}s)");
        var size = _app.Window.FramebufferSize;
        var json = new StringBuilder();
        json.Append(CultureInfo.InvariantCulture, $"{{\"width\":{size.X},\"height\":{size.Y},\"fps\":{FPS},\"duration\":{end:F3},\n\"events\":[\n");
        json.AppendJoin(",\n", _events);
        json.Append("\n],\n\"cursor\":[").Append(_cursorTrack.ToString().TrimEnd(',')).Append("]}\n");
        File.WriteAllText(Path.Combine(_out, "tour-timeline.json"), json.ToString());
        Console.WriteLine($"tour written to {_out}");
    }

    // ---------------------------------------------------------------------------------------------- the timeline

    static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    void Event(string type, string fields = "") =>
        _events.Add(string.Create(CultureInfo.InvariantCulture, $"{{\"t\":{VideoTime:F3},\"type\":\"{type}\"{(fields.Length > 0 ? "," + fields : "")}}}"));

    Step Caption(string title, string body) => new Do(() => Event("caption", $"\"title\":{Json(title)},\"body\":{Json(body)}"));

    Step NoCaption() => new Do(() => Event("captionOff"));

    Step Card(string title, string body) => new Do(() => Event("card", $"\"title\":{Json(title)},\"body\":{Json(body)}"));

    Step NoCard() => new Do(() => Event("cardOff"));

    /// <summary>
    /// A box round what <paramref name="find"/> finds when it comes to it (nothing if it finds nothing), with an
    /// optional label.
    /// </summary>
    Step Highlight(Func<(float2 At, float2 Size)?> find, string label = "") =>
        new Do(() =>
        {
            if (find() is not var (at, size))
            {
                Console.Error.WriteLine($"nothing to highlight for \"{label}\"");
                return;
            }
            Event("highlight", string.Create(CultureInfo.InvariantCulture, $"\"x\":{at.X:F1},\"y\":{at.Y:F1},\"w\":{size.X:F1},\"h\":{size.Y:F1},\"label\":{Json(label)}"));
        });

    Step Highlight(Func<Element?> find, string label = "", float pad = 4) => Highlight(() => Box(find(), pad), label);

    Step NoHighlights() => new Do(() => Event("highlightsOff"));

    static (float2 At, float2 Size)? Box(Element? element, float pad = 4) =>
        element is null ? null : (element.ScreenPosition - new float2(pad, pad), element.Size + new float2(2 * pad, 2 * pad));

    static (float2 At, float2 Size)? Around(IEnumerable<Element?> elements, float pad = 4)
    {
        var found = elements.OfType<Element>().ToList();
        if (found.Count == 0)
            return null;
        var min = found.Select(e => e.ScreenPosition).Aggregate(float2.Min);
        var max = found.Select(e => e.ScreenPosition + e.Size).Aggregate(float2.Max);
        return (min - new float2(pad, pad), max - min + new float2(2 * pad, 2 * pad));
    }

    // ------------------------------------------------------------------------------------- finding what's on screen

    static IEnumerable<Element> Tree(Element element)
    {
        yield return element;
        foreach (var child in element.VisibleChildren)
            foreach (var below in Tree(child))
                yield return below;
    }

    Element? Find(Func<Element, bool> match, Element? within = null) =>
        (within is null ? _app.Ui.Elements.Where(e => e.Visible).SelectMany(Tree) : Tree(within)).FirstOrDefault(match);

    Button? ButtonCalled(string text, Element? within = null) => Find(e => e is Button button && button.Text == text, within) as Button;

    Element? WithTooltip(string tooltip, Element? within = null) => Find(e => e.Tooltip == tooltip, within);

    DeckPanel DeckOf(int deck) => (DeckPanel)Find(e => e is DeckPanel panel && panel.Deck == _app.Decks[deck])!;

    List<Element> Pads(int deck) => [.. Tree(DeckOf(deck)).Where(e => e is HotcuePad)];

    /// <summary>
    /// Where a folder list row is, by its name: its whole row, from the tree's left edge.
    /// </summary>
    (float2 At, float2 Size)? Row(string name)
    {
        var tree = _app.Screen.FolderTree;
        var rows = tree.Rows;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Item.Name == name)
                return (tree.ScreenPosition + new float2(0, i * tree.RowHeight), new float2(tree.Size.X, tree.RowHeight));
        return null;
    }

    // ----------------------------------------------------------------------------------------- the pretend mouse

    static double Ease(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    IEnumerable<Step> MoveTo(Func<float2?> target, double seconds = 0.7)
    {
        float2 from = default, to = default;
        var found = false;
        yield return new Do(() =>
        {
            _cursorShown = true;
            from = _mouse;
            found = target() is { } where;
            to = target() ?? _mouse;
        });
        yield return new Over(seconds, through =>
        {
            _mouse = from + (to - from) * (float)Ease(through);
            _app.Ui.MouseMove(_mouse);
        });
        if (!found)
            Console.Error.WriteLine("nothing to move the mouse to");
    }

    IEnumerable<Step> MoveTo(Func<Element?> target, double seconds = 0.7) =>
        MoveTo(() => target() is { } e ? e.ScreenPosition + e.Size / 2 : null, seconds);

    IEnumerable<Step> Click(MouseButton button = MouseButton.Left)
    {
        yield return new Do(() =>
        {
            _mouseDown = true;
            Event("click", string.Create(CultureInfo.InvariantCulture, $"\"x\":{_mouse.X:F1},\"y\":{_mouse.Y:F1},\"right\":{(button == MouseButton.Right ? "true" : "false")}"));
            _app.Ui.MouseButton(button, true);
        });
        yield return new Hold(0.12);
        yield return new Do(() =>
        {
            _app.Ui.MouseButton(button, false);
            _mouseDown = false;
        });
        yield return new Hold(0.2);
    }

    IEnumerable<Step> ClickOn(Func<Element?> target, MouseButton button = MouseButton.Left, double seconds = 0.7) =>
        MoveTo(target, seconds).Concat(Click(button));

    IEnumerable<Step> ClickOn(Func<float2?> target, MouseButton button = MouseButton.Left, double seconds = 0.7) =>
        MoveTo(target, seconds).Concat(Click(button));

    Step HideMouse() =>
        new Do(() =>
        {
            _cursorShown = false;
            _app.Ui.MouseLeave();
        });

    // --------------------------------------------------------------------------------------------- the keyboard

    static string KeyName(GlfwKey key, GlfwModifier mods)
    {
        var name = key switch
        {
            GlfwKey.Escape => "Esc",
            GlfwKey.Up => "↑",
            GlfwKey.Down => "↓",
            GlfwKey.Enter => "Enter",
            GlfwKey.Backspace => "Backspace",
            GlfwKey.Home => "Home",
            >= GlfwKey.F1 and <= GlfwKey.F12 => key.ToString(),
            _ => DeckKeys.Label(key),
        };
        if ((mods & GlfwModifier.Alt) != 0)
            name = "Alt + " + name;
        if ((mods & GlfwModifier.Shift) != 0)
            name = "Shift + " + name;
        if ((mods & GlfwModifier.Control) != 0)
            name = "Ctrl + " + name;
        return name;
    }

    /// <summary>
    /// Presses a key as the keyboard would: the UI first, then the app's own shortcuts if the UI didn't use it,
    /// held for <paramref name="hold"/> seconds. What was pressed is shown on the video as <paramref name="label"/>.
    /// </summary>
    IEnumerable<Step> Press(GlfwKey key, GlfwModifier mods = 0, double hold = 0.12, string? label = null, bool show = true)
    {
        var handled = false;
        var uiMods = (Modifiers)((int)mods & 0xF);
        yield return new Do(() =>
        {
            if (show)
                Event("key", $"\"label\":{Json(label ?? KeyName(key, mods))}");
            _app.Ui.ModifiersHeld(uiMods);
            handled = _app.Ui.KeyDown(GlfwUiInput.ToUiKey(key), uiMods);
            if (!handled)
                _app.Key(key, GlfwKeyAction.Press, mods);
        });
        yield return new Hold(hold);
        yield return new Do(() =>
        {
            if (!handled)
                _app.Key(key, GlfwKeyAction.Release, mods);
            _app.Ui.ModifiersHeld(Modifiers.None);
        });
    }

    IEnumerable<Step> Type(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            yield return new Do(() => _app.Ui.TextInput(rune));
            yield return new Hold(0.09);
        }
    }

    /// <summary>
    /// Moves down the song list a row at a time until <paramref name="title"/> is chosen.
    /// </summary>
    IEnumerable<Step> BrowseTo(string title)
    {
        for (var i = 0; i < 20 && _app.Screen.Browser.SelectedSong?.Title != title; i++)
        {
            foreach (var step in Press(GlfwKey.Down, hold: 0.08))
                yield return step;
            yield return new Hold(0.18);
        }
    }

    // ------------------------------------------------------------------------------------------------- the tour

    IEnumerable<Step> Tour()
    {
        // The same window as the screenshot.
        for (var i = 0; i < 5 && _app.Window.FramebufferSize != Size; i++)
        {
            _app.Window.Size = Size - (_app.Window.FramebufferSize - _app.Window.Size) - (_app.Window.Size - Size);
            yield return new Wait(10);
        }
        // Not quite full, so the limiter isn't at work all through.
        _app.Audio.MasterVolume = 0.8f;
        _app.Audio.Crossfader = 0;
        var listed = _app.Listed;
        _app.ListEverySong();
        yield return new Until(() => listed().Count == Songs.Length, 30 * 60, "the songs to be found");
        yield return new Until(() => listed().All(_app.IsAnalysed), 180 * 60, "the songs to be analysed");
        yield return new Wait(30);

        var (a, b) = (_app.Decks[0], _app.Decks[1]);
        var screen = _app.Screen;
        var browser = screen.Browser;
        Step Rest(double seconds) => new Hold(seconds);

        yield return new Do(StartRecordingVideo);

        // ---- Opening card, over the library with every song analysed.
        yield return Card("Cuepoint", "A two-deck DJ app for Windows");
        yield return Rest(3.5);
        yield return NoCard();
        yield return Rest(0.6);

        yield return Caption("Every song analysed", "Tempo, beat grid, key (as Camelot) and energy, worked out in the background");
        yield return Highlight(() => browser.List, pad: 2);
        yield return Rest(4.5);
        yield return NoHighlights();

        // ---- Loading the decks from the list.
        yield return Caption("Load from the list", "Browse with the arrow keys, then 1 loads deck A and 2 loads deck B — or drag a song onto a deck");
        yield return new Do(() => browser.List.KeyDown(Key.Home, Modifiers.None));
        foreach (var step in BrowseTo(Songs[0].Title))
            yield return step;
        foreach (var step in Press(GlfwKey.Number1))
            yield return step;
        yield return Highlight(() => DeckOf(0), "Deck A");
        yield return Rest(1.6);
        foreach (var step in BrowseTo(Songs[1].Title))
            yield return step;
        foreach (var step in Press(GlfwKey.Number2))
            yield return step;
        yield return Highlight(() => DeckOf(1), "Deck B");
        yield return new Until(() => a.IsLoaded && b.IsLoaded && a.Analysis is not null && b.Analysis is not null, 30 * 60, "the decks to load");
        yield return Rest(2.2);
        yield return NoHighlights();

        // ---- Playing, with the scrolling waveforms.
        yield return Caption("Play", "Waveforms scroll past coloured by bass, mids and treble, with the beat grid, the bar and beat, and the song's overview");
        foreach (var step in Press(GlfwKey.X))
            yield return step;
        yield return Highlight(() => Around(_app.Ui.Elements.SelectMany(Tree).Where(e => e is ScrollingWaveform), 2), "Scrolling waveforms");
        yield return Rest(4.5);
        yield return NoHighlights();

        // ---- Sync, and the phase meter.
        yield return Caption("Sync lock", "SYNC locks deck B to deck A's tempo and beat; press play and it comes in right on the beat");
        yield return Highlight(() => ButtonCalled("SYNC", DeckOf(1)), "SYNC");
        foreach (var step in Press(GlfwKey.Comma))
            yield return step;
        yield return Rest(1.4);
        foreach (var step in Press(GlfwKey.M))
            yield return step;
        yield return Rest(1.2);
        yield return NoHighlights();
        yield return Highlight(() => Find(e => e is PhaseMeter), "Phase meter: in phase", 2);
        yield return Rest(3.5);
        yield return NoHighlights();

        // ---- The mixer.
        yield return Caption("The mixer", "Trim, three-band EQ with kills, filter, faders and crossfader — every one on the keyboard too");
        yield return Highlight(() => Find(e => e is MixerPanel), "Mixer", 2);
        foreach (var step in Press(GlfwKey.RightBracket, hold: 0.5))
            yield return step;
        yield return Rest(1.4);
        foreach (var step in Press(GlfwKey.F1))
            yield return step;
        yield return Rest(2.2);
        foreach (var step in Press(GlfwKey.F1))
            yield return step;
        yield return Rest(1.2);
        yield return NoHighlights();

        // ---- Hotcues, and suggested ones.
        yield return Caption("Hotcues", "Eight a deck, kept with the song: press a pad (or Q to F, U to ;) to set one, again to jump back to it");
        yield return Highlight(() => Around(Pads(0)), "Hotcues");
        foreach (var step in Press(GlfwKey.Q))
            yield return step;
        yield return Rest(2.2);
        foreach (var step in Press(GlfwKey.Q))
            yield return step;
        yield return Rest(1.6);
        yield return NoHighlights();
        yield return Caption("Suggested hotcues", "Right-click an empty pad: hotcues at the song's start, drop, breakdown and outro, from its analysis");
        foreach (var step in ClickOn(() => Pads(0).ElementAtOrDefault(2), MouseButton.Right))
            yield return step;
        yield return Rest(0.7);
        foreach (var step in ClickOn(() => ButtonCalled("Suggest hot cues for this song"), seconds: 0.5))
            yield return step;
        yield return Highlight(() => Around(Pads(0)), "Named and coloured");
        yield return Rest(3);
        yield return NoHighlights();
        yield return HideMouse();

        // ---- Loops.
        yield return Caption("Loops", "Beat-perfect loops from 1/16 to 32 beats, halved and doubled as they play");
        yield return Highlight(() => Around([ButtonCalled("LOOP 4", DeckOf(0)) ?? Find(e => e is Button button && button.Text.StartsWith("LOOP", StringComparison.Ordinal), DeckOf(0))]), "Loop");
        foreach (var step in Press(GlfwKey.V))
            yield return step;
        yield return Rest(2.5);
        foreach (var step in Press(GlfwKey.Number3))
            yield return step;
        yield return Rest(1.2);
        foreach (var step in Press(GlfwKey.Number3))
            yield return step;
        yield return Rest(1.2);
        foreach (var step in Press(GlfwKey.Number4))
            yield return step;
        foreach (var step in Press(GlfwKey.Number4))
            yield return step;
        yield return Rest(1);
        foreach (var step in Press(GlfwKey.V))
            yield return step;
        yield return Rest(0.8);
        yield return NoHighlights();

        // ---- Effects.
        yield return Caption("Effects", "Echo, reverb, flanger, gate and bit-crusher on each deck, timed to its beat");
        yield return Highlight(() => Around([ButtonCalled("FX", DeckOf(0)), Find(e => e is Dropdown<DeckEffect>, DeckOf(0))]), "FX");
        foreach (var step in ClickOn(() => ButtonCalled("FX", DeckOf(0))))
            yield return step;
        yield return Rest(3);
        foreach (var step in ClickOn(() => ButtonCalled("FX", DeckOf(0)), seconds: 0.2))
            yield return step;
        yield return NoHighlights();
        yield return Caption("Transitions to the phrase", "Right-click FX for an echo out or a filter sweep that builds to the next phrase and lands on it");
        foreach (var step in ClickOn(() => ButtonCalled("FX", DeckOf(1)), MouseButton.Right, 0.9))
            yield return step;
        yield return Rest(0.6);
        foreach (var step in ClickOn(() => ButtonCalled("Filter sweep to the next phrase"), seconds: 0.5))
            yield return step;
        yield return HideMouse();
        yield return Highlight(() => Find(e => e is MixerPanel), "Filter rising to the phrase", 2);
        yield return Rest(5);
        yield return NoHighlights();

        // ---- Key shift and key match.
        yield return Caption("Key shift and key match", "Move a song's key a semitone at a time without touching its tempo, or = to match the other deck's");
        yield return Highlight(() => Around([ButtonCalled("-", DeckOf(1)), ButtonCalled("=", DeckOf(1)), ButtonCalled("+", DeckOf(1)), ButtonCalled("KEY", DeckOf(1))]), "Key");
        foreach (var step in Press(GlfwKey.Number7, GlfwModifier.Alt))
            yield return step;
        yield return Rest(1.8);
        foreach (var step in Press(GlfwKey.H, GlfwModifier.Control))
            yield return step;
        yield return Rest(2.8);
        yield return NoHighlights();

        // ---- Searching.
        yield return Caption("Search", "By title, artist and album — or by tempo range and key: ~8A finds songs that mix with 8A");
        foreach (var step in Press(GlfwKey.F, GlfwModifier.Control))
            yield return step;
        yield return Rest(0.4);
        foreach (var step in Type("~8A 120-126"))
            yield return step;
        yield return Highlight(() => browser, pad: 2);
        yield return Rest(3.5);
        yield return NoHighlights();
        foreach (var step in Press(GlfwKey.Escape))
            yield return step;
        foreach (var step in Press(GlfwKey.Escape, show: false))
            yield return step;
        yield return Rest(0.5);

        // ---- Suggested next.
        yield return Caption("Suggested next", "What would mix well after the song playing, best first — and why: key, tempo and energy");
        foreach (var step in ClickOn(() => Row("Suggested next") is var (at, size) ? at + new float2(70, size.Y / 2) : null, seconds: 0.9))
            yield return step;
        yield return Highlight(() => browser, pad: 2);
        yield return Rest(4.5);
        yield return NoHighlights();

        // ---- Recording and streaming.
        yield return Caption("Record or go live", "Record the mix to WAV with its tracklist as a cue sheet, or stream it live to an Icecast server");
        foreach (var step in ClickOn(() => Find(e => e is RecordButton)))
            yield return step;
        yield return HideMouse();
        yield return Highlight(() => Around([Find(e => e is RecordButton), ButtonCalled("LIVE")]), "Recording");
        yield return Rest(3.5);
        yield return NoHighlights();

        // ---- The options.
        yield return Caption("Options", "In five tabs: Mixing, Sound, Screen, Controls and Library");
        foreach (var step in ClickOn(() => WithTooltip("Options")))
            yield return step;
        yield return Rest(1);
        foreach (var tab in new[] { "Sound", "Screen", "Controls", "Library", "Mixing" })
        {
            foreach (var step in ClickOn(() => ButtonCalled(tab, screen.Options), seconds: 0.45))
                yield return step;
            yield return Rest(0.9);
        }
        foreach (var step in Press(GlfwKey.Escape))
            yield return step;
        yield return HideMouse();
        yield return Rest(0.4);

        // ---- Every shortcut.
        yield return Caption("Keyboard or controller", "? shows every shortcut. Pioneer's DDJ-SB3, DDJ-FLX4 and DDJ-400 work out of the box, and any MIDI control can be learned");
        foreach (var step in Press(KeyboardShortcuts.SHOW, GlfwModifier.Shift, label: "?"))
            yield return step;
        yield return Rest(4.5);
        foreach (var step in Press(GlfwKey.Escape))
            yield return step;
        yield return Rest(0.4);

        // ---- Auto-Mix: the decks stopped, the recording saved, and the warm-up set opened in it.
        yield return Caption("Auto-Mix", "Right-click a playlist and open it in Auto-Mix, and it mixes the set by itself");
        foreach (var step in ClickOn(() => Find(e => e is RecordButton)))
            yield return step;
        // Stopped and let go of each other: a deck still following the other's tempo would move as Auto-Mix loads
        // the next song, which Auto-Mix takes as someone taking over.
        yield return new Do(() =>
        {
            a.Pause();
            b.Pause();
            a.SyncLocked = b.SyncLocked = false;
        });
        foreach (var step in ClickOn(() => Row("Warm-up set") is var (at, size) ? at + new float2(80, size.Y / 2) : null, MouseButton.Right, 0.9))
            yield return step;
        yield return Rest(0.6);
        foreach (var step in ClickOn(() => ButtonCalled("Open in Auto-Mix"), seconds: 0.5))
            yield return step;
        yield return Rest(1.2);
        foreach (var step in ClickOn(() => WithTooltip("Play or pause the auto-mix", screen.AutoMix)))
            yield return step;
        yield return HideMouse();
        yield return Highlight(() => screen.AutoMix, "Auto-Mix", 2);
        yield return Rest(3.5);
        yield return NoHighlights();

        // Skipped on to the mix.
        yield return new Do(PauseVideo);
        yield return new Until(() => b.IsPlaying || a.IsPlaying && b.IsPlaying, 240 * 60, "Auto-Mix to start mixing");
        yield return new Hold(1.5);
        yield return new Do(ResumeVideo);
        yield return new Do(() => Event("cut", $"\"label\":{Json("A couple of minutes later…")}"));
        yield return Caption("Auto-Mix", "Beatmatched and on the phrase, it moves the crossfader, EQ and filter as a DJ would");
        yield return Highlight(() => Find(e => e is MixerPanel), "", 2);
        yield return Rest(14);
        yield return NoHighlights();
        yield return NoCaption();

        // ---- Closing card.
        yield return Card("Cuepoint", "Free and open source · C# on .NET 10, drawn with Vulkan");
        yield return Rest(5);
        yield return NoCard();
        yield return Rest(0.3);
        _app.Decks.ToList().ForEach(deck => deck.Pause());
    }

    // ---------------------------------------------------------------------------------------- the video writer

    /// <summary>
    /// Pipes frames to ffmpeg at a steady frame rate on a thread of its own, repeating a frame where the app drew
    /// slower than that and dropping some where it drew faster, by the time each was drawn.
    /// </summary>
    sealed class VideoWriter
    {
        readonly Process _ffmpeg;
        readonly Stream _input;
        readonly BlockingCollection<(byte[] Rgba, double Time)> _frames = new(boundedCapacity: 8);
        readonly Thread _thread;
        readonly int _fps;
        byte[]? _last;

        public int Captured { get; private set; }
        public int Written { get; private set; }

        public VideoWriter(string ffmpeg, string path, int width, int height, int fps, string log)
        {
            _fps = fps;
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
            {
                "-y", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{width}x{height}", "-r", $"{fps}", "-i", "-",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "12", "-pix_fmt", "yuv420p", path,
            })
                start.ArgumentList.Add(arg);
            _ffmpeg = Process.Start(start)!;
            var logWriter = new StreamWriter(log);
            _ffmpeg.ErrorDataReceived += (_, e) =>
            {
                lock (logWriter)
                    logWriter.WriteLine(e.Data);
            };
            _ffmpeg.Exited += (_, _) =>
            {
                lock (logWriter)
                    logWriter.Dispose();
            };
            _ffmpeg.EnableRaisingEvents = true;
            _ffmpeg.BeginErrorReadLine();
            _input = _ffmpeg.StandardInput.BaseStream;
            _thread = new Thread(Write) { IsBackground = true, Name = "Tour video" };
            _thread.Start();
        }

        public void Add(byte[] rgba, double time)
        {
            Captured++;
            _frames.Add((rgba, time));
        }

        void Write()
        {
            foreach (var (rgba, time) in _frames.GetConsumingEnumerable())
            {
                WriteUntil(time);
                _last = rgba;
            }
        }

        // Writes the last frame for every output frame up to time.
        void WriteUntil(double time)
        {
            var due = (int)Math.Round(time * _fps);
            if (_last is null)
                return;
            while (Written < due)
            {
                _input.Write(_last);
                Written++;
            }
        }

        public void Finish(double end)
        {
            _frames.CompleteAdding();
            _thread.Join();
            WriteUntil(end);
            _input.Flush();
            _input.Dispose();
            _ffmpeg.WaitForExit();
        }
    }
}
