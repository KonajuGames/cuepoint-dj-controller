using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// One hotcue's pad: its number, lit in its colour when set. It acts as it goes down, not when let go, so a jump
/// lands on the beat it's pressed on: <see cref="Triggered"/> to set or jump, and with Shift held, or a right-click,
/// <see cref="Deleted"/> to clear it. Enter or Space trigger it from the keyboard. A <see cref="KeyHint"/>, the app's
/// own key for it, shows small in its corner.
/// </summary>
sealed class HotcuePad(Font font, int number) : Element
{
    public int Number { get; } = number;

    public bool IsSet { get; set; }

    /// <summary>
    /// What the pad says in place of its number, when it's doing something else (a grid tool, say); null for the
    /// number.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// A picture beside <see cref="Text"/>, tinted as the text is: before it, or after it if <see cref="IconAfter"/>;
    /// none if null.
    /// </summary>
    public Image? Icon { get; set; }

    public bool IconAfter { get; set; }
    public float4 Color { get; set; } = Colors.White;
    public bool Enabled { get; set; } = true;
    public float2 FixedSize { get; set; } = new(34, 30);

    /// <summary>
    /// The key that presses this pad, shown in its top-right corner in <see cref="HintFont"/>; none if null.
    /// </summary>
    public string? KeyHint { get; set; }

    public Font? HintFont { get; set; }

    public float4 EmptyBackground { get; set; } = Colors.Hex(0x2A2A35);
    public float4 HoverBackground { get; set; } = Colors.Hex(0x3A3A48);
    public float4 FocusColor { get; set; } = Colors.Hex(0x7AB8FF);

    /// <summary>
    /// Pressed: an empty pad should be set, a set one jumped to.
    /// </summary>
    public event Action<HotcuePad>? Triggered;

    /// <summary>
    /// Shift-clicked or right-clicked: the hotcue should be cleared.
    /// </summary>
    public event Action<HotcuePad>? Deleted;

    public override bool IsInteractive => Enabled;
    public override bool IsFocusable => Enabled;

    public override float2 Measure() => Size = FixedSize;

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        if ((modifiers & Modifiers.Shift) != 0)
            Deleted?.Invoke(this);
        else
            Triggered?.Invoke(this);
    }

    public override void SecondaryClick(float2 point)
    {
        if (Enabled)
            Deleted?.Invoke(this);
    }

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key is not (Key.Enter or Key.Space))
            return false;
        Triggered?.Invoke(this);
        return true;
    }

    public override void Draw(Batch batch)
    {
        var alpha = Enabled ? 1f : 0.4f;
        float4 Faded(float4 c) => c * new float4(1, 1, 1, alpha);

        var background =
            IsSet ? (IsPressed ? Theme.Mix(Color, Colors.White, 0.3f) : IsHovered ? Theme.Mix(Color, Colors.White, 0.15f) : Color)
            : IsHovered || IsPressed ? HoverBackground
            : EmptyBackground;
        batch.AddRect(ScreenPosition, Size, Faded(background));
        if (!IsSet)
            batch.AddRect(ScreenPosition + new float2(0, Size.Y - 2), new float2(Size.X, 2), Faded(Theme.Fade(Color, 0.6f)));

        // A word in place of the number goes in the small font, leaving the corner for the key.
        var text = Text ?? Number.ToString();
        var textFont = Text is not null && HintFont is { } small ? small : font;
        var extent = textFont.Measure(text);
        var textColor = Faded(IsSet ? Theme.TitleBar : Text is not null ? Theme.Text : Theme.Muted);

        // The icon and the text centred together, a few pixels apart.
        const float GAP = 4;
        var iconWidth = Icon is { } image ? image.Size.X + GAP : 0;
        var left = MathF.Round(ScreenPosition.X + (Size.X - extent.X - iconWidth) / 2);
        var textX = Icon is not null && !IconAfter ? left + iconWidth : left;
        var at = new float2(textX, MathF.Round(ScreenPosition.Y + (Size.Y - extent.Y) / 2));
        // A name from other software can be longer than the pad.
        batch.PushClip(ScreenPosition, Size);
        batch.AddText(textFont, text, new float2(MathF.Max(at.X, ScreenPosition.X + 2), at.Y), textColor);
        batch.PopClip();
        if (Icon is { } icon)
        {
            var iconX = IconAfter ? textX + extent.X + GAP : left;
            batch.AddImage(icon, new float2(iconX, ScreenPosition.Y + (Size.Y - icon.Size.Y) / 2), textColor);
        }

        if (KeyHint is { } hint && HintFont is { } hintFont)
        {
            var hintSize = hintFont.Measure(hint);
            var hintAt = new float2(MathF.Round(ScreenPosition.X + Size.X - hintSize.X - 2), ScreenPosition.Y);
            batch.AddText(hintFont, hint, hintAt, Faded(IsSet ? Theme.Fade(Theme.TitleBar, 0.6f) : Theme.Faint));
        }

        if (ShowFocus)
            batch.AddBorder(ScreenPosition, Size, 2, FocusColor);
    }
}
