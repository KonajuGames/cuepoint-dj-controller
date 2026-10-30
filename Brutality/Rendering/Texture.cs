using System.Runtime.InteropServices;
using Brutal.Numerics;
using Brutal.TextureApi;
using Brutal.TextureApi.Abstractions;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using StbLoader = Brutal.TextureApi.Stb.Loader;

namespace Brutality.Rendering;

/// <summary>
/// A sampled 2D texture on the GPU, loaded from any format TextureLoader supports (PNG, JPG, KTX2, DDS, ...).
/// </summary>
sealed class Texture : IDisposable
{
    public GpuImage Image { get; }
    public VkSampler Sampler { get; }

    readonly Device _device;

    Texture(GpuContext gpu, GpuImage image)
    {
        _device = gpu.Device;
        Image = image;

        var samplerInfo = Presets.Sampler.SamplerLinearRepeat;
        samplerInfo.MaxLod = image.MipLevels;
        Sampler = _device.CreateSampler(samplerInfo, null);
    }

    public static Texture LoadFromFile(GpuContext gpu, string path)
    {
        // Three-channel formats are rarely sampleable, so stb's loaders always expand to RGBA.
        var texture = TextureLoader.LoadFromFile(path, StbLoader.LoadSettings.ForceRgba8);
        try
        {
            return Upload(gpu, texture);
        }
        finally
        {
            TextureLoader.Unload(texture);
        }
    }

    /// <summary>
    /// A checkerboard, for when no texture file is given.
    /// </summary>
    public static Texture CreateCheckerboard(GpuContext gpu, int size = 256, int squares = 8)
    {
        var texture = GenericTexture.Defaults.RGBA8UNorm(new int2(size, size));
        try
        {
            var texels = MemoryMarshal.Cast<byte, byte4>(texture.Data);
            var square = size / squares;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                texels[y * size + x] =
                    (x / square + y / square) % 2 == 0 ? new byte4(235, 120, 40, 255) : new byte4(30, 30, 36, 255);

            return Upload(gpu, texture);
        }
        finally
        {
            texture.Destroy();
        }
    }

    static Texture Upload(GpuContext gpu, ITexture texture)
    {
        if (!texture.Is2d || texture.LayerCount != 1 || texture.FaceCount != 1)
            throw new NotSupportedException("Only single-layer 2D textures are supported");

        // TextureFormat mirrors VkFormat's values. 8-bit RGBA from an image file is colour data, so it's read as sRGB
        // and the GPU linearises it when sampling.
        var format = (VkFormat)(int)texture.Format;
        if (format == VkFormat.R8G8B8A8UNorm)
            format = VkFormat.R8G8B8A8SRGB;

        var extent = new VkExtent2D(texture.Width, texture.Height);
        var formatFeatures = gpu.PhysicalDevice.GetFormatProperties(format).OptimalTilingFeatures;
        if ((formatFeatures & VkFormatFeatureFlags.SampledImageBit) == 0)
            throw new NotSupportedException($"This GPU can't sample {format} textures");

        // Files with their own mips keep them; otherwise the GPU builds a full chain with blits, if it can.
        var generateMips =
            texture.LevelCount == 1 && (formatFeatures & VkFormatFeatureFlags.SampledImageFilterLinearBit) != 0;
        var mipLevels = generateMips ? VulkanHelpers.MaxMipLevels(extent.Width, extent.Height) : texture.LevelCount;

        var image = new GpuImage(
            gpu,
            format,
            extent,
            mipLevels,
            VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit,
            VkImageAspectFlags.ColorBit
        );

        // Pack every level into one staging buffer, back to back, and copy each into its mip.
        var regions = new VkBufferImageCopy[texture.LevelCount];
        var staged = new byte[Enumerable.Range(0, texture.LevelCount).Sum(l => texture.ImageData(l, 0, 0).Length)];
        var offset = 0;
        for (var level = 0; level < texture.LevelCount; level++)
        {
            var data = texture.ImageData(level, 0, 0);
            data.CopyTo(staged.AsSpan(offset));
            regions[level] = new VkBufferImageCopy
            {
                BufferOffset = new ByteSize64((ulong)offset),
                ImageSubresource = Subresources.Single(VkImageAspectFlags.ColorBit).ToLayers(level),
                ImageExtent = new VkExtent3D
                {
                    Width = Math.Max(1, extent.Width >> level),
                    Height = Math.Max(1, extent.Height >> level),
                    Depth = 1,
                },
            };
            offset += data.Length;
        }

        using var staging = gpu.CreateStagingBuffer<byte>(staged);

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
                VkAccessFlags.TransferWriteBit,
                levelCount: mipLevels
            );
            cmd.CopyBufferToImage(staging.Buffer, image.Image, VkImageLayout.TransferDstOptimal, regions);

