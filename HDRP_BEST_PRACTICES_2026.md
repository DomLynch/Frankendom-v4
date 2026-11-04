# HDRP Best Practices 2026 - Hades gate arena on Radeon 8060S (RDNA 3.5 iGPU)

Written 2026-11-04. Unity 6000.6.7f1, HDRP 17.6.

## How reliable this document is (read first)

- Web research was limited. Web search worked. Direct page fetches of docs.unity3d.com and unity.com FAILED (DNS errors), so no Unity manual page was read in full.
- Items marked **[WEB]** come from search-result summaries with a URL.
- Items marked **[KNOWLEDGE]** come from model knowledge of HDRP 14-17 manuals (training cutoff mid-2026). Treat these as starting values, not verified facts.
- Anything marked **UNVERIFIED** could not be checked. Anything with numbers has not been measured on this hardware.
- Search results never contained an HDRP 17.4-17.6 or Unity 6.6 changelog. Check the package CHANGELOG.md in `Library/PackageCache/com.unity.render-pipelines.high-definition@*` (local, authoritative).

## Biggest strategic finding

Unity's "Render Pipelines strategy for 2026" says HDRP is in maintenance. [WEB]
- No new HDRP features are planned.
- HDRP is not being deprecated. Near-term work is stability, regressions, critical issues, plus Switch 2 support in 2026 (version timing UNVERIFIED).
- URP gets the new investment. Render Graph was unified in 6.3.
- Consequence for this project: expect regressions to be fixed slowly, and design for a stable feature subset, not for new HDRP features. Pin the HDRP version and avoid upgrading casually.
- Source: https://unity.com/topics/render-pipelines-strategy-for-2026 ; discussion https://discussions.unity.com/t/render-pipelines-strategy-for-2026/1710004 ; 80.lv summary https://80.lv/articles/unity-unveils-2026-render-pipelines-strategy/ (2026; exact date UNVERIFIED).

---

## Executive checklist (ordered by impact/cost for this scene)

1. Set Lit Shader Mode to Deferred or Both, DX12 only, Render Graph on (always on in Unity 6). Remove DX11/Vulkan from the Windows API list. Turn off Static Batching if RT or GPU Resident Drawer is used.
2. Use physical light units. Moon directional light about 0.1-0.5 lux plus a bluish ambient, furnace as point/rect lights in lumen, then set Exposure to Fixed or Automatic with clamps (see Section 1). Exposure drives the whole night look.
3. Bake Adaptive Probe Volumes (APV) for the arena for diffuse GI and use the lighting scenarios/blending for "furnace on/off". APV is the cheapest high-quality GI on an iGPU.
4. Use Volumetric Fog (Balance mode, 64 slices, denoise on) plus 4-8 Local Volumetric Fog volumes with 3D noise masks for fog banks and the door shaft. This is the main "Unreal-like" mood gain and moderately cheap.
5. Use STP (Spatial-Temporal Post-processing) upscaler at Quality/Balanced, with hardware Dynamic Resolution on DX12. Do not use FSR2 unless the com.unity.amd package is needed (see Section 3).
6. Use SSR (Screen Space Reflections, Lit shader mode) for wet stone and water, with Ray-Tracing reflections as an optional "Ultra" tier only.
7. Use SSGI sparingly (half res) as a High-tier add-on over APV. Contact Shadows on the key light only. Keep shadow resolution moderate (2048 cascades for a small arena).
8. Water System (River/Ocean surface) with foam, caustics only on High; keep simulation resolution low. Verify DX12 water cost with the GPU profiler first.
9. Skin/hair/eyes: build diffusion profiles from the Digital Human sample values, Physical hair (Marschner) for hero hair with cards, Eye cinematic shader. Budget about 20-30 percent of frame time for the MetaHuman face close-ups only.
10. Decals (DBuffer) for blood/wet, Detail maps on Lit for stone, POM for stair detail. Avoid HDRP tessellation (feature status UNVERIFIED in 17.6).
11. VFX Graph for embers/sparks: HDRP Lit or Unlit output with Exposure Weight tuned, motion vectors on, soft particles. Keep particle counts below 20k on the iGPU.
12. Post: ACES or Neutral plus a custom LUT, subtle bloom (0.1-0.3), cinematic-only DoF, per-object motion blur low, tiny film grain. Dark scenes need contrast control so crushed blacks do not band (add dithering).
13. Perf tooling: GPU Resident Drawer on, GPU occlusion culling on, per-camera frame settings, three HDRP quality assets (Medium/High/Ultra RT). Profile with the Rendering Debugger and RenderDoc/PIX/RGP.
14. Ray tracing: treat it as the "Ultra RT" tier only. Use RT reflections in Performance mode at half resolution, RT AO or RT shadows (pick one), skip RTGI. Path tracing for stills/screenshots only.
15. Workflow: one Editor per project; agents serialize through the single live Editor via the Unity CLI/Pipeline; use separate project clones/worktrees for batch builds; screenshots via CLI exec with explicit compile/play waits.

