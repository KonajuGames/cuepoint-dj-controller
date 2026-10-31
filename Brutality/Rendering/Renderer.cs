using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutality.UI;

namespace Brutality.Rendering;

/// <summary>
/// Clears the window and draws the UI over it, using dynamic rendering and keeping two frames in flight.
/// </summary>
sealed unsafe class Renderer : IDisposable
{
    const int FRAMES_IN_FLIGHT = 2;

    /// <summary>
    /// What one frame in flight owns. The CPU records frame N+1 into the other slot while the GPU draws frame N.
    /// </summary>
    struct Frame
    {
        public CommandBuffer CommandBuffer;
        public VkSemaphore ImageAvailable;
        public VkFence InFlight;
    }

    static readonly float4 ClearColor = new(0.02f, 0.02f, 0.03f, 1.0f);

    readonly GpuContext _gpu;
    readonly GlfwWindow _window;
    readonly DeviceEx _device;

    Swapchain _swapchain;
    bool _swapchainStale;

    readonly OverlayRenderer _ui;

    readonly VkCommandPool _commandPool;
    readonly Frame[] _frames = new Frame[FRAMES_IN_FLIGHT];
    int _frameIndex;

    public Renderer(GpuContext gpu, GlfwWindow window, FontAtlas fonts)
    {
        _gpu = gpu;
        _window = window;
        _device = gpu.Device;

        _swapchain = new Swapchain(gpu, window.FramebufferSize, null);
        _ui = new OverlayRenderer(gpu, fonts, _swapchain.Format, FRAMES_IN_FLIGHT);

        _commandPool = _device.CreateCommandPool(
            new VkCommandPoolCreateInfo
            {
                QueueFamilyIndex = gpu.QueueFamily,
                Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit,
            },
            null
        );
        for (var i = 0; i < FRAMES_IN_FLIGHT; i++)
            _frames[i] = new Frame
            {
                CommandBuffer = _device.AllocateCommandBuffer(
                    new VkCommandBufferAllocateInfo { CommandPool = _commandPool, Level = VkCommandBufferLevel.Primary }
                ),
                ImageAvailable = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null),
                InFlight = _device.CreateFence(VkFenceCreateFlags.SignaledBit, null),
            };

        // A resize only marks the swapchain stale; it's rebuilt before the next frame is acquired.
        _window.OnFramebufferSize += (_, _) => _swapchainStale = true;
    }

    /// <summary>
    /// Waits for the GPU to finish everything and builds a swapchain at the window's current size. Returns false,
    /// leaving the swapchain stale, while the surface has no area: just after a restore from minimised, Windows can
    /// still report a 0x0 surface even though GLFW reports a framebuffer size.
    /// </summary>
    bool TryRecreateSwapchain()
    {
        var surfaceExtent = _gpu.PhysicalDevice.GetSurfaceCapabilitiesKHR(_gpu.Surface).CurrentExtent;
        if (surfaceExtent.Width == 0 || surfaceExtent.Height == 0)
            return false;

        _device.WaitIdle();
        var old = _swapchain;
        _swapchain = new Swapchain(_gpu, _window.FramebufferSize, old);
        old.Dispose();
        _swapchainStale = false;
        return true;
    }

    /// <summary>
    /// Records, submits and presents one frame. Call only while the window has a non-zero framebuffer.
    /// </summary>
    public void DrawFrame(Root ui)
    {
        if (_swapchainStale && !TryRecreateSwapchain())
            return;

        ref var frame = ref _frames[_frameIndex];
        _device.WaitForFence(frame.InFlight, -1);

        int imageIndex;
        try
        {
            var acquired = _device.AcquireNextImageKHR(
                _swapchain.Handle,
                -1,
                frame.ImageAvailable,
                VkFence.NullHandle,
                out imageIndex
            );
            // Still usable this frame; rebuild before the next one.
            if (acquired == VkResult.SuboptimalKHR)
                _swapchainStale = true;
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
        {
            // Nothing was acquired, so the semaphore is untouched and the fence is still signalled: retry next frame.
            _swapchainStale = true;
            return;
        }

        // Only reset once work is certain to be submitted, or the next wait on this fence would hang.
        _device.ResetFences([frame.InFlight]);

        var commandBuffer = frame.CommandBuffer;
        commandBuffer.Reset();
        commandBuffer.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        Record(commandBuffer, imageIndex, ui);
        commandBuffer.End();

        var renderFinished = _swapchain.RenderFinished[imageIndex];
        _gpu.Queue.Submit(
            [frame.ImageAvailable],
            [VkPipelineStageFlags.ColorAttachmentOutputBit],
            [commandBuffer],
            [renderFinished],
            frame.InFlight
        );

        var swapchainHandle = _swapchain.Handle;
        try
        {
            var presented = _gpu.Queue.PresentKHR(
                new VkPresentInfoKHR
                {
                    WaitSemaphoreCount = 1,
                    WaitSemaphores = &renderFinished,
                    SwapchainCount = 1,
                    Swapchains = &swapchainHandle,
                    ImageIndices = &imageIndex,
                }
            );
            if (presented == VkResult.SuboptimalKHR)
                _swapchainStale = true;
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
        {
            _swapchainStale = true;
        }

        _frameIndex = (_frameIndex + 1) % FRAMES_IN_FLIGHT;
    }

    void Record(CommandBuffer cmd, int imageIndex, Root ui)
    {
        var image = _swapchain.Images[imageIndex];
        var extent = _swapchain.Extent;

        // Previous contents are discarded (Undefined) since the image is cleared.
        cmd.TransitionImage(
            image,
            VkImageAspectFlags.ColorBit,
            VkImageLayout.Undefined,
            VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkAccessFlags.None,
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkAccessFlags.ColorAttachmentWriteBit
        );

        var colorAttachment = new VkRenderingAttachmentInfo
        {
            ImageView = _swapchain.ImageViews[imageIndex],
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearColorValue { Float32 = ClearColor },
        };

        cmd.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D(extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment,
            }
        );

        _ui.Record(cmd, _frameIndex, extent, ui);

        cmd.EndRendering();

        cmd.TransitionImage(
            image,
            VkImageAspectFlags.ColorBit,
            VkImageLayout.ColorAttachmentOptimal,
            VkImageLayout.PresentSrcKHR,
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkAccessFlags.ColorAttachmentWriteBit,
            VkPipelineStageFlags.BottomOfPipeBit,
            VkAccessFlags.None
        );
    }

    public void Dispose()
    {
        _device.WaitIdle();

        foreach (var frame in _frames)
        {
            _device.DestroyFence(frame.InFlight, null);
            _device.DestroySemaphore(frame.ImageAvailable, null);
        }
        _device.DestroyCommandPool(_commandPool, null);

        _ui.Dispose();
        _swapchain.Dispose();
    }
}
