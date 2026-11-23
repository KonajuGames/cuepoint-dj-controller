using Brutal.Numerics;
using Cuepoint.Audio.Stems;

namespace Cuepoint.UI;

/// <summary>
/// How to make a DJ edit of a song, in a box over the dimmed screen: how long its intro and outro are, what changes
/// halfway through each, how the intro's beats give way to the song, how the outro's take over from it and how they
/// end, and how often an echo repeats. Make (or Replace, for one made before) makes it; Defaults puts every choice back
/// to Auto; Escape or Cancel leaves it.
/// </summary>
sealed class DjEditDialog : Panel
{
    readonly Root _root;
    readonly Panel _box;
    readonly Label _heading;
    readonly Label _note;
    readonly Button _make;
    readonly List<Element> _dropdowns = [];

    readonly Dropdown<int> _introBars;
    readonly Dropdown<DjEditChange> _introChange;
    readonly Dropdown<DjEditTransition> _introEnd;
    readonly Dropdown<int> _introEndBeats;
    readonly Dropdown<bool> _introAcross;
    readonly Dropdown<int> _outroBars;
    readonly Dropdown<DjEditChange> _outroChange;
    readonly Dropdown<DjEditTransition> _outroStart;
    readonly Dropdown<int> _outroStartBeats;
    readonly Dropdown<bool> _outroUnder;
    readonly Dropdown<DjEditTransition> _outroEnd;
    readonly Dropdown<int> _outroEndBeats;
    readonly Dropdown<double> _echoBeats;

    Action<DjEditOptions> _make_ = _ => { };

    public float2 ScreenSize { get; set; }

    static readonly int[] Lengths = [8, 16, 32];
    static readonly int[] Spans = [1, 2, 4, 8, 16, 32];
    static readonly double[] EchoSpacings = [0.5, 0.75, 1, 2];

