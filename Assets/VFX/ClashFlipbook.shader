// Furnace/ClashFlipbook: sibling of Furnace/FlipbookMV for the ClashFx sheets (hit sparks 6x4, dust puff 8x8). Same output contract and vertex-stream contract
// (see FurnaceFlipbook.shader), but the grid is non-square: _TilesXY = (columns, rows). Without the Custom Vertex Streams it shows the single frame (sparks);
// with them and a motion-vector sheet it blends two frames (dust). _MVStrength 0 = plain cross-fade, no motion texture needed ("gray" = no motion).
Shader "Furnace/ClashFlipbook"
{
    Properties
    {
        [NoScaleOffset] _MainTex("Flipbook (RGBA)", 2D) = "white" {}
        [NoScaleOffset] _MotionTex("Motion Vector Sheet (RG, linear)", 2D) = "gray" {}
        _TilesXY("Tiles (columns, rows)", Vector) = (8, 8, 0, 0)
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
                float4 _TilesXY;
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
                float2 cell = 1.0 / _TilesXY.xy;
                float2 mv = SAMPLE_TEXTURE2D_LOD(_MotionTex, sampler_MotionTex, uv, 0).rg * _MVScale.xy + _MVBias.xy;
                float2 w = uv + mv * warp * (_MVStrength / _TilesXY.xy);
                float2 cellMin = floor(uv * _TilesXY.xy) * cell;
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
