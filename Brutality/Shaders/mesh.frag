#version 450

layout(set = 0, binding = 0) uniform sampler2D albedo;

layout(location = 0) in vec3 normal;
layout(location = 1) in vec2 uv;

layout(location = 0) out vec4 outColor;

const vec3 LIGHT_DIRECTION = normalize(vec3(0.4, 0.8, 0.6));
const float AMBIENT = 0.25;

void main()
{
    float diffuse = max(dot(normalize(normal), LIGHT_DIRECTION), 0.0);
    vec3 color = texture(albedo, uv).rgb;
    outColor = vec4(color * (AMBIENT + (1.0 - AMBIENT) * diffuse), 1.0);
}
