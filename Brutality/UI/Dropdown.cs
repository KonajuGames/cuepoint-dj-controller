using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// A box showing one choice from a list, with an arrow; clicking it, or Space or Enter while it has focus, opens the
/// list below it on <see cref="Menu"/> to choose another, with the current one marked. Up and Down step through the
/// list without opening it. The list is asked for afresh each time (see <see cref="Items"/>), so it can change, as
/// the sound cards plugged in do.
/// </summary>
sealed class Dropdown<T> : Element
{
    const float ARROW = 4;

    readonly Font _font;

    public Dropdown(Font font)
    {
        _font = font;
        Clicked += _ => Open();
    }

    /// <summary>
    /// What there is to choose from, asked for each time it's needed.
    /// </summary>
    public Func<IReadOnlyList<T>> Items { get; set; } = () => [];

    /// <summary>
    /// How each choice is written, in the box and the list.
    /// </summary>
    public Func<T, string> Text { get; set; } = item => item?.ToString() ?? "";

    /// <summary>
    /// The choice in effect, shown in the box. Set it to show what's in effect; choosing changes it, then raises
    /// <see cref="Chosen"/>.
    /// </summary>
    public T Selected { get; set; } = default!;

    /// <summary>
    /// Where the list opens. Without one, the dropdown can still be stepped through with Up and Down.
    /// </summary>
    public ContextMenu? Menu { get; set; }

    /// <summary>
    /// How wide the box is; longer choices are cut off.
    /// </summary>
    public float Width { get; set; } = 220;

    public float Padding { get; set; } = 6;

    /// <summary>
    /// A disabled dropdown draws faded and ignores the mouse and keyboard.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public float4 TextColor { get; set; } = Theme.Text;
    public float4 Background { get; set; } = Colors.Hex(0x2A2A35);
    public float4 HoverBackground { get; set; } = Colors.Hex(0x3A3A48);
    public float4 ArrowColor { get; set; } = Theme.Muted;
    public float4 FocusColor { get; set; } = Theme.Focus;

    /// <summary>
    /// Another choice was made, from the list or by stepping with Up and Down; <see cref="Selected"/> is it.
    /// </summary>
    public event Action<T>? Chosen;

    public override bool IsInteractive => Enabled;
    public override bool IsFocusable => Enabled;

    void Choose(T item)
    {
        Selected = item;
        Chosen?.Invoke(item);
    }

    /// <summary>
    /// Opens the list just below the box, at least as wide as it, with the current choice marked.
    /// </summary>
    public void Open()
    {
        if (Menu is not { } menu)
            return;
        var items = Items()
            .Select(item => new MenuItem(Text(item), () => Choose(item), Checked: EqualityComparer<T>.Default.Equals(item, Selected)))
            .ToList();
        if (items.Count > 0)
            menu.Show(ScreenPosition + new float2(0, Size.Y), items, minWidth: Size.X);
    }

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        switch (key)
        {
            case Key.Space or Key.Enter:
                Open();
                return true;
            case Key.Up or Key.Down:
                var items = Items();
                if (items.Count == 0)
                    return true;
                var at = -1;
                for (var i = 0; i < items.Count && at < 0; i++)
                {
                    if (EqualityComparer<T>.Default.Equals(items[i], Selected))
                        at = i;
                }
                var next = Math.Clamp(at + (key == Key.Down ? 1 : -1), 0, items.Count - 1);
                if (next != at)
                    Choose(items[next]);
                return true;
            default:
                return false;
        }
    }

    public override float2 Measure() => Size = new float2(Width, _font.LineHeight + 2 * Padding);

    public override void Draw(Batch batch)
    {
        float4 Faded(float4 color) => Enabled ? color : color * new float4(1, 1, 1, 0.4f);

        var hovered = Enabled && (IsHovered || IsPressed);
        batch.AddRect(ScreenPosition, Size, Faded(hovered ? HoverBackground : Background));

        // The choice, cut off short of the arrow.
        var textWidth = Size.X - 2 * Padding - 4 * ARROW;
        batch.PushClip(ScreenPosition, new float2(Padding + textWidth, Size.Y));
        batch.AddText(_font, Text(Selected), ScreenPosition + new float2(Padding, Padding), Faded(TextColor));
        batch.PopClip();

        // An arrow pointing down at the right.
        var right = ScreenPosition.X + Size.X - Padding - 1;
        var top = MathF.Round(ScreenPosition.Y + Size.Y / 2 - ARROW / 2);
        for (var i = 0; i < ARROW; i++)
            batch.AddRect(new float2(right - 2 * ARROW + 1 + i, top + i), new float2(2 * (ARROW - i) - 1, 1), Faded(ArrowColor));

        if (ShowFocus)
            batch.AddBorder(ScreenPosition, Size, 2, FocusColor);
    }
}
