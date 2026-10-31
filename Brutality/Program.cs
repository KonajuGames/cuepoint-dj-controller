using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutality;
using Brutality.Audio;
using Brutality.Input;
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

    // Step 1 of the DJ app: one deck with a fixed track. A song list replaces this in step 3.
    const string DECK_A_TRACK =
        "F:/Music/_CDs/2 Unlimited/Maximum Overdrive/01. 2 Unlimited - Maximum Overdrive (radio edit).mp3";

    using var audio = new AudioEngine();
    using var deckA = audio.CreateDeck("A");
    hud.AddDeck(deckA);
    hud.Log($"Audio decoders: {string.Join(", ", audio.Decoders)}");
    try
    {
        var loading = Stopwatch.StartNew();
        deckA.Load(DECK_A_TRACK);
        hud.Log($"Deck A: loaded {deckA.Artist} - {deckA.Title} in {loading.ElapsedMilliseconds} ms");
    }
    catch (Exception e)
    {
        // A missing or unsupported file leaves the deck empty rather than stopping the app.
        hud.Log($"Deck A: couldn't load {Path.GetFileName(DECK_A_TRACK)}: {e.Message}");
    }

    // App shortcuts go through the UI first, so Escape in the note box only leaves the box, and Space on a focused
    // button presses the button rather than the deck.
    input.UnhandledKey += (key, action, _) =>
    {
        if (action != GlfwKeyAction.Press)
            return;
        if (key == GlfwKey.Escape)
            window.ShouldClose = true;
        else if (key == GlfwKey.Space)
            deckA.TogglePlay();
    };

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
