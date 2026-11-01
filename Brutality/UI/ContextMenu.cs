using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// One choice on a <see cref="ContextMenu"/>. One with a <see cref="Submenu"/> opens it beside the menu, when the
/// mouse comes over it or it's clicked, rather than being chosen itself. <see cref="Checked"/> marks the one that's
/// in effect now.
/// </summary>
sealed record MenuItem(string Text, Action Chosen, bool Enabled = true, IReadOnlyList<MenuItem>? Submenu = null, bool Checked = false)
{
    public MenuItem(string text, IReadOnlyList<MenuItem> submenu, bool enabled = true)
        : this(text, () => { }, enabled && submenu.Count > 0, submenu) { }
}

/// <summary>
/// A short list of things to do, shown where something was right-clicked, with a submenu for an item that has one.
/// Choosing one, clicking anywhere else, or <see cref="Close"/> (for Escape) puts it away. It covers the whole screen,
/// invisibly, to catch the click that closes it, so add it to the root after everything it should sit over.
/// </summary>
sealed class ContextMenu : Panel
{
    const float EDGE = 4;
    const float ARROW = 4;

    readonly Font _font;
    readonly Panel _box;
    readonly Panel _subBox;
    readonly Dictionary<Button, MenuItem> _items = [];
    float2 _at;

    // The item whose submenu is open in _subBox, if any.
    Button? _openItem;

    public float2 ScreenSize { get; set; }

    public ContextMenu(Font font)
    {
        _font = font;
        Visible = false;
        _box = Add(NewBox());
        _subBox = Add(NewBox());
        _subBox.Visible = false;
        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    static Panel NewBox() =>
        new()
        {
            Padding = 4,
            Spacing = 1,
            Background = Theme.PanelRaised,
            BorderColor = Theme.PanelBorder,
            BorderWidth = 1,
        };

    // A press on the menu itself (a disabled item, or between items) mustn't count as a click away from it.
    bool _pressedInBox;

    public override void PointerDown(float2 point, Modifiers modifiers) =>
        _pressedInBox = _box.Contains(point) || (_subBox.Visible && _subBox.Contains(point));

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
        _items.Clear();
        CloseSubmenu();
        Fill(_box, items);
        _at = at;
        Visible = true;
    }

    /// <summary>
    /// The texts of the open submenu's items, or null if none is open.
    /// </summary>
    public IReadOnlyList<string>? SubmenuTexts =>
        _subBox.Visible ? _subBox.Children.OfType<Button>().Select(button => button.Text).ToList() : null;

    void Fill(Panel box, IEnumerable<MenuItem> items)
    {
        foreach (var button in box.Children.OfType<Button>())
            _items.Remove(button);
        box.Children.Clear();

        // Where any item is ticked, every item's text moves along to leave a column for the ticks, so they line up.
        var list = items.ToList();
        var tickColumn = list.Any(item => item.Checked) ? _font.Advance("•") + 6 : 0;
        foreach (var item in list)
        {
            var button = box.Add(
                new Button(_font, item.Text)
                {
                    TextIndent = tickColumn,
                    Padding = 6,
                    Enabled = item.Enabled,
                    Background = Colors.Transparent,
                    HoverBackground = Theme.Fade(Theme.Accent, 0.35f),
                    PressedBackground = Theme.Fade(Theme.Accent, 0.5f),
                    FocusColor = Theme.Focus,
                }
            );
            _items[button] = item;
            button.Clicked += _ =>
            {
                if (item.Submenu is not null)
                {
                    OpenSubmenu(button);
                    return;
                }
                Close();
                item.Chosen();
            };
        }
    }

    void OpenSubmenu(Button button)
    {
        if (_openItem == button || _items[button].Submenu is not { } submenu)
            return;
        Fill(_subBox, submenu);
        _openItem = button;
        _subBox.Visible = true;
    }

    void CloseSubmenu()
    {
        foreach (var button in _subBox.Children.OfType<Button>())
            _items.Remove(button);
        _subBox.Children.Clear();
        _openItem = null;
        _subBox.Visible = false;
    }

    public void Close()
    {
        Visible = false;
        CloseSubmenu();
    }

    public override float2 Measure()
    {
        // The mouse over an item with a submenu opens it; over any other item of the menu, it closes the one open.
        if (_box.Children.OfType<Button>().FirstOrDefault(button => button.IsHovered) is { } hovered && hovered != _openItem)
        {
            if (_items[hovered].Submenu is not null)
                OpenSubmenu(hovered);
            else
                CloseSubmenu();
        }
        MeasureBox(_box);
        if (_subBox.Visible)
            MeasureBox(_subBox);
        return Size = ScreenSize;
    }

    static void MeasureBox(Panel box)
    {
        // Every item as wide as the widest, so the highlight runs the full width, with room for a submenu's arrow.
        foreach (var button in box.Children.OfType<Button>())
            button.MinSize = float2.Zero;
        box.Measure();
        var width = box.Children.Count > 0 ? box.Children.Max(child => child.Size.X) + 4 * ARROW : 0;
        foreach (var button in box.Children.OfType<Button>())
            button.MinSize = new float2(MathF.Max(width, 160), 0);
        box.Measure();
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        var x = Math.Clamp(_at.X, EDGE, MathF.Max(EDGE, ScreenSize.X - _box.Size.X - EDGE));
        var y = Math.Clamp(_at.Y, EDGE, MathF.Max(EDGE, ScreenSize.Y - _box.Size.Y - EDGE));
        _box.Arrange(position + new float2(MathF.Round(x), MathF.Round(y)));

        if (_subBox.Visible && _openItem is { } item)
        {
            // To the right of its item, level with it; to the left if there's no room on the right.
            var right = _box.ScreenPosition.X + _box.Size.X - 2;
            var subX = right + _subBox.Size.X + EDGE <= position.X + ScreenSize.X ? right : _box.ScreenPosition.X - _subBox.Size.X + 2;
            var subY = Math.Clamp(
                item.ScreenPosition.Y - _subBox.Padding - _subBox.BorderWidth,
                position.Y + EDGE,
                MathF.Max(position.Y + EDGE, position.Y + ScreenSize.Y - _subBox.Size.Y - EDGE)
            );
            _subBox.Arrange(new float2(MathF.Round(MathF.Max(position.X + EDGE, subX)), MathF.Round(subY)));
        }
    }

    public override void Draw(Batch batch)
    {
        base.Draw(batch);

        // The ticks, in their column.
        foreach (var box in new[] { _box, _subBox })
        {
            if (!box.Visible)
                continue;
            foreach (var button in box.Children.OfType<Button>())
            {
                if (!_items[button].Checked)
                    continue;
                var at = button.ScreenPosition + new float2(button.Padding + button.BorderWidth, MathF.Round((button.Size.Y - _font.LineHeight) / 2));
                batch.AddText(_font, "•", at, button.TextColor);
            }
        }
        // A small arrow at the right of each item with a submenu.
        foreach (var button in _box.Children.OfType<Button>())
        {
            if (_items[button].Submenu is null)
                continue;
            var color = button.Enabled ? button.TextColor : button.TextColor * new float4(1, 1, 1, 0.4f);
            var right = button.ScreenPosition.X + button.Size.X - 8;
            var middle = button.ScreenPosition.Y + MathF.Round(button.Size.Y / 2);
            for (var i = 0; i < ARROW; i++)
                batch.AddRect(new float2(right - ARROW + i, middle - ARROW + i), new float2(1, 2 * (ARROW - i)), color);
        }
    }
}
