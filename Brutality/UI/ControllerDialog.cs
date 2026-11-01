using Brutal.Numerics;
using Brutality.Input;
using Brutality.Midi;

namespace Brutality.UI;

/// <summary>
/// Shows what each control is bound to on a connected controller, and binds them by MIDI learn: press Learn beside
/// a control, then move or press the one on the controller to bind it there. Changes are saved for that controller
/// as they're made; Reset goes back to its built-in mapping.
/// </summary>
sealed class ControllerDialog : Panel
{
    readonly MidiControls _controls;
    readonly MidiDevices _devices;
    readonly Panel _box;
    readonly Button _device;
    readonly Button _reset;
    readonly Label _hint;
    readonly ScrollPanel _scroll;
    readonly List<Row> _rows = [];
    int _deviceIndex;

    sealed record Row(ControlInfo Control, Label Binding, Button Learn, Button Clear);

    public float2 ScreenSize { get; set; }

    public ControllerDialog(MidiControls controls, MidiDevices devices, Font body, Font bold, Font heading)
    {
        _controls = controls;
        _devices = devices;
        Visible = false;
        Background = Colors.Hex(0x000000, 0.75f);

        _box = Add(
            new Panel
            {
                Padding = 20,
                Spacing = 12,
                Background = Theme.PanelRaised,
                BorderColor = Theme.PanelBorder,
                BorderWidth = 1,
                MinSize = new float2(640, 0),
            }
        );
        _box.Add(new Label(heading, "Controller") { Color = Theme.Text });

        var top = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 10 });
        _device = top.Add(new Button(bold, "") { MinSize = new float2(220, 0), FocusColor = Theme.Focus });
        _device.Clicked += _ =>
        {
            _controls.Learning = null;
            _deviceIndex++;
        };
        _reset = top.Add(new Button(bold, "Reset to built-in") { FocusColor = Theme.Focus });
        _reset.Clicked += _ =>
        {
            if (Device is { } device)
            {
                _controls.Learning = null;
                _controls.ResetMapping(device);
            }
        };
        _hint = _box.Add(new Label(body) { Color = Theme.Muted });

        _scroll = _box.Add(new ScrollPanel { Spacing = 2 });
        foreach (var control in controls.Controls)
        {
            var row = _scroll.Add(new Panel { Direction = Direction.Horizontal, Spacing = 10 });
            row.Add(new Panel { MinSize = new float2(220, 0) }).Add(new Label(body, control.Name) { Color = Theme.Text });
            var binding = row.Add(new Panel { MinSize = new float2(190, 0) }).Add(new Label(body) { Color = Theme.Muted });
            var learn = row.Add(new Button(body, "Learn") { MinSize = new float2(110, 0), Padding = 3, FocusColor = Theme.Focus });
            var clear = row.Add(new Button(body, "Clear") { Padding = 3, FocusColor = Theme.Focus });
            row.Add(new Panel { MinSize = new float2(_scroll.ScrollbarHitWidth, 0) });
            var id = control.Id;
            learn.Clicked += _ => _controls.Learning = _controls.Learning == id ? null : id;
            clear.Clicked += _ =>
            {
                if (Device is { } device)
                    _controls.Unbind(device, id);
            };
            _rows.Add(new Row(control, binding, learn, clear));
        }

        var close = _box.Add(new Button(bold, "Close") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        close.Clicked += _ => Close();
        Clicked += _ => Close();
    }

    /// <summary>
    /// The controller being shown, picked from those connected.
    /// </summary>
    MidiDevice? Device => _devices.Devices.Count == 0 ? null : _devices.Devices[_deviceIndex % _devices.Devices.Count];

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;

    public void Open() => Visible = true;

    /// <summary>
    /// Closes it, giving up on any control being learned.
    /// </summary>
    public void Close()
    {
        _controls.Learning = null;
        Visible = false;
    }

    /// <summary>
    /// Escape gives up on a learn first, then closes. Returns whether it did either.
    /// </summary>
    public bool Escape()
    {
        if (!Visible)
            return false;
        if (_controls.Learning is not null)
            _controls.Learning = null;
        else
            Close();
        return true;
    }

    public override float2 Measure()
    {
        var device = Device;
        var mapping = device is null ? null : _controls.MappingFor(device);
        _device.Text = device is null ? "No controller connected" : _devices.Devices.Count > 1 ? $"{device.Name}  (click for the next)" : device.Name;
        _reset.Enabled = device is not null;
        _hint.Text =
            device is null ? "Plug in a MIDI controller to bind its controls."
            : _controls.Learning is not null ? "Now move or press the control on the controller. Esc to stop."
            : "Learn, then move or press a control on the controller to bind it.";

        foreach (var row in _rows)
        {
            var bindings = mapping?.Bindings.Where(binding => binding.Control == row.Control.Id).ToList() ?? [];
            row.Binding.Text = bindings.Count == 0 ? "-" : string.Join("; ", bindings);
            var learning = _controls.Learning == row.Control.Id;
            row.Learn.Text = learning ? "Waiting..." : "Learn";
            row.Learn.Background = learning ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.55f) : Colors.Hex(0x2A2A35);
            row.Learn.Enabled = device is not null;
            row.Clear.Enabled = bindings.Count > 0;
        }

        _scroll.MaxHeight = MathF.Max(120, ScreenSize.Y - 260);
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(new float2(MathF.Round(position.X + (ScreenSize.X - _box.Size.X) / 2), MathF.Round(position.Y + (ScreenSize.Y - _box.Size.Y) / 2)));
    }
}
