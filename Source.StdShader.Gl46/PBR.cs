using Source.Common;
using Source.Common.Commands;
using Source.Common.MaterialSystem;
using Source.Common.ShaderAPI;
using Source.Common.ShaderLib;

namespace Source.StdShader.Gl46;

/// <summary>
/// Physically based shader (metalness/roughness workflow) for brushes and models.
/// Ported from thexa4/source-pbr (Source SDK 2013, Source 1 SDK license).
/// Brushes use the (bumped) lightmap for diffuse ambient lighting, models use the ambient cube plus local lights;
/// both use the cubemap for specular image based lighting.
/// </summary>
public class PBR : BaseVSShader
{
	public static string HelpString = "PBR shader";
	public static ShaderFlags Flags = ShaderFlags.None;
	public static List<ShaderParam> ShaderParams = [];

	public class ShaderParam
	{
		public readonly ShaderParamInfo Info;
		public readonly int Index;
		public ShaderParam(string name, ShaderParamType type, ReadOnlySpan<char> defaultParam, ReadOnlySpan<char> help, ShaderParamFlags flags = 0) {
			Info.Name = name;
			Info.Type = type;
			Info.DefaultValue = new(defaultParam);
			Info.Help = new(help);
			Info.Flags = flags;
			Index = (int)ShaderMaterialVars.Count + ShaderParams.Count;
			ShaderParams.Add(this);
		}
		public static implicit operator int(ShaderParam param) => param.Index;
		public ReadOnlySpan<char> GetName() => Info.Name;
		public new ShaderParamType GetType() => Info.Type;
		public ReadOnlySpan<char> GetDefaultValue() => Info.DefaultValue;
		public ShaderParamFlags GetFlags() => Info.Flags;
		public ReadOnlySpan<char> GetHelp() => Info.Help;
	}

	public static readonly ShaderParam ALPHATESTREFERENCE = new($"${nameof(ALPHATESTREFERENCE)}", ShaderParamType.Float, "0", "");
	public static readonly ShaderParam ENVMAP = new($"${nameof(ENVMAP)}", ShaderParamType.EnvMap, "", "Set the cubemap for this material.");
	public static readonly ShaderParam MRAOTEXTURE = new($"${nameof(MRAOTEXTURE)}", ShaderParamType.Texture, "", "Texture with metalness in R, roughness in G, ambient occlusion in B.");
	public static readonly ShaderParam EMISSIONTEXTURE = new($"${nameof(EMISSIONTEXTURE)}", ShaderParamType.Texture, "", "Emission texture");
	public static readonly ShaderParam NORMALTEXTURE = new($"${nameof(NORMALTEXTURE)}", ShaderParamType.Texture, "", "Normal texture (deprecated, use $bumpmap)");
	public static readonly ShaderParam BUMPMAP = new($"${nameof(BUMPMAP)}", ShaderParamType.Texture, "", "Normal texture");
	public static readonly ShaderParam USEENVAMBIENT = new($"${nameof(USEENVAMBIENT)}", ShaderParamType.Bool, "0", "Use the cubemaps to compute ambient light.");
	public static readonly ShaderParam SPECULARTEXTURE = new($"${nameof(SPECULARTEXTURE)}", ShaderParamType.Texture, "", "Specular F0 RGB map");
	public static readonly ShaderParam PARALLAX = new($"${nameof(PARALLAX)}", ShaderParamType.Bool, "0", "Use Parallax Occlusion Mapping.");
	public static readonly ShaderParam PARALLAXDEPTH = new($"${nameof(PARALLAXDEPTH)}", ShaderParamType.Float, "0.0030", "Depth of the Parallax Map");
	public static readonly ShaderParam PARALLAXCENTER = new($"${nameof(PARALLAXCENTER)}", ShaderParamType.Float, "0.5", "Center depth of the Parallax Map");
	public static readonly ShaderParam METALNESS = new($"${nameof(METALNESS)}", ShaderParamType.Float, "0", "Metalness used when there is no $mraotexture");
	public static readonly ShaderParam ROUGHNESS = new($"${nameof(ROUGHNESS)}", ShaderParamType.Float, "1", "Roughness used when there is no $mraotexture");

	static readonly ConVar mat_pbr_parallaxmap = new("mat_pbr_parallaxmap", "1", FCvar.Archive, "Enable parallax occlusion mapping on PBR materials");

