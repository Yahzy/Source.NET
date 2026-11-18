#version 460

// Depth-aware 4x4 blur of the raw SSAO term, multiplied onto the frame buffer by the blend state.

layout(location = 0) in vec2 vs_TexCoord;

layout(std140, binding = 6) uniform source_ps_constants {
    vec4 ps_const[256];
};

out vec4 fragColor;

layout(binding = 0) uniform sampler2D AOSampler;
layout(binding = 1) uniform sampler2D DepthSampler;

#define ZNear           ps_const[0].z
#define ZFar            ps_const[0].w
#define TexelSize       ps_const[1].xy
#define DebugMode       int(ps_const[1].z)
#define DepthFlip       ps_const[1].w

vec2 DepthUV(vec2 uv)
{
    return vec2(uv.x, mix(uv.y, 1.0 - uv.y, DepthFlip));
}

float LinearDepthAt(vec2 uv)
{
    float ndcZ = texture(DepthSampler, DepthUV(uv)).r * 2.0 - 1.0;
    return (ZFar * ZNear) / (ZFar + ndcZ * (ZNear - ZFar));
}

void main()
{
    vec2 uv = vs_TexCoord;
    float centerDepth = LinearDepthAt(uv);

    if (DebugMode == 2) {
        float d = clamp(centerDepth / 4096.0, 0.0, 1.0);
        fragColor = vec4(d, d, d, 1.0);
        return;
    }

    float sum = 0.0;
    float weightSum = 0.0;

    for (int y = -2; y < 2; ++y) {
        for (int x = -2; x < 2; ++x) {
            vec2 sampleUV = uv + (vec2(x, y) + 0.5) * TexelSize;
            float ao = texture(AOSampler, sampleUV).r;
            float depth = LinearDepthAt(sampleUV);

            // Reject samples across depth discontinuities (relative so it scales with distance)
            float w = 1.0 / (0.001 + abs(depth - centerDepth) / max(centerDepth, 1.0));
            sum += ao * w;
            weightSum += w;
        }
    }

    float ao = weightSum > 0.0 ? sum / weightSum : 1.0;
    fragColor = vec4(ao, ao, ao, 1.0);
}