---

## 1. Lighting

### Physical light units and exposure (night)
- HDRP uses physical units: directional in lux, others in lumen/candela/EV. [KNOWLEDGE]
- Starting values (artistic night, not realistic moonlight):
  - Moon directional light: 0.3-2 lux with color about (0.55, 0.65, 1.0). Real full moon is about 0.1-0.3 lux, which needs very long exposure.
  - Furnace glow: Point/Rect light 5,000-50,000 lumen, warm 1800-2200 K.
  - Embers/torches: Point lights 500-3,000 lumen.
- Exposure: Volume Exposure override, Mode Fixed, EV100 about 2 to 6 for a lit-night look, or Automatic with Limit Min about -2 and Limit Max about 8 and slow adaptation. Fixed exposure keeps screenshots comparable across agent runs. Pick Fixed for automated captures. [KNOWLEDGE]
- Cost: negligible. Pitfall: mixing Intensity units without checking, then "fixing" with a huge exposure shift.
- Source: HDRP manual, Physical Light Units (versioned page). https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/Physical-Light-Units.html (URL unchecked, UNVERIFIED)

### Adaptive Probe Volumes (APV) vs lightmaps
- APV is supported in HDRP and is the recommended path for dynamic objects and static lighting without UV lightmaps. Unity 6 uses a new baking backend, and 17.2 allows combining Sky Occlusion baking with APV. [WEB]
- Use APV for diffuse GI, lighting scenarios for furnace on/off, and Probe Volume Streaming only if the world grows. Use a Reflection Probe (baked) per room for specular. [KNOWLEDGE]
- Settings: min probe spacing about 1 m (indoor ruin detail) to 2 m; max subdivision 3-4; Brick/Probe placement "Rendering Layers" to avoid leaks; Leak Reduction mode Validity and Normal Bias. Keep SH bands L1 for Medium, L2 for High/Ultra. [KNOWLEDGE, UNVERIFIED values]
- Lightmaps: only worth it for large static environment surfaces with sharp contact; needs lightmap UVs on every prop, longer bake. GPU Progressive Lightmapper on AMD is supported but historically less stable than on NVIDIA; use CPU for final bakes if GPU bakes fail. [KNOWLEDGE]
- Cost on iGPU: APV about 0.3-1 ms; bake time is the real cost.
- Pitfall: dark scenes show light leaks as visible glow; always inspect with the Rendering Debugger Probe Volume views.
- Source: https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.2/manual/whats-new-17.html (2025) via search summary.

### SSGI vs ray-traced GI
- SSGI is a screen-space effect with a Full Resolution toggle; half resolution is usually enough. Costs about 2-5 ms on an iGPU at 1080p. [KNOWLEDGE, UNVERIFIED on this GPU]
- RTGI: expensive, use only in Ultra RT. APV plus SSGI is a better value. See Section 2.
- Pitfall: SSGI disoccludes at screen edges and fights APV in dark areas; keep Intensity modest and let APV carry the base.

