# Frankendom: overview for a third-party reviewer / performance dev
Prepared by Strategy, 2026-11-08. Plain English. No credentials in this file.

## What it is
A dark-fantasy 3D duel/souls-like. You play Joan of Arc. Seven zones in one seamless world, five bosses (Pitborn, Sobek, Grendel, Humbaba, Erlking), two trading camps, a free Steam alpha is planned. Unity 6000.6.7f1, HDRP, max quality ("AAA, no visible cuts for fps": Dom's rule). Target: native 2560x1600 on a ROG Flow Z13 (Ryzen AI Max+ 395 / Radeon 8060S, 64 GB), 1080p and 1440p must work for Steam players.

## Where it lives (Windows)
- Project root: C:\Users\dom12\Documents\unity_test_hdrp (git: github.com/DomLynch/Frankendom-v4, LFS for big files; the `_agents` folder is NOT in git; a text copy is on E:\FrankendomBackup\_agents)
- Code: Assets\Scripts (about 383 C# files, 63k lines, ONE assembly, no asmdef), Assets\Editor (build tools)
- Data layer (JSON): Assets\Data (198 files) mirrored byte-for-byte to Assets\StreamingAssets. Loaded at runtime via DataPath. Gameplay numbers, vendors, zones, stances, camera, HUD layout live here.
- Characters/art: Assets\Characters (Joan, Pitborn, Sobek, Grendel, Humbaba, Erlking, Horse), Assets\Env, Assets\HDRP (materials/settings)
- Builds: Builds\HDRP_v109* (each is a full player; hot data builds are hard-linked copies). Launchers: Builds\PLAY.bat, PLAY_ALL_ZONES.bat, PLAY_FINAL_BOSS.bat, PLAY_PREV.bat
- Tools: Tools\ (python/ps1 gates and patch scripts), _agents\ (lane folders: kits, manifests, audits, plans)

## Key code (start here)
- Game loop and duel: Scripts\GameLoop.cs, Combat\Duel.cs, Combat\DuelFighter.cs (60 Hz fixed simulation tick; deterministic, there is a simhash)
- Combat feel: Combat\* (AIBrain, StanceLayer, HitReact/HitPhysics in Scripts\Physics), Finishers\*, CameraRig.cs + CameraDeath.cs
- World/zones: Scripts\Zones (ZoneRuntime, ZoneSpan, ZoneSave, PlayArea), data: zones.json, zone_span.json, play_areas.json
- Economy/UI: Scripts\Market, Scripts\Loot, HudDraw.cs/GameHUD.cs (IMGUI OnGUI based), data: market.json, hub.json, boss_silver.json, consumables.json
- Quality/perf: RuntimeQuality, FighterLod, FpsNext.cs, quality_tiers.json, runtime_flags.json, hdrp_lighting_ultra.json, fighter_lod.json, opt1_levers.json
- Saves: Scripts\Save (SaveSlot, SafeFile, RunMode), loot_save.json / zones_save.json under the user's save folder (-savePath redirects it)

## How to run
- Player build is made by the Unity Editor in batch mode (Editor script BuildPlayerCmd.Run, ~8-12 min). Zone2Build bakes scene data (~6 min, only when scene data changes).
- Hot data release: copy changed JSON into a new Builds\HDRP_* folder (deploy_data.ps1, ~15 s). Always md5-compare tree vs build.
- Launch flags: -zones -quality:ultra2k -screen-width 2560 -screen-height 1600 -screen-fullscreen 0 -savePath <folder WITHOUT spaces> (use a sandbox save for every test)
- Unity licence needs Unity Hub open and signed in; only one Unity Editor at a time; never run two GPU jobs at once.
- Harness flags (-zoneshot, -physproof, -monsterqa, -autoplay ...) exist for capture/QA; a release build can compile them out with -noharness (FRANKEN_NO_HARNESS define, about 25% smaller game dll).

## Performance: measured facts (not guesses)
- Quiet native 2560x1600 runs: p50 frame time 41-56 ms (about 18-24 fps), no frame over 100 ms. fps drifted down between builds v109c (23.6) -> v109f (17.0) -> v109g (20.7): cause not yet bisected.
- About 32 of 37 ms per frame is post-processing, fog, anti-aliasing, lights and sun shadow. Hiding the fighter meshes saved only 0.1 ms, so the 400k-triangle monsters are NOT the main cost.
- The 100-200 ms frames in Player.log come from capture/loading harness runs, not play.
- Sim catch-up clamp clock.maxCatchupS = 0.1 s. Zone code is under 1% of hitch time.
- Ready staged levers (nothing applied): _agents\laneFPS (apply_fps.py: geo_lod, fighter_lod, fps_next N3; look-neutral only), plan in _agents\laneOPT_PLAN.md.
- Open perf ideas for you: fog (volumetric) cost and temporal denoise, SSGI/SSR settings, shadow cascades/resolution for the sun, antialiasing mode, VFX/decals, IMGUI OnGUI draw cost (many screens already guarded), mid-distance LODs for the 400k-tri monsters (Sobek/Humbaba/Erlking have none), cut proxies, prewarm/shader stalls at zone load, 2560x1600 render scale + upscaler option.

## Quality and safety nets
- Gates: Tools\data_gates, missing_keys, hc_gate (hard-coded numbers; now PASS/FAIL), json_keys_gate (warn). Smoke test: _agents\laneSMOKE\smoke.py (37 checks, sandbox save, not yet run on a build).
- Audits: _agents\laneAUDIT\AUDIT.md (6.5/10) and _agents\laneAUDIT2\AUDIT2.md (7.0/10, top remaining fixes). No unit tests, no asmdef, 194 swallowed catches, about 1,100 UI literals (kits staged), see audit.
- Requirements that must not be broken: _agents\REQUIREMENTS_CORE.md (multi-directional combat: overhead, thrust, slash left/right, guard in all directions, four stances), no weapons on monsters, everything possible in the data layer, no visible quality cuts, saves must stay safe.

## Rules for any outside dev
1. Work on a copy or branch; Dom/Strategy merges. Never touch the real save (always -savePath sandbox).
2. Never delete builds; Dom runs the delete scripts himself.
3. Look changes (colours, materials, camera) need Dom's own OK; perf work must be look-neutral or behind a switch.
4. Licensing: only Frankendom-own finisher clips (9100001-3, 5-7, v1_*) and Mixamo-based clips may ship; several Pitborn finisher clips look extracted from other commercial games and are flagged (see _agents\laneFINISHER4\OPENTRIDENT_REPLY.md): do not reuse them.
5. Artifacts to ask for: _agents\COORDINATION.md (live board), PLAN_EVENING_2026-11-08.md (current plan), STRATEGY_RESUME.md (history).
