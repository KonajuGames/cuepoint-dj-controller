using Brutal.Numerics;

namespace Cuepoint.UI;

/// <summary>
/// Every keyboard shortcut, over the screen dimmed behind it: the decks' in a column for each, then the rest, from
/// <see cref="KeyboardShortcuts"/>. ? opens and closes it, and Close, Escape or a click outside it closes it. Sized to
/// the screen by whoever lays it out, with <see cref="ScreenSize"/>; on a screen too short for it all, it scrolls.
/// </summary>
sealed class ShortcutsDialog : Panel
{
    const float MARGIN = 16;
    const float ACTION_WIDTH = 300;
    const float KEYS_WIDTH = 190;

    readonly ScrollPanel _box;
    readonly Panel _content;
    readonly Font _body;
    readonly Font _bold;
    readonly Font _heading;
    bool _pressedInBox;

    /// <summary>
    /// Change keys was clicked, to open the keys dialog.
    /// </summary>
    public event Action? ChangeKeysClicked;

    public float2 ScreenSize { get; set; }

    public ShortcutsDialog(Font body, Font bold, Font heading)
    {
        Visible = false;
        Background = Theme.Scrim(0.75f);

        (_body, _bold, _heading) = (body, bold, heading);
        _box = Add(new ScrollPanel { Padding = 20, Background = Theme.PanelRaised, BorderColor = Theme.PanelBorder, BorderWidth = 1 });
        _content = _box.Add(new Panel { Spacing = 14 });
        Build();

        // A click on the dimmed screen around it closes it too; one on the box's own empty space doesn't.
        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    /// <summary>
    /// The shortcuts as the keys are now, which can change.
    /// </summary>
    void Build()
    {
        var (body, bold, heading) = (_body, _bold, _heading);
        var content = _content;
        content.Children.Clear();
        content.Add(new Label(heading, "Keyboard shortcuts") { Color = Theme.Text });

        var decks = content.Add(new Panel { Spacing = 3 });
        Row(decks, bold, "Decks", "Deck A", "Deck B", Theme.Text, AppTheme.DeckA, AppTheme.DeckB);
        foreach (var shortcut in KeyboardShortcuts.Decks)
            Row(decks, body, shortcut.Action, shortcut.DeckA, shortcut.DeckB, Theme.Muted, Theme.Text, Theme.Text);

        var everywhere = content.Add(new Panel { Spacing = 3 });
        Row(everywhere, bold, "Mixer, lists and the rest", null, null, Theme.Text, Theme.Text, Theme.Text);
        foreach (var shortcut in KeyboardShortcuts.Everywhere)
            Row(everywhere, body, shortcut.Action, shortcut.DeckA, null, Theme.Muted, Theme.Text, Theme.Text);

        var buttons = content.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        var change = buttons.Add(new Button(bold, "Change keys...") { FocusColor = Theme.Focus });
        change.Clicked += _ => ChangeKeysClicked?.Invoke();
        var close = buttons.Add(new Button(bold, "Close") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        close.Clicked += _ => Close();
    }

    /// <summary>
    /// A line of the table: what it does, then the keys in a column for each deck, or across both.
    /// </summary>
    static void Row(Panel table, Font font, string action, string? first, string? second, float4 actionColor, float4 firstColor, float4 secondColor)
    {
        var row = table.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
        row.Add(new Panel { MinSize = new float2(ACTION_WIDTH, 0) }).Add(new Label(font, action) { Color = actionColor, MaxWidth = ACTION_WIDTH });
        if (first is null)
            return;
        var width = second is null ? 2 * KEYS_WIDTH + 12 : KEYS_WIDTH;
        row.Add(new Panel { MinSize = new float2(width, 0) }).Add(new Label(font, first) { Color = firstColor, MaxWidth = width });
        if (second is not null)
            row.Add(new Panel { MinSize = new float2(KEYS_WIDTH, 0) }).Add(new Label(font, second) { Color = secondColor, MaxWidth = KEYS_WIDTH });
    }

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;

    public void Toggle()
    {
        if (Visible)
            Close();
        else
        {
            Build();
            Visible = true;
        }
    }

    public void Close() => Visible = false;

    public override float2 Measure()
    {
        _box.MaxWidth = MathF.Max(0, ScreenSize.X - 2 * MARGIN);
        _box.MaxHeight = MathF.Max(0, ScreenSize.Y - 2 * MARGIN);
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(new float2(MathF.Round(position.X + (ScreenSize.X - _box.Size.X) / 2), MathF.Round(position.Y + (ScreenSize.Y - _box.Size.Y) / 2)));
    }
}
