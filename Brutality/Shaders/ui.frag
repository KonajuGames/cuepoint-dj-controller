#version 450

// Single channel: glyph coverage, or 1 at the atlas's white texel for solid fills.
layout(set = 0, binding = 0) uniform sampler2D atlas;

layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 color;

layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vec4(color.rgb, color.a * texture(atlas, uv).r);
}
