using Brutal.Numerics;
using Cuepoint.Streaming;

namespace Cuepoint.UI;

/// <summary>
/// Streaming the mix live, dropped down from the title bar's LIVE button: the Icecast server to send it to (its
/// address, port, mount point and source password), what to call the stream, how good to make it, and Go live or
/// Stop, with how it's getting on. Clicking anywhere else, the button again, or <see cref="Close"/> (for Escape) puts
/// it away. Add it to the root after everything it should sit over, and before the menu its list opens on. Call
/// <see cref="Refresh"/> once a frame.
/// </summary>
sealed class LiveStreamPanel : Panel
{
    const float FIELD = 220;

    readonly Root _root;
    readonly Panel _box;
    readonly Label _heading;
    readonly List<(Label Name, Element Field)> _rows = [];
    readonly TextBox _host;
    readonly TextBox _port;
    readonly TextBox _mount;
    readonly TextBox _password;
    readonly TextBox _name;
    readonly Dropdown<int> _bitrate;
    readonly Label _status;
    readonly Label _listen;
    readonly Button _go;

    float2 _anchor;
    bool _pressedInBox;

    public float2 ScreenSize { get; set; }

    /// <summary>
    /// The stream going, or null; set by whoever starts and stops it.
    /// </summary>
    public LiveStream? Stream { get; set; }

    /// <summary>
    /// Go live was pressed with these settings.
    /// </summary>
    public event Action<StreamServer>? GoLive;

    /// <summary>
    /// Stop was pressed.
    /// </summary>
    public event Action? StopRequested;

    /// <summary>
    /// Where the bitrate list opens.
    /// </summary>
    public ContextMenu? Menu
    {
        set => _bitrate.Menu = value;
    }

