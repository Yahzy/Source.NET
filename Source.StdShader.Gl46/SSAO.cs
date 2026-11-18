using Source.Common.MaterialSystem;
using Source.Common.ShaderAPI;
using Source.Common.ShaderLib;

namespace Source.StdShader.Gl46;

/// <summary>
/// Screen-space ambient occlusion. Reads the snapshot of the scene depth (StandardTextureId.FrameBufferFullDepth)
/// and writes the raw occlusion term, which SSAO_Combine then blurs and multiplies onto the frame buffer.
/// </summary>
public class SSAO : BaseVSShader
{
	public static string HelpString = "Screen-space ambient occlusion";
	public static ShaderFlags Flags = ShaderFlags.NotEditable;
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

	public static readonly ShaderParam PROJPARAMS = new($"${nameof(PROJPARAMS)}", ShaderParamType.Vec4, "[1 1 7 30000]", "tan(fovX/2), tan(fovY/2), zNear, zFar");
	public static readonly ShaderParam AOPARAMS = new($"${nameof(AOPARAMS)}", ShaderParamType.Vec4, "[48 2 0.1 4096]", "radius, intensity, bias, fade distance");
	public static readonly ShaderParam SAMPLES = new($"${nameof(SAMPLES)}", ShaderParamType.Integer, "12", "sample count");
	public static readonly ShaderParam DEPTHFLIP = new($"${nameof(DEPTHFLIP)}", ShaderParamType.Integer, "1", "1 if the depth snapshot is stored bottom-up");

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

	protected override void OnDrawElements(IMaterialVar[] vars, IShaderDynamicAPI shaderAPI, VertexCompressionType vertexCompression) {
		if (IsSnapshotting()) {
			ShaderShadow.EnableTexture(Sampler.Sampler0, true);
			ShaderShadow.EnableSRGBRead(Sampler.Sampler0, false);
			ShaderShadow.EnableSRGBWrite(false);
			ShaderShadow.EnableDepthWrites(false);
			ShaderShadow.EnableDepthTest(false);
			ShaderShadow.EnableAlphaWrites(true);
			ShaderShadow.VertexShaderVertexFormat(VertexFormat.Position, 1, null, 0);
			ShaderShadow.SetVertexShader("g_screenquad", 0);
			ShaderShadow.SetPixelShader("ssao", 0);
		}
		else {
			Span<float> v = stackalloc float[4];

			vars[PROJPARAMS].GetVecValue(v);
			shaderAPI.SetPixelShaderConstant(0, v, 1);

			vars[AOPARAMS].GetVecValue(v);
			shaderAPI.SetPixelShaderConstant(1, v, 1);

			shaderAPI.GetBackBufferDimensions(out int width, out int height);
			v[0] = 1.0f / Math.Max(width, 1);
			v[1] = 1.0f / Math.Max(height, 1);
			v[2] = Math.Clamp(vars[SAMPLES].GetIntValue(), 4, 32);
			v[3] = vars[DEPTHFLIP].GetIntValue() != 0 ? 1.0f : 0.0f;
			shaderAPI.SetPixelShaderConstant(2, v, 1);

			shaderAPI.BindStandardTexture(Sampler.Sampler0, StandardTextureId.FrameBufferFullDepth);
			shaderAPI.SetVertexShaderIndex(0);
			shaderAPI.SetPixelShaderIndex(0);
		}
		Draw();
	}
}

/// <summary>
/// Depth-aware blur of the SSAO term, multiplied onto the frame buffer.
/// </summary>
public class SSAO_Combine : BaseVSShader
{
	public static string HelpString = "Blurs the SSAO term and applies it to the frame buffer";
	public static ShaderFlags Flags = ShaderFlags.NotEditable;
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

	public static readonly ShaderParam AOTEXTURE = new($"${nameof(AOTEXTURE)}", ShaderParamType.Texture, "_rt_SSAO", "raw SSAO term");
	public static readonly ShaderParam PROJPARAMS = new($"${nameof(PROJPARAMS)}", ShaderParamType.Vec4, "[1 1 7 30000]", "tan(fovX/2), tan(fovY/2), zNear, zFar");
	public static readonly ShaderParam DEPTHFLIP = new($"${nameof(DEPTHFLIP)}", ShaderParamType.Integer, "1", "1 if the depth snapshot is stored bottom-up");
	public static readonly ShaderParam DEBUG = new($"${nameof(DEBUG)}", ShaderParamType.Integer, "0", "Used by SSAO_Debug: 1 = output the AO term, 2 = output linear depth");

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

	protected override void OnInitShaderInstance(IMaterialVar[] vars, ReadOnlySpan<char> materialName) {
		LoadTexture(AOTEXTURE);
	}

	protected virtual bool MultiplyOntoFrameBuffer => true;

	protected override void OnDrawElements(IMaterialVar[] vars, IShaderDynamicAPI shaderAPI, VertexCompressionType vertexCompression) {
		if (IsSnapshotting()) {
			ShaderShadow.EnableTexture(Sampler.Sampler0, true);
			ShaderShadow.EnableTexture(Sampler.Sampler1, true);
			ShaderShadow.EnableSRGBRead(Sampler.Sampler0, false);
			ShaderShadow.EnableSRGBRead(Sampler.Sampler1, false);
			ShaderShadow.EnableSRGBWrite(false);
			ShaderShadow.EnableDepthWrites(false);
			ShaderShadow.EnableDepthTest(false);
			ShaderShadow.EnableAlphaWrites(false);
			ShaderShadow.VertexShaderVertexFormat(VertexFormat.Position, 1, null, 0);
			ShaderShadow.SetVertexShader("g_screenquad", 0);
			ShaderShadow.SetPixelShader("ssao_combine", 0);

			if (MultiplyOntoFrameBuffer)
				EnableAlphaBlending(ShaderBlendFactor.DstColor, ShaderBlendFactor.Zero);
		}
		else {
			Span<float> v = stackalloc float[4];

			vars[PROJPARAMS].GetVecValue(v);
			shaderAPI.SetPixelShaderConstant(0, v, 1);

			ITexture? aoTexture = vars[AOTEXTURE].GetTextureValue();
			int aoWidth = aoTexture?.GetActualWidth() ?? 1;
			int aoHeight = aoTexture?.GetActualHeight() ?? 1;
			v[0] = 1.0f / Math.Max(aoWidth, 1);
			v[1] = 1.0f / Math.Max(aoHeight, 1);
			v[2] = MultiplyOntoFrameBuffer ? 0 : vars[DEBUG].GetIntValue();
			v[3] = vars[DEPTHFLIP].GetIntValue() != 0 ? 1.0f : 0.0f;
			shaderAPI.SetPixelShaderConstant(1, v, 1);

			BindTexture(Sampler.Sampler0, AOTEXTURE, -1);
			shaderAPI.BindStandardTexture(Sampler.Sampler1, StandardTextureId.FrameBufferFullDepth);
			shaderAPI.SetVertexShaderIndex(0);
			shaderAPI.SetPixelShaderIndex(0);
		}
		Draw();
	}
}

/// <summary>
/// Writes the blurred SSAO term (or linear depth) directly to the frame buffer, for debugging.
/// </summary>
public class SSAO_Debug : SSAO_Combine
{
	protected override bool MultiplyOntoFrameBuffer => false;
}
