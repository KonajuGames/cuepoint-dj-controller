using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Brutality.Rendering;

/// <summary>
/// The swapchain, with a view and a "render finished" semaphore per image. Rebuilt whole whenever the window's
/// framebuffer changes size.
/// </summary>
sealed class Swapchain : IDisposable
{
    public VkSwapchainKHR Handle { get; }
    public VkFormat Format { get; }
    public VkExtent2D Extent { get; }
    public VkImage[] Images { get; }
    public VkImageView[] ImageViews { get; }

    /// <summary>
    /// One per image rather than per frame in flight: presentation can still be reading the semaphore when the
    /// next frame that uses the same slot starts.
    /// </summary>
    public VkSemaphore[] RenderFinished { get; }

    readonly Device _device;

    public Swapchain(GpuContext gpu, int2 framebufferSize, Swapchain? previous)
    {
        _device = gpu.Device;
        var physicalDevice = gpu.PhysicalDevice;
        var capabilities = physicalDevice.GetSurfaceCapabilitiesKHR(gpu.Surface);

        // CurrentExtent is 0xFFFFFFFF (-1 as an int) when the surface lets the swapchain pick its size.
        var extent = capabilities.CurrentExtent;
        if (extent.Width == -1 && extent.Height == -1)
            extent = new VkExtent2D(
                Math.Clamp(framebufferSize.X, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp(framebufferSize.Y, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
            );
        Extent = extent;

        // One more than the minimum so the CPU rarely waits on the driver; a MaxImageCount of 0 means no limit.
        var imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, capabilities.MaxImageCount);

        VkCompositeAlphaFlagsKHR[] alphaModes =
        [
            VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
            VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR,
            VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR,
            VkCompositeAlphaFlagsKHR.InheritBitKHR,
        ];
        var compositeAlpha = alphaModes.First(mode => (capabilities.SupportedCompositeAlpha & mode) != 0);

        var surfaceFormat = PickSurfaceFormat(physicalDevice, gpu.Surface);
        Format = surfaceFormat.Format;

        Handle = _device.CreateSwapchainKHR(
            new VkSwapchainCreateInfoKHR
            {
                Surface = gpu.Surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = VkImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = VkSharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = compositeAlpha,
                PresentMode = VkPresentModeKHR.FifoKHR, // V-sync; the only mode every device must support.
                Clipped = true,
                OldSwapchain = previous?.Handle ?? VkSwapchainKHR.NullHandle,
            },
            null
        );

        Images = new VkImage[_device.GetSwapchainImagesKHR_Count(Handle)];
        _device.GetSwapchainImagesKHR_Write(Handle, Images);

        ImageViews = new VkImageView[Images.Length];
        RenderFinished = new VkSemaphore[Images.Length];
        for (var i = 0; i < Images.Length; i++)
        {
            ImageViews[i] = _device.CreateImageView(
                new VkImageViewCreateInfo
                {
                    Image = Images[i],
                    ViewType = VkImageViewType._2D,
                    Format = Format,
                    SubresourceRange = Subresources.Single(VkImageAspectFlags.ColorBit),
                },
                null
            );
            RenderFinished[i] = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }
    }

    /// <summary>
    /// An sRGB format if the surface has one, so shaders can work in linear colour and the hardware encodes on write.
    /// </summary>
    static VkSurfaceFormatKHR PickSurfaceFormat(PhysicalDevice physicalDevice, VkSurfaceKHR surface)
    {
        var formats = new VkSurfaceFormatKHR[physicalDevice.GetSurfaceFormatsKHR_Count(surface)];
        physicalDevice.GetSurfaceFormatsKHR_Write(surface, formats);

        foreach (var format in formats)
        {
            if (
                format.Format is VkFormat.B8G8R8A8SRGB or VkFormat.R8G8B8A8SRGB
                && format.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR
            )
                return format;
        }

        return formats[0];
    }

    public void Dispose()
    {
        foreach (var semaphore in RenderFinished)
            _device.DestroySemaphore(semaphore, null);
        foreach (var view in ImageViews)
            _device.DestroyImageView(view, null);
        _device.DestroySwapchainKHR(Handle, null);
    }
}
