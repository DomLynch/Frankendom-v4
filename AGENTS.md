# AGENTS.md: how the agents on this project work together

Read this before you change anything in `unity_test`. Last updated 2026-11-04 12:05 by Strategy (Claude).

## Goal (from Dom)
A 9/10 AAA vertical slice: Dracula vs the Pitborn at the Hades gate, Unity 6 URP, 120 fps at 1080p on this laptop (AMD 8060S iGPU). Frankendom V3 is the baseline to beat, not the ceiling. Keep iterating and improving.

## Who is working here
| Agent | Role |
|---|---|
| **Unity dev** (Claude session "unity") | Builds the game in `unity_test`: combat port, scene, builds, captures. Owns compile health and builds. |
| **Strategy** (Claude) | Coordinates every lane, reviews every picture against the art, and sets the shared art direction. Tracker: `C:\Users\dom12\Documents\kits_out\3d\GAP_ANALYSIS.md`. |
| **Goose** | Auditor and real-time improver (Dom's brief). |
| **Antigravity / Gemini** | Auditor and real-time improver (Dom's brief). |
| Hades env (Claude) | Props and weapons (`kits_out\3d\hades_env`). Copies finished GLBs into `Assets\Env\Props` and `Assets\Characters\Weapons`. |
| OpenTrident (Claude) | Character rigs and clips (Dracula, Pitborn). |
| MetaHuman (Claude) | MetaHuman head + RigLogic plugin (`kits_out\3d\metahuman\export\s2_unity\final`). |

**Ownership (Dom, 2026-11-04):** Claude owns this project: the Unity dev builds it and Strategy directs it. Goose and Antigravity are **advisors**. They're complementary and have already added a lot of value. They may make changes directly when a fix is **critical** (broken build, crash, a serious bug, a big quality miss). Otherwise, post a PROPOSE or an audit finding and the Unity dev implements it. Disagreement is fine; we settle it in `COORDINATION.md`, not by overwriting each other.

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
