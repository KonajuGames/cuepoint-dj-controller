using Brutal.GlfwApi;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace Brutality.Rendering;

/// <summary>
/// The Vulkan objects that live as long as the window: instance, surface, device, queue and the VMA allocator.
/// </summary>
sealed unsafe class GpuContext : IDisposable
{
    const VulkanHelpers.Api API_VERSION = VulkanHelpers.Api.VERSION_1_3;

    public Instance Instance { get; }
    public VkSurfaceKHR Surface { get; }
    public PhysicalDevice PhysicalDevice { get; }
    public DeviceEx Device { get; }
    public Queue Queue { get; }
    public int QueueFamily { get; }
    public VmaAllocator Allocator { get; }
    public string DeviceName { get; }

    readonly VkDebugUtilsMessengerEXT _debugMessenger;

    public GpuContext(GlfwWindow window, bool enableValidation)
    {
        enableValidation &= VulkanHelpers.Validation.IsSupported;

        var extensions = new HashSet<string>();
        VulkanHelpers.AddPlatformRequiredExtensions(extensions);
        var glfwExtensions = Glfw.GetRequiredInstanceExtensions(out var glfwExtensionCount);
        for (var i = 0; i < glfwExtensionCount; i++)
            extensions.Add(Utf8.GetString(glfwExtensions[i]));

        var layers = new HashSet<string>();
        if (enableValidation)
        {
            extensions.Add(VK.EXT_DEBUG_UTILS_EXTENSION_NAME);
            VulkanHelpers.AddValidationLayers(layers);
        }

        Instance = Vulkan.CreateInstance(
            new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo { ApiVersion = (int)API_VERSION },
                Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
                EnabledExtensions = extensions,
                EnabledLayers = layers,
            }
        );

        if (enableValidation)
        {
            var messengerInfo = VulkanHelpers.BuildDefaultDebugMessengerCreateInfo(
                new PfnVkDebugUtilsMessengerCallbackEXT(&VulkanHelpers.DebugUtilsMessengerCallbacks.Default)
            );
            _debugMessenger = Instance.CreateDebugUtilsMessengerEXT(messengerInfo, null);
        }

        if (Glfw.CreateWindowSurface(Instance.VkHandle, window, out var surfaceHandle) != (int)VkResult.Success)
            throw new InvalidOperationException("Failed to create a Vulkan surface for the window");
        Surface = new VkSurfaceKHR((nint)surfaceHandle);

        (PhysicalDevice, QueueFamily) = PickPhysicalDevice();
        DeviceName = VulkanDeviceInfo.GetDeviceName(PhysicalDevice);
        Console.WriteLine($"GPU: {DeviceName}, validation {(enableValidation ? "on" : "off")}");

        var deviceExtensions = new HashSet<string>();
        VulkanHelpers.AddSwapchainExtensions(deviceExtensions);
        VulkanHelpers.AddMandatoryIfSupportedDeviceExtensions(deviceExtensions, PhysicalDevice);

        var priority = 1.0f;
        var vulkan13Features = new VkPhysicalDeviceVulkan13Features { DynamicRendering = true };

        Device = PhysicalDevice.CreateDevice(
            new DeviceEx.CreateInfo
            {
                Extensions = deviceExtensions,
                QueueCreateInfos =
                [
                    new VkDeviceQueueCreateInfo
                    {
                        QueueFamilyIndex = QueueFamily,
                        QueueCount = 1,
                        QueuePriorities = &priority,
                    },
                ],
                Next = &vulkan13Features,
            }
        );

        Queue = Device.GetQueue(QueueFamily, 0);

