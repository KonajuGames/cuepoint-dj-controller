namespace Brutality.Input;

[Flags]
enum UiModifiers
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

static class UiModifiersExtensions
{
    /// <summary>
    /// The modifier for shortcuts like paste: Command on macOS, Control elsewhere.
    /// </summary>
    public static bool HasShortcutModifier(this UiModifiers modifiers) =>
        (modifiers & (OperatingSystem.IsMacOS() ? UiModifiers.Super : UiModifiers.Control)) != 0;

    /// <summary>
    /// The modifier for moving or deleting a word at a time: Option on macOS, Control elsewhere.
    /// </summary>
    public static bool HasWordModifier(this UiModifiers modifiers) =>
        (modifiers & (OperatingSystem.IsMacOS() ? UiModifiers.Alt : UiModifiers.Control)) != 0;
}
