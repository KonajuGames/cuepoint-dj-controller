using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// A box that's ticked or not, with some text beside it if given; clicking anywhere on it, or Space or Enter while it
/// has focus, ticks or unticks it. Without text it's just the box, for a row that names it already.
/// </summary>
sealed class Checkbox : Element
{
    const float BOX = 18;
    const float GAP = 8;

    public Font Font { get; set; }

    /// <summary>
    /// What's written beside the box; none if null or empty.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Whether it's ticked. Set it to show what's in effect; clicking changes it, then raises <see cref="Changed"/>.
    /// </summary>
    public bool Checked { get; set; }

    /// <summary>
    /// A disabled checkbox draws faded and ignores the mouse and keyboard.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public float4 TextColor { get; set; } = Theme.Text;
    public float4 BoxColor { get; set; } = Theme.Well;
    public float4 BorderColor { get; set; } = Theme.Faint;
    public float4 HoverBorderColor { get; set; } = Theme.Muted;
    public float4 CheckedColor { get; set; } = Theme.Accent;
    public float4 TickColor { get; set; } = Theme.TitleBar;
    public float4 FocusColor { get; set; } = Theme.Focus;

    /// <summary>
    /// It was ticked or unticked by the mouse or keyboard; <see cref="Checked"/> says which.
    /// </summary>
    public event Action<Checkbox>? Changed;

    public Checkbox(Font font, string? text = null)
    {
        Font = font;
        Text = text;
        Clicked += _ => Toggle();
    }

    public override bool IsInteractive => Enabled;
    public override bool IsFocusable => Enabled;

    bool HasText => Text is { Length: > 0 };

    void Toggle()
    {
        Checked = !Checked;
        Changed?.Invoke(this);
    }

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key is not (Key.Space or Key.Enter))
            return false;
        Toggle();
        return true;
    }

    public override float2 Measure()
    {
        if (!HasText)
            return Size = new float2(BOX, BOX);
        var text = Font.Measure(Text);
        return Size = new float2(BOX + GAP + text.X, MathF.Max(BOX, text.Y));
    }

    public override void Draw(Batch batch)
    {
        float4 Faded(float4 color) => Enabled ? color : color * new float4(1, 1, 1, 0.4f);

        var box = new float2(ScreenPosition.X, MathF.Round(ScreenPosition.Y + (Size.Y - BOX) / 2));
        var hovered = Enabled && (IsHovered || IsPressed);
        if (Checked)
        {
            batch.AddRect(box, new float2(BOX, BOX), Faded(hovered ? Theme.Mix(CheckedColor, Colors.White, 0.15f) : CheckedColor));
            DrawTick(batch, box, Faded(TickColor));
        }
        else
        {
            batch.AddRect(box, new float2(BOX, BOX), Faded(BoxColor));
            batch.AddBorder(box, new float2(BOX, BOX), 1, Faded(hovered ? HoverBorderColor : BorderColor));
        }

        if (HasText)
        {
            var at = new float2(box.X + BOX + GAP, MathF.Round(ScreenPosition.Y + (Size.Y - Font.LineHeight) / 2));
            batch.AddText(Font, Text, at, Faded(TextColor));
        }

        if (ShowFocus)
            batch.AddBorder(box - new float2(3, 3), new float2(BOX + 6, BOX + 6), 2, FocusColor);
    }

    /// <summary>
    /// A tick in the box at <paramref name="box"/>: a short stroke down to the right, then a long one up to the right,
    /// each two pixels thick.
    /// </summary>
    static void DrawTick(Batch batch, float2 box, float4 color)
    {
        for (var i = 0; i <= 3; i++)
            batch.AddRect(box + new float2(4 + i, 8 + i), new float2(2, 2), color);
        for (var i = 0; i <= 6; i++)
            batch.AddRect(box + new float2(7 + i, 11 - i), new float2(2, 2), color);
    }
}
