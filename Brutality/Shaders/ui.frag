#version 450

// RGBA: white with glyph coverage as alpha, white at the texel for solid fills, or an image's own colours.
layout(set = 0, binding = 0) uniform sampler2D atlas;

layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 color;

layout(location = 0) out vec4 outColor;

void main()
{
    outColor = color * texture(atlas, uv);
}
