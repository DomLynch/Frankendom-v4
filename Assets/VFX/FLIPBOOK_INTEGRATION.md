# Fire flipbook integration (Furnace/FlipbookMV)

Staged, nothing wired yet. Shader: `Assets/VFX/FurnaceFlipbook.shader` ("Furnace/FlipbookMV"). Data: the 4 `fire` rows in `Assets/Data/hdrp_furnace.json` now carry
`motionVectorMap` (""), `mvScale` [2,2], `mvBias` [-1,-1], `mvStrength` (0). `JsonUtility` ignores JSON keys that have no matching field, so the current
`HdrpFurnace.cs` runs unchanged. (`tint` already exists in `LayerRow` and is the red tint; set it per row.)

## When the Unreal flipbook arrives (kits_out\3d\hades_env\vfx_flipbooks\README)
1. Copy the colour sheet over `Assets/VFX/Flipbooks/furnace_fire_8x8.png` (EnsureFlipbooks keeps an existing file) and the MV sheet to
   `Assets/VFX/Flipbooks/furnace_fire_8x8_mv.png`. Import the MV sheet: sRGB off, mips off, Clamp, no compression (or RG-capable high quality).
2. In the JSON fire rows set `"motionVectorMap": "Assets/VFX/Flipbooks/furnace_fire_8x8_mv.png"`, `"mvStrength"` (start 0.5; negative flips the convention), and
   `mvScale`/`mvBias` per the README encoding (defaults assume 0..1 -> -1..1).
3. Edit `HdrpFurnace.cs` (about 10 lines):

```csharp
// a) LayerRow (line ~38-41): add fields
public string motionVectorMap; public float[] mvScale, mvBias; public float mvStrength;

// b) Data (line ~48): add  public string flipbookShader;   and in JSON root: "flipbookShader": "Furnace/FlipbookMV"
// c) BuildParticles: after "var shader = ...", add
var mvShader = string.IsNullOrEmpty(D.flipbookShader) ? null : Shader.Find(D.flipbookShader);
//    and pass it: BuildLayer(..., shader, ...) -> use (L.kind == "fire" && mvShader != null && !string.IsNullOrEmpty(L.motionVectorMap)) ? mvShader : shader
//    for the fire loop only (BuildLayer and MakeParticleMaterial take that shader).

// d) MakeParticleMaterial: change  if (shader.name == D.particleShader)  to  if (shader.name == D.particleShader || shader.name == "Furnace/FlipbookMV")
//    and inside that block add:
if (shader.name == "Furnace/FlipbookMV")
{
    m.SetTexture("_MotionTex", AssetDatabase.LoadAssetAtPath<Texture2D>(L.motionVectorMap));
    m.SetFloat("_Tiles", L.tiles); m.SetFloat("_MVStrength", L.mvStrength);
    if (L.mvScale != null && L.mvScale.Length >= 2) m.SetVector("_MVScale", new Vector4(L.mvScale[0], L.mvScale[1], 0, 0));
    if (L.mvBias != null && L.mvBias.Length >= 2) m.SetVector("_MVBias", new Vector4(L.mvBias[0], L.mvBias[1], 0, 0));
}

// e) BuildLayer, after rend.sharedMaterial = ...:  enable the frame-blend streams when the material uses the new shader
if (rend.sharedMaterial.shader.name == "Furnace/FlipbookMV" && L.tiles > 1)
    rend.SetActiveVertexStreams(new List<ParticleSystemVertexStream> {
        ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV,
        ParticleSystemVertexStream.UV2, ParticleSystemVertexStream.AnimBlend });
```

Property names: `_MainTex, _MotionTex, _Tiles, _MVScale, _MVBias, _MVStrength, _Tint, _Intensity, _ExposureWeight, _SoftDistance, _SrcBlend, _DstBlend`
(the last six are the same as Furnace/Particle, so the existing `SetColor/SetFloat` lines work as is; blend: add = 1/1, alpha = 1/10).
Frame index = Shuriken Texture Sheet Animation over particle age (already set up in BuildLayer); the streams only add the next-frame UV and blend factor.
With `mvStrength` 0 or no streams the shader is a plain (cross-faded or single-frame) flipbook. Note the `shader.name == D.particleShader` fallback
test in the C# must also accept the new name (step d), otherwise the else-branch writes HDRP/Unlit properties.

## Unverified (Editor closed, never compiled)
- `_MainTex_TexelSize` inside `UnityPerMaterial` (mirrors HDRP's own shaders); if the compiler complains, move it outside the CBUFFER.
- TEXCOORD0 as float4 when no custom streams are set: missing zw/blend should read 0 (D3D11 default); then t = 0 and only frame A is sampled.
- Cell clamp uses `floor(uv * tiles)`; a fragment exactly on a cell boundary could pick the neighbour cell (sub-pixel, hidden by the sheet's transparent border).
- MV sign/encoding convention of the Unreal bake is unknown; see README then adjust `mvStrength` sign / `mvScale`.
