namespace Brutality.Input;

[Flags]
enum Modifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,

    /// <summary>
    /// Windows key, or Command on macOS.
    /// </summary>
    Super = 8,
}

static class ModifiersExtensions
{
    /// <summary>
    /// The modifier for shortcuts like paste: Command on macOS, Control elsewhere.
    /// </summary>
    public static bool HasShortcutModifier(this Modifiers modifiers) =>
        (modifiers & (OperatingSystem.IsMacOS() ? Modifiers.Super : Modifiers.Control)) != 0;

    /// <summary>
    /// The modifier for moving or deleting a word at a time: Option on macOS, Control elsewhere.
    /// </summary>
    public static bool HasWordModifier(this Modifiers modifiers) =>
        (modifiers & (OperatingSystem.IsMacOS() ? Modifiers.Alt : Modifiers.Control)) != 0;
}
