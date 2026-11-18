#version 460

// Physically Based Rendering vertex shader for brushes and models.
// Ported from thexa4/source-pbr (Source SDK 2013, Source 1 SDK license).

//  DYNAMIC: "SKINNING"                 "0..1"
//  DYNAMIC: "NUM_LIGHTS"               "0..4"

layout(location = 0) in vec3 v_Position;
layout(location = 1) in vec3 v_Normal;
layout(location = 7) in ivec4 v_BoneIndex;
layout(location = 8) in vec2 v_BoneWeights;
layout(location = 10) in vec2 v_TexCoord0;
layout(location = 11) in vec2 v_TexCoord1;      // Lightmap coordinate (brushes)
layout(location = 12) in vec2 v_TexCoord2;      // Bumped lightmap offset (brushes)

layout(std140, binding = 0) uniform source_matrices {
    mat4 viewMatrix;
    mat4 projectionMatrix;
    mat4 modelMatrix;
};

layout(std140, binding = 5) uniform source_vs_constants {
    vec4 vs_const[256];
};

const int SHADER_SPECIFIC_CONST_0 = 48;

#include "common_gl460.vs"
#include "common_lighting_gl460.vs"
#include "common_skinning_gl460.vs"

#define cBaseTexCoordTransform0     vs_const[SHADER_SPECIFIC_CONST_0 + 0]
#define cBaseTexCoordTransform1     vs_const[SHADER_SPECIFIC_CONST_0 + 1]

const bool g_bSkinning = SKINNING != 0;

layout(location = 0) out vec2 vs_BaseTexCoord;
layout(location = 1) out vec4 vs_LightAtten;
layout(location = 2) out vec3 vs_WorldNormal;
layout(location = 3) out vec3 vs_WorldPos;
layout(location = 4) out vec3 vs_ProjPos;
layout(location = 5) centroid out vec4 vs_LightmapTexCoord1And2;
layout(location = 6) centroid out vec4 vs_LightmapTexCoord3;

#include "common_clipplanes_gl460.vs"

void main()
{
    vec4 texCoord0 = vec4(v_TexCoord0, 0.0, 1.0);

    vs_LightmapTexCoord3 = vec4(0.0);
    vs_LightmapTexCoord1And2.xy = v_TexCoord1 + v_TexCoord2;

    vec2 lightmapTexCoord2 = vs_LightmapTexCoord1And2.xy + v_TexCoord2;
    vec2 lightmapTexCoord3 = lightmapTexCoord2 + v_TexCoord2;

    // Reversed component order
    vs_LightmapTexCoord1And2.w = lightmapTexCoord2.x;
    vs_LightmapTexCoord1And2.z = lightmapTexCoord2.y;
    vs_LightmapTexCoord3.xy = lightmapTexCoord3;

    vec3 worldNormal, worldPos;
    SkinPositionAndNormal(g_bSkinning, vec4(v_Position, 1.0), v_Normal, v_BoneIndex, v_BoneWeights, worldPos, worldNormal);

    vec4 vProjPos = projectionMatrix * viewMatrix * vec4(worldPos, 1.0);
    gl_Position = vProjPos;
    WriteUserClipDistances(gl_Position);

    vs_ProjPos = vProjPos.xyz;
    vs_WorldPos = worldPos;
    vs_WorldNormal = normalize(worldNormal);

    InitLightInfo();

    // Scalar attenuations for four lights
    vs_LightAtten = vec4(0.0);
#if (NUM_LIGHTS > 0)
    vs_LightAtten.x = GetVertexAttenForLight(worldPos, 0, false);
#endif
#if (NUM_LIGHTS > 1)
    vs_LightAtten.y = GetVertexAttenForLight(worldPos, 1, false);
#endif
#if (NUM_LIGHTS > 2)
    vs_LightAtten.z = GetVertexAttenForLight(worldPos, 2, false);
#endif
#if (NUM_LIGHTS > 3)
    vs_LightAtten.w = GetVertexAttenForLight(worldPos, 3, false);
#endif

    // Base texture coordinate transform
    vs_BaseTexCoord.x = dot(texCoord0, cBaseTexCoordTransform0);
    vs_BaseTexCoord.y = dot(texCoord0, cBaseTexCoordTransform1);
}
