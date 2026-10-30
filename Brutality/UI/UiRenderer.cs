using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Brutality.Rendering;

namespace Brutality.UI;

/// <summary>
/// Draws a <see cref="UiRoot"/> over the scene: one alpha-blended pipeline for both text and solid
/// rectangles, reading the font atlas, with vertices rebuilt every frame.
/// </summary>
sealed unsafe class UiRenderer : IDisposable
{
    struct PushConstants
    {
        public float2 ScreenSize;
    }

    /// <summary>
    /// Per frame in flight, since the GPU may still be reading the previous frame's vertices.
    /// </summary>
    sealed class FrameVertices
    {
        public GpuBuffer? Buffer;
        public int Capacity;
    }

    readonly GpuContext _gpu;
    readonly DeviceEx _device;
    readonly UiBatch _batch;

    readonly GpuImage _atlasImage;
    readonly VkSampler _sampler;
    readonly VkDescriptorSetLayout _descriptorSetLayout;
    readonly VkDescriptorPool _descriptorPool;
    readonly VkDescriptorSet _descriptorSet;
    readonly VkPipelineLayout _pipelineLayout;
    readonly VkPipeline _pipeline;

    readonly FrameVertices[] _frames;

    /// <summary>
    /// Builds <paramref name="atlas"/> if it isn't already; add every font before this.
    /// </summary>
    public UiRenderer(GpuContext gpu, FontAtlas atlas, VkFormat colorFormat, VkFormat depthFormat, int framesInFlight)
    {
        _gpu = gpu;
        _device = gpu.Device;

        atlas.Build();
        _batch = new UiBatch(atlas);
        _atlasImage = UploadAtlas(gpu, atlas);
        _sampler = _device.CreateSampler(Presets.Sampler.SamplerLinearClamped, null);

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
            Sampler = _sampler,
            ImageView = _atlasImage.View,
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
        _pipeline = CreatePipeline(colorFormat, depthFormat);

        _frames = new FrameVertices[framesInFlight];
        for (var i = 0; i < framesInFlight; i++)
            _frames[i] = new FrameVertices();
    }

    static GpuImage UploadAtlas(GpuContext gpu, FontAtlas atlas)
    {
        var image = new GpuImage(
            gpu,
            VkFormat.R8UNorm,
            new VkExtent2D(atlas.Width, atlas.Height),
            1,
            VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
            VkImageAspectFlags.ColorBit
        );

        using var staging = gpu.CreateStagingBuffer<byte>(atlas.Pixels);
        gpu.SubmitAndWait(cmd =>
        {
            cmd.TransitionImage(
                image.Image,
                VkImageAspectFlags.ColorBit,
                VkImageLayout.Undefined,
                VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags.TopOfPipeBit,
                VkAccessFlags.None,
                VkPipelineStageFlags.TransferBit,
                VkAccessFlags.TransferWriteBit
            );
            cmd.CopyBufferToImage(
                staging.Buffer,
                image.Image,
                VkImageLayout.TransferDstOptimal,
                [
                    new VkBufferImageCopy
                    {
                        ImageSubresource = Subresources.Single(VkImageAspectFlags.ColorBit).ToLayers(),
                        ImageExtent = new VkExtent3D
                        {
                            Width = atlas.Width,
                            Height = atlas.Height,
                            Depth = 1,
                        },
                    },
                ]
            );
            cmd.TransitionImage(
                image.Image,
                VkImageAspectFlags.ColorBit,
                VkImageLayout.TransferDstOptimal,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags.TransferBit,
                VkAccessFlags.TransferWriteBit,
                VkPipelineStageFlags.FragmentShaderBit,
                VkAccessFlags.ShaderReadBit
            );
        });

        return image;
    }

    VkPipeline CreatePipeline(VkFormat colorFormat, VkFormat depthFormat)
    {
        using var shaderStages = ShaderLoader.Load(_device, "ui.vert", "ui.frag");
        var vertexInput = VertexInput.For<UiVertex>();

        var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
        var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
        var dynamicState = new VkPipelineDynamicStateCreateInfo
        {
            DynamicStateCount = 2,
            DynamicStates = dynamicStates,
        };

        // Drawn inside the scene's rendering pass, so the formats must match it even though depth isn't used.
        var rendering = new VkPipelineRenderingCreateInfo
        {
            ColorAttachmentCount = 1,
            ColorAttachmentFormats = &colorFormat,
            DepthAttachmentFormat = depthFormat,
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
                RasterizationState = Presets.Rasterization.Fill.CullNone,
                MultisampleState = Presets.Multisample.None,
                DepthStencilState = Presets.DepthStencil.NoDepthTest,
                ColorBlendState = Presets.BlendState.BlendColorAlpha,
                DynamicState = &dynamicState,
                Layout = _pipelineLayout,
            },
            null
        );
    }

    /// <summary>
    /// Draws <paramref name="ui"/>, which must already be laid out, into the current rendering pass. The caller must
    /// have waited for <paramref name="frameIndex"/>'s previous use to finish on the GPU.
    /// </summary>
    public void Record(CommandBuffer cmd, int frameIndex, VkExtent2D extent, UiRoot ui)
    {
        var screen = new float2(extent.Width, extent.Height);

        _batch.Clear();
        ui.Draw(_batch);

        var vertices = _batch.Vertices;
        var vertexCount = vertices.Length;
        if (vertexCount == 0)
            return;

        var frame = _frames[frameIndex];
        if (frame.Buffer is null || frame.Capacity < vertexCount)
        {
            // Safe to free: this frame slot's last submission has finished.
            frame.Buffer?.Dispose();
            frame.Capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(vertexCount, 1024));
            frame.Buffer = GpuBuffer.Create(
                _gpu.Allocator,
                ByteSize.Of<UiVertex>() * frame.Capacity,
                VkBufferUsageFlags.VertexBufferBit,
                VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit
            );
        }

        vertices.CopyTo(new Span<UiVertex>((void*)frame.Buffer.Info.PMappedData, vertexCount));
        _gpu.Allocator.FlushAllocation(frame.Buffer.Allocation, ByteSize.Zero, ByteSize.Of<UiVertex>() * vertexCount);

        // The scene flips Y in its viewport; UI coordinates already point down, so this one doesn't.
        cmd.SetViewport(
            0,
            [
                new VkViewport
                {
                    Width = extent.Width,
                    Height = extent.Height,
                    MaxDepth = 1,
                },
            ]
        );
        cmd.SetScissor(0, [new VkRect2D(extent)]);

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 0, [_descriptorSet], []);
        cmd.PushConstants(
            _pipelineLayout,
            VkShaderStageFlags.VertexBit,
            ByteSize.Zero,
            new PushConstants { ScreenSize = screen }
        );
        cmd.BindVertexBuffers(0, [frame.Buffer.Buffer], [new ByteSize64(0)]);
        cmd.Draw(vertexCount, 1, 0, 0);
    }

    public void Dispose()
    {
        foreach (var frame in _frames)
            frame.Buffer?.Dispose();

        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipelineLayout(_pipelineLayout, null);
        _device.DestroyDescriptorPool(_descriptorPool, null);
        _device.DestroyDescriptorSetLayout(_descriptorSetLayout, null);
        _device.DestroySampler(_sampler, null);
        _atlasImage.Dispose();
    }
}
