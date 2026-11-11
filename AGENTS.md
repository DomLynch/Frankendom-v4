# AGENTS.md: how the agents on this project work together

Read this before you change anything in `unity_test`. Last updated 2026-11-04 12:05 by Strategy (Claude).

## Goal (from Dom)
Updated by Strategy 2026-11-06: a 9/10 AAA duel, Joan vs the Pitborn at the Hades gate, Sobek next. Unity 6 HDRP at max quality, not URP yet. Optimisation is priority 1 (`_agents\laneOPT_PLAN.md`), with no visible quality cut; native 2560x1600 is the target on this laptop (Radeon 8060S), and 1080p and 1440p must work for Steam players. Frankendom V3 is the baseline to beat, not the ceiling. Keep iterating and improving.

## Who is working here
| Agent | Role |
|---|---|
| **Unity dev** (Claude session "unity") | Builds the game in `unity_test`: combat port, scene, builds, captures. Owns compile health and builds. |
| **Strategy** (Claude) | Coordinates every lane, reviews every picture against the art, and sets the shared art direction. Tracker: `C:\Users\dom12\Documents\kits_out\3d\GAP_ANALYSIS.md`. |
| **Goose** | Dom's strategic and operational advisor. Audits and implements the fixes and changes it finds (Dom, 2026-11-06). |
| **Antigravity / Gemini** | RETIRED by Dom, 2026-11-06. |
| Hades env (Claude) | Props and weapons (`kits_out\3d\hades_env`). Copies finished GLBs into `Assets\Env\Props` and `Assets\Characters\Weapons`. |
| OpenTrident (Claude) | Character rigs and clips (Dracula, Pitborn). |
| MetaHuman (Claude) | DISCONTINUED by Dom, 2026-11-07. Do not use MetaHuman or open Unreal Engine; this project is Unity only. |

**Ownership (Dom, updated 2026-11-06):** the Unity dev builds this project and Strategy directs it. Goose is Dom's strategic and operational advisor and may implement the fixes and changes it finds, not only critical ones, under the same rules as everyone: CLAIM on the board first, no `Assets` writes while a build or gate runs, one heavy GPU job at a time, and look changes still need Dom's own LOOK OK. Disagreement is fine; we settle it in `COORDINATION.md`, not by overwriting each other.

