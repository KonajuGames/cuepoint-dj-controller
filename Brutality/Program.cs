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

    var clock = Stopwatch.StartNew();
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
        hud.Update(time, size);
        renderer.DrawFrame((float)time, hud.Roots);
    }
}

static string FindFont(bool bold) =>
    FontAtlas.FindSystemFont(bold)
    ?? FontAtlas.FindSystemFont()
    ?? throw new FileNotFoundException("No usable system font found; install DejaVu Sans or Liberation Sans");

/// <summary>
/// The on-screen overlay: a stats panel in the top-left and a controls hint in the bottom-right.
/// </summary>
sealed class Hud
{
    static readonly float4 PanelBackground = UiColor.Hex(0x15151C, 0.82f);
    static readonly float4 PanelBorder = UiColor.Hex(0xEB7828, 0.9f);
    static readonly float4 Muted = UiColor.Hex(0x9A9AA8);
    static readonly float4 Accent = UiColor.Hex(0xEB7828);

    public IReadOnlyList<UiElement> Roots { get; }

    readonly Label _resolution;
    readonly Label _frameRate;

    double _sampleStart;
    int _framesSinceSample;

    public Hud(Font body, Font title, string deviceName, string? texturePath)
    {
        var stats = new Panel
        {
            Position = new float2(16, 16),
            Padding = 12,
            Spacing = 4,
            Background = PanelBackground,
            BorderColor = PanelBorder,
            BorderWidth = 1,
            MinSize = new float2(260, 0),
        };
        stats.Add(new Label(title, "Hello BRUTAL"));
        stats.Add(new Label(body, deviceName) { Color = Muted });
        _resolution = AddRow(stats, body, "Resolution");
        _frameRate = AddRow(stats, body, "Frame rate");
        AddRow(stats, body, "Texture", texturePath is null ? "Checkerboard" : Path.GetFileName(texturePath));

        var hint = new Panel
        {
            Anchor = UiAnchor.BottomRight,
            Position = new float2(16, 16),
            Padding = 8,
            Background = PanelBackground,
        };
        hint.Add(new Label(body, "Esc  Quit") { Color = Muted });

        Roots = [stats, hint];
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

    public void Update(double time, int2 framebufferSize)
    {
        _resolution.Text = $"{framebufferSize.X} × {framebufferSize.Y}";

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
