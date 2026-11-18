#ifndef COMMON_PBR_GL460_FS
#define COMMON_PBR_GL460_FS

// Physically Based Rendering helpers for brushes and models.
// Ported from thexa4/source-pbr (Source SDK 2013, Source 1 SDK license).

const float PI = 3.141592;
const float EPSILON = 0.00001;

// Schlick's approximation of the Fresnel factor
vec3 fresnelSchlick(vec3 F0, float cosTheta)
{
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

// Schlick's approximation of the Fresnel factor with account for roughness
vec3 fresnelSchlickRoughness(vec3 F0, float cosTheta, float roughness)
{
    return F0 + max(vec3(0.0), vec3(1.0 - roughness) - F0) * pow(1.0 - cosTheta, 5.0);
}

// GGX/Towbridge-Reitz normal distribution function, Disney's reparametrization of alpha = roughness^2
float ndfGGX(float cosLh, float roughness)
{
    float alpha = roughness * roughness;
    float alphaSq = alpha * alpha;

    float denom = (cosLh * cosLh) * (alphaSq - 1.0) + 1.0;
    return alphaSq / (PI * denom * denom);
}

// Single term for separable Schlick-GGX below
float gaSchlickG1(float cosTheta, float k)
{
    return cosTheta / (cosTheta * (1.0 - k) + k);
}

// Schlick-GGX approximation of geometric attenuation function using Smith's method
float gaSchlickGGX(float cosLi, float cosLo, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0; // Epic suggests using this roughness remapping for analytic lights
    return gaSchlickG1(cosLi, k) * gaSchlickG1(cosLo, k);
}

// Analytic approximation of the pre-integrated environment BRDF (Lazarov / Karis, "PBR on mobile")
vec3 EnvBRDFApprox(vec3 specularColor, float roughness, float NoV)
{
    const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
    const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
    vec4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * NoV)) * r.x + r.y;
    vec2 AB = vec2(-1.04, 1.04) * a004 + r.zw;
    return specularColor * AB.x + AB.y;
}

// Tangent frame from screen-space derivatives (expects DirectX / MikkTSpace normal maps).
// Flipping the sign of every y derivative (GL vs D3D window orientation) leaves T and B unchanged.
mat3 ComputeTangentFrame(vec3 N, vec3 P, vec2 uv, out vec3 T, out vec3 B)
{
    vec3 dp1 = dFdx(P);
    vec3 dp2 = dFdy(P);
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);

    vec3 c = cross(dp1, dp2);
    vec3 r0 = cross(dp2, c);
    vec3 r1 = cross(c, dp1);
    T = normalize(duv1.x * r0 + duv2.x * r1);
    B = normalize(duv1.y * r0 + duv2.y * r1);
    return mat3(T, B, N);
}

float GetAttenForLight(vec4 lightAtten, int lightNum)
{
    if (lightNum == 1) return lightAtten.y;
    if (lightNum == 2) return lightAtten.z;
    if (lightNum == 3) return lightAtten.w;
    return lightAtten.x;
}

// Direct light from one source. On lightmapped surfaces only the specular term is added,
// as the diffuse contribution of static lights is already baked into the lightmap.
vec3 CalculateLight(vec3 lightIn, vec3 lightIntensity, vec3 lightOut, vec3 normal, vec3 fresnelReflectance,
                    float roughness, float metalness, float lightDirectionAngle, vec3 albedo, bool bSpecularF0, bool bLightmapped)
{
    vec3 halfAngle = normalize(lightIn + lightOut);
    float cosLightIn = max(0.0, dot(normal, lightIn));
    float cosHalfAngle = max(0.0, dot(normal, halfAngle));

    vec3 F = fresnelSchlick(fresnelReflectance, max(0.0, dot(halfAngle, lightOut)));
    float D = ndfGGX(cosHalfAngle, roughness);
    float G = gaSchlickGGX(cosLightIn, lightDirectionAngle, roughness);

    // Metals reflect or absorb, so their diffuse contribution is zero
    vec3 kd = bSpecularF0 ? vec3(1.0) - F : mix(vec3(1.0) - F, vec3(0.0), metalness);
    vec3 diffuseBRDF = kd * albedo;

    // Cook-Torrance specular microfacet BRDF
    vec3 specularBRDF = (F * D * G) / max(EPSILON, 4.0 * cosLightIn * lightDirectionAngle);

    if (bLightmapped)
        return specularBRDF * lightIntensity * cosLightIn;

    return (diffuseBRDF + specularBRDF) * lightIntensity * cosLightIn;
}

vec2 ParallaxCorrect(vec2 texCoord, vec3 viewRelativeDir, vec3 worldSpaceWorldToEye, vec3 worldSpaceNormal,
                     sampler2D depthMap, float parallaxDepth, float parallaxCenter)
{
    float fLength = length(viewRelativeDir);
    float fParallaxLength = sqrt(fLength * fLength - viewRelativeDir.z * viewRelativeDir.z) / viewRelativeDir.z;
    vec2 vParallaxDirection = normalize(viewRelativeDir.xy);
    vec2 vParallaxOffsetTS = vParallaxDirection * fParallaxLength;
    float fViewDotHorizonFactor = min(clamp(dot(normalize(worldSpaceNormal), normalize(worldSpaceWorldToEye)), 0.0, 1.0), 0.5) * 2.0;
    vParallaxOffsetTS *= clamp(parallaxDepth * fViewDotHorizonFactor, 0.0, 1.0);

    vec2 dx = dFdx(texCoord);
    vec2 dy = dFdy(texCoord);

    const int nNumSteps = 20;
    float fStepSize = 1.0 / float(nNumSteps);
    float fPrevHeight = 1.0;
    float fCurrentBound = 1.0;

    vec2 vTexOffsetPerStep = fStepSize * vParallaxOffsetTS;
    vec2 vTexCurrentOffset = texCoord;

    vec2 pt1 = vec2(0.0);
    vec2 pt2 = vec2(0.0);

    for (int nStepIndex = 0; nStepIndex < nNumSteps; ++nStepIndex) {
        vTexCurrentOffset -= vTexOffsetPerStep;

        // Height is stored in the alpha channel of the normal map
        float fCurrHeight = parallaxCenter + textureGrad(depthMap, vTexCurrentOffset, dx, dy).a;

        fCurrentBound -= fStepSize;

        if (fCurrHeight > fCurrentBound) {
            pt1 = vec2(fCurrentBound, fCurrHeight);
            pt2 = vec2(fCurrentBound + fStepSize, fPrevHeight);
            break;
        }

        fPrevHeight = fCurrHeight;
    }

    float fDelta2 = pt2.x - pt2.y;
    float fDelta1 = pt1.x - pt1.y;
    float fDenominator = fDelta2 - fDelta1;
    float fParallaxAmount = abs(fDenominator) > EPSILON ? (pt1.x * fDelta2 - pt2.x * fDelta1) / fDenominator : 0.0;
    vec2 vParallaxOffset = vParallaxOffsetTS * (1.0 - fParallaxAmount);
    return texCoord - vParallaxOffset;
}

#endif // COMMON_PBR_GL460_FS
