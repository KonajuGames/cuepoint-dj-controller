#version 450

// Matches Renderer.PushConstants: 128 bytes, the minimum every device guarantees.
layout(push_constant) uniform PushConstants
{
    mat4 modelViewProjection;
    mat4 model;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec3 normal;
layout(location = 1) out vec2 uv;

void main()
{
    gl_Position = pc.modelViewProjection * vec4(inPosition, 1.0);
    normal = mat3(pc.model) * inNormal;
    uv = inUv;
}
