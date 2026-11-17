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
9. **Board lines say what is running (COO 2026-11-16 11:38):** every QA / gate script that posts a FOCUS TAKEN or RELEASED line takes a required `-Purpose` (one line: what the run proves) and prints its output folder; no hard-coded text, an empty purpose is refused (exit 8). `_agents\laneHOTSHIP\gateproof.ps1` and `gatepick.ps1` do this.
10. **-Mode CodeOnly means code only (COO 2026-11-16 12:30):** `Tools\_build_guard.ps1 -Mode CodeOnly` refuses and prints `not code-only: <files>` when git shows a changed or new file under `Assets\` that is not .cs/.asmdef/.asmref/.meta/data (`Assets\Data`, `Assets\StreamingAssets`), or when the setup step declares outputs in `$env:BUILD_SETUP_OUTPUTS`. An asset import re-processes whatever references it (the first scene opens, 25-30 GB); that is a Bake and runs alone.
11. **Frankendom asset-import rule (COO 2026-11-16 12:35):** opening `HadesSlice` in the Editor costs 25-30 GB by itself (measured: pe 12:27, 30 GB peak with one scene opened; the Joan controller / clip bakes reopened 23 scenes and hit 36 GB twice). So ANY asset import in a player build (a changed controller, a clip, a texture, a new asset under Resources) is a Bake: it runs ALONE at 30 GB+ free with nothing else open, no exceptions. Code and data (`Assets\Data`, `Assets\StreamingAssets`) are not a Bake. Do not add asset-import steps to a code-only build.
12. **CodeOnly is unproven here (COO 2026-11-16 12:40):** a Frankendom "code-only" build (pg) peaked at 40.9 GB with 20 scenes opened, so while `Tools\_codeonly_unproven.txt` exists the guard treats `-Mode CodeOnly` as Bake: runs ALONE, 30 GB+ free, nothing else open. The file goes only after a build logs a peak under 12 GB in `TestOut\v109<tag>_mem.csv` (`_agents\laneHOTSHIP\mem_watch.ps1`).
13. **Frankendom Editor start rule (COO/Strategy 2026-11-16 12:57):** every Frankendom Editor start needs 45 GB+ free, NO game running (Dom or QA, any project) and NO other Editor (`Tools\_build_guard.ps1`); `_agents\laneHOTSHIP\mem_watch.ps1` stops our own Editor the moment any game process starts and posts why. Until a build logs a peak under 12 GB, Frankendom ships HOT DATA ONLY between those quiet windows.
    **Amended (COO 2026-11-16 14:10):** a WARM CodeOnly build (`Tools\_build_guard.ps1 -Mode CodeOnly`, FK_BAKE_TEST not set, no `Tools\_codeonly_unproven.txt`; last code-only Editor peak 4.3 GB, pk) needs 24 GB+ free; other games' Editors and QA players are allowed. It still refuses while a Dom game runs, our own project's Editor is open, or a backup lock is held. COLD or FK_BAKE_TEST builds keep the 45 GB, ALONE, no-game rule. mem_watch for a warm code-only build stops our Editor at 8 GB free only (not on a game start).

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

## Build preflight guard (COO + Dom, 2026-11-16 09:13: recurring faults get a guard IN THE SCRIPT)
Every Frankendom build script (Tools\build_v109*.ps1) starts with `. "$p\Tools\_build_guard.ps1"; Assert-BuildSafe '<tag>'` and REFUSES (exit 3, prints why) when: another Unity.exe is open (the Editor runs alone); a git backup/pack/push is running or _agents\pool\backup.lock exists; free RAM is under 24 GB (builds peak near 36 GB); a Dom game is running (plain HadesSlice, or a fresh dom-runs log with a live HadesSlice); or the Unity licence probe (Licensing Client log) fails. Dry run: `powershell -File Tools\_build_guard.ps1 -DryRun`. backup.ps1 skips while this project's Unity.exe is open and holds the lock while it runs. Never bypass the guard to save minutes; fix the cause it names.
Every build script ENDS with `Add-BuiltLine '<tag>'` (from _build_guard.ps1), which appends 'BUILT <tag> ready to flip' (or FAILED/incomplete) to COORDINATION.md automatically (COO 10:48). Mode: `Assert-BuildSafe '<tag>' -Mode CodeOnly` for code-only builds (may run beside another game's Editor at 24 GB free), default Bake runs alone.
**QA players (COO 10:59, Strategy):** FOCUS windows are not exclusive for QA players. A QA player may run beside another lane's QA player or a code-only Editor at 16 GB+ free, unless it is the game Dom is in (a fresh dom-runs log, or a launcher/explorer parent). 'Another lane's FOCUS' is not a blocker in windowcheck.
**Hot tags flip first (COO 12:5x, Dom: 10-15 min):** a hot data tag flips as soon as the twin check + prod_data_check pass; the proof sweep (frames, probes, QA runs) NEVER sits in front of the flip: flip, post the LIVE line, then prove on live and fix with the next hot tag. If the LIVE line is not up within 5 minutes of Dom's game closing, the Lead runs deploy_data.ps1 -Apply on the staged tree, flips PLAY.bat and posts it.

## Build tags are reserved by script (Lead + Strategy, 16 Nov 22:06)
Nobody types a tag by hand. Get one with `powershell -File Tools\next_tag.ps1 -Owner <you>` (prints and atomically reserves the next free HDRP_v109 tag as Builds\HDRP_v109<tag>.reserved; refuses if another owner holds it). `deploy_data.ps1 -Apply` and `Assert-BuildSafe '<tag>'` (every build_v109*.ps1) reserve the tag themselves and refuse a tag reserved by another owner (set $env:FK_OWNER to your name so your own tag is reusable). Cause: tag collisions pn (14:08), pv (22:00) and a hot deploy landing during another build's write. Mechanism: Tools\_tag_reserve.ps1.

## Rule 13 amendment: warm code-only floor 14 GB (COO ruling 23:03, 16 Nov)
A WARM code-only Frankendom build (CodeOnly, no FK_BAKE_TEST, no Tools\_codeonly_unproven.txt; measured peak ~4.5 GB) needs 14 GB+ free RAM (was 24; 14 - 4.5 = 9.5 GB stays above the 8 GB own-pid stop). Other lanes' Editors and QA players may keep running. COLD / FK_BAKE_TEST / bake builds keep 45 GB+, ALONE, nothing else running. Implemented in Tools\_build_guard.ps1 (Get-BuildBlockers, $warm); launchers must not carry their own higher RAM wait (use the guard).

**Serialized fields are bake-class (Unity dev, Lead 2026-11-16 23:10):** a NEW public field or [SerializeField] on a scene-baked component (DuelFighter, CameraRig, CombatHudBars hosts, any MonoBehaviour in a scene or prefab) re-serializes every scene that holds it, so a 'code-only' build opens HadesSlice and peaks near 30 GB (qe 23:05: 33.5 GB, own-pid stop at 3.5 GB free). New serialized fields = bake-class. In a code-only take use private, [System.NonSerialized] or static members, and JSON-loaded config classes only; check the diff for new public fields before starting the Editor. If a code-only Editor passes ~8 GB, stop it, post the cause, and ship the data-only parts hot.


## Cold detection in the guard (COO 23:09, 16 Nov)
A 'warm' take is treated as COLD (45 GB free, ALONE) when the last measured Editor peak (newest TestOut\v109*_mem.csv, peakMyUnityGB) is over 12 GB, or any non-code Assets file (not .cs/.cs.meta/.json, not Data/StreamingAssets) changed since the newest real build's Assembly-CSharp.dll. Implemented in Tools\_build_guard.ps1. Also bake-class: a NEW serialized field on a scene-baked class (qe lastSpend) re-serializes every scene; the guard cannot see it, so mem_watch (own-pid stop at 8 GB) stays mandatory.

## Serialized-field detection in the guard (COO 23:11, 16 Nov)
Tools\_build_guard.ps1 diffs every Assets\Scripts .cs changed since the newest real build's dll against its last commit before that dll (git) and treats the take as COLD (45 GB, ALONE) when an added line declares a public field or [SerializeField] without [NonSerialized] (static/const/types/methods are skipped; a false positive only costs an ALONE slot). It prints the field. Together with the peak rule and the non-code Assets rule this is the three-part cold detector.

## QA player footprint (Lead, measured 17 Nov 08:4x; Strategy agreed 09:00)
A Frankendom HadesSlice player is NOT ~3 GB: working set climbs 2.8 -> 12.7 GB in 30 s at quality low 1280x720 (ws_low.csv, qc) and reaches 14.7 GB at full quality (ptb111). It is asset loading, not resolution. So the Frankendom QA player STARTS only at 27 GB+ free and its own guard ends it at free < 14 GB (Dom's machine is in trouble under ~12 GB). The generic 16 GB QA floor above assumes a small player and does not apply to Frankendom. Open: a QA-only lean flag (zone under test only, target < 6 GB) and the product problem (a 13-15 GB game cannot run on a 16 GB Steam machine): load per zone, report the working set after 60 s in the first zone in every release line (roadmap Step 5, before the alpha run).

## Compile-only is the DEFAULT code path (Unity dev, Lead/Strategy 17 Nov 08:50)
Code takes use compile-only (PlayerBuildInterface.CompilePlayerScripts via Assets\Editor\HdrpCompileOnly.cs + Tools\build_compileonly.ps1: only Assembly-CSharp.dll is replaced in a copy of the live build, no scene opened). Proven by qk: Editor peak 2.9 GB, 85 s, boot check 0 exceptions, live 08:5x. Rule: no new serialized fields (public or [SerializeField]) in a code take (the guard refuses them); a full HdrpBatch.Build is for baked changes only. The BuildOptions.BuildScriptsOnly path (Tools\build_scriptsonly.ps1) is NOT used: it needs a previous full build recorded in this Library ('Can't verify script data layout').


**QA player footprint (Unity dev, 17 Nov 09:00):** a scripted QA player takes 12.7-14.7 GB working set at start and no flag changes it (quality low / 720p: 12.7 GB; -permem:E1,E3,E4: 13.9 GB; ultra2k: 14.7 GB); Unity-managed memory is only ~2 GB, the rest is native/GPU memory that this iGPU (unified memory) counts as system RAM, so a dGPU player would see most of it as VRAM. QA therefore starts at >= 27 GB free with at most one Editor up and my own 14 GB stop; the product-memory question needs a measurement on a dGPU or via GPU counters, not on this laptop's working set.


## Build-suspend watcher (COO 09:24, 17 Nov)
Tools\build_suspend_watch.ps1, started detached by Assert-BuildSafe after the all-clear, polls every 1 s: when ANY game has a live process AND a fresh dom-runs log exists (Frankendom, Armagedom or FrontierDom dom-runs), it NtSuspendProcess-es this project's Unity.exe and its children (nothing killed) and posts BUILD SUSPENDED; when the game ends it resumes and posts BUILD RESUMED. Exits 90 s after the Editor is gone or after 60 min. Dry run: powershell -File Tools\build_suspend_watch.ps1 -DryRun.