### Rect area lights, light layers, contact shadows, soft shadows
- Rect area lights: supported with LTC shading, shadows only via ray tracing for rect; use as the furnace-door fill and keep shadow off. [KNOWLEDGE]
- Light Layers: enable in HDRP asset and use to prevent the furnace light from lighting distant fog/background and to isolate character lights (rim). [KNOWLEDGE]
- Contact Shadows: enable on key directional light, Length 0.15-0.3 m, Fade distance 15-30 m; cheap (about 0.3-0.8 ms). [KNOWLEDGE]
- Shadow quality: PCSS is not an HDRP built-in filter name in Unity 6; HDRP has Filtering Quality Low/Medium/High (High uses PCSS-like soft shadows with blocker search on directional/punctual lights). Use Medium for High tier, High for Ultra. RT shadows are the highest quality (Section 2). [KNOWLEDGE, UNVERIFIED]
- Cascade shadow maps: 3-4 cascades, 2048 resolution for a small arena, max distance 60 m.

---

## 2. Ray tracing in HDRP on AMD RDNA

### Facts
- HDRP ray tracing is DX12 only; enable Realtime Ray Tracing in the HDRP asset (triggers recompile); disable static batching; use recent Windows 10 1809 or Windows 11 and current drivers. [WEB] https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition%4015.0/manual/Ray-Tracing-Getting-Started.html (HDRP 15 page; GPU list in that page was NVIDIA-only, AMD RDNA2+ DXR works in practice, UNVERIFIED for Unity 6000.6)
- Ray-tracing modes per effect: Performance or Quality, and "Both" in the asset allows per-effect choice. Mixed tracing (Ray Marching plus Ray Tracing for hit fallback) is available for reflections and GI. [WEB + KNOWLEDGE]
- Path tracing: DX12 only, 17.0 added disc and tube area lights and separate volumetric fog denoise. [WEB] https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/whats-new-17.html (2024).
- Denoisers: OptiX is NVIDIA only; use Intel OIDN for path-traced stills. [KNOWLEDGE]
- Known issues:
  - Unity forum thread "UNITY 6 Raytracing - completely broken" (reports of fresh projects failing with RT enabled in 6000.0.20) - single-user report, not AMD-specific. https://discussions.unity.com/t/unity-6-raytracing-completely-broken-in-every-way-imaginable/1523367 [WEB]
  - Issue Tracker: AMD crash enabling path tracing in the HDRP sample scene (older versions, fixed by UUM-59522). https://issuetracker.unity3d.com/issues/amd-crash-on-gfxdeviced3d12base-drawbufferscommon-when-enabling-path-tracing-in-hdrp-sample-scene [WEB]
  - Forum thread (2025) says DX12 stability in Unity 6 HDRP is sometimes poor. [WEB]

### What is worth it (RDNA 3.5, 40 CU iGPU, estimates UNVERIFIED)
| Effect | Worth it? | Notes |
|---|---|---|
| RT Reflections | Yes, Ultra only | Performance mode, quarter/half res, ray length 20-50 m, 1 sample; fixes wet stone and water mirrors that SSR cannot do. Estimated 4-10 ms. |
| RT AO | Maybe | Replaces SSAO; half res; short ray; 2-4 ms |
| RT Shadows | Maybe for moon key light | Soft area shadows; cost per light; skip for many lights |
| RTGI | No | Too costly on iGPU; use APV+SSGI |
| Path tracing | Stills only | Use for hero screenshots: 128-512 samples, OIDN; not real time |
| Recursive RT / sub-surface RT | No | Skip |

- Pitfalls: acceleration-structure build costs scale with instanced objects; skinned characters add per-frame BLAS updates; foliage with alpha test increases any-hit cost; keep RT Culling distance short (50-100 m). Layer mask RT to exclude tiny props. [KNOWLEDGE]
- Run `dxdiag`/PIX to confirm the DXR tier (1.1). Strix Halo's Radeon 8060S supports DXR 1.1. [KNOWLEDGE; UNVERIFIED on driver version]

---

## 3. Upscaling and anti-aliasing (Unity 6 HDRP)

