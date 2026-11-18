#version 460

// Physically Based Rendering pixel shader for brushes and models.
// Ported from thexa4/source-pbr (Source SDK 2013, Source 1 SDK license).

// STATIC: "LIGHTMAPPED"                "0..1"
// STATIC: "USEENVAMBIENT"              "0..1"
// STATIC: "EMISSIVE"                   "0..1"
// STATIC: "SPECULAR"                   "0..1"
// STATIC: "PARALLAXOCCLUSION"          "0..1"
// STATIC: "MRAOTEXTURE"                "0..1"

// DYNAMIC: "WRITEWATERFOGTODESTALPHA"  "0..1"
// DYNAMIC: "PIXELFOGTYPE"              "0..2"
// DYNAMIC: "NUM_LIGHTS"                "0..4"
// DYNAMIC: "WRITE_DEPTH_TO_DESTALPHA"  "0..1"

layout(location = 0) in vec2 vs_BaseTexCoord;
layout(location = 1) in vec4 vs_LightAtten;
layout(location = 2) in vec3 vs_WorldNormal;
layout(location = 3) in vec3 vs_WorldPos;
layout(location = 4) in vec3 vs_ProjPos;
layout(location = 5) centroid in vec4 vs_LightmapTexCoord1And2;
layout(location = 6) centroid in vec4 vs_LightmapTexCoord3;

layout(std140, binding = 6) uniform source_ps_constants {
    vec4 ps_const[256];
};

out vec4 fragColor;

#include "common_vertexlitgeneric_gl460.fs"
#include "common_lightmappedgeneric_gl460.fs"
#include "common_pbr_gl460.fs"

#define g_BaseColor                 ps_const[0]     // Linear modulation color ($color, $color2, render color)
#define g_DiffuseModulation         ps_const[1]
#define g_EyePos                    ps_const[11]
#define g_FogParams                 ps_const[12]
#define g_PBRParams                 ps_const[27]    // x = parallax depth, y = parallax center, z = metalness, w = roughness (without MRAO texture)
#define ENVMAPLOD                   (g_EyePos.w)

layout(binding = 0) uniform sampler2D BaseTextureSampler;      // Base color, alpha in alpha
layout(binding = 1) uniform sampler2D NormalTextureSampler;    // Normal map, height in alpha
layout(binding = 2) uniform samplerCube EnvmapSampler;         // Cubemap
layout(binding = 7) uniform sampler2D LightmapSampler;         // Lightmap
layout(binding = 10) uniform sampler2D MRAOTextureSampler;     // Metalness, roughness, ambient occlusion
#if EMISSIVE
layout(binding = 11) uniform sampler2D EmissionTextureSampler; // Emission
#endif
#if SPECULAR
layout(binding = 12) uniform sampler2D SpecularTextureSampler; // Specular F0
#endif

vec3 AmbientLookupLightmap(vec3 textureNormal)
{
    vec2 bumpCoord1, bumpCoord2, bumpCoord3;
    ComputeBumpedLightmapCoordinates(vs_LightmapTexCoord1And2, vs_LightmapTexCoord3.xy, bumpCoord1, bumpCoord2, bumpCoord3);

    vec3 lightmapColor1 = LightMapSample(LightmapSampler, bumpCoord1);
    vec3 lightmapColor2 = LightMapSample(LightmapSampler, bumpCoord2);
    vec3 lightmapColor3 = LightMapSample(LightmapSampler, bumpCoord3);

    vec3 dp;
    dp.x = clamp(dot(textureNormal, bumpBasis[0]), 0.0, 1.0);
    dp.y = clamp(dot(textureNormal, bumpBasis[1]), 0.0, 1.0);
    dp.z = clamp(dot(textureNormal, bumpBasis[2]), 0.0, 1.0);
    dp *= dp;

    vec3 diffuseLighting = dp.x * lightmapColor1 + dp.y * lightmapColor2 + dp.z * lightmapColor3;
    float sum = max(dot(dp, vec3(1.0)), EPSILON);
    return diffuseLighting * g_DiffuseModulation.xyz / sum;
}

void SetupEnvMapAmbientCube(out vec3 envAmbientCube[6])
{
    envAmbientCube[0] = ENV_MAP_SCALE * textureLod(EnvmapSampler, vec3( 1.0,  0.0,  0.0), 12.0).rgb;
    envAmbientCube[1] = ENV_MAP_SCALE * textureLod(EnvmapSampler, vec3(-1.0,  0.0,  0.0), 12.0).rgb;
    envAmbientCube[2] = ENV_MAP_SCALE * textureLod(EnvmapSampler, vec3( 0.0,  1.0,  0.0), 12.0).rgb;
    envAmbientCube[3] = ENV_MAP_SCALE * textureLod(EnvmapSampler, vec3( 0.0, -1.0,  0.0), 12.0).rgb;
    envAmbientCube[4] = ENV_MAP_SCALE * textureLod(EnvmapSampler, vec3( 0.0,  0.0,  1.0), 12.0).rgb;
    envAmbientCube[5] = ENV_MAP_SCALE * textureLod(EnvmapSampler, vec3( 0.0,  0.0, -1.0), 12.0).rgb;
}

