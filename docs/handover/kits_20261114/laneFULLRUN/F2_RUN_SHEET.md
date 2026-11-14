# F2 RUN SHEET: recorded clean full run z1 -> z7 (Frankendom Lead, 14 Nov 2026 ~15:30)
Goal (Dom's F2): fresh save, zones 1-7, all 5 bosses killed, gates open, ends on the alpha end screen (tease gate), no soft-locks. Proof = recording + run log. Recorded ONCE, right.

## 0. Honest scope (say this in the DONE line)
The run is the staged harness (`fullrun_z1_z7.ps1`, camscene bot) on a QA copy: Joan takes 0.2x damage with an hp floor, boss kills are scripted (pin to the phase threshold, then a real hp-0 kill through Die()). It PROVES the flow: zone arrivals, wave -> boss order, BossHold release, phase entry, kill record, gate unlock, revive, z7 camp, end screen, exceptions, soft-locks. It does NOT prove difficulty, real kill times, real input, feel or fps. "F2 DONE" therefore reads: "flow z1-z7 clean, assisted bot". A real unassisted playthrough is Dom's play test.

## 1. Build and save
- Build under test = the build PLAY.bat names at run time (read back; expected HDRP_v109lx after the 15:35 release, else lw). Never the live folder itself: the script makes the hard-linked QA copy `Builds\HDRP_v109lcrun` (deploy_data.ps1). Refuse to start if the tree data differs from the live build (no staged kit leaks; `-AllowTreeDrift` off).
- Save: sandbox `C:\Users\dom12\AppData\Local\FrankendomSmoke\fullrun_lc` (segment 1 WIPES it, segments 2-5 reuse it). Dom's real save is never touched (`-savePath` always).
- Before: board check (no FOCUS by others, no HadesSlice running; a plain-flag HadesSlice = Dom playing: stop, do not launch). RAM free >= 22 GB. Post `FOCUS TAKEN Frankendom until HH:MM (F2 recorded run, segment N)`.

## 2. Route and what each segment must show (run in order, back to back, one window each, ~5 min)
| seg | route | must happen | key frames to open |
|---|---|---|---|
| 1 | z1 pit yard (arrive_start) -> Pitborn kill -> gate_sobek -> z2 Sobek reach -> Sobek kill | wipe save, 5 spawns revived, Pitborn phase 2 + kill, gate opens, Sobek phase 2 + kill, z2 stays empty of fodder | arrival z1, Pitborn p1/p2/kill, gate, Sobek p2/kill |
| 2 | hub camp (safe) -> gate_next -> z4 Grendel: z4_p wave, BossHold release, Grendel p1/p2/kill | hub is safe (no engage), BossHold waits for the wave then releases, tells ring, kill | hub arrival, wave, hold_release, Grendel p2/kill |
| 3 | z5 Humbaba: arrival, wave, ONE forced death + respawn, boss p1/p2/kill | grace holds raiders ~9 s (ls), revive 2.1 s, [BossPhase] line at 50% hp, kill | arrival, force_player_death, respawn, Humbaba p2/kill |
| 4 | z6 Erlking: arrival, wave, boss p1/p2/kill, walk to z6 gate | same, z6 lilac pool weaker (lm), Erlking C5 combos | arrival, Erlking p2/kill, gate |
| 5 | z7 market2 (arrive_from_prev) -> hearth -> Armourer Buy tab (Pitborn chest 120) -> end gate -> alpha_end screen | camp safe, stalls open, gear loop visible, end gate needs the z6 kill from segment 4 | z7 camp, Armourer Buy tab, end_screen "Thank you for playing the alpha" |
Gear loop add-on (F4, on lx): in segments 1-4 each boss drop shows its pillar in rarity colour; segment 5 opens the Tab screen (stat rows) and the Armourer.

## 3. Soft-lock definition (any of these in events.txt / frames = FAIL for that segment, name zone + time)
1. Player not controllable > 10 s after an arrival frame is lit (HUD up, no movement response, no cover).
2. A gate / boss / zone step times out (step `took` > its timeoutS) or the script falls back ("falling back").
3. Boss kill recorded but gate does not open, or end gate does not unlock after the z6 kill.
4. Player fell out of the world (`fell-out` lines) or stuck in geometry (position unchanged > 15 s while a walk step is active).
5. Revive does not return Joan to a lit playable frame within 6 s (death -> `revived in` line > 6 s).
6. Any exception line, `NullReference`, `IndexOutOfRange`, or Player.log crash (exit code != 0 / `-silent-crashes` dump).
7. Black frame > 8 s after the cover should have lifted (restart arrivals lit in 4.2-5.3 s on lu; hub 8.0 s is the known gap).
Not a soft-lock: Joan in her plain body suit ~2 s after a restart (known gear re-equip gap), assisted kill timing.

## 4. What to log (the `events.txt` extractor does most of it; add the rest by hand in RUN_LOG.md)
Per segment: start/stop wall time, build tag, save path, boot-to-lit seconds, each step's `took` seconds, [BossHold]/[BossPhase]/[Zones] engage + revive + end lines, boss kill record lines, gate unlock lines, exception count (must be 0), HadesSlice read-back after the player ends (must be none of ours). Per boss: phase-2 start time, kill time. Final: RUN_LOG.md with a table seg x {clean? soft-lock? frames opened}, the five-worst-snags list (section 6), and `F2 DONE <proof path>` or `F2 FAIL <seg, cause>` on the board.

## 5. Where things land
- Per segment: `_agents\laneFULLRUN\f2_seg<N>\` (`run\game.log`, `run\trace\camtrace.csv` + native frames, `events.txt`, `summary.txt`, `key\` + `key_sheet.png`).
- Video: ffmpeg gdigrab on the game window title ("Frankendom") per segment, native size, 30-60 fps, H.264 mp4, NOT desktop capture (start ffmpeg after the window exists, stop before the player exits; the boot-black proof failed once by recording the desktop): `C:\Users\dom12\Documents\kits_out\videos\raw\f2_seg<N>.mp4`. Raw files kept, no montage until the game has sound (Dom).
- Frames only count at native 2560x1600 and when opened and described.

## 6. After the run: five worst snags (Lead writes, each with a fix owner)
Format: `# | snag | evidence (frame/log) | DATA or CODE | owner (kits dev = data kit / unity-dev = code build + flip / agent = file-only patch) | release target`. Candidates already known from segments 1-5 earlier: cold boot ~19.5 s and Joan gear pop-in (CODE, worker-thread parse, F5), hub restart 8.0 s, boss phase 2 truncation/BossPhase line only at 50% hp, boss death frame not captured (script), ride legs (parked). The run decides the final five.

## 7. Time and cadence
Book 15:45-16:30 as a quiet slot on the board (other games hold their players). Segments back to back, ~5 min each + 1 min gap to read events/frames; the release windows stay: lx 15:35 (before), F5 after the run (16:35). A segment never takes a release window. Between segments the kits dev posts any soft-lock kit (scan segment logs as they land).
