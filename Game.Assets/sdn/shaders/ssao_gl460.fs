#version 460

// Screen-space ambient occlusion (spiral sampling after Alchemy AO, McGuire et al. 2011) reconstructed from the scene depth.

layout(location = 0) in vec2 vs_TexCoord;

layout(std140, binding = 6) uniform source_ps_constants {
    vec4 ps_const[256];
};

out vec4 fragColor;

layout(binding = 0) uniform sampler2D DepthSampler;

#define TanHalfFov      ps_const[0].xy
#define ZNear           ps_const[0].z
#define ZFar            ps_const[0].w
#define Radius          ps_const[1].x
#define Intensity       ps_const[1].y
#define Bias            ps_const[1].z
#define FadeDistance    ps_const[1].w
#define TexelSize       ps_const[2].xy
#define SampleCount     int(ps_const[2].z)
#define DepthFlip       ps_const[2].w

const float PI = 3.14159265;
const float SPIRAL_TURNS = 7.0;

vec2 DepthUV(vec2 uv)
{
    return vec2(uv.x, mix(uv.y, 1.0 - uv.y, DepthFlip));
}

// The engine uses a D3D-style [0, 1] projection while GL maps clip space z from [-1, 1] to the depth range,
// so the stored depth is 0.5 + 0.5 * ndc.z with ndc.z in [0, 1].
float LinearizeDepth(float depth)
{
    float ndcZ = depth * 2.0 - 1.0;
    return (ZFar * ZNear) / (ZFar + ndcZ * (ZNear - ZFar));
}

float RawDepth(vec2 uv)
{
    return texture(DepthSampler, DepthUV(uv)).r;
}

vec3 ViewPosition(vec2 uv, float linearDepth)
{
    vec2 ndc = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    return vec3(ndc * TanHalfFov * linearDepth, linearDepth);
}

vec3 ViewPositionAt(vec2 uv)
{
    return ViewPosition(uv, LinearizeDepth(RawDepth(uv)));
}

// Reconstruct the normal from neighbouring depths, picking the smaller difference on each axis
// so silhouettes don't produce bogus normals.
vec3 ReconstructNormal(vec2 uv, vec3 p)
{
    vec3 pr = ViewPositionAt(uv + vec2(TexelSize.x, 0.0));
    vec3 pl = ViewPositionAt(uv - vec2(TexelSize.x, 0.0));
    vec3 pd = ViewPositionAt(uv + vec2(0.0, TexelSize.y));
    vec3 pu = ViewPositionAt(uv - vec2(0.0, TexelSize.y));

    vec3 dx = abs(pr.z - p.z) < abs(p.z - pl.z) ? pr - p : p - pl;
    vec3 dy = abs(pd.z - p.z) < abs(p.z - pu.z) ? pd - p : p - pu;

    vec3 n = normalize(cross(dy, dx));
    return dot(n, p) > 0.0 ? -n : n;
}

float InterleavedGradientNoise(vec2 pixel)
{
    return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}

void main()
{
    vec2 uv = vs_TexCoord;
    float rawDepth = RawDepth(uv);

    // Sky / nothing drawn
    if (rawDepth >= 0.999999) {
        fragColor = vec4(1.0);
        return;
    }

    float linearDepth = LinearizeDepth(rawDepth);
    if (linearDepth >= FadeDistance) {
        fragColor = vec4(1.0);
        return;
    }

    vec3 p = ViewPosition(uv, linearDepth);
    vec3 n = ReconstructNormal(uv, p);

    // World-space radius projected to screen space at this depth
    vec2 radiusUV = (0.5 * Radius / linearDepth) / TanHalfFov;
    if (max(radiusUV.x, radiusUV.y) < TexelSize.x) {
        fragColor = vec4(1.0);
        return;
    }

    float angle = InterleavedGradientNoise(gl_FragCoord.xy) * 2.0 * PI;
    int count = SampleCount;
    float occlusion = 0.0;

    for (int i = 0; i < count; ++i) {
        float alpha = (float(i) + 0.5) / float(count);
        float theta = alpha * SPIRAL_TURNS * 2.0 * PI + angle;
        vec2 offset = vec2(cos(theta), sin(theta)) * alpha * radiusUV;

        vec2 sampleUV = uv + offset;
        if (any(lessThan(sampleUV, vec2(0.0))) || any(greaterThan(sampleUV, vec2(1.0))))
            continue;

        vec3 q = ViewPositionAt(sampleUV);
        vec3 v = q - p;
        float vv = dot(v, v);

        // Cosine between the normal and the direction to the occluder: unit independent, unlike the
        // original Alchemy estimator which is tuned for metre-scale scenes and vanishes in Source units.
        float cosAngle = dot(v, n) * inversesqrt(max(vv, 1e-4));

        // Range check: fade out samples towards the edge of the hemisphere radius
        float falloff = clamp(1.0 - sqrt(vv) / Radius, 0.0, 1.0);
        occlusion += falloff * max(cosAngle - Bias, 0.0);
    }

    float ao = max(0.0, 1.0 - occlusion * (2.0 * Intensity / float(count)));
    ao = mix(ao, 1.0, smoothstep(FadeDistance * 0.75, FadeDistance, linearDepth));

    fragColor = vec4(ao, ao, ao, 1.0);
}