void main()
{
    const bool bLightmapped = LIGHTMAPPED != 0;
    const bool bSpecularF0 = SPECULAR != 0;

    vec3 envAmbientCube[6];
#if USEENVAMBIENT
    SetupEnvMapAmbientCube(envAmbientCube);
#else
    envAmbientCube = vec3[6](ps_const[4].xyz, ps_const[5].xyz, ps_const[6].xyz,
                             ps_const[7].xyz, ps_const[8].xyz, ps_const[9].xyz);
#endif

    PixelShaderLightInfo cLightInfo[3] = PixelShaderLightInfo[3](
        PixelShaderLightInfo(ps_const[20], ps_const[21]),
        PixelShaderLightInfo(ps_const[22], ps_const[23]),
        PixelShaderLightInfo(ps_const[24], ps_const[25]));

    vec3 surfNormal = normalize(vs_WorldNormal);
    vec3 surfTangent, surfBase;
    mat3 normalBasis = ComputeTangentFrame(surfNormal, vs_WorldPos, vs_BaseTexCoord, surfTangent, surfBase);

    // Back faces (e.g. $nocull) have their interpolated normal facing away from the viewer
    vec3 outgoingLightRay = g_EyePos.xyz - vs_WorldPos;
    float flipSign = dot(surfNormal, outgoingLightRay) < 0.0 ? -1.0 : 1.0;

#if PARALLAXOCCLUSION
    vec3 outgoingLightDirectionTS = vec3(dot(outgoingLightRay, surfTangent), dot(outgoingLightRay, surfBase), dot(outgoingLightRay, surfNormal));
    vec2 texCoord = ParallaxCorrect(vs_BaseTexCoord, outgoingLightDirectionTS, outgoingLightRay, surfNormal,
                                    NormalTextureSampler, g_PBRParams.x, g_PBRParams.y);
#else
    vec2 texCoord = vs_BaseTexCoord;
#endif

    vec3 textureNormal = normalize((texture(NormalTextureSampler, texCoord).xyz - vec3(0.5)) * 2.0);
    vec3 normal = normalize(normalBasis * textureNormal); // World normal

    vec4 albedo = texture(BaseTextureSampler, texCoord);
    albedo.rgb *= g_BaseColor.rgb;

#if MRAOTEXTURE
    vec3 mrao = texture(MRAOTextureSampler, texCoord).xyz;
#else
    vec3 mrao = vec3(g_PBRParams.z, g_PBRParams.w, 1.0);
#endif
    float metalness = mrao.x, roughness = mrao.y, ambientOcclusion = mrao.z;

    textureNormal.y *= flipSign; // Fixup for the bumped lightmap basis

    vec3 outgoingLightDirection = normalize(outgoingLightRay);                       // Lo
    float lightDirectionAngle = max(0.0, dot(normal, outgoingLightDirection));       // cosLo
    vec3 specularReflectionVector = 2.0 * lightDirectionAngle * normal - outgoingLightDirection; // Lr

#if SPECULAR
    vec3 fresnelReflectance = texture(SpecularTextureSampler, texCoord).rgb;        // F0
#else
    vec3 fresnelReflectance = mix(vec3(0.04), albedo.rgb, metalness);               // F0
#endif

    // Ambient / image based lighting
    vec3 diffuseIrradiance = bLightmapped ? AmbientLookupLightmap(textureNormal) : PixelShaderAmbientLight(normal, envAmbientCube);
    vec3 ambientFresnel = fresnelSchlickRoughness(fresnelReflectance, lightDirectionAngle, roughness);
    vec3 diffuseContributionFactor = bSpecularF0 ? vec3(1.0) - ambientFresnel : mix(vec3(1.0) - ambientFresnel, vec3(0.0), metalness);
    vec3 diffuseIBL = diffuseContributionFactor * albedo.rgb * diffuseIrradiance;

    vec3 lookupHigh = ENV_MAP_SCALE * textureLod(EnvmapSampler, specularReflectionVector, roughness * ENVMAPLOD).rgb;
    vec3 lookupLow = PixelShaderAmbientLight(specularReflectionVector, envAmbientCube);
    vec3 specularIrradiance = mix(lookupHigh, lookupLow, roughness * roughness);
    vec3 specularIBL = specularIrradiance * EnvBRDFApprox(fresnelReflectance, roughness, lightDirectionAngle);

    vec3 ambientLighting = (diffuseIBL + specularIBL) * ambientOcclusion;

    // Direct lighting
    vec3 directLighting = vec3(0.0);
    for (int n = 0; n < NUM_LIGHTS; ++n) {
        vec3 lightIn = normalize(PixelShaderGetLightVector(vs_WorldPos, cLightInfo, n));
        vec3 lightColor = PixelShaderGetLightColor(cLightInfo, n) * GetAttenForLight(vs_LightAtten, n); // Li

        directLighting += CalculateLight(lightIn, lightColor, outgoingLightDirection, normal, fresnelReflectance,
                                         roughness, metalness, lightDirectionAngle, albedo.rgb, bSpecularF0, bLightmapped);
    }

    vec3 combinedLighting = directLighting + ambientLighting;
#if EMISSIVE
    combinedLighting += texture(EmissionTextureSampler, texCoord).rgb;
#endif

    float fogFactor = CalcPixelFogFactor(PIXELFOGTYPE, g_FogParams, g_EyePos.xyz, vs_WorldPos, vs_ProjPos.z);

#if WRITEWATERFOGTODESTALPHA && (PIXELFOGTYPE == PIXEL_FOG_TYPE_HEIGHT)
    float alpha = fogFactor;
#else
    float alpha = albedo.a;
#endif

    bool bWriteDepthToAlpha = (WRITE_DEPTH_TO_DESTALPHA != 0) && (WRITEWATERFOGTODESTALPHA == 0);
    fragColor = FinalOutput(vec4(combinedLighting, alpha), fogFactor, PIXELFOGTYPE, TONEMAP_SCALE_LINEAR, bWriteDepthToAlpha, vs_ProjPos.z);
}
