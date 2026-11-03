using Brutal.Numerics;
using Brutality.Audio;
using Brutality.Input;
using Brutality.Music;
using Brutality.UI;

namespace Brutality.Tests.UI;

public class KnobTests
{
    static readonly FontAtlas Atlas = new();
    static readonly Images Pictures = Images.Load(Atlas);

    static Knob NewKnob() => new(Pictures.Knob, Pictures.Dot) { Min = -1, Max = 1, DefaultValue = 0 };

    [Fact]
    public void Dragging_up_turns_it_up_and_down_turns_it_down_wherever_the_drag_starts()
    {
        var knob = NewKnob();
        var changes = 0;
        knob.Changed += _ => changes++;

        knob.PointerDown(new float2(5, 100), Modifiers.None);
        knob.PointerDrag(new float2(50, 60));
        Assert.Equal(0.5f, knob.Value, 3);
        knob.PointerDrag(new float2(50, 140));
        Assert.Equal(-0.5f, knob.Value, 3);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Turned_past_an_end_it_stops_there_and_comes_straight_back()
    {
        var knob = NewKnob();

        knob.PointerDown(new float2(0, 0), Modifiers.None);
        knob.PointerDrag(new float2(0, -400));
        Assert.Equal(1, knob.Value);
        knob.PointerDrag(new float2(0, -360));

        Assert.Equal(0.5f, knob.Value, 3);
    }

    [Fact]
    public void Shift_turns_it_finely()
    {
        var knob = NewKnob();

        knob.PointerDown(new float2(0, 0), Modifiers.Shift);
        knob.PointerDrag(new float2(0, -40));

        Assert.Equal(0.1f, knob.Value, 3);
    }

    [Fact]
    public void A_double_click_puts_it_back_and_the_wheel_nudges_it()
    {
        var knob = NewKnob();
        knob.Value = 0.7f;

        knob.PointerDown(default, Modifiers.None);
        knob.PointerDown(default, Modifiers.None);
        Assert.Equal(0, knob.Value);

        Assert.True(knob.Wheel(new float2(0, 1)));
        Assert.Equal(0.04f, knob.Value, 3);
    }

    [Fact]
    public void Set_from_code_it_says_nothing_and_stays_in_range()
    {
        var knob = NewKnob();
        var changes = 0;
        knob.Changed += _ => changes++;

        knob.Value = 3;

        Assert.Equal(1, knob.Value);
        Assert.Equal(0, changes);
    }
}

/// <summary>
/// The mixer, the headphones' settings and a deck's buttons built for real, with an audio engine and a system font,
/// as the app builds them.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public sealed class MixerAndDeckLayoutTests : IDisposable
{
    readonly FontAtlas _atlas = new();
    readonly Font _font;
    readonly Images _images;
    readonly AudioEngine _engine = new();
    readonly Deck _deck;

    public MixerAndDeckLayoutTests()
    {
        _font = _atlas.Add(FontAtlas.FindSystemFont() ?? throw new InvalidOperationException("No system font"), 14);
        _images = Images.Load(_atlas);
        _deck = _engine.CreateDeck("A");
    }

    public void Dispose()
    {
        _deck.Dispose();
        _engine.Dispose();
    }

    [Fact]
    public void The_mixer_s_knobs_work_the_deck_s_trim_eq_and_filter()
    {
        var mixer = new MixerPanel(_engine, [_deck], _font, _font, _images);

        var low = mixer.KnobFor(0, 3);
        low.PointerDown(new float2(0, 0), Modifiers.None);
        low.PointerDrag(new float2(0, 80));
        Assert.Equal(-1, _deck.Equalizer.Low);

        var filter = mixer.KnobFor(0, 4);
        filter.PointerDown(new float2(0, 0), Modifiers.None);
        filter.PointerDrag(new float2(0, -40));
        Assert.Equal(0.5f, _deck.Equalizer.Filter, 3);
    }

    [Fact]
    public void The_headphones_settings_work_their_blend_and_volume()
    {
        var headphones = new HeadphonesPanel(_engine, _font, _font, _font, _images);
        _engine.CueMix = 0;
        _engine.HeadphoneVolume = 0.5f;
        headphones.Refresh();

        headphones.Mix.PointerDown(new float2(0, 0), Modifiers.None);
        headphones.Mix.PointerDrag(new float2(0, -80));
        headphones.Volume.PointerDown(new float2(0, 0), Modifiers.None);
        headphones.Volume.PointerDrag(new float2(0, 32));

        Assert.Equal(0.5f, _engine.CueMix, 3);
        Assert.Equal(0.3f, _engine.HeadphoneVolume, 3);
    }

    [Fact]
    public void The_headphones_settings_open_and_close_from_their_button()
    {
        var headphones = new HeadphonesPanel(_engine, _font, _font, _font, _images);

        headphones.Toggle(new float2(500, 30));
        Assert.True(headphones.Visible);
        headphones.Toggle(new float2(500, 30));
        Assert.False(headphones.Visible);
    }

    [Theory]
    [InlineData(1920, false)]
    [InlineData(1280, false)]
    [InlineData(1280, true)]
    public void The_title_bar_s_middle_moves_left_to_keep_clear_of_its_buttons_and_always_shows(float width, bool crowded)
    {
        var bar = new TitleBar(_engine, [_deck], _font, _font, _font, _images) { Width = width };
        if (crowded)
        {
            // As full as it gets: a missing sound card, a recording, and OS2L.
            bar.AudioWarning = () => "Pioneer DDJ-SB3 speakers missing";
            bar.Recording = () => TimeSpan.FromHours(1.5);
            bar.Os2lConnected = () => true;
        }
        bar.Update();

        bar.Measure();
        bar.Arrange(default);

        var (centre, right) = bar.Sections;
        Assert.True(centre.Visible);
        Assert.True(right.Visible);
        Assert.True(centre.ScreenPosition.X + centre.Size.X <= right.ScreenPosition.X - 15.5f);
        if (!crowded && width == 1920)
            Assert.InRange(centre.ScreenPosition.X + centre.Size.X / 2, width / 2 - 1, width / 2 + 1);
    }

    DeckPanel Panel(RollPads? roll = null, JumpPads? jump = null) =>
        new(_deck, _font, _font, _font, _font, keys: new DeckKeyHints("Z", "X", "C", "3", "V", "4", "G", "T"), images: _images, roll: roll, jump: jump);

    [Fact]
    public void A_deck_wide_enough_keeps_its_buttons_as_they_are_in_one_row()
    {
        var panel = Panel();

        panel.Width = 900;

        Assert.Equal((false, false), panel.Fitting);
        Assert.Equal(DeckPanel.HEIGHT, panel.Height);
    }

    [Fact]
    public void A_narrower_deck_shortens_its_buttons_then_puts_them_in_two_rows()
    {
        var panel = Panel(new RollPads(_deck));

        panel.Width = 600;
        Assert.Equal((true, false), panel.Fitting);
        Assert.Equal(DeckPanel.HEIGHT, panel.Height);

        panel.Width = 420;
        Assert.Equal((true, true), panel.Fitting);
        Assert.True(panel.Height > DeckPanel.HEIGHT);
        Assert.Equal(new float2(420, panel.Height), panel.Measure());

        panel.Width = 900;
        Assert.Equal((false, false), panel.Fitting);
    }

    [Fact]
    public void Choosing_a_pad_mode_turns_it_on_and_the_others_off()
    {
        var roll = new RollPads(_deck);
        var jump = new JumpPads(_deck, roll: roll);
        var panel = Panel(roll, jump);
        var menu = new ContextMenu(_font);
        panel.Menu = menu;
        var dropdown = Assert.Single(panel.Children.OfType<IOpensMenu>());
        Assert.Same(menu, dropdown.Menu);

        roll.Active = true;
        panel.Refresh();
        ((Element)dropdown).RaiseClicked();
        Assert.True(menu.Visible);
        Assert.Contains("JUMP", menu.ItemTexts);
        menu.Choose("JUMP");

        Assert.True(jump.Active);
        Assert.False(roll.Active);

        ((Element)dropdown).RaiseClicked();
        menu.Choose("HOT CUE");
        Assert.False(jump.Active);
        Assert.False(roll.Active);
    }
}
