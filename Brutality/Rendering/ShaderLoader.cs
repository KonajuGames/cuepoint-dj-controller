using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Brutality.Rendering;

static unsafe class ShaderLoader
{
    /// <summary>
    /// Compiles the GLSL files in the output's Shaders folder (the stage comes from each file's extension) and wraps
    /// them in a ShaderStages, which destroys the modules when disposed.
    /// </summary>
    public static ShaderStages Load(Device device, string vertexFile, string fragmentFile)
    {
        using var compiler = ShaderC.CreateCompiler();
        using var options = ShaderC.CreateCompilerOptions();

        VkShaderModule Compile(string fileName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Shaders", fileName);
            using var spirv = compiler.CompileFile(path, ShaderC.InferShaderKind(fileName), "main", options);
            return ShaderModuleExtensions.FromSpirv(
                device,
                new ReadOnlySpan<byte>(spirv.GetBytes(), (int)spirv.GetLength()),
                null
            );
        }

        return new ShaderStages(
            device,
            [Presets.ShaderStage.Vertex(Compile(vertexFile)), Presets.ShaderStage.Fragment(Compile(fragmentFile))],
            null
        );
    }
}
