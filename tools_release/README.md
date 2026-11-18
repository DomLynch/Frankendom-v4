# tools_release: versioned copy of the release and QA tooling

Why this exists (audit K16): the live launchers `Builds\PLAY.bat` / `PLAY_PREV.bat` call `_agents\laneINT107\play_zones.ps1`, and the build, flip, release and QA scripts lived in git-ignored folders (`_agents\`, most of `Tools\`). This folder is a sanitized snapshot, so a second developer can rebuild the chain. The live files were not touched; `Builds\` and `_agents\` stay git-ignored. This is a copy: edit the live script, then re-copy (or the two drift).

Layout mirrors the original folder names:

| Here | Restored to | Contents |
|---|---|---|
| `Tools\` | `Tools\` | build, guard, gate, tag and prune scripts, configs, data gates (`Tools\lib\hckit3_common.ps1` is untracked in git) |
| `laneHOTSHIP\` | `_agents\laneHOTSHIP\` | `flip.ps1`, `gateproof.ps1` (+ `windowcheck.ps1`, `mem_watch.ps1`) |
| `laneBUILDSPEED\` | `_agents\laneBUILDSPEED\` | `deploy_data.ps1` (hot data deploy), `chain_fast.ps1`, fingerprinting |
| `laneINT107\` | `_agents\laneINT107\` | `play_zones.ps1` (the launcher target), 107 rehearsal and gate scripts |
| `unitydev\` | `_agents\unitydev\` | `release.ps1`, `prod_data_check.ps1`, `build_preflight.ps1`, `gate_release.ps1`, `boot_check.ps1`, `chain_timed.ps1` |
| `agents_root\` | `_agents\` | `qa_slot.ps1` (machine-wide QA player slots) |
| `pool\` | `_agents\pool\` | `unity_busy.py` |
| `Builds_launchers\` | `Builds\` | the `PLAY*.bat` launchers as examples (they name a specific build tag) |
| `env.ps1`, `restore.ps1` | stay here | set `FK_ROOT`; copy everything back into place |

## The release path

```
next_tag.ps1 -> build -> data gates -> deploy_data / compile-only -> boot check -> flip.ps1 -> release.ps1 -> PLAY.bat / PLAY_PREV.bat -> prune_builds.ps1
```

1. **Tag**: `Tools\next_tag.ps1 -Owner <name>` prints and reserves the next free 2-3 letter tag (via `_tag_reserve.ps1`, atomic, retries on a race; rolls `pz` to `qa`). Never type a tag by hand. Builds are named `Builds\HDRP_v109<tag>`.
2. **Pick the path** (default is the fast one):
   - **HOT (data only)**: `laneBUILDSPEED\deploy_data.ps1 -v v109<tag> [-Apply]` copies the live build to a new folder and swaps the changed StreamingAssets json; no Unity. Dry run by default. Runs `data_gates.py` and `missing_keys.py`.
   - **CODE only**: `Tools\build_compileonly.ps1` (compile-only Unity build from the tree). First checks signatures (`_build_sig.ps1`: serialized layout, boot hooks, script files) against the base build's `build_info.json`; a changed layout means a FULL build. `Tools\build_scriptsonly.ps1` is the older scripts-only variant.
   - **FULL** (only for a baked asset; needs `$env:FK_FORCING_ASSET` per the fast-release rule): `Tools\build_release.ps1 -v v110` (static gates, compile with both defines, Unity build, md5 tree==build), or a per-tag wrapper such as `Tools\build_v109qe.ps1` (cold rebuild; template) and `build_v109my.ps1` (bake then build). `build_and_gate.ps1` is the parameterised dev build + gate. `chain_timed.ps1` and `laneBUILDSPEED\chain_fast.ps1` run the MonsterBatch / Zone2Build / player build chain.
   - Every Unity launch first calls `Tools\_build_guard.ps1` (`Assert-BuildSafe`: licence probe, RAM, another Editor, board rules, memory suspend) and `unitydev\build_preflight.ps1`.
3. **Gates**: `Tools\data_gates.py` (six data gates), `hc_gate.py` (hard-code scan against `hardcode_baseline.json`), `json_keys_gate.py`, `missing_keys.py`, `Tools\compile_check.ps1` (Roslyn compile with no Unity start), `Tools\gate_unified.ps1` (+ `gate_config.json`). `unitydev\boot_check.ps1` is the 25-30 s smoke boot (must show `[Zones] on`, no exception).
4. **Flip**: `laneHOTSHIP\flip.ps1 -Build HDRP_v109<tag>` refuses on empty/small/unparsable StreamingAssets json, tree vs build mismatch (`$env:FK_FLIP_ALLOW_DIFF` lists allowed files), or `build_info.json` result not `Succeeded`; then runs `prod_data_check.ps1` and `unitydev\release.ps1 -Apply`, then reads the four launchers back and prints `FLIPPED OK` plus `PATH HOT|CODE|FULL`. Trust only that line. `unitydev\gate_release.ps1` is the boot-check-then-release wrapper.
5. **Release**: `unitydev\release.ps1 -Build HDRP_v109<tag> [-Apply]` writes `PLAY.bat`, `PLAY_ZONES.bat`, `PLAY_ALL_ZONES.bat`, `PLAY_FINAL_BOSS.bat` to the new build, points `PLAY_PREV.bat` at the build `PLAY.bat` named before, then runs prune. Roll back by running it again with the old tag.
6. **Prune**: `Tools\prune_builds.ps1` keeps the last 3 `HDRP_v109*` folders, never one a launcher names or a running player uses (`-WhatIf` lists only).
7. **QA**: `laneHOTSHIP\gateproof.ps1 -V 109<tag> -Purpose "<what is proved>"` (`-V` = the build id without the leading v) is the recorded proof run (target guard, pure `-DryRun`, boots the build, drives the scenario, collects frames and logs). It takes a machine slot from `agents_root\qa_slot.ps1` (`-Acquire/-Bind/-Release`, max players and free-RAM rule) and voids itself if Dom is playing (`windowcheck.ps1`, fresh `dom-runs` log).

## Launcher contract

- `Builds\PLAY.bat` runs `..\_agents\laneINT107\play_zones.ps1 -Build %~dp0HDRP_v109<tag>\HadesSlice.exe`: the single `HDRP_v109<tag>` in that file is the live build. `PLAY_PREV.bat` is identical but names the previous live build. `flip.ps1`, `prune_builds.ps1` and `_build_guard.ps1` find the live tag by matching `HDRP_v109\w+` in `PLAY.bat`, so keep that exact folder naming.
- `play_zones.ps1` starts the exe with `-zones -quality:ultra2k` at 2560x1600 and a timestamped log in `dom-runs\`; `-Fresh` renames only `zones_save.json` under `%USERPROFILE%\AppData\LocalLow\Dom Games\Frankendom` to a `.bak_` file.
- Examples are in `Builds_launchers\`.

## Fresh Windows machine: what must exist

- Windows 10/11, PowerShell 5.1, git with **git LFS** (`git lfs install`, then `git lfs pull`), Python 3 on PATH (`python`), robocopy (built in).
- **Unity 6000.6.7f1** with Windows build support and HDRP at `C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe` (the path is a literal in several scripts and in `Tools\build_config.json` / `Tools\paths.json`; change all via `findstr /s /i 6000.6.7f1`). Unity must be licensed and signed in (`_build_guard` refuses on "Access token is unavailable").
- **Blender 5.x** at `C:\Program Files\Blender Foundation\Blender 5.2\blender.exe` (`Tools\paths.json`, `blenderExe`) for asset scripts; not needed to flip a release.
- `Tools\paths.json` has `kitsOut` as `C:/Users/<USERNAME>/Documents/kits_out`: put your user name in (sanitized placeholder).
- Folder layout under the repo root: `Builds\` (git-ignored, holds `HDRP_v109<tag>\HadesSlice.exe` + `HadesSlice_Data\StreamingAssets`, `build_info.json` per build, and the `PLAY*.bat` launchers), `TestOut\` (logs), `_agents\` (git-ignored), `Tools\`, `dom-runs\`.
- Not versioned here and needed by some scripts: `_agents\COORDINATION.md` (the board; the guards and flip append to and read it; create an empty file), `_agents\qa_slots\` (made by `qa_slot.ps1`), `_agents\pool\backup.ps1` and `prune_builds.log` paths, `Tools\build_vNNN` history, the `Assets\Editor\*.cs` build entry points (`BuildPlayer.cs`, `HdrpBatch.cs`, tracked in git), the Editor batch classes the chain scripts call (`MonsterBatch`, `Zone2BuildBatch`, `TexStreamBatch`), `claude-watchdog\UNITY_SLOT.md` (the Unity slot file read by `persistent_probe.ps1`), and the baseline build itself (`deploy_data` and the compile-only path copy from the current live build, so you need one full build first).

## Tag rules

Tags are 2 or 3 lowercase letters (`tz`, `q4`, `kzz`), unique, handed out by `Tools\next_tag.ps1` (reserve file kept by `_tag_reserve.ps1`). Build folder = `HDRP_v109<tag>`. A wrong name corrupts the launchers and the prune keep-list (see the build naming rule). One purpose per tag; 10-15 minutes per release.

## Recovery checklist

1. Clone the repo, `git lfs install && git lfs pull`, install Unity 6000.6.7f1 and sign in.
2. `. .\tools_release\env.ps1` (sets `FK_ROOT`; also `setx FK_ROOT <path>` for new shells), then `.\tools_release\restore.ps1` (dry run) and `.\tools_release\restore.ps1 -Apply`.
3. Edit `Tools\paths.json` (`<USERNAME>`, Unity, Blender paths) and `Tools\build_config.json` (`paths.unityExe`).
4. Create `_agents\COORDINATION.md` (empty), `Builds\`, `TestOut\`, `dom-runs\`.
5. Get a baseline build into `Builds\HDRP_v109<tag>\` (copy a shipped build, or run a FULL build with the `build_v109*.ps1` template and `$env:FK_FORCING_ASSET`), and make `Builds\PLAY.bat` name it (use `Builds_launchers\PLAY.bat` as the example; run `release.ps1` for the first flip).
6. Dry runs, no Unity: `Tools\next_tag.ps1 -Owner you`, `laneBUILDSPEED\deploy_data.ps1 -v v109<tag>`, `laneHOTSHIP\flip.ps1 -Build HDRP_v109<tag> -DryRun`, `laneHOTSHIP\gateproof.ps1 ... -DryRun`, `Tools\prune_builds.ps1 -WhatIf`.
7. Real chain: `deploy_data.ps1 -Apply` (or `build_compileonly.ps1`), `boot_check.ps1`, `flip.ps1`, confirm `FLIPPED OK` and that `PLAY.bat` and `PLAY_PREV.bat` name the right folders, then double-click `PLAY.bat`.
8. Known caveat: scripts read the board and other live paths; a few lane scripts (INT107 apply/rehearsal) refer to scratch folders that are not included.
