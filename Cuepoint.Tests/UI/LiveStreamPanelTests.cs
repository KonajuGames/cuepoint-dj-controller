using Cuepoint.Streaming;
using Cuepoint.UI;

namespace Cuepoint.Tests.UI;

/// <summary>
/// The live streaming panel's settings, built with a system font as the app builds it.
/// </summary>
public class LiveStreamPanelTests
{
    static LiveStreamPanel Make(StreamServer server)
    {
        var atlas = new FontAtlas();
        var font = atlas.Add(FontAtlas.FindSystemFont() ?? throw new InvalidOperationException("No system font"), 14);
        var root = new Root();
        return root.Add(new LiveStreamPanel(root, server, font, font, font));
    }

    static readonly StreamServer Kept = new("stream.example.com", 8000, "live", "hackme", "Friday mix", 128);

    [Fact]
    public void Going_live_takes_what_s_typed_trimmed_and_a_mount_point_without_its_slash()
    {
        var panel = Make(Kept);
        StreamServer? started = null;
        panel.GoLive += server => started = server;
        var (host, port, mount, _, name) = panel.Fields;
        (host.Text, port.Text, mount.Text, name.Text) = (" radio.example.org ", "8010", "/house", " ");

        panel.GoButton.RaiseClicked();

        Assert.Equal(new StreamServer("radio.example.org", 8010, "house", "hackme", "Cuepoint", 128), started);
        Assert.Equal("/house", started!.MountPath);
        Assert.Equal("http://radio.example.org:8010/house", started.ListenAt);
    }

    [Theory]
    [InlineData("", "8000", "live", "hackme", "server's address")]
    [InlineData("example.com", "80000", "live", "hackme", "port")]
    [InlineData("example.com", "8000", "/", "hackme", "mount point")]
    [InlineData("example.com", "8000", "live", "", "password")]
    public void Settings_that_won_t_do_say_why_rather_than_going_live(string hostText, string portText, string mountText, string password, string problem)
    {
        var panel = Make(Kept);
        var started = false;
        panel.GoLive += _ => started = true;
        var (host, port, mount, secret, _) = panel.Fields;
        (host.Text, port.Text, mount.Text, secret.Text) = (hostText, portText, mountText, password);

        panel.GoButton.RaiseClicked();
        panel.Refresh();

        Assert.False(started);
        Assert.Contains(problem, panel.StatusText);
    }

    [Fact]
    public void Why_a_stream_stopped_is_said_until_the_settings_change()
    {
        var panel = Make(Kept);
        panel.Stopped("The server turned down the password");
        panel.Refresh();
        Assert.Equal("The server turned down the password", panel.StatusText);

        // Typed, as it would be.
        panel.Fields.Password.TextInput(new System.Text.Rune('x'));
        panel.Refresh();
        Assert.DoesNotContain("password", panel.StatusText);
    }

    [Fact]
    public void While_live_the_button_stops_it()
    {
        var panel = Make(Kept);
        var stops = 0;
        panel.StopRequested += () => stops++;
        // Nowhere to connect to: it only needs to be there.
        using var stream = new LiveStream(Kept with { Host = "127.0.0.1", Port = 1 });
        panel.Stream = stream;
        panel.Refresh();

        Assert.Equal("STOP", panel.GoButton.Text);
        panel.GoButton.RaiseClicked();
        Assert.Equal(1, stops);
        Assert.True(panel.Fields.Password.Secret);
    }
}