	public override string? GetFallbackShader(IMaterialVar[] vars) => null;
	public override ShaderFlags GetFlags() => Flags;
	public override int GetNumParams() => base.GetNumParams() + ShaderParams.Count;
	public override ReadOnlySpan<char> GetParamName(int paramIndex) {
		int baseClassParamCount = base.GetNumParams();
		return paramIndex < baseClassParamCount ? base.GetParamName(paramIndex) : ShaderParams[paramIndex - baseClassParamCount].GetName();
	}
	public override ReadOnlySpan<char> GetParamHelp(int paramIndex) {
		int baseClassParamCount = base.GetNumParams();
		return paramIndex < baseClassParamCount ? base.GetParamHelp(paramIndex) : ShaderParams[paramIndex - baseClassParamCount].GetHelp();
	}
	public override ShaderParamType GetParamType(int paramIndex) {
		int baseClassParamCount = base.GetNumParams();
		return paramIndex < baseClassParamCount ? base.GetParamType(paramIndex) : ShaderParams[paramIndex - baseClassParamCount].GetType();
	}
	public override ReadOnlySpan<char> GetParamDefault(int paramIndex) {
		int baseClassParamCount = base.GetNumParams();
		return paramIndex < baseClassParamCount ? base.GetParamDefault(paramIndex) : ShaderParams[paramIndex - baseClassParamCount].GetDefaultValue();
	}

	protected override void OnInitShaderParams(IMaterialVar[] parms, ReadOnlySpan<char> materialName) {
		// Fallback for the deprecated parameter
		if (parms[NORMALTEXTURE].IsDefined() && !parms[BUMPMAP].IsDefined())
			parms[BUMPMAP].SetStringValue(parms[NORMALTEXTURE].GetStringValue());

		// PBR relies heavily on envmaps
		if (!parms[ENVMAP].IsDefined())
			parms[ENVMAP].SetStringValue("env_cubemap");
	}

	protected override void OnInitShaderInstance(IMaterialVar[] parms, ReadOnlySpan<char> materialName) {
		if (parms[BUMPMAP].IsDefined())
			LoadBumpMap(BUMPMAP);

		int envMapFlags = HardwareConfig.GetHDRType() == HDRType.None ? (int)TextureFlags.SRGB : 0;
		LoadCubeMap(ENVMAP, envMapFlags | (int)TextureFlags.AllMips);

		if (parms[EMISSIONTEXTURE].IsDefined())
			LoadTexture(EMISSIONTEXTURE, (int)TextureFlags.SRGB);

		if (parms[MRAOTEXTURE].IsDefined())
			LoadTexture(MRAOTEXTURE);

		if (parms[(int)ShaderMaterialVars.BaseTexture].IsDefined())
			LoadTexture((int)ShaderMaterialVars.BaseTexture, (int)TextureFlags.SRGB);

		if (parms[SPECULARTEXTURE].IsDefined())
			LoadTexture(SPECULARTEXTURE, (int)TextureFlags.SRGB);

		if (IsFlagSet(parms, MaterialVarFlags.Model)) {
			SetFlags2(parms, MaterialVarFlags2.SupportsHardwareSkinning);
			SetFlags2(parms, MaterialVarFlags2.DiffuseBumpmappedModel);
			SetFlags2(parms, MaterialVarFlags2.LightingVertexLit);
			SetFlags2(parms, MaterialVarFlags2.NeedsBakedLightingSnapshots);
		}
		else {
			SetFlags2(parms, MaterialVarFlags2.LightingLightmap);
			SetFlags2(parms, MaterialVarFlags2.LightingBumpedLightmap);
		}
	}

