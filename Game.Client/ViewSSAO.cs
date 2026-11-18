using Source;
using Source.Common;
using Source.Common.Bitmap;
using Source.Common.Commands;
using Source.Common.Formats.Keyvalues;
using Source.Common.MaterialSystem;
using Source.Engine;

namespace Game.Client;

/// <summary>
/// Screen-space ambient occlusion pass. Runs after the opaque/translucent world and entities are drawn,
/// before view models, so the player's weapon never receives (or casts) occlusion.
/// </summary>
public static class ViewSSAO
{
	static readonly ConVar r_ssao = new("r_ssao", "0", FCvar.Archive, "Enable screen-space ambient occlusion");
	static readonly ConVar r_ssao_radius = new("r_ssao_radius", "48", FCvar.Archive, "SSAO sampling radius in world units");
	static readonly ConVar r_ssao_intensity = new("r_ssao_intensity", "2.0", FCvar.Archive, "SSAO strength");
	static readonly ConVar r_ssao_bias = new("r_ssao_bias", "0.1", FCvar.Archive, "SSAO angle bias (cosine); raise if flat surfaces self-occlude");
	static readonly ConVar r_ssao_samples = new("r_ssao_samples", "12", FCvar.Archive, "SSAO samples per pixel (4-32)");
	static readonly ConVar r_ssao_fade = new("r_ssao_fade", "4096", FCvar.Archive, "Distance at which SSAO fades out entirely");
	static readonly ConVar r_ssao_debug = new("r_ssao_debug", "0", FCvar.Cheat, "0 = off, 1 = show the AO term, 2 = show linear depth");
	static readonly ConVar r_ssao_depthflip = new("r_ssao_depthflip", "1", FCvar.Cheat, "Flip the depth snapshot vertically (it is stored bottom-up when rendering to the back buffer)");

	const string AOTextureName = "_rt_SSAO";

	static readonly TextureReference AOTexture = new();
	static readonly MaterialReference SSAOMaterial = new();
	static readonly MaterialReference CombineMaterial = new();
	static readonly MaterialReference CombineDebugMaterial = new();

	public static bool IsEnabled() => r_ssao.GetBool();

	public static void InitRenderTargets(IMaterialSystem materialSystem) {
		AOTexture.Init(materialSystem.CreateNamedRenderTargetTextureEx2(AOTextureName, 1, 1, RenderTargetSizeMode.FullFrameBuffer,
			ImageFormat.RGBA8888, MaterialRenderTargetDepth.None, TextureFlags.ClampS | TextureFlags.ClampT | TextureFlags.PointSample, 0));
	}

	public static void ShutdownRenderTargets() => AOTexture.Shutdown();

	static bool InitMaterials() {
		if (SSAOMaterial.IsValid() && CombineMaterial.IsValid() && CombineDebugMaterial.IsValid())
			return true;

		SSAOMaterial.Init("__ssao", MaterialDefines.TEXTURE_GROUP_OTHER, new KeyValues("SSAO"));

		KeyValues combine = new("SSAO_Combine");
		combine.SetString("$AOTEXTURE", AOTextureName);
		CombineMaterial.Init("__ssao_combine", MaterialDefines.TEXTURE_GROUP_OTHER, combine);

		KeyValues combineDebug = new("SSAO_Debug");
		combineDebug.SetString("$AOTEXTURE", AOTextureName);
		CombineDebugMaterial.Init("__ssao_combine_debug", MaterialDefines.TEXTURE_GROUP_OTHER, combineDebug);

		return SSAOMaterial.IsValid() && CombineMaterial.IsValid() && CombineDebugMaterial.IsValid();
	}

	static void SetVar(IMaterial material, ReadOnlySpan<char> name, float x, float y, float z, float w) {
		IMaterialVar var = material.FindVar(name, out bool found, false);
		if (found)
			var.SetVecValue(x, y, z, w);
	}

	static void SetVar(IMaterial material, ReadOnlySpan<char> name, int value) {
		IMaterialVar var = material.FindVar(name, out bool found, false);
		if (found)
			var.SetIntValue(value);
	}

	public static void Render(in ViewSetup view) {
		if (!r_ssao.GetBool() || view.Ortho)
			return;

		ITexture? aoTexture = AOTexture.Get();
		if (aoTexture == null || !InitMaterials())
			return;

		IMaterial ssao = SSAOMaterial.Get()!;
		int debug = r_ssao_debug.GetInt();
		IMaterial combine = (debug != 0 ? CombineDebugMaterial.Get() : CombineMaterial.Get())!;

		// Same projection the engine builds in ComputeViewMatrices
		float aspectRatio = view.AspectRatio;
		if (aspectRatio == 0.0f)
			aspectRatio = view.Height != 0 ? (float)view.Width / view.Height : 1.0f;
		float tanHalfFovX = MathF.Tan(view.FOV * MathF.PI / 360.0f);
		float tanHalfFovY = tanHalfFovX / aspectRatio;

		int depthFlip = r_ssao_depthflip.GetBool() ? 1 : 0;

		SetVar(ssao, "$PROJPARAMS", tanHalfFovX, tanHalfFovY, view.ZNear, view.ZFar);
		SetVar(ssao, "$AOPARAMS", r_ssao_radius.GetFloat(), r_ssao_intensity.GetFloat(), r_ssao_bias.GetFloat(), r_ssao_fade.GetFloat());
		SetVar(ssao, "$SAMPLES", r_ssao_samples.GetInt());
		SetVar(ssao, "$DEPTHFLIP", depthFlip);

		SetVar(combine, "$PROJPARAMS", tanHalfFovX, tanHalfFovY, view.ZNear, view.ZFar);
		SetVar(combine, "$DEPTHFLIP", depthFlip);
		if (debug != 0)
			SetVar(combine, "$DEBUG", debug);

		using MatRenderContextPtr renderContext = new(materials);

		renderContext.CopyRenderTargetDepthToTexture();

		int aoWidth = aoTexture.GetActualWidth();
		int aoHeight = aoTexture.GetActualHeight();

		RenderUtils renderUtils = Singleton<RenderUtils>();

		renderContext.PushRenderTargetAndViewport(aoTexture);
		renderUtils.DrawScreenSpaceRectangle(ssao, 0, 0, aoWidth, aoHeight,
			0, 0, aoWidth - 1, aoHeight - 1, aoWidth, aoHeight, null, 1, 1, 0);
		renderContext.PopRenderTargetAndViewport();

		renderUtils.DrawScreenSpaceRectangle(combine, view.X, view.Y, view.Width, view.Height,
			view.X, view.Y, view.X + view.Width - 1, view.Y + view.Height - 1, aoWidth, aoHeight, null, 1, 1, 0);
	}
}