        Allocator = Device.CreateVmaAllocator(
            new VmaAllocatorCreateInfo
            {
                Instance = Instance,
                PhysicalDevice = PhysicalDevice,
                Device = Device,
                VulkanApiVersion = (uint)API_VERSION,
            }
        );
    }

    /// <summary>
    /// Picks a Vulkan 1.3 device with one queue family that can both draw and present, preferring a discrete GPU.
    /// </summary>
    (PhysicalDevice, int) PickPhysicalDevice()
    {
        (PhysicalDevice Device, int Family, int Score)? best = null;

        foreach (var candidate in Instance.PhysicalDevices)
        {
            var properties = candidate.GetProperties();
            if (properties.ApiVersion < (int)API_VERSION)
                continue;

            var families = new VkQueueFamilyProperties[candidate.GetQueueFamilyProperties_Count()];
            candidate.GetQueueFamilyProperties_Write(families);

            for (var i = 0; i < families.Length; i++)
            {
                if ((families[i].QueueFlags & VkQueueFlags.GraphicsBit) == 0 || !candidate.GetSurfaceSupportKHR(i, Surface))
                    continue;

                var score = properties.DeviceType switch
                {
                    VkPhysicalDeviceType.DiscreteGpu => 2,
                    VkPhysicalDeviceType.IntegratedGpu => 1,
                    _ => 0,
                };
                if (best is null || score > best.Value.Score)
                    best = (candidate, i, score);
                break;
            }
        }

        return best is { } found
            ? (found.Device, found.Family)
            : throw new InvalidOperationException(
                "No Vulkan 1.3 device has a queue family that can draw and present to the window"
            );
    }

    /// <summary>
    /// Records commands with <paramref name="record"/>, submits them and waits for the GPU to finish. For uploads at
    /// load time, not for per-frame work.
    /// </summary>
    public void SubmitAndWait(Action<CommandBuffer> record)
    {
        var pool = Device.CreateCommandPool(
            new VkCommandPoolCreateInfo { QueueFamilyIndex = QueueFamily, Flags = VkCommandPoolCreateFlags.TransientBit },
            null
        );
        var fence = Device.CreateFence(VkFenceCreateFlags.None, null);
        try
        {
            var commandBuffer = Device.AllocateCommandBuffer(
                new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary }
            );
            commandBuffer.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
            record(commandBuffer);
            commandBuffer.End();

            Queue.Submit([], [], [commandBuffer], [], fence);
            Device.WaitForFence(fence, -1);
        }
        finally
        {
            Device.DestroyFence(fence, null);
            Device.DestroyCommandPool(pool, null);
        }
    }

    /// <summary>
    /// Creates a host-visible, persistently mapped buffer holding a copy of <paramref name="data"/>.
    /// </summary>
    public GpuBuffer CreateStagingBuffer<T>(ReadOnlySpan<T> data)
        where T : unmanaged
    {
        var buffer = GpuBuffer.Create(
            Allocator,
            ByteSize.Of(data),
            VkBufferUsageFlags.TransferSrcBit,
            VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit
        );
        data.CopyTo(new Span<T>((void*)buffer.Info.PMappedData, data.Length));
        Allocator.FlushAllocation(buffer.Allocation, ByteSize.Zero, ByteSize.Of(data));
        return buffer;
    }

    /// <summary>
    /// Creates a device-local buffer and fills it with <paramref name="data"/> through a staging buffer.
    /// </summary>
    public GpuBuffer CreateDeviceBuffer<T>(ReadOnlySpan<T> data, VkBufferUsageFlags usage)
        where T : unmanaged
    {
        var size = ByteSize.Of(data);
        using var staging = CreateStagingBuffer(data);
        var buffer = GpuBuffer.Create(Allocator, size, usage | VkBufferUsageFlags.TransferDstBit);
        SubmitAndWait(cmd => cmd.CopyBuffer(staging.Buffer, buffer.Buffer, size));
        return buffer;
    }

    public void Dispose()
    {
        // Reverse creation order: everything made from the device has already been destroyed by its owner.
        Device.WaitIdle();
        Device.DestroyVmaAllocator(Allocator);
        Device.Dispose();
        Instance.DestroySurfaceKHR(Surface, null);
        if (!_debugMessenger.IsNull())
            Instance.DestroyDebugUtilsMessengerEXT(_debugMessenger, null);
        Instance.Dispose();
    }
}
