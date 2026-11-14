# FULLRUN step 10: DISCOVERY run z1 -> z7 on live lc (STAGED, NOT RUN). Bandits dev, 2026-11-13 ~20:55
Script: `fullrun_z1_z7.ps1` (parses with 0 errors; `-DryRun` prints the steps and estimate for all 5 segments, launches and writes nothing; nobody has run it with a player). unity-dev runs one segment per window, in order 1..5. Nothing here flips, edits live data or touches the live build: it runs on a hard-linked QA COPY `Builds\HDRP_v109lcrun` (deploy_data.ps1 from the build PLAY.bat names, read back at run time = `HDRP_v109lc` now).

## How a segment goes
`powershell -File _agents\laneFULLRUN\fullrun_z1_z7.ps1 -Segment N` (add `-DryRun` first to read the steps).
1. Reads the live build from PLAY.bat. Makes the QA copy if it is not there, refusing if the tree data differs from the live build in zones/bandits/ai_adapt/poise/boss_phase/look_zones/accent_lights/mount/fight_balance/opponents (so staged kits cannot leak into the run; `-AllowTreeDrift` overrides). On the copy only: menu.json off (a stray key pauses the sim), Joan `takenMul 0.2`, `hpFloorFrac 0.97` (the harness assist, see "cannot judge"); Joan's damage stays 1.0.
2. Writes the segment's steps into the copy's `camtrace.json`: frames 2 Hz, `width 0` = native 2560x1600, `globalTimeoutS = MaxMinutes*60 - 25`.
3. Waits for the Frankendom slot (minute 3-5 of the ten), no FOCUS claim by others and no HadesSlice running, takes `player.lock`, posts FOCUS TAKEN until +MaxMinutes (default 5).
4. Launches the real launcher flags from `play_zones.ps1` (`-zones -quality:ultra2k -screen-width 2560 -screen-height 1600 -screen-fullscreen 0`) plus the harness flags `-camscene -camtrace:<dir> -banditylog:5 -silent-crashes -savePath <sandbox> -logFile`. Hard stop: kills the player at MaxMinutes minus 5 s, releases the lock, posts FOCUS RELEASED.
5. Extracts (read-only): `events.txt` ([BossHold], [BossPhase], [Zones] engage/revive/end screen, [CamScene], [Bandits] gate, [AiAdapt] combo/enrage, [GroundRing], [CombatSlowMo], fell-out-of-world, exceptions, Joan [HudHp]), `summary.txt` (counts), and `key\` = the nearest native frame to each tagged moment: `<zone>_arr_arrival`, `_p1_boss_start`, `_p2_phase2`, `_kill_boss_death`, `_dead_after_death`, `z4_wave_wave`, `z4_rel_hold_release`, `z5_force_player_death`, `z5_respawn_respawn`, `z7_hearth_z7_camp`, `z7_endscreen_end_screen`. The full frames stay in `seg<N>\run\trace\frames`.
Output: `_agents\laneFULLRUN\seg<N>\` (`_progress.txt`, `run\game.log`, `run\trace\camtrace.csv`, frames, events, summary, key). Sandbox save `C:\Users\dom12\AppData\Local\FrankendomSmoke\fullrun_lc`: **segment 1 wipes it, 2..5 reuse it** so boss kill records and gate unlocks carry over (segment 5 needs the z6_erlking kill from segment 4 for the end gate).

## Segments, resume spot, estimate (step seconds; add about 30 s boot; window budget 5 min)
| seg | covers | resume spot (restart step) | est steps | notes |
|---|---|---|---|---|
| 1 | z1 Pitborn, z2 Sobek | pit_yard / arrive_start, revives all five boss spawns | 240 s (tight: 270 with boot; use `-P1S 15 -P2S 15` if it overruns) | z2 stays empty of fodder, bosses only |
| 2 | hub, z4 Grendel | hub_camp / arrive_from_prev | 190 s | z4_p wave + BossHold release, then Grendel |
| 3 | z5 Humbaba | z5_humbaba / arrive_from_prev | 162 s | wave fight, ONE forced death + respawn frames, boss |
| 4 | z6 Erlking | z6_erlking / arrive_from_prev | 159 s | wave, boss, walk to the z6 gate |
| 5 | z7 camp + end | z7_market2 / arrive_from_prev | 103 s | hearth, end gate, alpha_end screen |
Each segment restarts at its own resume spot, so a failed or cut-off segment is re-run alone (`-Segment N`); you never have to replay earlier zones. If a segment is killed by the 5-min stop, its `events.txt` and frames up to then are still extracted.

## Boss pacing (per boss, the same block)
`<z>_meet` walk to the boss stand until it engages (Pitborn: pos (0, 13.5), his `pitborn_stand` spot has no position; others use their stand spots) -> `<z>_p1` real fighting `-P1S` s (default 20 s; 4-direction light cycle, Joan damage 1.0) -> `<z>_p2` at the moment the boss is pinned to just under its phase threshold (ai_adapt enrage hpFrac applied to the effective max hp: Pitborn 520 -> 240, Sobek 552 -> 310, Grendel 650 -> 370, Humbaba 728 -> 345, Erlking 900 -> 430) so the phase starts and phase 2 plays for `-P2S` s with Joan's own blows (HPLEFT step) -> `<z>_kill` kill 1 s after engage (a real hp 0 through the sim's Die(), so kill record, gate unlock, silver, drop and kill slow-mo all run) -> `<z>_dead` 4 s hold. This is pacing, not a measured duel: it does not tell how long a real fight lasts.

## What the run can judge
Flow z1 -> z7 with the real launcher flags and native frames: arrival frames per zone (look before/after against `refframes\kr_native_2560x1600`), engage/BossHold release (z4), phase entry lines and the phase-2 pattern frame, kill record + gate unlock, wave -> boss order, the Joan death + revive path (z5), hub safe-zone behaviour, z7 + alpha_end screen, exceptions, fell-out-of-world spam, per-step wall time, and the CamZone pass rule columns in camtrace.csv.

## What the run cannot judge (do not read these from it)
- **Difficulty / fairness / real kill times**: Joan takes 0.2x damage and has an hp floor 0.97; the boss kill is scripted (pin + kill step). No assist-free win is proven (the 12 Nov report: the camscene bot lands about 1 hit in 10 against a guarding boss).
- **Real input**: no keys, mouse, controller, menu, inventory, trading, riding, abilities or potions (the bot presses light strikes in a 4-direction cycle only; no guard, parry, dodge, skills).
- **Feel**: hit feel, camera comfort in a real fight, audio, UI readability, controls.
- **Phase-2 attack variety**: the bot's pacing means the new combo strings may or may not fire; combo counts are in events if AiAdapt logs them, otherwise not provable. Not proof the C5 pattern reads.
- **fps**: frames cost time (2 Hz native capture), segments are 5-minute slices; use the quiet-slot fps lane for numbers.
- **Boss death through real damage** (kill slow-mo timing, finisher), the 150 s BossHold fallback, long-run memory/leaks (each segment is a fresh process), save/continue across a quit, and anything that happens beyond the 5 minutes of a segment.
- The first frames after a zone restart can be black for a few seconds (harness fade); the 12 Nov report saw about 33 s black after restart, not proven a player bug.
- Wave rows (z4/z5/z6 `_p`) behave as live data says; z2 stays empty by Dom's ruling.

## Unverified assumptions in the script (read before trusting a segment)
- Step fields (`ready, restart (zone, spot, revive), hold, walk (zone+spot or pos, untilEngaged), fight (cycle, boss, killAfter, hpLeft), forceDeath (count, gapS)`) are taken from ZoneCamScene.cs / CtStep as read today; the live build's compiled code was not diffed against the tree.
- `camtrace.csv` columns `t` and `step`, and frame `f_<row>.jpg` numbering = `frames.csv` row, are from the dev2 handover (section 5); the key-frame extractor skips with no error if a column name differs. The raw frames are always kept.
- Pitborn meet position (0, 13.5) and the z7 `hearth` / `gate_alpha_end` spots are read from zones.json; nobody has walked them in this order.
- Estimates are step timeouts/pacing, not measurements; the restart step may take longer than the 35 s I allowed.

## Window rule note
A 5-minute run starting at minute 3 ends at minute 8, i.e. it runs into FrontierDom's slot (minutes 6-9). The 20:01 setup-session rule allows a run past the window only if the next project posted nothing; check the board before starting, or use `-MaxMinutes 3` with `-P1S 10 -P2S 10` only for the short segments 3-5 (estimates 160/159/103 s need roughly 3.5 min including boot, so a strict 3-minute window does not fit; two shorter runs per zone would be needed).
