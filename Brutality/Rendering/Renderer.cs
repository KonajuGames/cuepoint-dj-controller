using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.Render.Mesh;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutality.UI;

namespace Brutality.Rendering;

/// <summary>
/// Draws a spinning, textured, lit cube and sphere side by side, with UI on top, using dynamic rendering and keeping
/// two frames in flight.
/// </summary>
sealed unsafe class Renderer : IDisposable
{
    const int FRAMES_IN_FLIGHT = 2;

    struct PushConstants
    {
        public float4x4 ModelViewProjection;
        public float4x4 Model;
    }

    /// <summary>
    /// What one frame in flight owns. The CPU records frame N+1 into the other slot while the GPU draws frame N.
    /// </summary>
    struct Frame
    {
        public CommandBuffer CommandBuffer;
        public VkSemaphore ImageAvailable;
        public VkFence InFlight;
    }

    readonly GpuContext _gpu;
    readonly GlfwWindow _window;
    readonly DeviceEx _device;

    readonly VkFormat _depthFormat;
    Swapchain _swapchain;
    bool _swapchainStale;

    readonly GpuMesh _cube;
    readonly GpuMesh _sphere;
    readonly Texture _texture;
    readonly OverlayRenderer _ui;

    readonly VkDescriptorSetLayout _descriptorSetLayout;
    readonly VkDescriptorPool _descriptorPool;
    readonly VkDescriptorSet _descriptorSet;
    readonly VkPipelineLayout _pipelineLayout;
    readonly VkPipeline _pipeline;

    readonly VkCommandPool _commandPool;
    readonly Frame[] _frames = new Frame[FRAMES_IN_FLIGHT];
    int _frameIndex;

