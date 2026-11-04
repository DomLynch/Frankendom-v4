// Hand-written HDRP particle shader for the furnace VFX (Shuriken).
// Why it exists: HDRP/Unlit ignores Shuriken vertex colour and vertex alpha (UnlitData.hlsl: color = map * _UnlitColor), so colour-over-lifetime,
// alpha fade and the 2000 K gradient do not work on it. This shader uses tex * vertex colour * _Tint, multiplies by _Intensity (HDR, unbounded)
// and blends via _SrcBlend/_DstBlend: additive = (One, One), smoke = premultiplied alpha (One, OneMinusSrcAlpha).
// Output is premultiplied: rgb*a, a. _ExposureWeight 0 = display-referred (1.0 is white at any exposure, same as HDRP Unlit emissive weight 0);
// 1 = scene-referred nits (multiplied by the current exposure like a lit emissive).
// Soft particles use the HDRP camera depth. Forward pass only (transparent, no shadows, no lighting).
Shader "Furnace/Particle"
{
    Properties
    {
        [NoScaleOffset] _MainTex("Flipbook (RGBA)", 2D) = "white" {}
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

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _Intensity;
                float _ExposureWeight;
                float _SoftDistance;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(v.positionOS); // camera-relative in HDRP
                o.positionCS = TransformWorldToHClip(positionWS);
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * i.color * _Tint;

                float soft = 1.0;
                if (_SoftDistance > 0.0001)
                {
                    float sceneEyeDepth = LinearEyeDepth(LoadCameraDepth(uint2(i.positionCS.xy)), _ZBufferParams);
                    soft = saturate((sceneEyeDepth - i.positionCS.w) / _SoftDistance);
                }

                float a = c.a * soft;
                float3 rgb = c.rgb * _Intensity;
                rgb = lerp(rgb, rgb * GetCurrentExposureMultiplier(), _ExposureWeight);
                return float4(rgb * a, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
