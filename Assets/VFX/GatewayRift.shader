// Furnace/GatewayRift: the dimensional rift in the Hades-gate doorway (HDRP, transparent, forward only, no lighting, no second camera).
// One quad sits just in front of the doorway opening. Inside the opening it renders a "window into another world":
//   * the world panorama (equirect, or a cubemap with _USE_CUBE) is sampled by the VIEW DIRECTION through each pixel of the frame
//     (worldPos - cameraPos, optionally reflected about the frame normal, spread to widen the field of view, rotated by yaw/pitch/roll + a slow spin),
//   * parallax layers: each layer lives on a sphere of radius _LayerNRadius around the frame centre, so the ray from the camera through the frame
//     meets it at a point that depends on the camera position (near layers slide against far ones = depth from any angle):
//       layer 0 = panorama (radius 1e4, infinite), layer 1 = equirect cloud layer (RGBA), layer 2 = procedural dust veil (3D noise).
// Around the opening (the quad is larger than the opening) it draws an edge halo: a ripple refraction of the opaque scene behind
// (HDRP colour pyramid via SampleCameraColor, valid in this forward transparent pass), a chromatic fringe, and crackling energy veins.
// _Open 0..1 drives the opening: a noise-masked dissolve from the centre outward with a hot rim; _Shock 0..1 sends a ring outward;
// _Flash adds a white-hot wash. Output is premultiplied (One, OneMinusSrcAlpha): additive glow terms add rgb without alpha.
// Intensity is display-referred (1.0 = white at any exposure) like Furnace/Particle: _ExposureWeight 0 display, 1 scene nits.
Shader "Furnace/GatewayRift"
{
    Properties
    {
        [NoScaleOffset] _PanoTex("Panorama (equirect, sRGB)", 2D) = "black" {}
        [NoScaleOffset] _CloudTex("Parallax cloud layer (equirect RGBA)", 2D) = "black" {}
        [NoScaleOffset] _Cube("Panorama cubemap (needs _USE_CUBE)", Cube) = "black" {}
        [Toggle(_USE_CUBE)] _UseCube("Use cubemap instead of equirect", Float) = 0

        _Tint("Panorama tint", Color) = (1,1,1,1)
        _Intensity("HDR intensity (display-referred)", Float) = 1
        _ExposureWeight("Exposure weight (0 display, 1 nits)", Range(0, 1)) = 0
        _StarBoost("Highlight boost (stars bloom)", Float) = 1.5

        _Open("Open 0..1", Range(0, 1)) = 0
        _Flash("Flash 0..1", Range(0, 1)) = 0
        _Shock("Shock ring progress 0..1 (0 = off)", Range(0, 1)) = 0

        _QuadSize("Quad size metres (xy)", Vector) = (5.6, 6.6, 0, 0)
        _FrameHalf("Opening half size metres (xy)", Vector) = (2, 2.5, 0, 0)
        _CornerRadius("Opening corner radius (m)", Float) = 0.15
        _EdgeSoft("Opening edge softness (m)", Float) = 0.05
        _EdgeDarken("Edge darkening (tunnel depth)", Range(0, 1)) = 0.45
        _InnerFade("Edge darkening reach (m)", Float) = 0.7

        _Rot("View rotation yaw/pitch/roll (deg)", Vector) = (0, 0, 0, 0)
        _SpinSpeed("Panorama spin (deg/s yaw)", Float) = 0.6
        _Spread("Field-of-view spread (1 = true view)", Float) = 2.2
        _Reflect("Reflect view about frame normal 0..1", Range(0, 1)) = 0

        _L0Radius("Layer 0 radius (m)", Float) = 10000
        _L1Radius("Layer 1 radius (m)", Float) = 60
        _L1Intensity("Layer 1 intensity", Float) = 0.9
        _L1Tint("Layer 1 tint", Color) = (1,1,1,1)
        _L1Drift("Layer 1 drift (deg/s)", Float) = 1.5
        _L2Radius("Layer 2 radius (m)", Float) = 14
        _L2Intensity("Layer 2 intensity", Float) = 0.5
        _L2Color("Layer 2 colour", Color) = (0.5,0.25,1,1)
        _L2Scale("Layer 2 noise scale", Float) = 3.2
        _L2Speed("Layer 2 noise speed", Float) = 0.07

        _DissolveNoise("Dissolve noise amount", Float) = 0.45
        _DissolveScale("Dissolve noise scale (1/m)", Float) = 1.6
        _DissolveSoft("Dissolve softness", Float) = 0.12
        _RimWidth("Dissolve rim width", Float) = 0.14
        [HDR] _RimColor("Dissolve rim colour (display HDR)", Color) = (3,1.6,6,1)

        [HDR] _VeinColor("Vein colour (display HDR)", Color) = (2.2,1.1,5,1)
        _VeinScale("Vein scale (1/m)", Float) = 2.4
        _VeinSpeed("Vein speed", Float) = 0.9
        _VeinSharp("Vein sharpness", Float) = 10
        _VeinReach("Vein reach inside the opening (m)", Float) = 0.8
        _VeinOutReach("Vein reach outside the opening (m)", Float) = 0.45
        _VeinCrackle("Vein crackle (0 steady, 1 flickering)", Range(0, 1)) = 0.8

        _HaloWidth("Halo width (m)", Float) = 0.55
        _RippleAmp("Ripple amplitude (screen uv)", Float) = 0.012
        _RippleFreq("Ripple frequency (1/m)", Float) = 9
        _RippleSpeed("Ripple speed", Float) = 3.5
        _ChromaAmt("Chromatic fringe (fraction of ripple)", Float) = 0.6
        _RefractMix("Refraction on (0 = glow only, 1 = on)", Range(0, 1)) = 1
        [HDR] _HaloColor("Halo glow colour (display HDR)", Color) = (0.6,0.3,1.4,1)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }

            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local_fragment _ _USE_CUBE

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            TEXTURE2D(_PanoTex);
            SAMPLER(sampler_PanoTex);
            TEXTURE2D(_CloudTex);
            SAMPLER(sampler_CloudTex);
            TEXTURECUBE(_Cube);
            SAMPLER(sampler_Cube);

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _Intensity;
                float _ExposureWeight;
                float _StarBoost;
                float _Open;
                float _Flash;
                float _Shock;
                float4 _QuadSize;
                float4 _FrameHalf;
                float _CornerRadius;
                float _EdgeSoft;
                float _EdgeDarken;
                float _InnerFade;
                float4 _Rot;
                float _SpinSpeed;
                float _Spread;
                float _Reflect;
                float _L0Radius;
                float _L1Radius;
                float _L1Intensity;
                float4 _L1Tint;
                float _L1Drift;
                float _L2Radius;
                float _L2Intensity;
                float4 _L2Color;
                float _L2Scale;
                float _L2Speed;
                float _DissolveNoise;
                float _DissolveScale;
                float _DissolveSoft;
                float _RimWidth;
                float4 _RimColor;
                float4 _VeinColor;
                float _VeinScale;
                float _VeinSpeed;
                float _VeinSharp;
                float _VeinReach;
                float _VeinOutReach;
                float _VeinCrackle;
                float _HaloWidth;
                float _RippleAmp;
                float _RippleFreq;
                float _RippleSpeed;
                float _ChromaAmt;
                float _RefractMix;
                float4 _HaloColor;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionRWS : TEXCOORD1;
                float3 centerRWS   : TEXCOORD2;
                float3 axisZ       : TEXCOORD3;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(v.positionOS); // camera-relative in HDRP
                o.positionCS = TransformWorldToHClip(positionWS);
                o.uv = v.uv;
                o.positionRWS = positionWS;
                float4x4 m = GetObjectToWorldMatrix();
                o.centerRWS = float3(m._m03, m._m13, m._m23);
                o.axisZ = normalize(float3(m._m02, m._m12, m._m22)); // quad local +Z in world: the way the portal looks (+Z world with identity rotation)
                return o;
            }

            // ---------------------------------------------------------------- noise (hash-based value noise)
            float Hash13(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.zyx + 31.32);
                return frac((p.x + p.y) * p.z);
            }

            float VNoise3(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = Hash13(i);
                float n100 = Hash13(i + float3(1, 0, 0));
                float n010 = Hash13(i + float3(0, 1, 0));
                float n110 = Hash13(i + float3(1, 1, 0));
                float n001 = Hash13(i + float3(0, 0, 1));
                float n101 = Hash13(i + float3(1, 0, 1));
                float n011 = Hash13(i + float3(0, 1, 1));
                float n111 = Hash13(i + float3(1, 1, 1));
                return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                            lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
            }

            float Fbm3(float3 p)
            {
                float s = 0.0;
                float a = 0.5;
                for (int o = 0; o < 3; o++)
                {
                    s += a * VNoise3(p);
                    p = p * 2.03 + 17.3;
                    a *= 0.5;
                }
                return s / 0.875;
            }

            // ---------------------------------------------------------------- directions
            float3 RotateYaw(float3 d, float a)
            {
                float s, c;
                sincos(a, s, c);
                return float3(c * d.x + s * d.z, d.y, -s * d.x + c * d.z);
            }

            float3 RotatePitch(float3 d, float a)
            {
                float s, c;
                sincos(a, s, c);
                return float3(d.x, c * d.y - s * d.z, s * d.y + c * d.z);
            }

            float3 RotateRoll(float3 d, float a)
            {
                float s, c;
                sincos(a, s, c);
                return float3(c * d.x - s * d.y, s * d.x + c * d.y, d.z);
            }

            // world direction -> panorama space: data rotation + spin about the vertical
            float3 ToPano(float3 d, float extraYawDeg)
            {
                float3 r = RotateRoll(d, radians(_Rot.z));
                r = RotatePitch(r, radians(_Rot.y));
                r = RotateYaw(r, radians(_Rot.x + extraYawDeg));
                return r;
            }

            // ray from the camera through frame point h (relative to the frame centre) meets a sphere of radius R around the frame centre
            float3 SphereDir(float3 h, float3 V, float R)
            {
                float b = dot(h, V);
                float c = dot(h, h) - R * R;
                float t = -b + sqrt(max(b * b - c, 0.0));
                return normalize(h + V * t);
            }

            void EquirectUV(float3 d, out float2 uv, out float2 dx, out float2 dy)
            {
                float u = atan2(d.x, d.z) * INV_TWO_PI + 0.5;
                float v = asin(clamp(d.y, -1.0, 1.0)) * INV_PI + 0.5;
                float u2 = frac(u + 0.5) - 0.5; // continuous across the atan seam
                float2 uvA = float2(u, v);
                float2 uvB = float2(u2, v);
                float2 dxA = ddx(uvA);
                float2 dyA = ddy(uvA);
                float2 dxB = ddx(uvB);
                float2 dyB = ddy(uvB);
                bool useB = (abs(dxA.x) + abs(dyA.x)) > (abs(dxB.x) + abs(dyB.x));
                uv = useB ? uvB : uvA;
                dx = useB ? dxB : dxA;
                dy = useB ? dyB : dyA;
            }

            float3 SamplePano(float3 d)
            {
            #if defined(_USE_CUBE)
                return SAMPLE_TEXTURECUBE(_Cube, sampler_Cube, d).rgb;
            #else
                float2 uv, dx, dy;
                EquirectUV(d, uv, dx, dy);
                return SAMPLE_TEXTURE2D_GRAD(_PanoTex, sampler_PanoTex, uv, dx, dy).rgb;
            #endif
            }

            float4 SampleClouds(float3 d)
            {
                float2 uv, dx, dy;
                EquirectUV(d, uv, dx, dy);
                return SAMPLE_TEXTURE2D_GRAD(_CloudTex, sampler_CloudTex, uv, dx, dy);
            }

            // signed distance to the rounded-rectangle opening (negative inside), metres
            float OpeningSDF(float2 m)
            {
                float2 hf = _FrameHalf.xy;
                float r = min(_CornerRadius, min(hf.x, hf.y) - 0.001);
                float2 q = abs(m) - (hf - r);
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float t = _TimeParameters.x;
                float2 m = (i.uv - 0.5) * _QuadSize.xy;   // metres from the frame centre: x right, y up
                float d = OpeningSDF(m);                  // < 0 inside the opening
                float exposure = lerp(1.0, GetCurrentExposureMultiplier(), _ExposureWeight);

                float3 V = normalize(i.positionRWS - GetCurrentViewPosition()); // camera -> pixel (camera-relative rendering: view position is the origin)
                float3 h = i.positionRWS - i.centerRWS;                         // frame point relative to the frame centre
                float3 F = i.axisZ;                                             // the way the portal looks

                // ------------------------------------------------ opening dissolve: from the centre outward, noise-masked
                float2 nrm = m / max(_FrameHalf.xy, 0.01);
                float rc = length(nrm);
                float nz = Fbm3(float3(m * _DissolveScale, t * 0.35));
                float field = rc + (nz - 0.5) * _DissolveNoise;
                float front = lerp(-0.35, 1.55 + 0.6 * _DissolveNoise + _DissolveSoft, saturate(_Open));
                float xr = front - field;
                float reveal = saturate(xr / max(_DissolveSoft, 0.001));
                float rim = smoothstep(0.0, _RimWidth * 0.3, xr) * (1.0 - smoothstep(_RimWidth * 0.3, _RimWidth, xr));
                rim *= step(0.0001, _Open) * (1.0 - smoothstep(0.92, 1.0, _Open)); // the rim dies as the dissolve completes
                float inMask = saturate(-d / max(_EdgeSoft, 0.001));

                float3 rgb = float3(0, 0, 0);
                float alpha = 0.0;

                // ------------------------------------------------ inside: the other world
                {   // evaluated for every pixel on purpose: ddx/ddy inside EquirectUV must stay in uniform control flow
                    float3 Vs = normalize(F + (V - F) * _Spread);               // widen the field of view through the frame
                    Vs = normalize(lerp(Vs, reflect(Vs, F), _Reflect));
                    float spin = _SpinSpeed * t;

                    float3 d0 = ToPano(Vs, spin);
                    float3 col = SamplePano(d0) * _Tint.rgb;
                    float mx = max(col.r, max(col.g, col.b));
                    col *= 1.0 + _StarBoost * smoothstep(0.55, 1.0, mx);
                    col *= _Intensity;

                    // layer 1: cloud sphere (parallax)
                    float3 hs = h * (1.0 / max(_Spread, 0.001));
                    float3 d1 = ToPano(SphereDir(hs, Vs, _L1Radius), spin + _L1Drift * t);
                    float4 cl = SampleClouds(d1);
                    float ca = saturate(cl.a);
                    col = col * (1.0 - ca * 0.85) + cl.rgb * _L1Tint.rgb * (_L1Intensity * _Intensity) * ca;

                    // layer 2: procedural dust veil, nearest sphere (strongest parallax)
                    float3 d2 = ToPano(SphereDir(hs, Vs, _L2Radius), spin * 1.6);
                    float vn = Fbm3(d2 * _L2Scale + float3(0, t * _L2Speed, t * _L2Speed * 0.6));
                    float veil = smoothstep(0.46, 0.80, vn);
                    col += _L2Color.rgb * (veil * _L2Intensity * _Intensity);

                    // tunnel depth: darker toward the frame so the opening reads as a hole with thickness
                    float depthFade = lerp(1.0 - _EdgeDarken, 1.0, saturate(-d / max(_InnerFade, 0.001)));
                    col *= depthFade;

                    rgb += col * exposure * (inMask * reveal);
                    alpha += inMask * reveal;
                }

                // ------------------------------------------------ energy veins along the frame (inside and a little outside), crackling
                float zoneIn = saturate(1.0 + d / max(_VeinReach, 0.001)) * step(d, 0.0);
                float zoneOut = saturate(1.0 - d / max(_VeinOutReach, 0.001)) * step(0.0, d);
                float zone = max(zoneIn, zoneOut);
                float qEdge = min(_QuadSize.x * 0.5 - abs(m.x), _QuadSize.y * 0.5 - abs(m.y)); // distance to the quad border: fade everything to 0 there
                float edgeFade = smoothstep(0.0, 0.35, qEdge);
                if (zone > 0.001 && _Open > 0.0)
                {
                    float vv = VNoise3(float3(m * _VeinScale, t * _VeinSpeed));
                    float vw = VNoise3(float3(m * _VeinScale * 2.1 + 7.0, t * _VeinSpeed * 1.7));
                    float ridge = 1.0 - abs(2.0 * (0.65 * vv + 0.35 * vw) - 1.0);
                    float lines = pow(saturate(ridge), _VeinSharp);
                    float gate = lerp(1.0, smoothstep(0.35, 0.85, VNoise3(float3(m * 0.9, t * 4.0))), _VeinCrackle);
                    float vein = lines * zone * gate * smoothstep(0.0, 0.25, _Open) * edgeFade;
                    rgb += _VeinColor.rgb * vein * exposure;
                }

                // ------------------------------------------------ dissolve rim, flash, shock ring (glow only)
                rgb += _RimColor.rgb * rim * inMask * exposure;
                rgb += _Flash * inMask * float3(1.0, 0.92, 0.85) * 6.0 * exposure;

                // ------------------------------------------------ outside the opening: halo = ripple refraction + chromatic fringe + glow
                if (d > 0.0 && _Open > 0.0)
                {
                    float halo = exp(-d / max(_HaloWidth, 0.001));
                    float shockR = _Shock * (_HaloWidth * 5.0 + 1.0);
                    float shockBand = (_Shock > 0.0001) ? exp(-((d - shockR) / 0.22) * ((d - shockR) / 0.22)) * (1.0 - _Shock) : 0.0;
                    float openGain = smoothstep(0.0, 0.5, _Open);
                    float wave = sin(d * _RippleFreq - t * _RippleSpeed + nz * 3.0);
                    float amp = (halo * openGain + shockBand * 2.5) * wave * edgeFade;

                    float2 dirOut = normalize(m + 1e-4);
                    float2 uvS = i.positionCS.xy * _ScreenSize.zw;
                    float2 off = float2(dirOut.x, -dirOut.y) * (_RippleAmp * amp);
                    float refA = saturate(halo * openGain * 1.2 + shockBand) * _RefractMix * edgeFade;
                    if (refA > 0.001)
                    {
                        float3 sc;
                        sc.r = SampleCameraColor(saturate(uvS + off * (1.0 + _ChromaAmt)), 0).r;
                        sc.g = SampleCameraColor(saturate(uvS + off), 0).g;
                        sc.b = SampleCameraColor(saturate(uvS + off * (1.0 - _ChromaAmt)), 0).b;
                        rgb += sc * refA;
                        alpha += refA;
                    }
                    rgb += _HaloColor.rgb * (halo * halo * openGain + shockBand * 0.8) * edgeFade * exposure;
                }

                alpha = saturate(alpha);
                return float4(rgb, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