	protected override void OnDrawElements(IMaterialVar[] parms, IShaderDynamicAPI shaderAPI, VertexCompressionType vertexCompression) {
		int baseTexture = (int)ShaderMaterialVars.BaseTexture;
		bool hasBaseTexture = parms[baseTexture].IsTexture();
		bool hasNormalTexture = parms[BUMPMAP].IsTexture();
		bool hasMraoTexture = parms[MRAOTEXTURE].IsTexture();
		bool hasEmissionTexture = parms[EMISSIONTEXTURE].IsTexture();
		bool hasEnvTexture = parms[ENVMAP].IsTexture();
		bool hasSpecularTexture = parms[SPECULARTEXTURE].IsTexture();
		bool isAlphaTested = IsFlagSet(parms, MaterialVarFlags.AlphaTest);
		bool isModel = IsFlagSet(parms, MaterialVarFlags.Model);
		bool lightmapped = !isModel;
		bool useEnvAmbient = parms[USEENVAMBIENT].GetIntValue() == 1;

		BlendType blendType = EvaluateBlendRequirements(baseTexture, true);
		bool fullyOpaque = (blendType != BlendType.BlendAdd) && (blendType != BlendType.Blend) && !isAlphaTested;

		if (IsSnapshotting()) {
			ShaderShadow.EnableAlphaTest(isAlphaTested);
			if (parms[ALPHATESTREFERENCE].GetFloatValue() > 0.0f)
				ShaderShadow.AlphaFunc(ShaderAlphaFunc.GreaterEqual, parms[ALPHATESTREFERENCE].GetFloatValue());

			SetDefaultBlendingShadowState(baseTexture, true);

			ShaderShadow.EnableTexture(Sampler.Sampler0, true);     // Base color
			ShaderShadow.EnableSRGBRead(Sampler.Sampler0, true);
			ShaderShadow.EnableTexture(Sampler.Sampler1, true);     // Normal map
			ShaderShadow.EnableSRGBRead(Sampler.Sampler1, false);
			ShaderShadow.EnableTexture(Sampler.Sampler7, true);     // Lightmap, read the same way LightmappedGeneric does
			ShaderShadow.EnableSRGBRead(Sampler.Sampler7, HardwareConfig.GetHDRType() == HDRType.None);
			ShaderShadow.EnableTexture(Sampler.Sampler10, true);    // MRAO
			ShaderShadow.EnableSRGBRead(Sampler.Sampler10, false);
			ShaderShadow.EnableTexture(Sampler.Sampler11, true);    // Emission
			ShaderShadow.EnableSRGBRead(Sampler.Sampler11, true);
			ShaderShadow.EnableTexture(Sampler.Sampler12, true);    // Specular F0
			ShaderShadow.EnableSRGBRead(Sampler.Sampler12, true);

			if (hasEnvTexture) {
				ShaderShadow.EnableTexture(Sampler.Sampler2, true);
				if (HardwareConfig.GetHDRType() == HDRType.None)
					ShaderShadow.EnableSRGBRead(Sampler.Sampler2, true);
			}

			ShaderShadow.EnableSRGBWrite(true);

			if (isModel)
				ShaderShadow.VertexShaderVertexFormat(VertexFormat.Position | VertexFormat.Normal, 1, null, 0);
			else
				ShaderShadow.VertexShaderVertexFormat(VertexFormat.Position | VertexFormat.Normal, 3, null, 0);

			bool useParallax = parms[PARALLAX].GetIntValue() != 0 && mat_pbr_parallaxmap.GetBool() && hasNormalTexture;

			ShaderShadow.SetVertexShader("pbr", 0);

			StaticShaderIndex pshIndex = new(ShaderShadow, ShaderType.Pixel, "pbr");
			pshIndex.Set("LIGHTMAPPED", lightmapped);
			pshIndex.Set("USEENVAMBIENT", useEnvAmbient);
			pshIndex.Set("EMISSIVE", hasEmissionTexture);
			pshIndex.Set("SPECULAR", hasSpecularTexture);
			pshIndex.Set("PARALLAXOCCLUSION", useParallax);
			pshIndex.Set("MRAOTEXTURE", hasMraoTexture);
			ShaderShadow.SetPixelShader("pbr", pshIndex.GetIndex());

			DefaultFog();

			ShaderShadow.EnableAlphaWrites(fullyOpaque);
		}
		else {
			bool lightingOnly = mat_fullbright.GetInt() == 2 && !IsFlagSet(parms, MaterialVarFlags.NoDebugOverride);

			if (hasBaseTexture && !lightingOnly)
				BindTexture(Sampler.Sampler0, baseTexture, (int)ShaderMaterialVars.Frame);
			else
				shaderAPI.BindStandardTexture(Sampler.Sampler0, StandardTextureId.Grey);

			// $color / $color2 / entity render color, as a linear base color tint
			SetModulationPixelShaderDynamicState_LinearColorSpace((int)PixelShaderConst.SelfIllumTint);

			if (hasEnvTexture && Config.UseSpecular())
				BindTexture(Sampler.Sampler2, ENVMAP, -1);
			else
				shaderAPI.BindStandardTexture(Sampler.Sampler2, StandardTextureId.Black);

			if (hasEmissionTexture)
				BindTexture(Sampler.Sampler11, EMISSIONTEXTURE, -1);
			else
				shaderAPI.BindStandardTexture(Sampler.Sampler11, StandardTextureId.Black);

			if (hasNormalTexture)
				BindTexture(Sampler.Sampler1, BUMPMAP, -1);
			else
				shaderAPI.BindStandardTexture(Sampler.Sampler1, StandardTextureId.NormalMapFlat);

			if (hasMraoTexture)
				BindTexture(Sampler.Sampler10, MRAOTEXTURE, -1);
			else
				shaderAPI.BindStandardTexture(Sampler.Sampler10, StandardTextureId.White);

			if (hasSpecularTexture)
				BindTexture(Sampler.Sampler12, SPECULARTEXTURE, -1);
			else
				shaderAPI.BindStandardTexture(Sampler.Sampler12, StandardTextureId.Black);

			shaderAPI.BindStandardTexture(Sampler.Sampler7, StandardTextureId.Lightmap);

			// Brushes get their lighting from the lightmap, not the ambient cube or local lights
			LightState lightState = default;
			if (isModel)
				shaderAPI.GetDX9LightState(out lightState);

			MaterialFogMode fogType = shaderAPI.GetSceneFogMode();
			int numBones = shaderAPI.GetCurrentNumBones();

			bool writeDepthToAlpha = false;
			bool writeWaterFogToAlpha = false;
			if (fullyOpaque) {
				writeDepthToAlpha = shaderAPI.ShouldWriteDepthToDestAlpha();
				writeWaterFogToAlpha = fogType == MaterialFogMode.LinearBelowFogZ;
			}

			Span<float> eyePos_EnvMapLOD = [0, 0, 0, 0];
			shaderAPI.GetWorldSpaceCameraPosition(eyePos_EnvMapLOD);

			// Highest mip used for the roughest reflections
			int envMapLOD = 6;
			ITexture? envTexture = parms[ENVMAP].GetTextureValue();
			if (envTexture != null) {
				int width = envTexture.GetMappingWidth();
				int mips = 0;
				while ((width >>= 1) != 0)
					++mips;
				envMapLOD = mips;
			}
			eyePos_EnvMapLOD[3] = Math.Clamp(envMapLOD, 4, 12);
			shaderAPI.SetPixelShaderConstant((int)PixelShaderConst.EyePosSpecExponent, eyePos_EnvMapLOD);

			DynamicShaderIndex vshIndex = new(shaderAPI, ShaderType.Vertex);
			vshIndex.Set("SKINNING", numBones > 0);
			vshIndex.Set("NUM_LIGHTS", lightState.NumLights);
			shaderAPI.SetVertexShaderIndex(vshIndex.GetIndex());

			DynamicShaderIndex pshIndex = new(shaderAPI, ShaderType.Pixel);
			pshIndex.Set("NUM_LIGHTS", lightState.NumLights);
			pshIndex.Set("WRITEWATERFOGTODESTALPHA", writeWaterFogToAlpha);
			pshIndex.Set("WRITE_DEPTH_TO_DESTALPHA", writeDepthToAlpha);
			pshIndex.Set("PIXELFOGTYPE", shaderAPI.GetPixelFogCombo1(true));
			shaderAPI.SetPixelShaderIndex(pshIndex.GetIndex());

			SetVertexShaderTextureTransform(VertexShaderConst.ShaderSpecificConst0, (int)ShaderMaterialVars.BaseTextureTransform);

			shaderAPI.SetPixelShaderStateAmbientLightCube((int)PixelShaderConst.AmbientCube, !lightState.AmbientLight);
			shaderAPI.CommitPixelShaderLighting((int)PixelShaderConst.LightInfoArray);
			shaderAPI.SetPixelShaderFogParams((int)PixelShaderConst.FogParams);

			// The tint is already applied to the base color, so the lightmap only needs its scale
			float lightmapScale = shaderAPI.GetLightMapScaleFactor();
			Span<float> lightmapModulation = [lightmapScale, lightmapScale, lightmapScale, 1.0f];
			shaderAPI.SetPixelShaderConstant((int)PixelShaderConst.DiffuseModulation, lightmapModulation);

			Span<float> pbrParams = [
				GetFloatParam(PARALLAXDEPTH, parms, 0.003f),
				GetFloatParam(PARALLAXCENTER, parms, 0.5f),
				Math.Clamp(GetFloatParam(METALNESS, parms, 0.0f), 0.0f, 1.0f),
				Math.Clamp(GetFloatParam(ROUGHNESS, parms, 1.0f), 0.0f, 1.0f)
			];
			shaderAPI.SetPixelShaderConstant((int)PixelShaderConst.Constant27, pbrParams);
		}
		Draw();
	}
}
