using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// One choice on a <see cref="ContextMenu"/>.
/// </summary>
sealed record MenuItem(string Text, Action Chosen, bool Enabled = true);

/// <summary>
/// A short list of things to do, shown where something was right-clicked. Choosing one, clicking anywhere else, or
/// <see cref="Close"/> (for Escape) puts it away. It covers the whole screen, invisibly, to catch the click that
/// closes it, so add it to the root after everything it should sit over.
/// </summary>
sealed class ContextMenu : Panel
{
    const float EDGE = 4;

    readonly Font _font;
    readonly Panel _box;
    float2 _at;

    public float2 ScreenSize { get; set; }

    public ContextMenu(Font font)
    {
        _font = font;
        Visible = false;
        _box = Add(
            new Panel
            {
                Padding = 4,
                Spacing = 1,
                Background = Theme.PanelRaised,
                BorderColor = Theme.PanelBorder,
                BorderWidth = 1,
            }
        );
        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    // A press on the menu itself (a disabled item, or between items) mustn't count as a click away from it.
    bool _pressedInBox;

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;

    // A right-click elsewhere just closes it, rather than opening another under it.
    public override void SecondaryClick(float2 point) => Close();

    /// <summary>
    /// Shows <paramref name="items"/> with the menu's top-left corner at <paramref name="at"/>, moved in to stay on
    /// the screen.
    /// </summary>
    public void Show(float2 at, IEnumerable<MenuItem> items)
    {
        _box.Children.Clear();
        foreach (var item in items)
        {
            var button = _box.Add(
                new Button(_font, item.Text)
                {
                    Padding = 6,
                    Enabled = item.Enabled,
                    Background = Colors.Transparent,
                    HoverBackground = Theme.Fade(Theme.Accent, 0.35f),
                    PressedBackground = Theme.Fade(Theme.Accent, 0.5f),
                    FocusColor = Theme.Focus,
                }
            );
            button.Clicked += _ =>
            {
                Close();
                item.Chosen();
            };
        }
        _at = at;
        Visible = true;
    }

    public void Close() => Visible = false;

    public override float2 Measure()
    {
        // Every item as wide as the widest, so the highlight runs the full width.
        foreach (var button in _box.Children.OfType<Button>())
            button.MinSize = float2.Zero;
        _box.Measure();
        var width = _box.Children.Max(child => child.Size.X);
        foreach (var button in _box.Children.OfType<Button>())
            button.MinSize = new float2(MathF.Max(width, 160), 0);
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        var x = Math.Clamp(_at.X, EDGE, MathF.Max(EDGE, ScreenSize.X - _box.Size.X - EDGE));
        var y = Math.Clamp(_at.Y, EDGE, MathF.Max(EDGE, ScreenSize.Y - _box.Size.Y - EDGE));
        _box.Arrange(position + new float2(MathF.Round(x), MathF.Round(y)));
    }
}