- STP: spatial-temporal upscaler; available on all platforms from HDRP 17.2 (Unity 6.2); better quality than TAAU; supports dynamic resolution only with hardware dynamic resolution (DX12 provides). [WEB] https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.2/manual/whats-new-17.html (2025)
- Dynamic resolution: hardware DRS is the default on DX12; software DRS fallback. STP and TAAU run before post-process and only work with the TAA anti-aliasing mode. [WEB] https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.2/manual/Dynamic-Resolution.html
- FSR 2: available via the internal com.unity.amd package (FidelityFX Super Resolution 2.1 per HDRP 17.0 notes); exposes sharpness/quality; auto-recommend overrides the manual scale callback. [WEB] same pages. FSR3 frame generation: not found in any source, UNVERIFIED (assume not in HDRP).
- DLSS: NVIDIA only; not applicable.
- Recommendation: TAA (or STP) at Quality = about 67 percent scale for Ultra, 59 percent for High, 50 percent for Medium on 1440p/4K output. Sharpen 0.3-0.5. Use the "Anti-ghosting" and "Motion Rejection" options conservatively with fur/hair. [KNOWLEDGE]
- Pitfalls: hair cards with alpha clip + TAA = shimmering; use alpha-to-coverage off and Transparent Depth Postpass; render particles with motion vectors; Camera Cuts need `ResetPostProcessingHistory` to avoid ghosting in cinematics.

---

## 4. Atmosphere

- Physically Based Sky: Space emission texture for stars; set Planet/Atmosphere to Earth; for night, put the sun directional below horizon and use a second directional as moon; PBR sky is about 0.5-1 ms plus ~1 ms per sky update on iGPU. [KNOWLEDGE] Alternative: HDRI sky (cheapest) for stylised hell gate.
- Volumetric Clouds: presets Sparse/Cloudy/Overcast/Stormy/Custom; local clouds (cloud volumes) added in later HDRP builds - exact version UNVERIFIED; use low temporal accumulation and reduced cloud resolution. Enable shadows for ground cloud shadow only on Ultra. Cost 1-3 ms.
- Volumetric Fog: Volumetric Fog override with Balance mode: Volume Slice Count 64 (32 for Medium), Resolution Percentage 12.5-25, denoising on, Fog Distance 60-120 m, Anisotropy 0.4-0.7 (forward scatter for the door shaft). Costs 1-2.5 ms.
- Local Volumetric Fog: Box/OBB volumes with a Texture3D (32^3 to 64^3 Perlin/worley) as the Density Mask; scroll the texture via Scroll Speed to animate fog banks; blend distance 2-4 m; keep count under 10 overlapping. The furnace doorway: a thin LVF cone plus a spot/rect light with volumetric Multiplier 2-5. [KNOWLEDGE]
- Water System: Water Surface (River, Ocean, Pool) with the Water Rendering toggle in the HDRP asset and Frame Settings; foam, caustics (set Resolution 256-512), underwater, deformers. Needs DX12 or Vulkan; set Simulation Resolution 128 for a small river; keep the water volume small. Cost 1-4 ms, caustics extra. [KNOWLEDGE] Docs: https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/water.html (URL UNVERIFIED)
- Pitfalls: heavy fog with TAA smears; volumetric fog denoise adds ghosting; keep Max Fog Distance low. Fog and Physically Based Sky both need the Fog override and an Exposure that matches.

---

## 5. Characters

### SSS and skin
- HDRP uses Diffusion Profile assets in HDRP Global Settings (up to 15 in view). Fields: Scattering Distance (RGB), Max Radius (mm), Texturing Mode (Post-scatter for face with high-res textures), Transmission, Index of Refraction 1.4, Dual lobe smoothness, Lobe mix. [KNOWLEDGE]
- Starting skin values (UNVERIFIED, from memory of HDRP defaults/Digital Human sample): Scattering distance red-heavy, e.g. (0.8, 0.35, 0.25) with Max Radius about 3-6 mm; Index of Refraction 1.4; Dual lobe on, Smoothness1 about 0.65-0.8 and Smoothness2 about 0.3-0.5, mix about 0.5-0.7. Start from the Digital Human sample profile and tweak. Do not trust these numbers; compare against the sample.
- Sample budget (HDRP asset > Subsurface Scattering): Low/Medium/High; cost 1-3 ms for a full-screen face. Use High only for cinematic close-ups. [KNOWLEDGE]
- Skin LUT/pre-integration: HDRP uses pre-integrated scattering internally; the "skin LUT" is in the diffusion profile (Subsurface Scattering Pre-integrated). The Digital Human sample also ships a skin shader with custom curvature/diffusion; UNVERIFIED which packages carry it in Unity 6.

