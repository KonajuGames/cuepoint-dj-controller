using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutality;
using Brutality.Rendering;
using Brutality.UI;

// Usage: Brutality [texture file]. Any format TextureLoader reads (PNG, JPG, KTX2, DDS, ...); defaults to a checkerboard.

WindowsShell.AttachParentConsole();

try
{
    if (!Glfw.Init())
    {
        var errorCode = Glfw.GetError(out var errorMessage);
        throw new InvalidOperationException($"Failed to initialize GLFW: {errorCode}, {errorMessage}");
    }

    // GLFW can't find Brutal's Vulkan loader by itself, so touching Vulkan first loads it.
    _ = Vulkan.GetInstanceProcAddr;

    try
    {
        Run(args.FirstOrDefault());
    }
    finally
    {
        Glfw.Terminate();
    }
}
catch (Exception e)
{
    // A windowed app has nowhere to print an unhandled exception, so it would otherwise just vanish.
    WindowsShell.ShowFatalError(e);
    return 1;
}

return 0;

static void Run(string? texturePath)
{
    Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);

    // Declared first so it's disposed last, after the GPU objects that draw to it.
    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Hello BRUTAL", Size = new int2(1280, 720) });

    window.OnKey += (handle, key, _, _, _) =>
    {
        if (key == GlfwKey.Escape)
            handle.ShouldClose = true;
    };

#if DEBUG
    const bool ENABLE_VALIDATION = true;
#else
    const bool ENABLE_VALIDATION = false;
#endif

    var fonts = new FontAtlas();
    var bodyFont = fonts.Add(FindFont(bold: false), 16);
    var titleFont = fonts.Add(FindFont(bold: true), 22);

    using var gpu = new GpuContext(window, ENABLE_VALIDATION);
    using var renderer = new Renderer(gpu, window, texturePath, fonts);

    var hud = new Hud(bodyFont, titleFont, gpu.DeviceName, texturePath);
    using var input = new GlfwUiInput(window, hud.Ui);

    var clock = Stopwatch.StartNew();
    var lastTime = 0.0;
    while (!window.ShouldClose)
    {
        Glfw.PollEvents();

        // Minimised: there's no framebuffer to draw to, so sleep until something happens.
        var size = window.FramebufferSize;
        if (size.X == 0 || size.Y == 0)
        {
            Glfw.WaitEvents();
            continue;
        }

        var time = clock.Elapsed.TotalSeconds;
        hud.Update(time, time - lastTime, size);
        lastTime = time;

        hud.Ui.Layout(new float2(size.X, size.Y));
        input.UpdateCursor();
        renderer.DrawFrame((float)hud.SceneTime, hud.Ui);
    }
}

static string FindFont(bool bold) =>
    FontAtlas.FindSystemFont(bold)
    ?? FontAtlas.FindSystemFont()
    ?? throw new FileNotFoundException("No usable system font found; install DejaVu Sans or Liberation Sans");

/// <summary>
/// The on-screen overlay: a stats panel with playback buttons in the top-left, a scrolling event log in the
/// top-right and a controls hint in the bottom-right. It also owns the scene's clock, which the buttons pause and
/// reset.
/// </summary>
sealed class Hud
{
    static readonly float4 PanelBackground = UiColor.Hex(0x15151C, 0.82f);
    static readonly float4 PanelBorder = UiColor.Hex(0xEB7828, 0.9f);
    static readonly float4 Muted = UiColor.Hex(0x9A9AA8);
    static readonly float4 Accent = UiColor.Hex(0xEB7828);

    public UiRoot Ui { get; } = new();

    /// <summary>
    /// Seconds of animation shown so far; stands still while paused.
    /// </summary>
    public double SceneTime { get; private set; }

    readonly Label _resolution;
    readonly Label _frameRate;
    readonly Button _pause;
    readonly Button _reset;
    readonly ScrollPanel _log;
    readonly Font _body;
    bool _paused;
    int2 _lastFramebufferSize;

    double _sampleStart;
    int _framesSinceSample;

