# FRANKENDOM LEAD HANDOVER, 14 Nov 2026 00:45 (new account Lead; Dom shut the laptop down for ~8 h)

Read first: this file, then `_agents\HANDOVER_LEAD_2026-11-13_kzzz.md` (older), `_agents\ROADMAP_2026-11-13.md`, `AGENTS.md`, board tail `_agents\COORDINATION.md`, and the two side-lane handovers written at shutdown: `_agents\unitydev\HANDOVER_dev3_*.md`, `_agents\laneTRIPOLIVE\HANDOVER_kitsdev_2026-11-14.md`.

## 1. State
- LIVE = `Builds\HDRP_v109li` (PLAY.bat read back 00:44), PREV = `HDRP_v109lh`, then `lg`. Git clean, in sync with GitHub (last backup e38efee). Nothing running at 00:44 except an unrelated process (not ours).
- All session ids are dead after the shutdown: find sessions by TITLE with `mcp__ccd_session_mgmt__list_sessions`: "Frankendom - Lead Dev", "Frankendom Unity dev" (builds/gates/flips), "Frankendom kits dev: stages data kits" (file-only staging), "z - Strategy - Fable 5.2" and "z - COO - Opus 5.5" (Dom's voice; Dom's order: always take their advice, safety rules still win), Armagedom/FrontierDom/Opentrident/Tripo.
- Releases shipped tonight (all hot data except none): la camp NPC weight fix; lb/le pit GREY (closed, known issues: no warm glow from the yard, doorway fog milky); lc hub(z3)+z7 look; ld step 5 phase-2 combos (Pitborn/Sobek/Grendel/Humbaba); lf z4 look; lg RIDING (mount.json enabled, mountSinkM 0.045); lh z4 look ROLLED BACK (milky blue-white fog in look-back view); li LOADING CARD hold cut (extraAfterPrewarmS 8.0 -> 1.0, calm 3.0 -> 1.5) = boot black ~25 s -> est. ~16 s (NOT yet measured on a real launch).
- Step 3 (BOSSHOLD release) proven in frames. Steps 1,2,3,5 done; 4 mostly (z5/z6 looks PARKED, pit CLOSED); 7 riding live but unpolished; 10 full run: seg 1 and 2 done, 3-5 NOT run; 9 fps last; 6 and 8 skipped by Dom.

## 2. Pending, in order (Strategy/COO order, windows :05 :20 :35 :50 x 5 min, MACHINE RULE v3: short capture allowed beside a bake if free RAM >= 22 GB)
1. Measure the boot black on a REAL PLAY.bat launch of li (card gone / first lit frame / Joan controllable) + 3 frames (pop-in/hitch after the card drops). Revert if bad: `python -I _agents\laneBOOT\bootkit.py card revert --go` + deploy + flip, or PLAY_PREV.bat. Kit `eager` (shield out of eagerFiles, est. 18.6 -> 13 s QA) NOT shipped: check Joan's shield attaches in time.
2. RIDER SEAT: kit `_agents\laneLG\lgkit.py ride` (seatRaiseM +0.05, seatBackM -0.03 on horse/demon/camel; rebase on li files first). Proof must be OUTSIDE the camp (z3 is the camp, no riding) in z1 or z4 open ground, mounted plain + demon, native: legs along the flanks? sword? Then flip. SWORD through the horse's neck: C# patch `_agents\laneLG\weaponhide\MountRider.cs.patched` (+ .diff) = one CODE build (~90 s, HdrpBatch.Build, Editor gate: RAM >= 20 GB), then data kit `hidekey`. Joan's legs at the saddle need a riding clip/IK (not data): parked beyond the seat nudge.
3. Full-run segments 3, 4, 5: `_agents\laneFULLRUN\fullrun_z1_z7.ps1 -Segment N` (-DryRun first); take arrival key frames >= 20 s after boot. Then the five-worst-snags list (a draft is on the board 00:15; seg 3-5 add).
4. Stablemaster Buy tab never seen (market.json: hub_trade_c/z7_trade_c list mount_horse 50, warhorse 400, camel 550; prices kit `laneMOUNTP7\shop` NOT applied: economy tuned only after Dom plays). Hub riding is allowed (dismount.inHub false); camel unproven.
5. Step 7 hub/progression, then step 9 fps (quiet slot only), then close step 10.
6. Parked: z5/z6 look kits, z6 lilac pool (it is the z6 look row bounceLux 700/key 38: kit laneLG z6pool bounceLux 400/key 30), pit doorway glow kit laneLG pitglow, z5/z6 bossHold kit (laneBOSSHOLD\z56: only if frames show a wave and boss overlapping; z6 wave frame showed Erlking staying back).
7. Unproven: new combos lllh/lh/llh actually start (needs a balance/Report run), Erlking C5 hhh/lhh may truncate (heavy_overhead has no chain follow), ghost_hound close still (hounds spawn, never in frame).

## 3. Rules that bit tonight
- Dom's words beat relayed orders; Strategy overruled COO once (full run before step 7); I followed Dom's direct order (riding first).
- End your OWN player pid before posting FOCUS RELEASED; read back Get-Process HadesSlice. A HadesSlice with plain PLAY.bat flags (no -camscene/-savePath) is DOM playing: never stop it, never launch beside it.
- deploy_data.ps1 `-v` takes the tag WITHOUT the "v": use `-v 109li` (a bare `-v li` made folder HDRP_vlh, I renamed it and fixed build_info version). Build folders keep the HDRP_v109 prefix.
- Flip routine: edit tree twins -> `deploy_data.ps1 -v 109<tag> -Base HDRP_<live> -Apply -IgnoreAssetChanges -SkipGates` -> `_agents\unitydev\prod_data_check.ps1 -Build HDRP_v109<tag>` (exit 0) -> `_agents\unitydev\release.ps1 -Build HDRP_v109<tag> -TreeAhead -Apply` (refuses while a game runs; its board append can fail on a locked COORDINATION.md, harmless, flip still happens) -> read PLAY.bat back -> `_agents\pool\backup.ps1`. Roll back = PLAY_PREV.bat.
- COORDINATION.md is often locked: append with System.IO.File.Open(...,'Append','Write','ReadWrite') + StreamWriter, retry.
- Frames only count at native 2560x1600 and when opened; look-back direction matters (a like-for-like pair from one camera missed the milky z4 fog); a QA copy is never flipped.
- Data rule (Dom): no hard-coded values; every new tunable goes in JSON, both twins (Assets\Data + Assets\StreamingAssets) byte-identical. No C# changed in this repo tonight; the sword hook would be the first (default OFF).
- _agents\ is gitignored: copies of key docs live in docs\handover (refreshed at shutdown).

## 4. Open for Dom
Play li (boot time, riding); 67% FSR2 keep/revert; Sobek/Humbaba difficulty; settings menu + controller; z7 shop vs fight; pit orange vs grey was decided by Strategy for Dom = grey.