### Hair
- HDRP Hair shader (Shader Graph master stack, Hair): Material Type Approximate (Kajiya-Kay, cards only, cheaper, best for dark hair) vs Physical (Marschner, strands or cards, best for light hair), Multiple Scattering optional. [WEB] https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/HDRP-Features.html ; https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@15.0/manual/master-stack-hair.html
- Semi-transparent hair cards must be sorted manually back-to-front; use alpha clip + Transparent Depth Postpass for most cards, and separate meshes for sorting-critical layers. [WEB]
- Strands (Hair System): Unity Demo Team packages `com.unity.demoteam.hair` and `com.unity.demoteam.digital-human` are on GitHub (Unity-Technologies org), not the registry. Availability and Unity 6.6 compatibility UNVERIFIED. [KNOWLEDGE]
- Dracula long hair: dark hair so Approximate model + cards; fur collar: cards with Hair shader or alpha-clipped fur shells; avoid per-strand on iGPU.

### Eye
- HDRP Eye shader: Cinematic (caustics, refraction, parallax iris) and Classic. Use Cinematic for MetaHuman close-ups; add Specular Occlusion. [WEB, Enemies] https://80.lv/articles/enemies-the-working-process-behind-unity-s-new-tech-demo/ (2022) and https://unity.com/fr/blog/news/introducing-enemies-the-latest-evolution-in-high-fidelity-digital-humans-from-unity (2022)

### Enemies/Digital Human learnings (2022 sources, not Unity 6 verified)
- New skin shader, GPU skin attachment for vellus hair, eye caustics, SSGI, RT reflections, DLSS. Digital Human 2.0 package was promised. Unity 6 package names/availability UNVERIFIED. [WEB]

---

## 6. Materials and surfaces

- Lit detail maps: use Detail Map (albedo/normal/smoothness packed) with tiling 4-8x for stone to prevent blur at 7 m camera range.
- Decals: Decal Projector (DBuffer) with Decal Layers; blood and wet splats via Decal Shader Graph; enable Metal and AO affect; atlas size 4096; draw distance 30-60 m. Cost low. [KNOWLEDGE]
- Wetness/puddles: use a Layered Lit or custom Shader Graph with a global "Wetness" parameter driving Smoothness up, Albedo darker, and puddle mask; SSR/RT reflections make it pay off.
- Tessellation: HDRP Lit tessellation is not RT compatible and may be deprecated; prefer POM (Parallax Occlusion Mapping) via Height Map for stairs/ruins; use displacement meshes only for hero pieces. Status in 17.6 UNVERIFIED.
- Texture streaming: Mip Streaming is supported with a Texture Streaming Memory Budget; with 48 GB shared memory, set budget 4-8 GB and keep Streaming on for 4K props; Streaming Virtual Texturing in HDRP is experimental/UNVERIFIED.

---

## 7. VFX (VFX Graph)

- Output contexts: HDRP Lit Quad/Mesh, HDRP Unlit (for fire/embers), Distortion Quad for heat haze; Lit allows emissive plus lighting, Unlit allows Exposure Weight control (set 0 for glow-independent embers or 1 for HDRP-correct brightness). [KNOWLEDGE]
- Motion vectors: set `Generate Motion Vector` on outputs for TAA/STP; otherwise embers trail ghost. Add Soft Particles (Soft Particle Fade Distance 0.1-0.5 m).
- Distortion: use `Output Particle HDRP Distortion Quad` with a normal map; requires "Distortion" in Frame Settings. Cheap but screen-space.
- Cost: keep embers below 20k particles on the iGPU; use Fixed Delta Time and Sort Mode Off when not needed; GPU sort for transparent layers only. Blood: use VFX Graph meshes plus decals.
- Lights from particles: use real Point Lights in a small pool (4-8) driven by script for furnace and spark flashes; VFX Graph particle lights are not a built-in HDRP feature, UNVERIFIED.

