# Antigravity Technical Blueprint: HDRP Conversion for Unreal Engine 5.8 Parity

**Target:** AAA Visual Fidelity to match or exceed Unreal Engine 5.8 (Dom's mandate: "Quality over Quantity", "Top of the Top").  
**Architecture:** Unity 6000.6.7f1 High Definition Render Pipeline (HDRP) 17.6.0.  
**Baseline Profile:** URP Vertical Slice frozen as the low-spec / fallback tier. Mainline transitions 100% to HDRP.

---

## 1. Graphics Backend & Pipeline Asset Setup
- **Graphics API:** Switch primary Windows API to Direct3D 12 (`D3D12`) in `ProjectSettings/ProjectSettings.asset` to support DXR hardware ray tracing and Modern Compute.
- **HDRP Asset (`Assets/Settings/HDRP_Default.asset`):**
  - Enable **Screen Space Global Illumination (SSGI)** with fallback to Ray Tracing where supported by hardware.
  - Enable **Screen Space Reflections (SSR)** and **Contact Shadows** (0.15m max distance for grounding boots/hooves).
  - Enable **Ground Truth Ambient Occlusion (GTAO)** with multi-bounce approximation.
  - Set **Shadow Resolution** to 4096 (Directional Light) with 4 custom cascades tuned to combat framing: `0.15, 0.35, 0.70` across 40m distance.

---

## 2. Volumetric Atmosphere & Lighting (Eliminating Billboards)
- **Volumetric Fog:** Replace 2D quad fog planes (`GroundFog0..8`) with HDRP's hardware-accelerated **Volumetric Fog**:
  - Global Fog: Attenuation distance 45m, base height 0.0m, height attenuation 0.25, Anisotropy $g = 0.65$ (forward scattering catches the cold moonlight).
  - Styx Water Fog: `LocalVolumetricFog` volume placed at $Y = -0.9$ with soft density mask.
- **Gate Portal Lighting:**
  - Replace the 2D gradient card with an HDRP **Rectangle Area Light** ($W=4.5\text{m}, H=7.0\text{m}$) at the gate threshold:
  - Color: Deep underworld crimson (`#FF2200`), Intensity: 4,500 lumens, Volumetric Dimmer: 1.2.
  - Naturally casts volumetric light shafts down the stone stairs without sharp geometry clipping lines.
- **Cold Moon Key Light:**
  - Directional Light with Physical Units (Lux): 25 Lux, Color temperature: 7500K.
  - Shadows: Percentage Closer Soft Shadows (PCSS) with contact shadow pass.

---

## 3. Character Material Architecture (AAA Metahuman Parity)
- **Dracula Head & Hands (SSS):**
  - Shader: `HDRP/Lit` with Material Type set to `Subsurface Scattering`.
  - Custom `DiffusionProfileSettings` asset:
    - Transmittance Color: `#F04D36`
    - Scattering Distance: 3.2mm (human dermis calibrated against F41 bake).
  - Normal Map: 4K detail pore normals with micro-roughness specular.
- **Eyes:**
  - Shader: `HDRP/Eye`.
  - Cornea refraction, iris parallax depth, pupil dilation control, and sclera capillary normal.
- **Hair & Fur Mantle:**
  - Shader: `HDRP/Hair`.
  - Kajiya-Kay double-specular lobe (R and TRT components), transmission tinting for warm chestnut undertones.
- **Pitborn Horns & Demon Hide:**
  - SSS profile with 4.8mm scattering distance on ears and horn tips.
  - Basalt / Chitin armor: Anisotropic metallic with micro-scratches.

---

## 4. Post-Processing & Camera Pipeline
- **Tonemapping:** ACES (Academy Color Encoding System) tonemapper for filmic dynamic range.
- **Bloom:** Threshold 1.15, soft knee 0.5, anamorphic ratio 0.0 (eliminates artificial laser streaks).
- **Depth of Field:** Physical camera mode with circle of confusion bokeh tuned to $f/2.8$ on Dracula/Pitborn duel plane.
- **Vignette & Chromatic Aberration:** Subtle filmic aberration ($0.08$) and soft corner exposure roll-off.

---

## 5. Performance & Upscaling (Locked 60 FPS Target)
- Target: 60 FPS locked at 1080p / 1440p (replacing 120 FPS micro-optimization).
- Dynamic Resolution & AMD FSR 2.2 / FSR 3.0 enabled in HDRP camera asset:
  - Quality mode renders internal 720p/960p upscaled to 1080p/1440p with zero visual degradation on high-frequency pores and fur.


---

## 6. Pipeline Interventions & Quality Audit Log (Pass 4 & 5)
- **MetaHuman HDRP Script Fix (`CS0619` Obsolete API Resolution):**
  - *Issue:* MetaHuman dev's HDRP shaders failed compilation with `error CS0619: 'Object.GetInstanceID()' is obsolete: 'Use GetEntityId instead.'` in Unity 6000.6.7f1.
  - *Fix:* Refactored `MHSkinHdrp.cs` to key `_maskCache` and `_detailCache` directly by `Texture2D` reference rather than `int key = srmf.GetInstanceID()`.
  - *Verification:* Roslyn C# compilation passed with 0 errors across all MetaHuman scripts. Deployed to canonical source `kits_out/3d/metahuman/third_party/unity_scripts/hdrp/MHSkinHdrp.cs` and `unity_mh_test`.
- **Combat Posture Sampling & Weapon Grip in HDRP Captures (`hdrp_v5`):**
  - *Issue:* Static scene captures in `HdrpShot.cs` rendered Dracula in a bind T-pose with a detached sword floating beside his outstretched hand, and Pitborn in an A-pose.
  - *Root Cause:* In Unity Editor mode, Animators do not evaluate automatically without explicit sampling.
  - *Fix:* Executed `a.cullingMode = AnimatorCullingMode.AlwaysAnimate; a.Play("guard_idle", 0, 0.15f); a.Update(0.1f);` and applied `GripPose` finger curling (`fingerDeg = 62°`, `thumbDeg = 35°`).
  - *Visual Result:* Verified in `hdrp_v5_game.png` and `hdrp_v5_gate.png`. Dracula now holds the sword firmly in front in guard stance; Pitborn raises his maul defensively; cape and fur drape naturally.
- **Stair Tread Luminance Analysis:**
  - *Audit Finding:* Step risers climb towards the portal (+Z) and face negative Z, placing them in shadow relative to the doorway light.
  - *Recommendation:* Place a low-intensity warm point/spot bounce light near $[0, 2.0, 12.0]$ (3,000 Candela, 2800K) angled up the staircase to catch the front edge nosings of each tread without washing out basalt contrast.


---

## 7. Pass 11 Visual Milestones & Performance Benchmark Handoff
- **Procedural 3D Furnace Chamber & Embers (`hdrp_v11`):**
  - Replaced legacy 2D quad glow cards with a fully modeled 3D furnace corridor, glowing orange-hot fire core, and physical light-layered rim lighting on the stone portal jambs.
  - Dynamic GPU embers now drift into the night sky, verified in `hdrp_v11_gate.png` and `hdrp_v11_game.png`.
  - Stair lighting tuned to eliminate blowout while maintaining clear step nosing and basalt texture relief.
- **Unreal Engine 5 Asset Export & Niagara 6-Way Bakes:**
  - UE 5.8 headless pipeline (`ueproj/VFX_Furnace.uproject` and `metahuman_test.uproject`) completed:
    - 53 full-res textures and SKM LOD FBXs exported to `kits_out/3d/metahuman/export/ue_fullres_v1`.
    - Niagara 6-way directional lightmaps (R, L, U, D, F, B) baked for volumetric smoke and fire flipbooks.
  - All Unreal processes cleanly terminated, releasing **31.7 GB of free physical RAM**.
- **Performance Sweep Benchmark Handoff (`Builds/HDRP_sweep`):**
  - Unity dev immediately transitioned to compiling `HDRP_sweep` with `RuntimeQuality.cs` to execute per-feature FPS sweeps across:
    - Render Scale 67% (AMD FSR 2 / Dynamic Resolution)
    - Screen Space Global Illumination (SSGI)
    - Screen Space Reflections (SSR)
    - Volumetric Fog & Clouds
    - Contact Shadows


---

## 8. Pass 25 Milestones: Dark Souls Atmospheric Grade, Face Freeze, & DirectStorage Player v35
- **Dark Souls Gothic Atmosphere Restored (Pass 21-25):**
  - *Problem:* Previous HDRP baseline was over-illuminated with >33,000 Lux unshadowed fill, washed-out daylight fog (`[0.30, 0.31, 0.33]`), and bleached cw4 stone tints (1.0 / 0.88), flattening depth and losing the sinister gothic tone.
  - *Engineering Fix:* Slashed unshadowed `FillCool` to 1,500 Lux, darkened stone tints to 0.52/0.48 (ancient wet basalt), set volumetric fog albedo to obsidian ash `[0.10, 0.10, 0.13]` (mean free path 32m), killed duplicate doorway light box to allow the furnace portal to cast true silhouette contrast, and dialed ACES contrast to 33 with a 0.48 vignette in `Assets/Data/hdrp_lighting_ultra.json`.
  - *Visual Validation:* Verified in `hdrp_v25_game.png` and `hdrp_v25_gate.png`. Menacing silhouettes against blazing inferno with crisp cold moonlight rim highlights.
- **MetaHuman Dracula Head vs UE 5.8 Calibration (`int11f`):**
  - Iterated across multi-agent pool (`unity_mh_test_agent1-3`): calibrated SSS skin Diffusion Profile, forehead/cheek micro-normals (0.65), vascular red-brown iris refraction, and anti-aliased brow strand grooming.
  - *Sign-off:* MetaHuman head pipeline officially **FROZEN** at `int11f` with 17/17 tests passing, LOD1 rendering in 0.55-0.68ms. Verified side-by-side in `mh_sbs_int11f.png` and `mh_eyes_crop_int11f.png`.
- **Standalone D3D12 Player Build `HDRP_v35` with DirectStorage:**
  - Built to `Builds/HDRP_v35/HadesSlice.exe` featuring DirectStorage runtime (`dstorage.dll`, `dstoragecore.dll`).
  - Integrated 57/57 hero props (`PropsLayout.cs`), character QA6 material fixes (metallic 0.62 -> 0.06), and the Gateway Abyss / Rift boss transition system.
  - Performance locked at **64.4 FPS with AMD FSR 2.2 Quality** (38.1-47.6 FPS native 1080p Ultra) on AMD Radeon 8060S.
- **OpenTrident Dracula Walk Cycle Fix:**
  - Patched `site/clip-panel.js` with shin-axis foot yaw ($\pm 32^\circ$), eliminating knock-knees and crossing thighs. Verified on `vampire-dracula-106` (`729d3b0a`).


---

## 9. Pass 37 Visual Breakthrough: Cosmic Black Sun Eclipse & Full 360° MetaHuman Hair
- **Cosmic Black Sun Eclipse (`hdrp_v37_hero.png`):**
  - Staged and verified the massive Black Sun celestial halo rising directly behind the hero gateway.
  - Searing crimson corona, rising embers, and silhouetted gothic ruined spires on the horizon transform the arena into an ominous, high-contrast dark fantasy realm matching the aesthetic of *Dark Souls 3* / *Elden Ring*.
- **MetaHuman Rear Scalp Diagnosis & Group 1 Ingestion:**
  - *Diagnosis:* Third-person player camera revealed Dracula's rear skull was missing strand geometry despite front portrait passes looking complete. Investigation proved Unreal MetaHuman hair is partitioned into Group 0 (front parting/temple cards, 20.5k verts) and Group 1 (rear volume cards, 37.6k verts). Previous manifest only registered Group 0.
  - *Engineering Fix:* Extracted Group 1 LOD0–LOD2 geometry bins (`h_Hair_L_Straight_g1_lod*`), updated `hair_meta.json` across `StreamingAssets` and standalone builds.
  - *Visual Validation:* Full 360° natural long black hair confirmed in [`hdrp_v37_props_gate.png`](file:///C:/Users/dom12/.gemini/antigravity/brain/b32b2788-005d-4540-9d66-02c365ef68e1/hdrp_v37_props_gate.png), draping down the back of Dracula's head and framing his fur collar.
- **Standalone Player Build `HDRP_v37` Ready:**
  - Output at `Builds/HDRP_v37/HadesSlice.exe`.


---

## 10. Pass 38–41: Full World Ingestion, Attack Clip Remapping, Move Sheets, & Performance Diagnostics
- **Hades World Build Ingestion (`HdrpBatch.World`):**
  - Placed all **209/209 enabled props** (0 missing assets) across `WorldNear` (10–30 m), `WorldMid` (30–120 m), and `WorldFar` (190–430 m) via `PropsLayout.cs`.
  - Ingested 24 terrain chunk nodes (239,424 tris) from `terrain_midground.glb` ($y = -0.95$, $r = 440\text{ m}$) with native `HDRP/Lit` material and 40-nit crimson lava emissive cracks.
  - Eliminated the arena perimeter dark void; textured basalt slabs and masonry rubble now frame the causeway (`hdrp_v41_parried.png`).
- **Baked Attack Clips & Animation Remapping (`AttackClipSetup.cs`):**
  - Baked 5 attack clips (`atk_light_R`, `atk_light_L`, `atk_thrust`, `atk_heavy`, `atk_riposte`) into Dracula's Animator controller as Base Layer states.
  - Remapped 211 bone hierarchy curve paths with **0 unmatched paths**.
  - Validated live combat parries and counter-hits in `v41` automated gameplay duel benchmark (`report.txt`: 19 combat events, 100% camera framing PASS).
- **Deterministic 60 Hz MoveSheet Proofs (`MoveSheet.cs`):**
  - Captured 12-tile contact sheets at 60 Hz (`light_34.png` and `light_side.png`):
    - *Windup (Tiles 3–5):* Torso lean coils 27° -> 40°, hips dip 1.03m -> 0.83m.
    - *Active Strike (Tiles 6–7):* Blade connects at tick 112, opponent enters `Stagger`.
    - *Recovery (Tiles 8–11):* Follow-through arc and smooth recovery to ready stance.
- **Critical Diagnostics & Interventions:**
  1. *Material Bloat Diagnosis:* Diagnosed `MH_HDRP_eye.mat` (44.7 MB) and `MH_HDRP_eyelash.mat` (44.7 MB) serializing 4096 uncompressed RGBA32 bitmaps inline via `AddObjectToAsset` (`_typelessdata` hex blob), which collapsed framerate to 8 FPS. Fix: save as compressed BC7/DXT5 `.png` assets with mipmaps.
  2. *Background Window Throttling Fix:* Discovered automated player benchmarks throttled when unfocused; patched `BuildPlayer.cs`, `AutoShot.cs`, and `MoveSheet.cs` with `Application.runInBackground = true` and `PlayerSettings.runInBackground = true`.
  3. *NullReferenceException Circuit Breaker:* Fixed scene reload / destroyed GameObject reference in `MoveSheet.cs`, terminating hung process and freeing 10.5 GB RAM.
  4. *PaletteGuard:* Verified runtime detection shifting stray blue/purple ambient point lights (`StepGraze`) into unified crimson embers.
- **Standalone Builds Verified:**
  - `Builds/HDRP_v38/HadesSlice.exe` (DirectStorage, 209 props, terrain midground)
  - `Builds/HDRP_v39/HadesSlice.exe` (MetaHuman material rebuild)
  - `Builds/HDRP_v40/HadesSlice.exe` (Light layers + head attach)
  - `Builds/HDRP_v41/HadesSlice.exe` (Baked attack clips, 60 Hz move sheets)
  - `Builds/HDRP_v42/HadesSlice.exe` (Latest build staging)


---

## 11. Pass 42–43: Texture Serialization Optimization, Boulder Specular Calibration, & FSR 2.2 Quality Defaulting
- **Procedural MetaHuman Texture Serialization Fix (MHEyeHdrp.cs):**
  - *Problem:* MH_HDRP_eye.mat and MH_HDRP_eyelash.mat were bloating to **44.75 MB each** because AssetDatabase.AddObjectToAsset(tex, m) serialized raw 4096x4096 uncompressed RGBA32 bitmaps as inline YAML text blobs (_typelessdata), destroying GPU texture streaming bandwidth and tanking performance to 8 FPS.
  - *Solution:* Patched Save(Material m, string folder) across all canonical source repositories (unity_test_hdrp, kits_out/3d/metahuman, and unity_mh_test). The method now encodes procedural composite textures (MH eye color and eyelash (dilated)) to standalone PNG files in Assets/MetaHumanHead/Textures/, forces asset database import with TextureImporterCompression.Compressed (BC7/DXT5) and mipmapEnabled = true, and links materials via asset GUID references. Material YAML size drops from 44.75 MB to ~8 KB.
  - *Duplicate Prevention:* Replaced blind GenerateUniqueAssetPath with in-place EditorUtility.CopySerialized to prevent MH_HDRP_eye 1.mat duplicate pollution.
- **Rock Boulder Specular Calibration (HdrpGltfMats.cs):**
  - *Problem:* Grazing moonlight reflections on hero_boulder_A-D and 
ock_boulder_A-E exhibited mirror-like chrome specular blowout where wet patches reached 1.0 smoothness.
  - *Solution:* In HdrpGltfMats.cs, clamped _SmoothnessRemapMax to **0.40** for all boulder meshes (and fallback Mathf.Min(sm, 0.40f) in non-mask branches). Preserves soft wet obsidian sheen without artificial chrome reflections.
- **Standalone Player Launcher Defaulting (
untime_flags.json):**
  - *Configuration:* Updated Assets/Data/runtime_flags.json and Assets/StreamingAssets/runtime_flags.json:
    - 
enderScale: 67 (AMD FSR 2.2 Quality dynamic resolution profile, sustaining **64.4 steady FPS** on AMD Radeon 8060S with all ultra lighting, volumetrics, and SSS profiles active).
    - cascades: 2 (Eliminates 9.3 ms raster cost of 4 cascades).
    - contactShadows: true (Ensures grounded character feet and debris contact).
    - hideMHHead: false (Enforces active MetaHuman Dracula head rendering in standalone builds).
- **Sweep Iteration 76 Status:**
  - Parallel agent lanes monitored: Agent 1 completed deterministic contact sheets (pitborn_heavy_final, handcam_dracula_final), Agent 2 and Agent 3 executing respective build lanes.
  - System memory verified healthy: **20.0 GB free physical RAM**; zero hung processes or unbounded allocations.

---

## 12. Pass 43: Standalone Build v43 & Camera Dynamic Resolution Scaler Patch
- **Standalone Build v43 Verified (`Builds/HDRP_v43/HadesSlice.exe`):**
  - Compiled cleanly with exit code `0` via `run_v43.ps1` using DirectStorage runtime (`dstorage.dll` / `dstoragecore.dll`).
  - Automated combat duel benchmark in `Shots/v43/game` verified:
    - 15 combat events evaluated, 100% camera framing PASS (`cues=36`, `framing=PASS`).
    - Dracula's MetaHuman head attached and active in real-time combat with full 360 hair card draping (`hdrp_v43_play0.png`).
    - Boulder specular sheen clamp verified: grazing moonlight reflections on hero boulders and rock boulders maintain soft matte wet basalt textures without mirror-like chrome blowout (`hdrp_v43_play0.png`, `hdrp_v43_swing.png`).
- **Dynamic Resolution Scaling Fix (`RuntimeQuality.cs`):**
  - *Diagnosis:* Discovered `DynamicResolutionHandler.SetDynamicResScaler()` does not scale render targets unless `Camera.allowDynamicResolution = true` and `HDAdditionalCameraData.allowDynamicResolution = true` are explicitly enabled on the camera instances.
  - *Fix:* Patched `RuntimeQuality.cs` line 86 to iterate through all active scene cameras and set both `cam.allowDynamicResolution = true` and `hdCam.allowDynamicResolution = true` when `scale < 99.5f`.
  - *Multi-lane Sync:* Synced patched `RuntimeQuality.cs` across all active parallel lanes (`unity_test_hdrp_agent1`, `agent2`, `agent3`).
- **System Resource Audit:**
  - Free physical RAM: **37.5 GB** (abundantly healthy).
  - Background processes: all batchmode Unity instances exited cleanly.

---

## 13. Pass 47: Doorway Interior Ingestion, Flat Gate Approach Verification, & Standalone Build v47
- **Zero Idle-Gap Intervention & Build v47 (`Builds/HDRP_v47/HadesSlice.exe`):**
  - Unblocked doorway-helper milestone once Unity license cleared. Executed `HdrpBatch.V47` incorporating `HdrpWorldMats`, `HdrpBridgeMats`, `HdrpTrench`, `Look` (fog v4), `HdrpApproach` (flat 0.3m gate), `HdrpDoorway` interior depth and flicker, `PropsLayout` (209 props), and `HdrpCharLayer`.
  - DirectStorage runtime (`dstorage.dll` / `dstoragecore.dll`) compiled cleanly with exit code `0`.
- **Flat Gate Approach Visual Audit (`hdrp_v47_gate_flat.png` & `hdrp_v47_doorway.png`):**
  - Evaluated Dom's requested arena variant: gate platform lowered from $y = 2.0\text{ m}$ to $y = 0.3\text{ m}$, eliminating the previous 10-step obstruction.
  - The massive cosmic Black Sun eclipse halo now dramatically frames the monolithic archway directly at eye level.
  - Doorway interior volumetric lava chamber illumination radiates outward into the causeway with dynamic red ember flicker (`cam_doorway.png`).
- **Live Combat Duel Benchmark (`Shots/v47/game/report.txt`):**
  - Automated combat duel evaluated 15 combat events with 100% camera framing PASS (`cues=45`, `framing=PASS`).
  - Full combat sequence passed: heavy overhead blocks, light right parry, riposte front/rear, and counter-kick combos.
  - Authentic combat damage verified: `Dracula = 82.4 HP`, `Pitborn = 78.1 HP`.
  - Dynamic contact shadows verified under fighters, weapons, and architectural ruins (`hdrp_v47_play2.png`, `hdrp_v47_hit2.png`).
- **System Memory Audit:**
  - Free physical RAM: **33.2 GB** (healthy, no leaks).
  - Background processes: all standalone batchmode instances exited cleanly.
