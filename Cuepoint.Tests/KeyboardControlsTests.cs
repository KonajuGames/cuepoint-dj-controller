using Brutal.GlfwApi;
using Cuepoint.Audio;

namespace Cuepoint.Tests;

[Collection(AudioEngineTests.NAME)]
public class KeyboardControlsTests
{
    const GlfwModifier NUDGE = GlfwModifier.Control | GlfwModifier.Shift;

    [Fact]
    public void Ctrl_and_Shift_with_a_tempo_key_nudge_while_its_held_without_moving_the_tempo()
    {
        using var engine = new AudioEngine();
        using var deck = engine.CreateDeck("A");
        var controls = new KeyboardControls([deck], engine);
        var keys = KeyboardControls.Keys[0];

        controls.Key(keys.TempoUp, GlfwKeyAction.Press, NUDGE);
        controls.Update(0.1);
        Assert.Equal(KeyboardControls.NUDGE, deck.Bend);
        Assert.Equal(0, deck.Tempo);

        // Letting go of the modifiers first doesn't end it; the key coming up does.
        controls.Key(keys.TempoUp, GlfwKeyAction.Release, 0);
        Assert.Equal(0, deck.Bend);

        controls.Key(keys.TempoDown, GlfwKeyAction.Press, NUDGE);
        Assert.Equal(-KeyboardControls.NUDGE, deck.Bend);
        controls.Key(keys.TempoUp, GlfwKeyAction.Press, NUDGE);
        Assert.Equal(0, deck.Bend);
        controls.Key(keys.TempoDown, GlfwKeyAction.Release, NUDGE);
        Assert.Equal(KeyboardControls.NUDGE, deck.Bend);
        controls.Key(keys.TempoUp, GlfwKeyAction.Release, NUDGE);
        Assert.Equal(0, deck.Bend);
    }

    [Fact]
    public void The_tempo_and_volume_keys_still_slide_their_faders()
    {
        using var engine = new AudioEngine();
        using var deck = engine.CreateDeck("A");
        var controls = new KeyboardControls([deck], engine);
        var keys = KeyboardControls.Keys[0];
        var volume = deck.Volume;

        controls.Key(keys.TempoUp, GlfwKeyAction.Press, 0);
        controls.Update(0.05);
        controls.Key(keys.TempoUp, GlfwKeyAction.Release, 0);
        controls.Key(keys.TempoDown, GlfwKeyAction.Press, GlfwModifier.Shift);
        controls.Update(0.05);

        Assert.True(deck.Tempo > 0);
        Assert.True(deck.Volume < volume);
        Assert.Equal(0, deck.Bend);
    }
}