---

## 8. Post-processing

- Tonemapping: ACES is filmic with hue shifts (reds shift orange; furnace glows will desaturate). Neutral keeps hues. Use Neutral or ACES + custom LUT (Color Lookup, contribution 0.3-1.0). HDRP LUT size 33 with HDR 64^3 for grading. A Gran-Turismo style tonemapper in Unity 6 is UNVERIFIED. [KNOWLEDGE]
- Bloom: Quality High on Ultra, Medium otherwise; Threshold 0, Intensity 0.15-0.3, Scatter 0.6-0.7, High-Quality Filtering on; lens dirt off.
- Depth of Field: gameplay Camera: off or Focus Mode None; cinematics: Physical Camera Mode, High Quality, with Aperture f/2.8 to f/4 and Near/Far Quality 4-8 samples; costs 2-4 ms.
- Motion blur: Intensity 0.1-0.3, Maximum Velocity 50-100, Sample Count 8; per-object via motion vectors; avoid with TAA ghosting.
- Lens: Lens Distortion off, Chromatic Aberration 0.05-0.1 only in cinematics, Vignette 0.2-0.3, Film Grain type Medium 1 intensity 0.1-0.2, Response 0.8 (also helps banding in dark scenes).
- Color grading dark scenes: cool shadows (blue-teal), warm highlights (orange from the furnace), slight toe lift to avoid crushed blacks; test with Exposure fixed.
- Pitfall: HDR display output differs; keep a Rec.709 SDR path for screenshot comparison.

---

## 9. Performance on the iGPU

- GPU Resident Drawer and GPU occlusion culling: enable from the HDRP/SRP asset (HDRP 17.2 / Unity 6.2+); BRG variants must be kept; static batching off; requires instanced meshes with GPU-resident-compatible materials. [WEB] https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.2/manual/whats-new-17.html
- Render Graph is the HDRP default in Unity 6; use the Render Graph Viewer window to find pass cost and memory. [KNOWLEDGE]
- Async compute: option in the HDRP asset under Rendering; benefit on an iGPU with shared memory is small and can cause stalls; test per frame.
- Per-camera Frame Settings: Custom Frame Settings on cameras to disable SSS/SSGI/RT on non-hero cameras (reflection probes, minimap).
- Quality ladder: create three HDRP assets (Medium, High, Ultra RT) and map to Unity Quality levels. Toggle by script `QualitySettings.SetQualityLevel`. Use Dynamic Resolution to hold 30/60 fps targets. Settings table below.
- Profiling: Rendering Debugger; Unity Profiler with GPU module; RenderDoc (supports DX12); PIX for Windows; Radeon GPU Profiler (RGP) for RDNA 3.5 - support for Radeon 8060S in the current RGP release is UNVERIFIED; Frame Debugger and Memory Profiler.
- Shared memory note: iGPU reads from system RAM; frame time is memory-bandwidth bound (about 256 GB/s class on Strix Halo, UNVERIFIED). Prefer lower-resolution effect buffers and fewer full-screen passes.

---

## 10. Unity 6.x HDRP highlights 2025-2026

- HDRP 17.0 (Unity 6.0, 2024): FSR 2.1 via com.unity.amd, path tracing disc/tube lights, APV new baking backend. [WEB]
- HDRP 17.2 (Unity 6.2, 2025): STP on all platforms, GPU Resident Drawer and GPU occlusion culling in HDRP, Sky Occlusion with APV, fallback UI for upscalers, reflection probe Importance. [WEB]
- HDRP 17.3 (Unity 6.3 LTS): "refer to the What's new in Unity page" - no HDRP-specific page content found. [WEB] Unified Render Graph in 6.3. [WEB]
- Unity 6.4 shipped about 2026-03-19 and 6.5 about 2026-06-15; built-in pipeline deprecation starting 6.5 (sources conflict). [WEB, UNVERIFIED]
- HDRP in maintenance (see top). 6.6 and 17.6 changes: UNVERIFIED (no source found).