    public Renderer(GpuContext gpu, GlfwWindow window, string? texturePath, FontAtlas fonts)
    {
        _gpu = gpu;
        _window = window;
        _device = gpu.Device;

        _depthFormat = gpu.PhysicalDevice.FindSupportedFormat(
            [VkFormat.D32SFloat, VkFormat.D32SFloatS8UInt, VkFormat.D24UNormS8UInt],
            VkImageTiling.Optimal,
            VkFormatFeatureFlags.DepthStencilAttachmentBit
        );
        _swapchain = new Swapchain(gpu, window.FramebufferSize, _depthFormat, null);

        _cube = GpuMesh.Generate(gpu, mesh => ProcGenMeshLibrary.GenerateCube(mesh, float3.Zero, 1.0f, 1));
        _sphere = GpuMesh.Generate(gpu, mesh => ProcGenMeshLibrary.GenerateSphere(mesh, float3.Zero, 0.6f, 32));
        _texture = texturePath is null ? Texture.CreateCheckerboard(gpu) : Texture.LoadFromFile(gpu, texturePath);

        // One combined image sampler, read by the fragment shader.
        var samplerBinding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.FragmentBit,
        };
        _descriptorSetLayout = _device.CreateDescriptorSetLayout(
            new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &samplerBinding },
            null
        );

        var poolSize = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        _descriptorPool = _device.CreateDescriptorPool(
            new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &poolSize },
            null
        );

        var setLayout = _descriptorSetLayout;
        Span<VkDescriptorSet> sets = stackalloc VkDescriptorSet[1];
        _device.AllocateDescriptorSets(
            new VkDescriptorSetAllocateInfo
            {
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                SetLayouts = &setLayout,
            },
            sets
        );
        _descriptorSet = sets[0];

        var imageInfo = new VkDescriptorImageInfo
        {
            Sampler = _texture.Sampler,
            ImageView = _texture.Image.View,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };
        _device.UpdateDescriptorSets(
            [
                new VkWriteDescriptorSet
                {
                    DstSet = _descriptorSet,
                    DstBinding = 0,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &imageInfo,
                },
            ],
            []
        );

        _pipelineLayout = _device.CreatePipelineLayout(
            [_descriptorSetLayout],
            [
                new VkPushConstantRange
                {
                    StageFlags = VkShaderStageFlags.VertexBit,
                    Size = ByteSize.Of<PushConstants>(),
                },
            ],
            null
        );
        _pipeline = CreatePipeline();
        _ui = new OverlayRenderer(gpu, fonts, _swapchain.Format, _depthFormat, FRAMES_IN_FLIGHT);

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

    VkPipeline CreatePipeline()
    {
        // The pipeline doesn't need the shader modules once it's built.
        using var shaderStages = ShaderLoader.Load(_device, "mesh.vert", "mesh.frag");

        var vertexInput = VertexInput.For<MeshVertex>();

        // Viewport and scissor are dynamic, so a resize doesn't need a new pipeline.
        var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
        var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
        var dynamicState = new VkPipelineDynamicStateCreateInfo
        {
            DynamicStateCount = 2,
            DynamicStates = dynamicStates,
        };

        // ProcGenMeshLibrary winds triangles clockwise seen from outside, the opposite of the Presets' default. The
        // flipped viewport in Record keeps on-screen winding the same as in world space.
        var rasterization = new VkPipelineRasterizationStateCreateInfo
        {
            CullMode = VkCullModeFlags.BackBit,
            FrontFace = VkFrontFace.Clockwise,
            LineWidth = 1,
        };

        var colorFormat = _swapchain.Format;
        var rendering = new VkPipelineRenderingCreateInfo
        {
            ColorAttachmentCount = 1,
            ColorAttachmentFormats = &colorFormat,
            DepthAttachmentFormat = _depthFormat,
        };

        return _device.CreateGraphicsPipeline(
            VkPipelineCache.NullHandle,
            new VkGraphicsPipelineCreateInfo
            {
                Next = &rendering,
                StageCount = shaderStages.Count,
                Stages = shaderStages,
                VertexInputState = vertexInput,
                InputAssemblyState = Presets.InputAssembly.TriangleList,
                ViewportState = &viewportState,
                RasterizationState = &rasterization,
                MultisampleState = Presets.Multisample.None,
                DepthStencilState = Presets.DepthStencil.DepthTestWrite,
                ColorBlendState = Presets.BlendState.BlendNone,
                DynamicState = &dynamicState,
                Layout = _pipelineLayout,
            },
            null
        );
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
        _swapchain = new Swapchain(_gpu, _window.FramebufferSize, _depthFormat, old);
        old.Dispose();
        _swapchainStale = false;
        return true;
    }

    /// <summary>
    /// Records, submits and presents one frame. Call only while the window has a non-zero framebuffer.
    /// </summary>
    public void DrawFrame(float time, Root ui)
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
        Record(commandBuffer, imageIndex, time, ui);
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

    void Record(CommandBuffer cmd, int imageIndex, float time, Root ui)
    {
        var image = _swapchain.Images[imageIndex];
        var depth = _swapchain.Depth;
        var extent = _swapchain.Extent;

        // Previous contents are discarded (Undefined) since both attachments are cleared.
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
        cmd.TransitionImage(
            depth.Image,
            _depthFormat.GetAspectMask(),
            VkImageLayout.Undefined,
            VkImageLayout.DepthAttachmentOptimal,
            VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
            VkAccessFlags.DepthStencilAttachmentWriteBit,
            VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
            VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit
        );

        var colorAttachment = new VkRenderingAttachmentInfo
        {
            ImageView = _swapchain.ImageViews[imageIndex],
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearColorValue { Float32 = new float4(0.02f, 0.02f, 0.03f, 1.0f) },
        };
        var depthAttachment = new VkRenderingAttachmentInfo
        {
            ImageView = depth.View,
            ImageLayout = VkImageLayout.DepthAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.DontCare,
            ClearValue = new VkClearDepthStencilValue { Depth = 1.0f },
        };

        cmd.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D(extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment,
                DepthAttachment = &depthAttachment,
            }
        );

        // A negative height flips Y so +Y is up, as the projection matrix expects.
        cmd.SetViewport(
            0,
            [
                new VkViewport
                {
                    Y = extent.Height,
                    Width = extent.Width,
                    Height = -extent.Height,
                    MaxDepth = 1,
                },
            ]
        );
        cmd.SetScissor(0, [new VkRect2D(extent)]);

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 0, [_descriptorSet], []);

        // Row vectors, as in System.Numerics: transforms compose left to right.
        var view = float4x4.CreateLookAt(new float3(0, 0.8f, 3.2f), float3.Zero, float3.UnitY);
        var projection = float4x4.CreatePerspectiveFieldOfView(
            float.DegreesToRadians(60),
            (float)extent.Width / extent.Height,
            0.1f,
            100f
        );
        var viewProjection = view * projection;

        void DrawMesh(GpuMesh mesh, float4x4 model)
        {
            cmd.PushConstants(
                _pipelineLayout,
                VkShaderStageFlags.VertexBit,
                ByteSize.Zero,
                new PushConstants { ModelViewProjection = model * viewProjection, Model = model }
            );
            mesh.Draw(cmd);
        }

        DrawMesh(
            _cube,
            float4x4.CreateFromYawPitchRoll(time * 0.7f, time * 0.4f, 0) * float4x4.CreateTranslation(-0.9f, 0, 0)
        );
        DrawMesh(_sphere, float4x4.CreateRotationY(time * 0.5f) * float4x4.CreateTranslation(0.9f, 0, 0));

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

        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipelineLayout(_pipelineLayout, null);
        _device.DestroyDescriptorPool(_descriptorPool, null);
        _device.DestroyDescriptorSetLayout(_descriptorSetLayout, null);

        _ui.Dispose();
        _texture.Dispose();
        _sphere.Dispose();
        _cube.Dispose();
        _swapchain.Dispose();
    }
}
