using Brutal.Collections;
using Brutal.Numerics;
using Brutal.Render.Mesh;
using Brutal.VulkanApi;

namespace Brutality.Rendering;

/// <summary>
/// The vertex layout the mesh pipeline expects; VertexInput reads the attribute formats from these fields.
/// </summary>
struct MeshVertex(float3 position, float3 normal, float2 uv)
{
    public float3 Position = position;
    public float3 Normal = normal;
    public float2 Uv = uv;
}

/// <summary>
/// Device-local vertex and index buffers for one indexed triangle mesh.
/// </summary>
sealed class GpuMesh : IDisposable
{
    public GpuBuffer Vertices { get; }
    public GpuBuffer Indices { get; }
    public int IndexCount { get; }

    GpuMesh(GpuBuffer vertices, GpuBuffer indices, int indexCount)
    {
        Vertices = vertices;
        Indices = indices;
        IndexCount = indexCount;
    }

    /// <summary>
    /// Runs one of the ProcGenMeshLibrary generators and uploads the result.
    /// </summary>
    public static GpuMesh Generate(GpuContext gpu, Action<ProcGenMesh> generate)
    {
        using var positions = new NativeStrideList(ByteSize.Of<float3>(), 0);
        using var normals = new NativeStrideList(ByteSize.Of<float3>(), 0);
        using var uvs = new NativeStrideList(ByteSize.Of<float2>(), 0);
        using var indices = new NativeStrideList(ByteSize.Of<int>(), 0);

        var mesh = new ProcGenMesh(positions, indices, normals, uvs);
        generate(mesh);

        // The generator writes separate streams; the pipeline reads one interleaved buffer.
        var positionSpan = positions.ToSpan<float3>();
        var normalSpan = normals.ToSpan<float3>();
        var uvSpan = uvs.ToSpan<float2>();
        var vertices = new MeshVertex[mesh.VertexCount];
        for (var i = 0; i < vertices.Length; i++)
            vertices[i] = new MeshVertex(positionSpan[i], normalSpan[i], uvSpan[i]);

        ReadOnlySpan<int> indexSpan = indices.ToSpan<int>();
        return new GpuMesh(
            gpu.CreateDeviceBuffer<MeshVertex>(vertices, VkBufferUsageFlags.VertexBufferBit),
            gpu.CreateDeviceBuffer(indexSpan, VkBufferUsageFlags.IndexBufferBit),
            indexSpan.Length
        );
    }

    public void Draw(CommandBuffer commandBuffer)
    {
        commandBuffer.BindVertexBuffers(0, [Vertices.Buffer], [new ByteSize64(0)]);
        commandBuffer.BindIndexBuffer(Indices.Buffer, new ByteSize64(0), VkIndexType.UInt32);
        commandBuffer.DrawIndexed(IndexCount, 1, 0, 0, 0);
    }

    public void Dispose()
    {
        Vertices.Dispose();
        Indices.Dispose();
    }
}