---

## 11. Workflow: Unity Claude Code plugin, Unity CLI, Pipeline package

- Official plugin: `claude plugin install unity@unity-agent-plugin` or add marketplace `Unity-Technologies/unity-agent-plugin`; bundles Unity skills (about 29 at launch), the Unity CLI and an MCP server for live Editor control. Launched about 2026-09-09 (one source says Aug 10, UNVERIFIED). Free. [WEB] https://gamedev.net/news/5704-official-unity-plugin-for-claude-code/ ; https://www.pocketgamer.biz/unity-launches-official-claude-code-plugin-with-29-built-in-engine-skills/ ; https://wikidocs.net/blog/@openwiki/30922/
- Unity CLI + Pipeline package: Pipeline requires Unity 6.0+; package auto-starts a local server inside the Editor; `unity pipeline install` installs it; built for local development, not for shipped games. Versions in Unity's Sept 2026 guide: CLI 1.0.0-beta.6, com.unity.pipeline 0.5.0-exp.1. [WEB] https://unity.com/en/resources/unity-pipeline-cli-technical-walkthrough ; https://unity.com/resources/a-beginners-guide-to-unity-cli-and-the-pipeline-package
- Best practices (mostly [KNOWLEDGE], UNVERIFIED for this CLI build):
  - One live Editor per project. Unity locks the project via `Temp/UnityLockfile`; a second Editor or batchmode instance on the same path fails. Serialize agents with an external lock (a file mutex) around Editor-affecting CLI calls, or give each agent its own clone/git worktree and Library cache.
  - For batch builds and tests, use a separate clone or close the Editor first; do not run batchmode on the live project.
  - After script edits, wait for domain reload/compilation before issuing the next CLI command; poll the Editor's compile state.
  - Screenshots: capture from Game view (ScreenCapture.CaptureScreenshot with supersize) or a Render Texture from the Main Camera; fix Exposure to Fixed, disable Auto exposure, fix time-of-day and random seeds so captures are comparable. Use the Rendering Debugger to export per-pass views.
  - Perf capture: log `FrameTimingManager` timings or use `ProfilerRecorder` for GPU ms per pass; store CSVs next to the screenshots. Keep resolution and quality level fixed between runs.
  - Unity MCP and Pipeline: load the `unity:unity-cli` skill first (it exists in this environment) and prefer CLI exec over hand-editing scenes/YAML.

---

## Settings starter table

| Feature | Value | Tier |
|---|---|---|
| Graphics API | DX12 only | All |
| Lit Shader Mode | Deferred (Both if RT needed) | All |
| Upscaler | STP, hardware DRS | All |
| Render scale | 50 / 59 / 67 percent | Medium / High / Ultra |
| Anti-aliasing | TAA (STP implied) | All |
| Exposure | Fixed (captures); Auto with clamps (play) | All |
| Moon light | 0.3-2 lux, blue, contact shadows on | All |
| Shadow filtering | Low / Medium / High | Medium / High / Ultra |
| Shadow maps | 1024 / 2048 / 2048-4096 | Medium / High / Ultra |
| GI | APV L1 / APV L2 / APV L2 + SSGI half | Medium / High / Ultra |
| SSR | Half res / Full res / Full + RT reflections | Medium / High / Ultra |
| RT reflections | off / off / on (Performance, half res) | Medium / High / Ultra |
| RT AO or RT shadows | off / off / one of them | Medium / High / Ultra |
| Path tracing | Stills only | Ultra |
| Volumetric fog | 32 slices / 64 slices / 64-96 slices + denoise | Medium / High / Ultra |
| Local Volumetric Fog | 4 / 8 / 10 | Medium / High / Ultra |
| Clouds | off or low / Cloudy preset / Cloudy + shadows | Medium / High / Ultra |
| Water | Simple, no caustics / foam / caustics 512 | Medium / High / Ultra |
| SSS sample budget | Low / Medium / High | Medium / High / Ultra |
| Hair | Approximate cards / Physical cards / Physical + multi-scatter | Medium / High / Ultra |
| Eye | Classic / Cinematic / Cinematic | Medium / High / Ultra |
| Bloom | Low / Medium / High | Medium / High / Ultra |
| DoF | off / cinematic only / cinematic High | Medium / High / Ultra |
| Motion blur | off / low / low | Medium / High / Ultra |
| Decals | on, atlas 2048 / 4096 / 4096 | Medium / High / Ultra |
| GPU Resident Drawer + GPU occlusion | on | All |
| VFX particles budget | 5k / 10k / 20k | Medium / High / Ultra |
| Texture streaming budget | 2 / 4 / 8 GB | Medium / High / Ultra |

