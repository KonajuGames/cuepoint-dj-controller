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

    // Fixed tracks until the song list (step 3): an MP3 on A, and an .m4a on B, which goes through Media Foundation.
    const string DECK_A_TRACK =
        "F:/Music/_CDs/2 Unlimited/Maximum Overdrive/01. 2 Unlimited - Maximum Overdrive (radio edit).mp3";
    const string DECK_B_TRACK = "F:/Music/5 Seconds of Summer/Teeth - Single/01 Teeth.m4a";

    using var audio = new AudioEngine();
    using var deckA = audio.CreateDeck("A");
    using var deckB = audio.CreateDeck("B");
    hud.AddDeck(deckA);
    hud.AddDeck(deckB);
    hud.Log($"Audio decoders: {string.Join(", ", audio.Decoders)}");
    LoadDeck(deckA, DECK_A_TRACK);
    LoadDeck(deckB, DECK_B_TRACK);

    void LoadDeck(Deck deck, string path)
    {
        try
        {
            var loading = Stopwatch.StartNew();
            deck.Load(path);
            hud.Log($"Deck {deck.Name}: loaded {deck.Artist} - {deck.Title} in {loading.ElapsedMilliseconds} ms");
        }
        catch (Exception e)
        {
            // A missing or unsupported file leaves the deck empty rather than stopping the app.
            hud.Log($"Deck {deck.Name}: couldn't load {Path.GetFileName(path)}: {e.Message}");
        }
    }

    // App shortcuts go through the UI first, so Escape in the note box only leaves the box, and typing there
    // doesn't work the decks. Cue acts on press and release; held keys' auto-repeat is ignored.
    input.UnhandledKey += (key, action, _) =>
    {
        if (action == GlfwKeyAction.Repeat)
            return;
        var pressed = action == GlfwKeyAction.Press;

        switch (key)
        {
            case GlfwKey.Escape when pressed:
                window.ShouldClose = true;
                break;
            case GlfwKey.Z:
                if (pressed)
                    deckA.CueDown();
                else
                    deckA.CueUp();
                break;
            case GlfwKey.X when pressed:
                deckA.TogglePlay();
                break;
            case GlfwKey.N:
                if (pressed)
                    deckB.CueDown();
                else
                    deckB.CueUp();
                break;
            case GlfwKey.M when pressed:
                deckB.TogglePlay();
                break;
        }
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
