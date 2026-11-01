using Brutal.GlfwApi;

namespace Brutality;

/// <summary>
/// The keys for the hotcue pads. Each deck gets a block of eight under the hand it's already played with (Z and X
/// for deck A, N and M for deck B): two rows of four, the top row for hotcues 1 to 4 and the home row for 5 to 8.
/// </summary>
static class HotcueKeys
{
    static readonly GlfwKey[][] Keys =
    [
        [GlfwKey.Q, GlfwKey.W, GlfwKey.E, GlfwKey.R, GlfwKey.A, GlfwKey.S, GlfwKey.D, GlfwKey.F],
        [GlfwKey.U, GlfwKey.I, GlfwKey.O, GlfwKey.P, GlfwKey.J, GlfwKey.K, GlfwKey.L, GlfwKey.Semicolon],
    ];

    /// <summary>
    /// Which deck (by position, from 0) and hotcue (from 0) <paramref name="key"/> is for, if any.
    /// </summary>
    public static (int Deck, int Hotcue)? For(GlfwKey key)
    {
        for (var deck = 0; deck < Keys.Length; deck++)
        {
            var hotcue = Array.IndexOf(Keys[deck], key);
            if (hotcue >= 0)
                return (deck, hotcue);
        }
        return null;
    }

    /// <summary>
    /// What's printed on the keys for the deck at <paramref name="deck"/>'s hotcues, in order, for labelling its
    /// pads; empty for a deck without keys.
    /// </summary>
    public static IReadOnlyList<string> Labels(int deck) =>
        deck >= 0 && deck < Keys.Length ? Keys[deck].Select(key => key == GlfwKey.Semicolon ? ";" : key.ToString()).ToList() : [];
}
