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

## Current shared direction (open to proposals)
- **Mood:** dark, ominous, restrained. No braziers at the gate; Dom found them cheesy. The gate doorway reads as a furnace beyond: black interior, low hot glow, slow embers.
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