    public LiveStreamPanel(Root root, StreamServer server, Font small, Font body, Font bold)
    {
        _root = root;
        Visible = false;
        _box = Add(
            new Panel
            {
                Padding = 12,
                Background = Theme.PanelRaised,
                BorderColor = Theme.PanelBorder,
                BorderWidth = 1,
            }
        );
        _heading = Add(new Label(bold, "LIVE STREAM") { Color = Theme.Text });
        TextBox Field(string name, string text, bool secret = false, float width = FIELD)
        {
            var box = Add(new TextBox(body) { Text = text, Width = width, Secret = secret, FocusColor = Theme.Focus });
            box.Submitted += _ => Go();
            box.Cancelled += _ => Close();
            // Whatever was wrong with them is gone as they're changed.
            box.TextChanged += _ => _problem = null;
            _rows.Add((Add(new Label(small, name) { Color = Theme.Muted }), box));
            return box;
        }
        _host = Field("SERVER", server.Host);
        _port = Field("PORT", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), width: 80);
        _mount = Field("MOUNT", server.Mount);
        _password = Field("PASSWORD", server.Password, secret: true);
        _name = Field("NAME", server.Name);
        _bitrate = Add(
            new Dropdown<int>(body)
            {
                Width = 120,
                Items = () => StreamServer.Bitrates,
                Text = kbps => $"{kbps} kbps",
                Selected = server.Kbps,
                Tooltip = "How good it sounds, and how much it sends: 128 kbps of Opus is about as good as 256 of MP3",
            }
        );
        _rows.Add((Add(new Label(small, "QUALITY") { Color = Theme.Muted }), _bitrate));
        _status = Add(new Label(body) { Color = Theme.Muted, MaxWidth = 340, Wrap = true });
        _listen = Add(new Label(small) { Color = Theme.Faint, MaxWidth = 340 });
        _go = Add(new Button(bold, "GO LIVE") { MinSize = new float2(100, 28), FocusColor = Theme.Focus });
        _go.Clicked += _ => Go();

        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    public override void SecondaryClick(float2 point)
    {
        if (!_box.Contains(point))
            Close();
    }

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key != Key.Escape)
            return false;
        Close();
        return true;
    }

    /// <summary>
    /// Shows it with its top right corner at <paramref name="anchor"/>, or puts it away if it's showing.
    /// </summary>
    public void Toggle(float2 anchor)
    {
        if (Visible)
        {
            Close();
            return;
        }
        _anchor = anchor;
        Refresh();
        Visible = true;
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        _root.Focus(null);
    }

    /// <summary>
    /// The settings as they're typed now, or why they won't do.
    /// </summary>
    internal (StreamServer? Server, string? Problem) Typed()
    {
        var host = _host.Text.Trim();
        if (host.Length == 0)
            return (null, "Give the server's address, as stream.example.com");
        if (!int.TryParse(_port.Text.Trim(), out var port) || port is < 1 or > 65535)
            return (null, "The port's a number, as 8000");
        var mount = _mount.Text.Trim();
        if (mount.Trim('/').Length == 0 || mount.Contains(' '))
            return (null, "Give the mount point, as live, with no spaces");
        if (_password.Text.Length == 0)
            return (null, "Give the server's source password");
        return (new StreamServer(host, port, mount.TrimStart('/'), _password.Text, _name.Text.Trim().Length > 0 ? _name.Text.Trim() : "Cuepoint", _bitrate.Selected), null);
    }

    string? _problem;

    /// <summary>
    /// Says why the stream stopped, once it's been let go, until the settings are changed or it goes live again.
    /// </summary>
    public void Stopped(string problem) => _problem = problem;

    void Go()
    {
        if (Stream is not null)
        {
            StopRequested?.Invoke();
            return;
        }
        var (server, problem) = Typed();
        _problem = problem;
        if (server is not null)
        {
            _root.Focus(null);
            GoLive?.Invoke(server);
        }
    }

    internal Button GoButton => _go;

    internal string StatusText => _status.Text;

    internal (TextBox Host, TextBox Port, TextBox Mount, TextBox Password, TextBox Name) Fields => (_host, _port, _mount, _password, _name);

    public void Refresh()
    {
        var stream = Stream;
        _go.Text = stream is null ? "GO LIVE" : "STOP";
        _go.Background = stream is { State: StreamState.Live } ? Theme.Mix(Theme.PanelRaised, Theme.Warning, 0.6f) : Theme.Control;
        // Changed while live, they're used the next time it goes live.
        _bitrate.Enabled = stream is null;
        (_status.Text, _status.Color) = stream switch
        {
            null when _problem is { } problem => (problem, Theme.Warning),
            null => ("Streams the main mix, as the recording has it, with the song playing as its title.", Theme.Muted),
            { State: StreamState.Connecting } => ("Connecting...", Theme.Muted),
            { State: StreamState.Live, LiveSince: { } since } => (
                $"Live for {Clock(DateTime.Now - since)}, {stream.BytesSent / 1024 / 1024.0:F1} MB sent{(stream.Listeners is { } listening ? $", {listening} {(listening == 1 ? "listener" : "listeners")}" : "")}",
                Theme.Text
            ),
            { Problem: { } why } => (why, Theme.Warning),
            _ => ("Stopped", Theme.Muted),
        };
        _listen.Text = stream is { State: StreamState.Live } ? $"Listen at {stream.Server.ListenAt}" : "";
    }

    static string Clock(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";

    public override float2 Measure()
    {
        foreach (var child in Children)
            child.Measure();
        var labels = _rows.Max(row => row.Name.Size.X) + 10;
        var width = MathF.Max(labels + FIELD, 340);
        var height = _heading.Size.Y + 10 + _rows.Sum(row => MathF.Max(row.Field.Size.Y, row.Name.Size.Y) + 6) + 4 + _status.Size.Y + 4
            + (_listen.Text.Length > 0 ? _listen.Size.Y + 4 : 0) + 8 + _go.Size.Y;
        _box.MinSize = new float2(width, height) + new float2(2 * _box.Padding, 2 * _box.Padding);
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        void Place(Element child, float x, float y) => child.Arrange(new float2(MathF.Round(x), MathF.Round(y)));

        var boxAt = new float2(Math.Clamp(_anchor.X - _box.Size.X, 4, MathF.Max(4, ScreenSize.X - _box.Size.X - 4)), _anchor.Y);
        _box.Arrange(boxAt);
        var left = boxAt.X + _box.Padding;
        var y = boxAt.Y + _box.Padding;
        Place(_heading, left, y);
        y += _heading.Size.Y + 10;
        var labels = _rows.Max(row => row.Name.Size.X) + 10;
        foreach (var (name, field) in _rows)
        {
            var row = MathF.Max(field.Size.Y, name.Size.Y);
            Place(name, left, y + (row - name.Size.Y) / 2);
            Place(field, left + labels, y + (row - field.Size.Y) / 2);
            y += row + 6;
        }
        y += 4;
        Place(_status, left, y);
        y += _status.Size.Y + 4;
        if (_listen.Text.Length > 0)
        {
            Place(_listen, left, y);
            y += _listen.Size.Y + 4;
        }
        Place(_go, left, y + 8);
    }
}
