// Furnace/SixWay: six-way lit smoke particle shader for the Unreal-baked doorway smoke (Shuriken, HDRP, forward only, no ShaderGraph). OPTIONAL: nothing references it yet.
// Textures (kits_out\3d\hades_env\vfx_flipbooks\furnace_smoke_doorway README), both 8x8 sheets, linear data (sRGB off, no mips):
//   _RLUTex: R = light from Right, G = light from Left, B = light from Up, A = opacity (straight alpha)
//   _DBFTex: R = light from Down,  G = light from Back, B = light from Front, A = emissive mask (orange fire / ember glow)
// Lighting: three directional "lights" given as shader vectors (world-space direction TOWARD the light + colour), set from JSON by the C# that builds the material
// (see FLIPBOOK_INTEGRATION_SIXWAY.md). Each direction is moved into view space (billboard axes: +x right, +y up, +z toward the camera = Front) and blended with
// the usual six-way weights: weight_axis = L_axis^2 (sums to 1 for a unit vector), channel = the positive or negative side of that axis.
//   lit = sum_i colour_i * ( Lx^2 * (Lx >= 0 ? Right : Left) + Ly^2 * (Ly >= 0 ? Up : Down) + Lz^2 * (Lz >= 0 ? Front : Back) ) + _Ambient * mean(six channels)
// Limitation: billboards are not rotated in the shader, so layers using this shader should have startRotation / rotationSpeed 0 (otherwise the lighting rotates with the quad).
// Output is premultiplied like Furnace/Particle: rgb = (lit * albedo) * alpha + emissive, a = alpha; blend One / OneMinusSrcAlpha (default). Optional motion-vector warp with the
// same encoding as Furnace/FlipbookMV (_MVStrength 0 = off). Needs the same Shuriken custom vertex streams as Furnace/FlipbookMV for the frame blend:
//   Position, Color, UV (TEXCOORD0.xy), UV2 (TEXCOORD0.zw), AnimBlend (TEXCOORD1.x); without them it shows the single frame.
Shader "Furnace/SixWay"
{
    Properties
    {
        [NoScaleOffset] _RLUTex("Six-way RLU (R right, G left, B up, A opacity)", 2D) = "white" {}
        [NoScaleOffset] _DBFTex("Six-way DBF (R down, G back, B front, A emissive)", 2D) = "black" {}
        [NoScaleOffset] _MotionTex("Motion Vector Sheet (RG, linear)", 2D) = "gray" {}
        _Tiles("Tiles per side (8 = 8x8 sheet)", Float) = 8
        _MVScale("MV decode scale (xy)", Vector) = (2, 2, 0, 0)
        _MVBias("MV decode bias (xy)", Vector) = (-1, -1, 0, 0)
        _MVStrength("MV strength (0 = off)", Float) = 0
        _Tint("Albedo tint", Color) = (1,1,1,1)
        _Intensity("Light intensity (display-referred)", Float) = 1
        _Ambient("Ambient (mean of six channels)", Color) = (0.02, 0.02, 0.025, 1)
        _L0Dir("Light 0 direction to light (world xyz)", Vector) = (0, 0.35, 1, 0)
        _L0Col("Light 0 colour (HDR rgb)", Color) = (1, 0.3, 0.08, 1)
        _L1Dir("Light 1 direction to light (world xyz)", Vector) = (0, -1, 0.2, 0)
        _L1Col("Light 1 colour (HDR rgb)", Color) = (0.6, 0.12, 0.03, 1)
        _L2Dir("Light 2 direction to light (world xyz)", Vector) = (0, 1, -0.3, 0)
        _L2Col("Light 2 colour (HDR rgb)", Color) = (0.03, 0.035, 0.05, 1)
        _EmissiveColor("Emissive colour (HDR rgb)", Color) = (1, 0.35, 0.1, 1)
        _EmissiveIntensity("Emissive intensity (display-referred)", Float) = 0.5
        _ExposureWeight("Exposure Weight (0 display-referred, 1 nits)", Range(0, 1)) = 0
        _SoftDistance("Soft Particle Distance (m)", Float) = 0.5
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend("Dst Blend", Float) = 10
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

            TEXTURE2D(_RLUTex);
            SAMPLER(sampler_RLUTex);
            TEXTURE2D(_DBFTex);
            TEXTURE2D(_MotionTex);
            SAMPLER(sampler_MotionTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _RLUTex_TexelSize;
                float4 _MVScale;
                float4 _MVBias;
                float4 _Tint;
                float4 _Ambient;
                float4 _L0Dir;
                float4 _L0Col;
                float4 _L1Dir;
                float4 _L1Col;
                float4 _L2Dir;
                float4 _L2Col;
                float4 _EmissiveColor;
                float _Tiles;
                float _MVStrength;
                float _Intensity;
                float _EmissiveIntensity;
                float _ExposureWeight;
                float _SoftDistance;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float4 uv         : TEXCOORD0; // xy current frame, zw next frame (0 when the custom streams are not set)
                float  blend      : TEXCOORD1; // AnimBlend (0 when not set)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float4 uv         : TEXCOORD0;
                float  blend      : TEXCOORD1;
                float3 l0         : TEXCOORD2; // view-space unit directions toward the three lights
                float3 l1         : TEXCOORD3;
                float3 l2         : TEXCOORD4;
            };

            float3 ToView(float3 dirWS)
            {
                float3 d = mul((float3x3)UNITY_MATRIX_V, dirWS);
                float len2 = dot(d, d);
                return len2 > 1e-8 ? d * rsqrt(len2) : float3(0, 0, 1);
            }

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(v.positionOS); // camera-relative in HDRP
                o.positionCS = TransformWorldToHClip(positionWS);
                o.color = v.color;
                o.uv = v.uv;
                o.blend = v.blend;
                o.l0 = ToView(_L0Dir.xyz);
                o.l1 = ToView(_L1Dir.xyz);
                o.l2 = ToView(_L2Dir.xyz);
                return o;
            }

            struct Frame { float4 rlu; float4 dbf; };

            Frame SampleFrame(float2 uv, float warp)
            {
                float2 w = uv;
                if (abs(_MVStrength) > 0.00001)
                {
                    float2 cell = 1.0 / _Tiles;
                    float2 mv = SAMPLE_TEXTURE2D_LOD(_MotionTex, sampler_MotionTex, uv, 0).rg * _MVScale.xy + _MVBias.xy;
                    w = uv + mv * warp * (_MVStrength / _Tiles);
                    float2 cellMin = floor(uv * _Tiles) * cell;
                    float2 pad = 0.5 * _RLUTex_TexelSize.xy;
                    w = clamp(w, cellMin + pad, cellMin + cell - pad);
                }
                Frame f;
                f.rlu = SAMPLE_TEXTURE2D(_RLUTex, sampler_RLUTex, w);
                f.dbf = SAMPLE_TEXTURE2D(_DBFTex, sampler_RLUTex, w);
                return f;
            }

            // Six-way interpolation for one unit direction toward the light (view space: +x right, +y up, +z toward the camera = Front).
            float SixWay(float3 L, Frame f)
            {
                float3 wgt = L * L;
                float x = L.x >= 0.0 ? f.rlu.r : f.rlu.g; // Right : Left
                float y = L.y >= 0.0 ? f.rlu.b : f.dbf.r; // Up : Down
                float z = L.z >= 0.0 ? f.dbf.b : f.dbf.g; // Front : Back
                return wgt.x * x + wgt.y * y + wgt.z * z;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float t = saturate(i.blend);
                Frame f = SampleFrame(i.uv.xy, -t);
                if (t > 0.0001)
                {
                    Frame g = SampleFrame(i.uv.zw, 1.0 - t);
                    f.rlu = lerp(f.rlu, g.rlu, t);
                    f.dbf = lerp(f.dbf, g.dbf, t);
                }

                float3 lit = _L0Col.rgb * SixWay(i.l0, f) + _L1Col.rgb * SixWay(i.l1, f) + _L2Col.rgb * SixWay(i.l2, f);
                float mean6 = (f.rlu.r + f.rlu.g + f.rlu.b + f.dbf.r + f.dbf.g + f.dbf.b) * (1.0 / 6.0);
                lit += _Ambient.rgb * mean6;

                float4 tintVtx = i.color * _Tint;
                float soft = 1.0;
                if (_SoftDistance > 0.0001)
                {
                    float sceneEyeDepth = LinearEyeDepth(LoadCameraDepth(uint2(i.positionCS.xy)), _ZBufferParams);
                    soft = saturate((sceneEyeDepth - i.positionCS.w) / _SoftDistance);
                }

                float vis = tintVtx.a * soft;
                float a = f.rlu.a * vis;
                float3 rgb = lit * tintVtx.rgb * _Intensity * a                               // smoke body, premultiplied
                           + _EmissiveColor.rgb * (_EmissiveIntensity * f.dbf.a * vis);       // fire / ember glow, adds light independent of smoke opacity
                rgb = lerp(rgb, rgb * GetCurrentExposureMultiplier(), _ExposureWeight);
                return float4(rgb, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
