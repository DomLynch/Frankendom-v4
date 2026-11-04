// Furnace/FlipbookMV: motion-vector flipbook particle shader for the furnace fire (Shuriken, HDRP, forward only, no ShaderGraph).
// Sibling of Furnace/Particle (same output contract: premultiplied, display-referred HDR _Intensity, _ExposureWeight, soft particles, _SrcBlend/_DstBlend),
// plus two-frame blending with motion-vector warping for low-fps Unreal-baked 8x8 sheets.
//
// REQUIRED Shuriken setup for the frame blend (otherwise the shader degrades to the single frame, exactly like Furnace/Particle):
//   Texture Sheet Animation enabled (Grid, tiles = _Tiles), and Renderer > Custom Vertex Streams, in this order:
//     Position (Vector3), Color (Vector4), UV (Vector2)  -> TEXCOORD0.xy  current frame
//                                          UV2 (Vector2) -> TEXCOORD0.zw  next frame
//                                          AnimBlend (Float) -> TEXCOORD1.x  blend 0..1 between the two
//   C#:  renderer.SetActiveVertexStreams(new List<ParticleSystemVertexStream>{ Position, Color, UV, UV2, AnimBlend });
//   The frame index therefore comes from particle age through Shuriken's Texture Sheet Animation (frame over time), same as Furnace/Particle.
// Motion vectors: _MotionTex rg, decoded as mv = tex.rg * _MVScale.xy + _MVBias.xy (defaults 2 / -1 map 0..1 to -1..1; "gray" = no motion).
//   Frame A is sampled at uvA - mvA * t * k, frame B at uvB + mvB * (1 - t) * k, with k = _MVStrength / _Tiles (so strength 1 = one full cell
//   width at |mv| = 1). Flip the sign of _MVStrength (or one component of _MVScale) if the bake's convention is opposite.
//   The MV texture must be imported linear (sRGB off), no mipmaps, Clamp.
// Blend: additive = (One, One), alpha = premultiplied (One, OneMinusSrcAlpha), as in Furnace/Particle.
Shader "Furnace/FlipbookMV"
{
    Properties
    {
        [NoScaleOffset] _MainTex("Flipbook (RGBA)", 2D) = "white" {}
        [NoScaleOffset] _MotionTex("Motion Vector Sheet (RG, linear)", 2D) = "gray" {}
        _Tiles("Tiles per side (8 = 8x8 sheet)", Float) = 8
        _MVScale("MV decode scale (xy)", Vector) = (2, 2, 0, 0)
        _MVBias("MV decode bias (xy)", Vector) = (-1, -1, 0, 0)
        _MVStrength("MV strength (0 = plain cross-fade)", Float) = 0
        _Tint("Tint", Color) = (1,1,1,1)
        _Intensity("HDR Intensity (display-referred)", Float) = 1
        _ExposureWeight("Exposure Weight (0 display-referred, 1 nits)", Range(0, 1)) = 0
        _SoftDistance("Soft Particle Distance (m)", Float) = 0.5
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend("Dst Blend", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_MotionTex);
            SAMPLER(sampler_MotionTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_TexelSize;
                float4 _MVScale;
                float4 _MVBias;
                float4 _Tint;
                float _Tiles;
                float _MVStrength;
                float _Intensity;
                float _ExposureWeight;
                float _SoftDistance;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float4 uv         : TEXCOORD0; // xy current frame, zw next frame (UV2 stream; 0 when streams are not set)
                float  blend      : TEXCOORD1; // AnimBlend stream (0 when not set)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float4 uv         : TEXCOORD0;
                float  blend      : TEXCOORD1;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(v.positionOS); // camera-relative in HDRP
                o.positionCS = TransformWorldToHClip(positionWS);
                o.color = v.color;
                o.uv = v.uv;
                o.blend = v.blend;
                return o;
            }

            // Samples one frame: motion vector at the frame's own uv, colour at the warped uv kept inside the frame's cell (no bleed from neighbours).
            // Returns premultiplied rgb in .rgb and alpha in .a.
            float4 SampleFrame(float2 uv, float warp)
            {
                float2 cell = 1.0 / _Tiles;
                float2 mv = SAMPLE_TEXTURE2D_LOD(_MotionTex, sampler_MotionTex, uv, 0).rg * _MVScale.xy + _MVBias.xy;
                float2 w = uv + mv * warp * (_MVStrength / _Tiles);
                float2 cellMin = floor(uv * _Tiles) * cell;
                float2 pad = 0.5 * _MainTex_TexelSize.xy;
                w = clamp(w, cellMin + pad, cellMin + cell - pad);
                float4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, w);
                return float4(c.rgb * c.a, c.a);
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float t = saturate(i.blend);
                float4 f = SampleFrame(i.uv.xy, -t); // A: pushed back along its own motion by t
                if (t > 0.0001)
                {
                    float4 g = SampleFrame(i.uv.zw, 1.0 - t); // B: pulled forward from the next frame by (1 - t)
                    f = lerp(f, g, t);
                }

                float4 tintVtx = i.color * _Tint;

                float soft = 1.0;
                if (_SoftDistance > 0.0001)
                {
                    float sceneEyeDepth = LinearEyeDepth(LoadCameraDepth(uint2(i.positionCS.xy)), _ZBufferParams);
                    soft = saturate((sceneEyeDepth - i.positionCS.w) / _SoftDistance);
                }

                float a = f.a * tintVtx.a * soft;
                float3 rgb = f.rgb * tintVtx.rgb * (tintVtx.a * soft) * _Intensity; // f.rgb is already alpha-premultiplied
                rgb = lerp(rgb, rgb * GetCurrentExposureMultiplier(), _ExposureWeight);
                return float4(rgb, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