    public Hud(Font body, Font title, string deviceName, string? texturePath)
    {
        _body = body;

        var stats = Ui.Add(
            new Panel
            {
                Position = new float2(16, 16),
                Padding = 12,
                Spacing = 4,
                Background = PanelBackground,
                BorderColor = PanelBorder,
                BorderWidth = 1,
                MinSize = new float2(260, 0),
            }
        );
        stats.Add(new Label(title, "Hello BRUTAL"));
        stats.Add(new Label(body, deviceName) { Color = Muted });
        _resolution = AddRow(stats, body, "Resolution");
        _frameRate = AddRow(stats, body, "Frame rate");
        AddRow(stats, body, "Texture", texturePath is null ? "Checkerboard" : Path.GetFileName(texturePath));

        var buttons = stats.Add(new Panel { Direction = UiDirection.Horizontal, Spacing = 8 });
        _pause = buttons.Add(new Button(body, "Pause") { MinSize = new float2(84, 0) });
        _reset = buttons.Add(new Button(body, "Reset") { MinSize = new float2(84, 0) });
        _pause.Clicked += _ =>
        {
            _paused = !_paused;
            Log(_paused ? $"Paused at {SceneTime:F1} s" : "Resumed");
        };
        _reset.Clicked += _ =>
        {
            SceneTime = 0;
            Log("Reset the scene clock");
        };

        var logPanel = Ui.Add(
            new Panel
            {
                Anchor = UiAnchor.TopRight,
                Position = new float2(16, 16),
                Padding = 12,
                Spacing = 6,
                Background = PanelBackground,
                BorderColor = PanelBorder,
                BorderWidth = 1,
            }
        );
        logPanel.Add(new Label(title, "Event log"));
        _log = logPanel.Add(
            new ScrollPanel
            {
                MaxHeight = 150,
                MinSize = new float2(240, 0),
                Spacing = 2,
                StickToEnd = true,
            }
        );
        Log($"GPU: {deviceName}");
        Log($"Texture: {texturePath ?? "generated checkerboard"}");
        Log("Scroll here with the mouse wheel");

        var hint = Ui.Add(
            new Panel
            {
                Anchor = UiAnchor.BottomRight,
                Position = new float2(16, 16),
                Padding = 8,
                Background = PanelBackground,
            }
        );
        hint.Add(new Label(body, "Esc  Quit") { Color = Muted });
    }

    /// <summary>
    /// Adds a timestamped line to the event log, dropping the oldest past a limit.
    /// </summary>
    public void Log(string message)
    {
        const int MAX_ENTRIES = 200;
        if (_log.Children.Count >= MAX_ENTRIES)
            _log.Children.RemoveAt(0);
        _log.Add(new Label(_body, $"{DateTime.Now:HH:mm:ss}  {message}") { Color = Muted });
    }

    /// <summary>
    /// Adds a "name  value" line to <paramref name="parent"/> and returns the value's label, in the accent colour.
    /// </summary>
    static Label AddRow(Panel parent, Font font, string name, string value = "")
    {
        var row = parent.Add(new Panel { Direction = UiDirection.Horizontal, Spacing = 8 });
        row.Add(new Label(font, name) { Color = Muted });
        return row.Add(new Label(font, value) { Color = Accent });
    }

    public void Update(double time, double deltaTime, int2 framebufferSize)
    {
        if (!_paused)
            SceneTime += deltaTime;
        _pause.Text = _paused ? "Resume" : "Pause";
        _reset.Enabled = SceneTime > 0;

        _resolution.Text = $"{framebufferSize.X} × {framebufferSize.Y}";
        if (framebufferSize != _lastFramebufferSize)
        {
            Log($"Framebuffer is {framebufferSize.X} × {framebufferSize.Y}");
            _lastFramebufferSize = framebufferSize;
        }

        // Averaged over half a second, so the number is readable.
        _framesSinceSample++;
        var elapsed = time - _sampleStart;
        if (elapsed >= 0.5)
        {
            var fps = _framesSinceSample / elapsed;
            _frameRate.Text = $"{fps:F0} fps  ({1000 / fps:F2} ms)";
            _sampleStart = time;
            _framesSinceSample = 0;
        }
    }
}
