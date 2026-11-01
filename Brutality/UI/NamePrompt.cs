using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// Asks for a name, in a box over the dimmed screen: Enter or OK to accept, Escape or Cancel to give up. What's
/// typed is checked before it's accepted, and whatever's wrong with it is shown under the box.
/// </summary>
sealed class NamePrompt : Panel
{
    readonly Root _root;
    readonly Panel _box;
    readonly Label _title;
    readonly Label _problem;
    Func<string, string?> _check = _ => null;
    Action<string> _accept = _ => { };

    public TextBox Box { get; }

    public float2 ScreenSize { get; set; }

    public NamePrompt(Root root, Font body, Font bold, Font heading)
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
        _title = _box.Add(new Label(heading) { Color = Theme.Text });
        Box = _box.Add(new TextBox(body) { Width = 380, FocusColor = Theme.Focus });
        _problem = _box.Add(new Label(body) { Color = Colors.Hex(0xEE6666) });
        var buttons = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        var ok = buttons.Add(new Button(bold, "OK") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        var cancel = buttons.Add(new Button(bold, "Cancel") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });

        ok.Clicked += _ => Accept();
        cancel.Clicked += _ => Close();
        Box.Submitted += _ => Accept();
        Box.Cancelled += _ => Close();
        Box.TextChanged += _ => _problem.Text = "";
    }

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;

    /// <summary>
    /// Asks for a name, starting from <paramref name="initial"/>, all of it selected to type over.
    /// <paramref name="check"/> says what's wrong with a name, or null if nothing is; <paramref name="accept"/> takes
    /// it, and may throw <see cref="InvalidOperationException"/> to say why it couldn't, leaving the prompt up.
    /// </summary>
    public void Show(string title, string initial, Func<string, string?> check, Action<string> accept)
    {
        _title.Text = title;
        _check = check;
        _accept = accept;
        Box.Text = initial;
        Box.SelectAll();
        _problem.Text = "";
        Visible = true;
        _root.Focus(Box);
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        if (_root.Focused == Box)
            _root.Focus(null);
    }

    void Accept()
    {
        if (_check(Box.Text) is { } problem)
        {
            _problem.Text = problem;
            return;
        }
        try
        {
            _accept(Box.Text);
            Close();
        }
        catch (InvalidOperationException e)
        {
            _problem.Text = e.Message;
        }
    }

    public override float2 Measure()
    {
        _problem.Visible = _problem.Text.Length > 0;
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(new float2(MathF.Round(position.X + (ScreenSize.X - _box.Size.X) / 2), MathF.Round(position.Y + (ScreenSize.Y - _box.Size.Y) / 3)));
    }
}
