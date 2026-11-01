using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// Asks whether to go ahead with something that can't easily be taken back, in a box over the dimmed screen. The
/// confirming button says what will happen ("Delete"); Enter presses it, and Escape, Cancel or a click on the
/// dimmed screen gives up. It takes focus itself while shown, so those keys reach it.
/// </summary>
sealed class ConfirmDialog : Panel
{
    readonly Root _root;
    readonly Panel _box;
    readonly Label _title;
    readonly Label _message;
    readonly Button _confirm;
    Action _confirmed = () => { };

    public float2 ScreenSize { get; set; }

    public ConfirmDialog(Root root, Font body, Font bold, Font heading)
    {
        _root = root;
        Visible = false;
        Background = Colors.Hex(0x000000, 0.6f);

        _box = Add(
            new Panel
            {
                Padding = 20,
                Spacing = 12,
                Background = Theme.PanelRaised,
                BorderColor = Theme.PanelBorder,
                BorderWidth = 1,
                MinSize = new float2(420, 0),
            }
        );
        _title = _box.Add(new Label(heading) { Color = Theme.Text, MaxWidth = 520 });
        _message = _box.Add(new Label(body) { Color = Theme.Muted, MaxWidth = 520 });
        var buttons = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        _confirm = buttons.Add(
            new Button(bold, "")
            {
                MinSize = new float2(90, 0),
                Background = Colors.Hex(0xB83A3A),
                HoverBackground = Colors.Hex(0xD04848),
                PressedBackground = Colors.Hex(0x962E2E),
                FocusColor = Theme.Focus,
            }
        );
        var cancel = buttons.Add(new Button(bold, "Cancel") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });

        _confirm.Clicked += _ => Confirm();
        cancel.Clicked += _ => Close();
        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    // A press on the box itself, around its text, mustn't count as a click on the dimmed screen.
    bool _pressedInBox;

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    public override bool IsInteractive => true;
    public override bool IsFocusable => Visible;
    public override Cursor Cursor => Cursor.Arrow;

    /// <summary>
    /// Asks <paramref name="title"/>, explained by <paramref name="message"/>, with <paramref name="confirm"/> on
    /// the button that goes ahead and runs <paramref name="confirmed"/>.
    /// </summary>
    public void Show(string title, string message, string confirm, Action confirmed)
    {
        _title.Text = title;
        _message.Text = message;
        _confirm.Text = confirm;
        _confirmed = confirmed;
        Visible = true;
        _root.Focus(this);
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        if (_root.Focused == this || _root.Focused is { } focused && IsInside(focused))
            _root.Focus(null);
    }

    bool IsInside(Element element) => _box.Children.OfType<Panel>().Any(panel => panel.Children.Contains(element));

    void Confirm()
    {
        Close();
        _confirmed();
    }

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        switch (key)
        {
            case Key.Enter:
                Confirm();
                return true;
            case Key.Escape:
                Close();
                return true;
        }
        return false;
    }

    public override float2 Measure()
    {
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(new float2(MathF.Round(position.X + (ScreenSize.X - _box.Size.X) / 2), MathF.Round(position.Y + (ScreenSize.Y - _box.Size.Y) / 3)));
    }
}
