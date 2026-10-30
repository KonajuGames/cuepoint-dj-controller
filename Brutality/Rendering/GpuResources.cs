using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;

namespace Brutality.Rendering;

/// <summary>
/// A buffer and the VMA allocation that backs it.
/// </summary>
sealed class GpuBuffer : IDisposable
{
    public VkBuffer Buffer { get; }
    public VmaAllocation Allocation { get; }
    public VmaAllocationInfo Info { get; }
    public ByteSize Size { get; }

    readonly VmaAllocator _allocator;

    GpuBuffer(VmaAllocator allocator, VkBuffer buffer, VmaAllocation allocation, VmaAllocationInfo info, ByteSize size)
    {
        _allocator = allocator;
        Buffer = buffer;
        Allocation = allocation;
        Info = info;
        Size = size;
    }

    public static GpuBuffer Create(
        VmaAllocator allocator,
        ByteSize size,
        VkBufferUsageFlags usage,
        VmaAllocationCreateFlags flags = VmaAllocationCreateFlags.None
    )
    {
        allocator.CreateBuffer(
            new VkBufferCreateInfo { Size = size, Usage = usage },
            new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto, Flags = flags },
            out var buffer,
            out var allocation,
            out var info
        );
        return new GpuBuffer(allocator, buffer, allocation, info, size);
    }

    public void Dispose() => _allocator.DestroyBuffer(Buffer, Allocation);
}

/// <summary>
/// A 2D image, its VMA allocation and a view covering every mip level.
/// </summary>
sealed class GpuImage : IDisposable
{
    public VkImage Image { get; }
    public VkImageView View { get; }
    public VkFormat Format { get; }
    public VkExtent2D Extent { get; }
    public int MipLevels { get; }

    readonly Device _device;
    readonly VmaAllocator _allocator;
    readonly VmaAllocation _allocation;

    public GpuImage(
        GpuContext gpu,
        VkFormat format,
        VkExtent2D extent,
        int mipLevels,
        VkImageUsageFlags usage,
        VkImageAspectFlags aspect
    )
    {
        _device = gpu.Device;
        _allocator = gpu.Allocator;
        Format = format;
        Extent = extent;
        MipLevels = mipLevels;

        _allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = extent.Width, Height = extent.Height, Depth = 1 },
                MipLevels = mipLevels,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = usage,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice },
            out var image,
            out _allocation,
            out _
        );
        Image = image;

        View = _device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = aspect,
                    LevelCount = mipLevels,
                    LayerCount = 1,
                },
            },
            null
        );
    }

    public void Dispose()
    {
        _device.DestroyImageView(View, null);
        _allocator.DestroyImage(Image, _allocation);
    }
}