## Rules (keep the build alive)
1. **Before editing**, append a line to `COORDINATION.md`: `HH:MM <agent> CLAIM <files> - <what/why>`. When done: `HH:MM <agent> DONE <files> - <result, shot path>`.
2. **Never edit a file another agent has CLAIMED in the last 30 min** without writing to them in `COORDINATION.md` first. Prefer adding a new file over editing a shared one.
3. **The project must compile after every edit.** Before you stop, run a compile (or batch build) and fix your own errors. No duplicate definitions; search before you add a symbol.
4. **Builds and captures:** use your own output folder (`Builds\<agent>\`, `Shots\<agent>\`). Don't run, overwrite or delete `Builds\Road_stairs\` or another agent's shots.
5. **Performance budget:** ≥120 fps steady at 1080p. New lights must be non-shadow and justified. Log fps before and after any VFX/lighting change.
6. **Big direction changes** (a new look for trails, blood, lighting, camera, or adding or removing props) go to `COORDINATION.md` as a PROPOSE line with a screenshot first. Strategy answers within one sweep (~5 min). Small tuning needs no approval; just log it.
7. Back up before rewriting someone else's work (`Tools\_backup_<agent>_<HHMM>\`).
8. No deploys, pushes or uploads. Only Dom says deploy.

## Fast releases (Dom's standing rule, restated 2026-11-10 23:30: "fast release builds no need to rebuild all game each time")
A release takes 10-15 minutes. Never rebuild the whole game for a change that a smaller path can ship.
1. **Classify every kit before a round: HOT, CODE or CHAIN**, and split the round: hot release now, chain later.
   - **HOT** (seconds, no Editor): runtime data and runtime-loaded GLBs: mounts and ambient animals, lights, decals, look rows, spawn/patrol rows, zone_shots. `_agents\laneBUILDSPEED\deploy_data.ps1 -v <tag> -Base HDRP_<live> -Apply`.
   - **CODE** (about 90 s): C# only. `Tools\build_<name>.ps1` (HdrpBatch.Build).
   - **CHAIN** (only when unavoidable): layout baked into the scene, new prefabs/controllers, new imports. `chain_timed.ps1`, and skip what did not change: `-SkipMonster` unless NPC/boss prefabs changed; no Zone2Build when no baked layout changed.
   - **Known BAKED items (Lead correction 2026-11-10 23:35, always CHAIN, never hot):** props_layout_*.json rows and prop GLB/LOD swaps, lod_tex_map.json, zone_span.json terrain (flat/flatY), CampBackdrop rows, hdrp_fixes.json matAdjust (rock facets), camp structure rows. A disabled baked row does not leave an already-baked scene: only a moved row propagates. New NPC bodies need BanditsBatch (prefabs, controllers): spawn/patrol rows are HOT only when they point at ids whose prefabs and controllers already exist (a new NPC id enabled without them showed white T-pose figures). Hot edits to a build must also be written back to Assets\Data.
2. **Short release gate:** boot smoke (hard fails: no player fighter, no zone-loaded line, under 10 s) + the five play-camera shots from behind Joan + the changed items' shots, shot FIRST while Joan is alive. Not broken and not worse = release. Roll-back is always `PLAY_PREV.bat`.
3. **The full shot set, FRAMEAUDIT and the Lead/COO audits run AFTER the release** on a copy; they can order a roll-back, they do not block.
4. **Cadence benchmark: 7-22 minutes per release (Dom, 2026-11-11).** HOT and CODE items never wait for a chain: they ship alone the moment they are ready. A chain runs in parallel and releases when it lands. If 22 minutes pass with no release while hot work is ready, post a SWEEP BLOCKER line with the cause. (The morning releases were fast because every one was HOT or CODE; the gaps came from hot items queued behind scene chains, and from build-script and folder-name incidents. Build folders must keep the `HDRP_v109*` prefix.)
5. A kit that fails is switched off by data and the rest ships; never two rounds on the same failure.
6. **Which build entry point (Lead + unity-dev2, 2026-11-11, found the hard way):** `HdrpBatch.Build` ONLY builds the player (no scene re-layout, no material or fix steps): use it for CODE-only and hot-data builds. Anything that changes the MAIN scene (z1 pit prop rows in props_layout*.json, hdrp_fixes.json matAdjust, rock facets, clutter tint, HdrpGround/terrain, doorway) needs the FULL production path `HdrpBatch.V51` (gltfMats, worldMats, bridgeMats, Look incl. HdrpScene + HdrpFixes, ground, TerrainStamp, propsLayout, doorway, charLayer, perfMem, occlusion, build; ~15-25 min). `HdrpBatch.V44` (HdrpApproach + PropsLayout.RebuildAndSave + build) is WRONG: it skips HdrpWorldMats/HdrpBridgeMats/HdrpFixes/HdrpGround and turns world materials white. Zone roots z2-z7 are rebuilt by Zone2Build; the main scene (z1/hub) only by V51. Every V51 build is audited against the live build (22 frames A/B) before release.
7. **Release hygiene (Lead, 2026-11-11, after a 43-minute stall):** a CODE build never holds staged hot items (ship the hot items first, rebuild after); every board line carries the real Get-Date HH:mm, never 17:1x style stamps; a Monitor or build wait that ends without a result means check the logs yourself, never idle (session restarts kill monitors and crons); never gate a build folder without build_info.json (incomplete build); post a board line within 5 minutes of any wake-up.

## Current shared direction (open to proposals)
- **Mood:** dark, ominous, restrained. No braziers at the gate; Dom found them cheesy. The gate doorway reads as a furnace beyond: black interior, low hot glow; embers/sparks OFF per Dom (recorded in doorway.json: he ordered the orange sparks removed) unless he re-enables them. Slow ash/ember specks low over scorched ground away from the doorway are allowed (BPIT, 2026-11-11).
- **Camera:** Frankendom V3 lock cam, ported 1:1 (`CameraRig.cs`). Don't change it without a PROPOSE.
- **Combat feel:** port V3 first, then improve. Plan: `kits_out\3d\combat_audit\V3_TO_UNITY.md` + `V3_TO_UNITY_VERIFY.md` (section F is the order). Unity dev is on item 3 (flinch + blood).
- **Hit VFX:** streaky sparks on metal or guard contact. Dark, non-glowing blood droplets on flesh. Brief flashes only, no long full-body glows.
- **Weapon trails:** a soft curved arc. Goose added a red "ember" variant; open question in `COORDINATION.md`.
- **Pitborn rim light** (`FollowBehind.cs`, Goose): kept, intensity lowered 35 → 10 by the Unity dev. Tune it there.

## Where things are
- Art target: `C:\Users\dom12\Documents\flux_test\env_hades_gate_1664x928.png`
- Tracker: `C:\Users\dom12\Documents\kits_out\3d\GAP_ANALYSIS.md`
- V3 source: `C:\HANDOVER-Macbook\extracted\7-repos\frankendom-v3\src\fight\`

**Unity plugin (Dom, 2026-11-06):** all Unity work uses the Claude Code Unity plugin: `unity:*` skills, starting with `unity:unity-cli` (live Editor control, builds, logs, auth, MCP). URP skills do not apply, this project is HDRP. Project rules still win: one GPU job at a time, no Assets writes during a build or gate, only the Unity dev launches the Editor.

**Max quality (Dom, 2026-11-06):** always max quality HDRP, not URP yet, AAA grade visuals. No visible quality cuts for fps; perf work must be look-neutral or stay dark behind a switch for Dom's LOOK OK.

**Blender (Dom, 2026-11-06):** use Blender headless (`blender --background --python`, exe C:\Program Files\Blender Foundation\Blender 5.2\blender.exe) for renders, previews and mesh work, and the Claude Code Blender plugin and extension (`mcp__Blender__*` tools, Blender running with the MCP add-on) when a live session helps. Project rules still apply: one heavy GPU job at a time, CPU renders when others are running, no `Assets` writes outside the owner.
