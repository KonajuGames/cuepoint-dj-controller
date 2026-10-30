using Brutal.VulkanApi;

namespace Brutality.Rendering;

static class Barriers
{
    /// <summary>
    /// Moves mip levels <paramref name="baseMip"/> to <paramref name="baseMip"/> + <paramref name="levelCount"/> of
    /// an image from one layout to another, ordering the access on either side.
    /// </summary>
    public static void TransitionImage(
        this CommandBuffer commandBuffer,
        VkImage image,
        VkImageAspectFlags aspect,
        VkImageLayout from,
        VkImageLayout to,
        VkPipelineStageFlags srcStage,
        VkAccessFlags srcAccess,
        VkPipelineStageFlags dstStage,
        VkAccessFlags dstAccess,
        int baseMip = 0,
        int levelCount = 1
    )
    {
        commandBuffer.PipelineBarrier(
            srcStage,
            dstStage,
            VkDependencyFlags.None,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = srcAccess,
                    DstAccessMask = dstAccess,
                    OldLayout = from,
                    NewLayout = to,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image,
                    SubresourceRange = new VkImageSubresourceRange
                    {
                        AspectMask = aspect,
                        BaseMipLevel = baseMip,
                        LevelCount = levelCount,
                        LayerCount = 1,
                    },
                },
            ]
        );
    }
}
