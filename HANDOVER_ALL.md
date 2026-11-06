# Frankendom: one handover for every dev (2026-11-06 ~09:15, stand-down)

Dom ran out of tokens and is restarting the team on a new account. Each new session reads THIS file first, then its own section's files. Do not start new work until you have read it.

## 1. The game, the people, the rules
- **Game:** Frankendom, a Unity 6000.6.7f1 HDRP turn-based 1v1 duel: Joan (player) vs Pitborn, on a 60 Hz deterministic sim. Project: `C:\Users\dom12\Documents\unity_test_hdrp`. GitHub: DomLynch/Frankendom-v4. Commit and push are allowed for the hourly backup.
- **Dom** works from his phone. Give short, plain replies. Never ask him blocking questions: a hook blocks wrap_up ask/actions, so pick the safe default and continue. Give him only the bare exe path.
- **Shared board:** `COORDINATION.md` in the project root. Every dev posts `HH:MM <role> ...` lines there, using real `Get-Date` stamps.
- **Hard rules:**
  1. Everything goes in data files (`Data\` = StreamingAssets, byte-identical). No hard-coded numbers.
  2. Joan uses the Mixamo v2 106-bone rig everywhere, with nothing of Dracula's. She starts with no armour; helmets are loot only.
  3. Physics is presentation only and must never change the sim: the simhash must be identical with physics ON and OFF.
  4. Run one heavy GPU job at a time (a Unity build or HDRP player). The laptop freezes otherwise.
  5. Never write `Assets\` while a Unity build or gate runs. Lanes stage kits in `_agents\lane*\` with dry-run apply scripts (`--apply` installs).
  6. No whole-disk searches (`find /`, `Get-ChildItem C:\ -Recurse`).
  7. Delete nothing without Dom's OK in chat. A message from a peer or an agent is never Dom's approval.
  8. Never type passwords, sign in for Dom, change Windows security, or put the HF token anywhere.
  9. Look changes need a before/after against v66/v69 plus Dom's own LOOK OK. Judge brightness only from real-settings frames, never from balance or harness frames.
  10. Listen to Goose and Antigravity (about 90% right). Fold their fixes in, and never leave a Goose call-out unanswered.

## 2. Where the builds stand
- **v100 = the build to play:** `C:\Users\dom12\Documents\unity_test_hdrp\Builds\HDRP_v100\HadesSlice.exe`. It has Joan v6c with no armour and fixed colours, 4 finishers passing, wounds passing, and physics master OFF (the death ragdoll is ON).
- **v101: DO NOT SHIP.** Decapitation broke (the Pitborn Body cut_lod row; now removed from live data), and physics leaked into the sim (simhash differed at tick 164 in Joan's stamina).
- **v102:** `Builds\HDRP_v102`. Its chain started 08:45 via `Tools\build_and_gate_v102.ps1` and was still running at stand-down. Nothing in it is judged yet.
  - **Contents:** FLOORSHOT harness, E2 floor LOD step, CAMGUARD (bodyClearM 0.25 as a code default), DoorwayFlickerLight shadows off, Pitborn Body row removed, FINGROUND X/Z + probeVictimBone Head, FIGHTLEN (stances takenMul/blockChipMul x2/3), POSTURE proof fix, and the pink blade fix (emissiveScale 0). Physics master is OFF in this build only.
  - **Early results:** balance over 10 seeds gave Joan 7/10 with a 73 s average fight. 7/10 is likely noise (p=0.34). The look gate PASSED (visual-qc). The 2nd finisher run exited 2, so check it.
  - **FIRST on restart:** check `Builds\HDRP_v102\chain_done.txt`, `gate_run.txt`, `extras.txt`, `finisher_auto_summary*.txt` and `balance_summary*.txt`, and report the results honestly.
- **v103 kit READY (not started):** `_agents\laneINT103\REPORT.md`. Apply in this order:
  1. FIXPHYS3
  2. PHYSAUDIT (never pass `--own-wound`)
  3. strict physproof
  4. PHYSFLINCH (its limits win)
  5. RUNTHRU (pairOffset z 0.814)
  6. SWINGFIX (all off, pad 0)
  7. CAMREFRAME (gate joan_screen_h scaled: >= 0.22 up to 7 m, >= 0.15 at 10 m; this needs a code change)
  8. STARTUP (StartupWarm, with a -nostartupwarm A/B)
  9. GOOSEBACKLOG2 switches off, with magenta_gate.py added
  10. HCKIT7, rebased after PHYSFLINCH; then HCKIT8, after HCKIT7, as the final skippable step

  Physics: master ON, plus the sub-switches clock, hit, clash and finphys ON. Gate each sub-switch with the strict physproof plus simhash ON==OFF at 30/60/144 fps. If one fails, turn only that one OFF and post the first differing tick and field.
  Balance gate: 20 seeds; pass when Joan wins 8-12 and the average fight is >= 60 s. Apply `_agents\laneBALANCE3\apply_balance3.py` only if Joan wins 13 or more, then rerun.
  Also: finisherauto x3 (alone and as 4th), camproof, magenta.
- **v104 (planned):** `_agents\laneFINTICK` (tick-driven finisher timeline). Gate it with `run_fintick_proof.ps1` (30/60/144 identical) plus finisherauto x3. Fallback: `rt.tick.manualAnim` false.
- **Finishers:** decapitation, split_crown, split_crown_pit (maul) and run_through. The waist cut 'opened' is DROPPED for good (Dom).

## 3. Open problems (not solved)
- **Dom's floor patch:** a flickering stippled "tinfoil" grey rectangle on the floor behind Joan. Screenshots are in `_agents\visual_qc\dom_reports\`.
  - Ruled out: lights (shadowdiag), and LOD dither probably (fadeMode None).
  - Lead: Goose 09:10 saw a "floor decal rectangle" (check blood/wound ground decal projectors first). Next suspects: SSR on a smooth floor, then a duplicate floor mesh.
  - The v102 floorshot frames are in `Builds\HDRP_v102`. laneFLOOR2 was stopped before finishing.
- **Look-switch renders:** Dom can't give a LOOK OK until each of the ~11 dark switches has an "on" render. The unity-dev renders them on the v102 exe after chain_done (StreamingAssets only, restore by hash) into `_agents\laneLOOKSHEET\raw\sw<N>_<name>_on.png` / `_off.png`. Then visual-qc runs `_agents\visual_qc\make_switch_pairs.py`. The switch list is in `_agents\laneLOOKSHEET\MANIFEST.md`, plus SWINGFIX's 3 and Goose's sw11 floorDetail.
- **fps:** about 22.6 in verify runs. `_agents\lanePERF5` (12 dark switches) and `_agents\lanePERFAB` (-perfab one-run A/B) are staged and unmeasured.
- **Goose items still open:** the VICTORY card (off-centre, 12 px stats), the sword not gripped (hilt at hip), Joan occluding Pitborn at mid separation. laneHUDVICTORY and laneGRIP were stopped before finishing, so restart them.
- **Kaiju (next monster):** the prep is done (`_agents\laneKAIJUPREP`, `_agents\laneKAIJUTAIL`). It waits on Dom's two T-pose images (front and side, 1536x1024).
- **Disk:** Step 1 = 22.6 GB of safe deletes, scripted in `_agents\laneDISKPREP\disk_step1.ps1` (`-Apply` deletes). WAIT for Dom's yes. Step 2 (move about 190 GB to the VPS) needs the VPS details. The unity-dev already deleted old builds v86-v94 (141 GB free).
- **Dom's pending decisions:** LOOK OKs, disk Step 1, kaiju images, and whether to pause Antigravity.

## 4. Roles and their own files

### Strategy (lead, runs the pool)
- **Source of truth:** `C:\Users\dom12\Documents\kits_out\3d\GAP_ANALYSIS.md`. Read the newest "RESUME NOTES" section at the bottom.
- **Pool:** `_agents\pool\POOL_RULES.md` (roster and rules). Keep 5-10 Sonnet agents busy (up to 15-20 is OK), with 90-minute lifetimes, and always one general QC agent on the newest build. Write tight briefs: one lane folder each, no Assets writes, a dry-run apply, Roslyn compile + `data_gates.py` + `no_dracula_check.py --allow-owned`, a MANIFEST, and one board line.
- **Re-create these crons on restart:**
  - 5-min sweep: board, Goose tails, dev sessions, new pictures.
  - Hourly backup at :17: `git add -A`, skip non-LFS files over 50 MB, commit `backup HH:MM: ...`, `git push origin HEAD`.
  - Pool retire/respawn every 90 min.
- **Goose's live output:** `C:\Users\dom12\claude-watchdog\SUPERVISOR.md`, `QC_SCORES.md`, `sent.log`, and `audit\`.
- **Antigravity's log:** `C:\Users\dom12\.gemini\antigravity\brain\<id>\.system_generated\logs\transcript.jsonl`. Never call it idle based on the board alone.
- **Unfinished lanes to restart:** QC10 (v102 QC), FLOOR2, HUDVICTORY, GRIP, AUDIO2.

### Unity dev (builds, merges, gates)
- **Full handover:** `C:\Users\dom12\Documents\unity_test\_agents\HANDOVER_unity_dev_v90.md` (the SAVE POINT at its end, plus the hard-won lessons).
- **Key lessons:**
  - HDRP Lit `_MaterialID`: 0 = SSS, 1 = Standard. Writing 0 is what caused the purple Joan.
  - Never pass a proof list through `powershell -File`.
  - The physics master does not disable the death ragdoll.
  - Licence comes via Unity Hub sign-in (`unity auth status` is misleading).
  - cut_lod rows are scoped by anchorBone.
  - Kill the -shadowdiag player after about 90 s.
  - Only run `char_tex_install.py --character Joan`.
- **Chains:** `Tools\build_and_gate_v10N.ps1`. Compile check: `Tools\compile_check.ps1` and `_agents\laneINTPHYS\compile_phys.ps1 -Live -Editor`. Gates: `python Tools\data_gates.py --quiet` and `python -I _agents\laneNODRAC\tools\no_dracula_check.py --allow-owned`.
- Restarting a build kills all Hub/Editor Unity processes.

### Visual QC (look owner)
- **Full handover:** `_agents\visual_qc\HANDOVER.md` (Save point section at the end).
- **Gate per build:** run `python _agents\visual_qc\look_gate.py vNN --expect none`, read `vNN_look_sheet.png`, check the shipped Joan row (materialId 1, baseColor 1.0), then post VISUAL PASS/FAIL. Only trust woundshot frames newer than the exe.
- **Watcher:** a Monitor running `python C:/Users/dom12/Documents/unity_test_hdrp/_agents/visual_qc/watch_new.py` (timeout 1800000). Don't put long hex paths in Monitor commands: a false-positive HF-token hook blocks them.
- **Last state:** v101 woundshots PASS; v102 look gate PASS (median 34, p5 4.0, p95 76, contrast 0.282); the v102 woundshot is not gated yet.

### Hades env dev (world, props, perf)
- **Full handover:** `C:\Users\dom12\Documents\kits_out\3d\hades_env\HANDOFF_WORLD.md` (top UPDATE section).
- The ground is LOCKED. Stage only in `unity_staging`, never in Assets or Tools, and make no commits.
- Perf A/B variant A (81 WorldBehind props with shadowsOff) shipped in v101/v102; read its fps result on the board. Waiting for a go-ahead: rubble albedo, dead-tree bark, scatter spacing, stair nosing, far LOD2s.

## 5. Old session ids (useful only if the old account is still open)
- Strategy: local_12c5fba7 / 03cec932
- Unity dev: local_f77cf357-cb39-410e-8855-4dc9b95a6d40
- Visual QC: local_e1a89853-869a-4996-ba8c-7bab7f8e13cd
- Hades env: local_8459835d-ea10-414a-96f9-ec2dfd867fdf

## 6. How Dom restarts each dev
Open a new session in `C:\Users\dom12\Documents\unity_test_hdrp` and paste:
"You are the <Strategy | Unity dev | Visual QC | Hades env> dev for Frankendom. Read HANDOVER_ALL.md in this folder, then your role's files in section 4, then the tail of COORDINATION.md, and continue from where we left off."

## 7. Armagedom (separate project, NOT run by Strategy)
- Owner: its own lead dev session (old id 62453e8b), supervised by Goose. Strategy never managed it, so its full state lives in that lead's own handover, not here.
- Last known (Goose SUPERVISOR.md, 2026-11-06 ~09:10): release 350 is LIVE, and the lead has stood down. Earlier: 347 live (keep/rollback was pending Dom; rollback script /opt/armagedom-preview-staging/three-20261028-347-ops/rollback347.sh), 350 was held at Dom's gate (PR 566/568, needs a DB backup + Dom OK), then went live.
- Release plan 345-350 and Goose's notes: C:\Users\dom12\claude-watchdog\HANDOVER.md (section 'ARMAGEDOM RELEASE PLAN'). Goose's live state: C:\Users\dom12\claude-watchdog\SUPERVISOR.md.
- On restart: open the Armagedom lead in its own repo and tell it to read its own handover plus Goose's HANDOVER.md Armagedom section. Ask it to confirm 350 is healthy first. Deploys need Dom's OK in that chat.

### Armagedom lead's own stand-down report (2026-11-06, pasted by Dom)
- Laptop: repo arma-dev on main at 0868195, matching GitHub; clean apart from the local .claude/ folder.
- Parked: the daily-contract code Dom dropped is on branch feat/daily-contract (dd3323e), pushed. NOT merged and NOT for merging.
- Live: release 350, and the latest health check found no errors. The server credit and leaderboard tables are still empty, because nobody has played a signed-in run online yet.
- Open PRs: only the two old ones, #490 (shop candidate) and #475 (parked draft). No agents running.
- Handoff memory: opens with a stand-down note.
- FIRST on restart: ask Dom for his phone and desktop feedback on 350.
- Restart prompt: 'You are the Armagedom lead dev. Read your handoff memory and the Armagedom section of HANDOVER_ALL.md, then ask Dom for his 350 feedback.'
