using Brutal.GlfwApi;
using Brutal.Numerics;

namespace Cuepoint.UI;

/// <summary>
/// The keys that work the decks, their pads and the crossfader, to change, over the screen dimmed behind it: each
/// deck's in a column, the mixer's below. Click a key, then press the one to use instead; a key already doing
/// something else swaps with it. Escape stops listening, then closes it. Sized to the screen by whoever lays it out,
/// with <see cref="ScreenSize"/>; on a screen too short for it all, it scrolls.
/// </summary>
sealed class KeysDialog : Panel
{
    const float MARGIN = 16;
    const float ACTION_WIDTH = 260;
    const float KEY_WIDTH = 120;

    readonly ScrollPanel _box;
    readonly Panel _content;
    readonly Font _body;
    readonly Font _bold;
    readonly Font _heading;
    readonly Label _status;
    readonly Dictionary<string, Button> _keys = [];
    bool _pressedInBox;

    /// <summary>
    /// The binding waiting for a key, while it's listening for one.
    /// </summary>
    public string? Learning { get; private set; }

    /// <summary>
    /// The bindings to show, as they are now.
    /// </summary>
    public Func<IReadOnlyList<KeyBinding>> Bindings { get; set; } = () => [];

    /// <summary>
    /// A key was pressed for a binding: its id and the key. Returns what to say about it, as what it swapped with,
    /// or why it couldn't be used.
    /// </summary>
    public Func<string, GlfwKey, string>? Chosen { get; set; }

    /// <summary>
    /// Put back the usual keys was clicked.
    /// </summary>
    public event Action? ResetClicked;

    public float2 ScreenSize { get; set; }

    public KeysDialog(Font body, Font bold, Font heading)
    {
        (_body, _bold, _heading) = (body, bold, heading);
        Visible = false;
        Background = Theme.Scrim(0.75f);
        _box = Add(new ScrollPanel { Padding = 20, Background = Theme.PanelRaised, BorderColor = Theme.PanelBorder, BorderWidth = 1 });
        _content = _box.Add(new Panel { Spacing = 12 });
        _status = new Label(body) { Color = Theme.Accent };

        // A click on the dimmed screen around it closes it too; one on the box's own empty space doesn't.
        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    void Build()
    {
        _content.Children.Clear();
        _keys.Clear();
        _content.Add(new Label(_heading, "Keys") { Color = Theme.Text });
        _content.Add(new Label(_body, "Click a key, then press the one to use instead. Shift, Ctrl and Alt work with whichever's chosen.") { Color = Theme.Muted });

        var bindings = Bindings();
        var decks = _content.Add(new Panel { Spacing = 3 });
        var header = decks.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
        header.Add(new Panel { MinSize = new float2(ACTION_WIDTH, 0) });
        header.Add(new Panel { MinSize = new float2(KEY_WIDTH, 0) }).Add(new Label(_bold, "Deck A") { Color = AppTheme.DeckA });
        header.Add(new Panel { MinSize = new float2(KEY_WIDTH, 0) }).Add(new Label(_bold, "Deck B") { Color = AppTheme.DeckB });
        foreach (var row in bindings.Where(binding => binding.Deck == 0))
        {
            var line = decks.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
            line.Add(new Panel { MinSize = new float2(ACTION_WIDTH, 0) }).Add(new Label(_body, row.Name) { Color = Theme.Muted });
            foreach (var deck in new[] { 0, 1 })
            {
                var id = row.Id.Replace("0.", $"{deck}.");
                if (bindings.FirstOrDefault(binding => binding.Id == id) is { } binding)
                    line.Add(KeyButton(binding));
            }
        }

        var mixer = _content.Add(new Panel { Spacing = 3 });
        foreach (var binding in bindings.Where(binding => binding.Deck < 0))
        {
            var line = mixer.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
            line.Add(new Panel { MinSize = new float2(ACTION_WIDTH, 0) }).Add(new Label(_body, binding.Name) { Color = Theme.Muted });
            line.Add(KeyButton(binding));
        }

        _content.Add(_status);
        var buttons = _content.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        var reset = buttons.Add(new Button(_bold, "Put back the usual keys") { FocusColor = Theme.Focus });
        reset.Clicked += _ =>
        {
            Learning = null;
            ResetClicked?.Invoke();
            _status.Text = "The usual keys are back";
            Refresh();
        };
        var close = buttons.Add(new Button(_bold, "Close") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        close.Clicked += _ => Close();
    }

    Button KeyButton(KeyBinding binding)
    {
        var button = new Button(_body, DeckKeys.Label(binding.Key)) { MinSize = new float2(KEY_WIDTH, 22), FocusColor = Theme.Focus };
        button.Clicked += _ =>
        {
            Learning = binding.Id;
            _status.Text = $"Press a key for {(binding.Deck >= 0 ? $"deck {(char)('A' + binding.Deck)}'s " : "")}{binding.Name.ToLowerInvariant()}, or Escape to leave it";
            Refresh();
        };
        _keys[binding.Id] = button;
        return button;
    }

    /// <summary>
    /// Shows each key as it is now, the one listening picked out.
    /// </summary>
    void Refresh()
    {
        foreach (var binding in Bindings())
        {
            if (!_keys.TryGetValue(binding.Id, out var button))
                continue;
            var listening = binding.Id == Learning;
            button.Text = listening ? "Press a key..." : DeckKeys.Label(binding.Key);
            button.Background = listening ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.6f) : Theme.Control;
        }
    }

    /// <summary>
    /// A key pressed while it's open: given to the binding listening for one, if any. Returns whether it was taken
    /// in, as nothing else should act on a key pressed here.
    /// </summary>
    public bool Learn(GlfwKey key)
    {
        if (Learning is not { } id)
            return false;
        Learning = null;
        _status.Text = Chosen?.Invoke(id, key) ?? "";
        Refresh();
        return true;
    }

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key != Key.Escape)
            return false;
        if (Learning is not null)
        {
            Learning = null;
            _status.Text = "";
            Refresh();
        }
        else
            Close();
        return true;
    }

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    public override bool IsInteractive => true;
    public override bool IsModal => true;
    public override Cursor Cursor => Cursor.Arrow;

    public void Open()
    {
        Learning = null;
        Build();
        _status.Text = "";
        Visible = true;
    }

    public void Close()
    {
        Learning = null;
        Visible = false;
    }

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