    public DjEditDialog(Root root, Font body, Font bold, Font heading)
    {
        _root = root;
        Visible = false;
        Background = Theme.Scrim(0.6f);

        _box = Add(
            new Panel
            {
                Padding = 20,
                Spacing = 8,
                Background = Theme.PanelRaised,
                BorderColor = Theme.PanelBorder,
                BorderWidth = 1,
                MinSize = new float2(560, 0),
            }
        );
        _heading = _box.Add(new Label(heading) { Color = Theme.Text });

        Dropdown<T> Choice<T>(string name, IReadOnlyList<T> items, Func<T, string> text, string tooltip)
        {
            var row = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
            row.Add(new Panel { MinSize = new float2(150, 0) }).Add(new Label(body, name) { Color = Theme.Muted });
            var dropdown = row.Add(
                new Dropdown<T>(body)
                {
                    Width = 320,
                    Items = () => items,
                    Text = text,
                    Selected = items[0],
                    FocusColor = Theme.Focus,
                    Tooltip = tooltip,
                }
            );
            dropdown.Chosen += _ => Follow();
            _dropdowns.Add(dropdown);
            return dropdown;
        }
        void Section(string name) => _box.Add(new Label(bold, name) { Color = Theme.Text });

        DjEditChange[] changes = [DjEditChange.Auto, DjEditChange.Nothing, DjEditChange.Bass, DjEditChange.Instruments, DjEditChange.BassAndInstruments, DjEditChange.Break];
        DjEditTransition[] endings = [DjEditTransition.Auto, DjEditTransition.Hit, DjEditTransition.Fade, DjEditTransition.Filter, DjEditTransition.Echo, DjEditTransition.Cut];

        Section("Intro");
        _introBars = Choice("Length", Lengths, bars => $"{bars} bars", "How many bars of beats come before the song");
        _introChange = Choice("Halfway", changes, change => ChangeName(change, intro: true), "What comes in with the drums halfway through the intro, from the same bars of the song");
        _introEnd = Choice("Into the song", endings, IntroEndName, "How the beats give way to the song");
        _introEndBeats = Choice("Over", Spans, SpanName, "How long the beats take to give way");
        _introAcross = Choice(
            "Where",
            [false, true],
            across => across ? "Across the song's start" : "Before the song starts",
            "Before the song's first downbeat, or carrying on into its first bars, under them, which suits a song that starts with a voice alone"
        );

        Section("Outro");
        _outroBars = Choice("Length", Lengths, bars => $"{bars} bars or more, to a phrase's end", "How many bars of beats come after the song, at least, carried on to the end of a phrase");
        _outroChange = Choice("Halfway", changes, change => ChangeName(change, intro: false), "What carries on with the drums from the song, from the same bars, until halfway through the outro");
        _outroStart = Choice(
            "Out of the song",
            [DjEditTransition.Auto, DjEditTransition.Fade, DjEditTransition.Filter],
            start => start switch
            {
                DjEditTransition.Fade => "Fade in",
                DjEditTransition.Filter => "Filter sweep in",
                _ => "Auto: straight in as the song's beats stop",
            },
            "How the beats take over from the song"
        );
        _outroStartBeats = Choice("Over", Spans, SpanName, "How long the beats take to come in");
        _outroUnder = Choice(
            "Where",
            [false, true],
            under => under ? "Under the song's last bars" : "After the song's beats stop",
            "Coming in once the song's beats have stopped, or already coming in under its last bars"
        );
        _outroEnd = Choice("Ending", endings, OutroEndName, "How the outro ends");
        _outroEndBeats = Choice("Over", Spans, SpanName, "How long the ending takes");

        Section("Echo");
        _echoBeats = Choice("Repeats every", EchoSpacings, EchoName, "How far apart an echo's repeats are, for an intro or outro that echoes out");

        _note = _box.Add(new Label(body) { Color = Theme.Muted, MaxWidth = 520, Wrap = true });
        var buttons = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        _make = buttons.Add(new Button(bold, "Make") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        var defaults = buttons.Add(new Button(bold, "Defaults") { MinSize = new float2(90, 0), FocusColor = Theme.Focus, Tooltip = "Every choice back to Auto, 16 bars each end" });
        var cancel = buttons.Add(new Button(bold, "Cancel") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        _make.Clicked += _ => Make();
        defaults.Clicked += _ => Choose(DjEditOptions.Default);
        cancel.Clicked += _ => Close();
    }

    static string ChangeName(DjEditChange change, bool intro) =>
        change switch
        {
            DjEditChange.Nothing => "Nothing: beats alone",
            DjEditChange.Bass => intro ? "The bass comes in" : "The bass drops out",
            DjEditChange.Instruments => intro ? "The instruments come in" : "The instruments drop out",
            DjEditChange.BassAndInstruments => intro ? "The bass and instruments come in" : "The bass and instruments drop out",
            DjEditChange.Break => "A bar without the kick",
            _ => "Auto: the bass, or what the song has",
        };

    static string IntroEndName(DjEditTransition end) =>
        end switch
        {
            DjEditTransition.Hit => "Hit on its first beat, dying away",
            DjEditTransition.Fade => "Fade out",
            DjEditTransition.Filter => "Filter sweep out",
            DjEditTransition.Echo => "Echo out",
            DjEditTransition.Cut => "Cut",
            _ => "Auto: a fill, a hit, or a gap, as the song starts",
        };

    static string OutroEndName(DjEditTransition end) =>
        end switch
        {
            DjEditTransition.Hit => "Hit, dying away",
            DjEditTransition.Fade => "Fade out",
            DjEditTransition.Filter => "Filter sweep out",
            DjEditTransition.Echo => "Echo out",
            DjEditTransition.Cut => "Cut at the phrase's end",
            _ => "Auto: a last hit on the one",
        };

    static string SpanName(int beats) => beats % 4 == 0 ? (beats == 4 ? "1 bar" : $"{beats / 4} bars") : beats == 1 ? "1 beat" : $"{beats} beats";

    static string EchoName(double beats) =>
        beats switch
        {
            0.5 => "Half a beat",
            0.75 => "Three quarters of a beat",
            1 => "1 beat",
            _ => $"{beats:0.##} beats",
        };

    /// <summary>
    /// Where the dialog's dropdowns open their lists: a menu drawn over it.
    /// </summary>
    public ContextMenu? Menu
    {
        set
        {
            foreach (var dropdown in _dropdowns.OfType<IOpensMenu>())
                dropdown.Menu = value;
        }
    }

    public override bool IsInteractive => true;
    public override bool IsModal => true;
    public override Cursor Cursor => Cursor.Arrow;

    /// <summary>
    /// Shows how to make a DJ edit of the song called <paramref name="title"/>, starting from
    /// <paramref name="options"/>; with <paramref name="replacing"/> said, it replaces one made before, as that says.
    /// <paramref name="make"/> takes the options chosen.
    /// </summary>
    public void Show(string title, DjEditOptions options, string? replacing, Action<DjEditOptions> make)
    {
        _heading.Text = replacing is null ? $"Make a DJ edit of {title}" : $"Make the DJ edit of {title} again";
        _make.Text = replacing is null ? "Make" : "Replace";
        _note.Text = replacing ?? "Saved beside the song as a FLAC, its own drums looped from its drum stem.";
        _make_ = make;
        Choose(options);
        Visible = true;
        _root.Focus(_make);
    }

    /// <summary>
    /// The options as chosen now.
    /// </summary>
    public DjEditOptions Options =>
        new()
        {
            IntroBars = _introBars.Selected,
            IntroChange = _introChange.Selected,
            IntroEnd = _introEnd.Selected,
            IntroEndBeats = _introEndBeats.Selected,
            IntroEndAcrossSong = _introAcross.Selected,
            OutroBars = _outroBars.Selected,
            OutroChange = _outroChange.Selected,
            OutroStart = _outroStart.Selected,
            OutroStartBeats = _outroStartBeats.Selected,
            OutroStartUnderSong = _outroUnder.Selected,
            OutroEnd = _outroEnd.Selected,
            OutroEndBeats = _outroEndBeats.Selected,
            EchoBeats = _echoBeats.Selected,
        };

    void Choose(DjEditOptions options)
    {
        static T Nearest<T>(IReadOnlyList<T> items, Func<T, double> value, double wanted) => items.MinBy(item => Math.Abs(value(item) - wanted))!;
        _introBars.Selected = Nearest(Lengths, bars => bars, options.IntroBars);
        _introChange.Selected = options.IntroChange;
        _introEnd.Selected = options.IntroEnd;
        _introEndBeats.Selected = Nearest(Spans, beats => beats, options.IntroEndBeats);
        _introAcross.Selected = options.IntroEndAcrossSong;
        _outroBars.Selected = Nearest(Lengths, bars => bars, options.OutroBars);
        _outroChange.Selected = options.OutroChange;
        _outroStart.Selected = options.OutroStart is DjEditTransition.Fade or DjEditTransition.Filter ? options.OutroStart : DjEditTransition.Auto;
        _outroStartBeats.Selected = Nearest(Spans, beats => beats, options.OutroStartBeats);
        _outroUnder.Selected = options.OutroStartUnderSong;
        _outroEnd.Selected = options.OutroEnd;
        _outroEndBeats.Selected = Nearest(Spans, beats => beats, options.OutroEndBeats);
        _echoBeats.Selected = Nearest(EchoSpacings, beats => beats, options.EchoBeats);
        Follow();
    }

    // Choices that do nothing with what else is chosen are greyed out: how long, and where, for Auto; where, for a
    // hit, which lands on the first beat whichever; and the echo's spacing, unless something echoes.
    void Follow()
    {
        _introEndBeats.Enabled = _introEnd.Selected != DjEditTransition.Auto;
        _introAcross.Enabled = _introEnd.Selected is not (DjEditTransition.Auto or DjEditTransition.Hit);
        _outroStartBeats.Enabled = _outroUnder.Enabled = _outroStart.Selected != DjEditTransition.Auto;
        _outroEndBeats.Enabled = _outroEnd.Selected is not (DjEditTransition.Auto or DjEditTransition.Cut);
        _echoBeats.Enabled = _introEnd.Selected == DjEditTransition.Echo || _outroEnd.Selected == DjEditTransition.Echo;
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        _root.Focus(null);
    }

    void Make()
    {
        var options = Options;
        Close();
        _make_(options);
    }

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key == Key.Escape)
        {
            Close();
            return true;
        }
        return base.KeyDown(key, modifiers);
    }

    public override float2 Measure()
    {
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(new float2(MathF.Round(position.X + (ScreenSize.X - _box.Size.X) / 2), MathF.Round(position.Y + Math.Max(0, (ScreenSize.Y - _box.Size.Y) / 3))));
    }
}
