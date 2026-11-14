# F2 recorded run z1-z7 (Frankendom unity-dev), 2026-11-14
Five segments, each one window, QA copy of the live build, one shared fresh sandbox save (segment 1 wipes it), native 2560x1600 frames, own pid each time, raw frames + game.log kept per segment.
| seg | zones / bosses | window | build base | result |
|---|---|---|---|---|
| 1 | z1 Pitborn, z2 Sobek | 14:05 | lv data | both bosses fought and killed ([BossPhase] Pitborn 240/520, Sobek 283/480; '[Bandits] gate ... open (boss gone)' z1 + z2; Sobek's trident drop), 0 exceptions |
| 2 | hub, z4 Grendel | 14:35 | lv | BOSSHOLD ok, raider wave killed, [BossPhase] Grendel 311/520, boss gone, 0 exceptions |
| 3 | z5 Humbaba | 14:50 | lv | arrival lit, 3 raiders killed, forced death revived in 2.1 s, [BossPhase] Humbaba 278/560, boss gone, 0 exceptions |
| 4 | z6 Erlking | 16:20 | ly | 3 raiders killed, Erlking fought and killed (hp 529 -> 430 then kill), boss gone, gate walk, 0 exceptions |
| 5 | z7 camp + end gate | 16:35 | ly | arrival lit after 26 s hold, hearth walk, z7_end walk, '[Zones] end screen alpha_end' (THANK YOU FOR PLAYING THE ALPHA), 0 exceptions |
Totals: 5 bosses killed (Pitborn, Sobek, Grendel, Humbaba, Erlking), 0 exceptions, 0 'fell out of the world', no stuck / gave-up lines, ends on the end gate screen. Frames: f2_seg1..f2_seg5 (key_sheet*.png, run\trace\frames). Not video: no recording tool in the harness, frame sets at 2-4 Hz (the Lead parked the video until sound).
Harness caveats: the first arrival frame after a cold boot is black (card off in harness runs), Joan shows her plain body suit for 2-3 s after a restart until the gear re-equips, bosses are killed by the harness's data kill after the phase-2 window (killAfter), so these are proof runs, not a skill run.