All values are starting points, UNVERIFIED on the Radeon 8060S. Measure first.

---

## Sources

Fetched via search summaries (page fetch failed, so no direct read):
- Render Pipelines strategy for 2026 - https://unity.com/topics/render-pipelines-strategy-for-2026 ; https://discussions.unity.com/t/render-pipelines-strategy-for-2026/1710004 ; https://80.lv/articles/unity-unveils-2026-render-pipelines-strategy/ (2026)
- What's new HDRP 17.0 - https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/whats-new-17.html (2024)
- What's new HDRP 17.2 - https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.2/manual/whats-new-17.html (2025)
- HDRP 17.3 what's new - https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/whats-new.html
- HDRP Dynamic Resolution - https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.2/manual/Dynamic-Resolution.html
- HDRP Features 17.3 - https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/HDRP-Features.html
- Hair master stack 15.0 - https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@15.0/manual/master-stack-hair.html
- Ray Tracing Getting Started 15.0 - https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition%4015.0/manual/Ray-Tracing-Getting-Started.html
- Path tracing limitations 16.0 - https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@16.0/manual/path-tracing-limitations.html
- Unity 6 ray tracing forum thread - https://discussions.unity.com/t/unity-6-raytracing-completely-broken-in-every-way-imaginable/1523367
- AMD path tracing crash issue - https://issuetracker.unity3d.com/issues/amd-crash-on-gfxdeviced3d12base-drawbufferscommon-when-enabling-path-tracing-in-hdrp-sample-scene
- Enemies demo (2022) - https://80.lv/articles/enemies-the-working-process-behind-unity-s-new-tech-demo/ ; https://unity.com/fr/blog/news/introducing-enemies-the-latest-evolution-in-high-fidelity-digital-humans-from-unity
- Unity HDRP e-book blog (Unity 6 lighting) - https://unity.com/blog/lighting-and-environments-hdrp-updates-unity-6
- Unity CLI / Pipeline - https://unity.com/en/resources/unity-pipeline-cli-technical-walkthrough ; https://activation.unity3d.com/blog/meet-the-unity-cli
- Official Claude Code plugin - https://gamedev.net/news/5704-official-unity-plugin-for-claude-code/ ; https://www.pocketgamer.biz/unity-launches-official-claude-code-plugin-with-29-built-in-engine-skills/ ; https://www.invenglobal.com/articles/25799/unity-unveils-official-unity-plugin-for-claude-code ; https://wikidocs.net/blog/@openwiki/30922/ ; https://www.strayspark.studio/blog/claude-code-unity-what-works-2026
- Unity 6.5 news (secondary, UNVERIFIED) - https://www.pulse.bot/gaming/news/unity-65-released-c2a2362b-f047-418d-8878-a79d65cb6341/

Not found / UNVERIFIED overall: any Unity 6.6 / HDRP 17.6 release notes, AMD RDNA 3.5 RT benchmarks in Unity, diffusion-profile numeric values, Digital Human/Hair package versions for Unity 6, tessellation status, FSR3 in HDRP, RGP support for the 8060S.
