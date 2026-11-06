// lanePROPVIS vNN: unlit ID / depth shader for the -propvis harness (PropVis.cs). Never used by the game; only PropVis swaps it in.
// Output is NOT a colour: three base-10 digits (0..9, written as digit/9) of a number v in 0..999. Why digits: this project's HDRP colour buffer is
// R11G11B10 (colorBufferFormat 74: 6-bit mantissa, no alpha), so a raw id like 688 would be rounded; digit/9 levels are 11 % apart, far above the
// quantisation error, and PropVis checks the residual. Mode 0 = v is _IdVal (material, one per prop, 0 = not a prop), mode 1 = v is a log-quantised eye depth
// (1..999, 0 = nothing drawn). Raw return = no exposure, no fog, no grading. Forward pass only, ZWrite on, Cull Off (glTF winding never hides a surface).
Shader "PropVis/Id"
{
    Properties { _IdVal("Id value (0..999)", Float) = 0 }
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Cull Off
            ZWrite On
            ZTest LEqual
            Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _IdVal;
            CBUFFER_END
            float _PropVisMode;      // 0 id, 1 depth (global, set by PropVis)
            float _PropVisNear;      // metres
            float _PropVisLogRange;  // log2(far / near)

            struct Attributes { float3 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float eyeDepth : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(v.positionOS); // camera-relative in HDRP
                o.positionCS = TransformWorldToHClip(positionWS);
                o.eyeDepth = o.positionCS.w;                              // perspective: clip w = eye depth
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float v = _IdVal;
                if (_PropVisMode > 0.5)
                {
                    float u = saturate(log2(max(i.eyeDepth, 1e-4) / max(_PropVisNear, 1e-4)) / max(_PropVisLogRange, 1e-3));
                    v = 1.0 + floor(u * 998.0 + 0.5);
                }
                float d2 = floor(v / 100.0);
                float rest = v - d2 * 100.0;
                float d1 = floor(rest / 10.0);
                float d0 = rest - d1 * 10.0;
                return float4(d2 / 9.0, d1 / 9.0, d0 / 9.0, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