            if (generateMips)
                GenerateMips(cmd, image);
            else
                cmd.TransitionImage(
                    image.Image,
                    VkImageAspectFlags.ColorBit,
                    VkImageLayout.TransferDstOptimal,
                    VkImageLayout.ShaderReadOnlyOptimal,
                    VkPipelineStageFlags.TransferBit,
                    VkAccessFlags.TransferWriteBit,
                    VkPipelineStageFlags.FragmentShaderBit,
                    VkAccessFlags.ShaderReadBit,
                    levelCount: mipLevels
                );
        });

        return new Texture(gpu, image);
    }

    /// <summary>
    /// Fills mips 1..N by halving the level above each one. Expects every level in TransferDstOptimal with mip 0
    /// written, and leaves every level in ShaderReadOnlyOptimal.
    /// </summary>
    static void GenerateMips(CommandBuffer cmd, GpuImage image)
    {
        var width = image.Extent.Width;
        var height = image.Extent.Height;

        for (var level = 1; level < image.MipLevels; level++)
        {
            // The previous level is finished: make it the blit source.
            cmd.TransitionImage(
                image.Image,
                VkImageAspectFlags.ColorBit,
                VkImageLayout.TransferDstOptimal,
                VkImageLayout.TransferSrcOptimal,
                VkPipelineStageFlags.TransferBit,
                VkAccessFlags.TransferWriteBit,
                VkPipelineStageFlags.TransferBit,
                VkAccessFlags.TransferReadBit,
                baseMip: level - 1
            );

            var nextWidth = Math.Max(1, width / 2);
            var nextHeight = Math.Max(1, height / 2);
            var blit = new VkImageBlit
            {
                SrcSubresource = Subresources.Single(VkImageAspectFlags.ColorBit).ToLayers(level - 1),
                DstSubresource = Subresources.Single(VkImageAspectFlags.ColorBit).ToLayers(level),
            };
            blit.SrcOffsets[1] = new VkOffset3D(width, height, 1);
            blit.DstOffsets[1] = new VkOffset3D(nextWidth, nextHeight, 1);

            cmd.BlitImage(
                image.Image,
                VkImageLayout.TransferSrcOptimal,
                image.Image,
                VkImageLayout.TransferDstOptimal,
                [blit],
                VkFilter.Linear
            );

            // Done reading from it.
            cmd.TransitionImage(
                image.Image,
                VkImageAspectFlags.ColorBit,
                VkImageLayout.TransferSrcOptimal,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags.TransferBit,
                VkAccessFlags.TransferReadBit,
                VkPipelineStageFlags.FragmentShaderBit,
                VkAccessFlags.ShaderReadBit,
                baseMip: level - 1
            );

            (width, height) = (nextWidth, nextHeight);
        }

        // The last level was only ever written to.
        cmd.TransitionImage(
            image.Image,
            VkImageAspectFlags.ColorBit,
            VkImageLayout.TransferDstOptimal,
            VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags.TransferBit,
            VkAccessFlags.TransferWriteBit,
            VkPipelineStageFlags.FragmentShaderBit,
            VkAccessFlags.ShaderReadBit,
            baseMip: image.MipLevels - 1
        );
    }

    public void Dispose()
    {
        _device.DestroySampler(Sampler, null);
        Image.Dispose();
    }
}
