# 16:35 CODE RELEASE: ONE apply list (bandits dev, file-only; nothing applied or compiled). Base = tree twins on live lw. Run AFTER the F2 recorded run (15:45-16:30).
Check first (read-only, any time): python -I _agents\laneLG\bundle\check_bundle.py   -> every code patch must say APPLIES-CLEAN (it did at 15:31: 8 of 8, no file touched by two patches). If a tree file changed (STALE), the owner re-runs its make_patch.py / make_patches.py.
## 0. hot_vs_tree
powershell _agents\unitydev\hot_vs_tree.ps1 (as unity-dev does today) so the data twins and the live build are known equal before anything is written.
## 1. CODE: copy each FINAL file over its tree file (tree md5 -> final md5). FINAL = _agents\laneLG\bundle\final\<Name>.cs.patched (+ .diff), built by make_final.py from my RELOADCOVER patches, the F5 agent's patches and the review agent's .reviewed files (LootCfg REQUIRED: the F5 LOOTPAD comment swallowed equipHintBottom/equipHintSize, 3 compile errors at LootUI.cs:278; QualityTiers: choosing only the Upscaler row was ignored).
 Assets\Scripts\StartupWarm.cs        9c2daa5159 -> f8f636558a   (RELOADCOVER)
 Assets\Scripts\GameLoop.cs           2ed65a3e1a -> a02b938a2e   (RELOADCOVER)
 Assets\Scripts\HUD\LoadingCard.cs    39ba687432 -> 60f2b7010a   (F5 cardwait)
 Assets\Scripts\QualityTiers.cs       8b0ef5802b -> b6fdcda1a8   (F5 userscale + review fix)   [was 45d54e6f75]
 Assets\Scripts\Loot\LootCfg.cs       313be1f08e -> 27b0323e78   (F5 lootpad + review fix, REQUIRED) [was 998a858a9f]
 Assets\Scripts\Loot\LootScreen.cs    b8da1fbdd4 -> ffeab2c600   (F5 lootpad)
 Assets\Scripts\Menu\MenuCfg.cs       5cbb083012 -> 5f4e00c913   (F5 dpadnav + bootmenu)
 Assets\Scripts\Menu\MenuUI.cs        36b6090b7f -> 8f2eac2ca5   (F5)
 CHECKS (re-run any time): python -I _agents\laneLG\bundle\check_bundle.py -> 8/8 APPLIES-CLEAN on the current tree. powershell -File _agents\laneLG\bundle\compile_bundle.ps1 [-Final] = Unity 6000.6.7f1 Roslyn over 392 non-Editor .cs: tree alone 0 errors, tree + the 8 FINAL files 0 errors; negative control (F5's original LootCfg .patched) reproduces the 3 LootUI.cs:278 errors, so the check bites. (The review agent counted 408 files; mine skips Editor folders: both 0.)
 Behaviour notes (review): StartupWarm cover.onReload defaults true in code (reload only; the kit sets it true too); LoadingCard waitForStartupWarm holds until maxSeconds 14 if StartupWarm never runs.## 2. DATA kits, in this order, v2 = REBASED ON THE lx TREE (lx = f4ui + f5menu + f5shots + mkt_z7 already applied: loot_ui.json 06c3a54c00, menu.json 351724f3d7, zone_shots.json 7a4d315474, market.json 8970152b3e). Start state of the files below on lx: startup_warm 0f1d81d0a1, loading_card 81ca419b34, quality_tiers 15ea65a016, game_settings 56d314772c, hub e18c6356d4, chase e14b414b9e, zone_span 06af47e058.
 Tools: lgkit = python -I _agents\laneLG\lgkit.py ; f5 = python -I _agents\laneLG\bundle\f5kit_lx.py (a copy of laneF5\f5kit.py with the kits rebased on lx; same commands). No --go = dry run; revert --go in REVERSE order; each writes both twins; a kit refuses (BAD) if its base is not the lx file.
 a) lgkit f1bundle apply --go      startup_warm.json  0f1d81d0a1 -> 329e63f710   (gear cap 8 s, attach frames 100, reload cover keys)
 b) f5 f5loot apply --go           loot_ui.json       06c3a54c00 -> 53ed3fbf6b   (blockWhileMenu on top of lx's stat rows; needs the LootScreen patch)
 c) f5 f5menu apply --go           menu.json          351724f3d7 -> 1bed0ca0c1   (Render scale / Upscaler rows + D-pad axes on top of lx's Controls rows; ALTERNATIVE f5menuboot, pick one)
 d) f5 f5card apply --go           loading_card.json  81ca419b34 -> 1d7e6b6130   (maxSeconds 14 + waitForStartupWarm; needs the LoadingCard patch)
 e) f5 f5gfx apply --go            quality_tiers.json 15ea65a016 -> 31eb54ad88 + game_settings.json 56d314772c -> d5758efc6e   (userScale on; needs the QualityTiers patch)
 f) f5 f5plain apply --go  (LAST; Lead YES)   hub.json e18c6356d4 -> af5764211a, chase.json e14b414b9e -> 1b54a5e59c, zone_span.json 06af47e058 -> 98569614a0   (enabled true: a plain HadesSlice.exe launch = the PLAY.bat game)
 DROPPED: f5shots (already in lx). HELD: f5start (frame check of f5_open_t5/t20/wide first).
 Proved 15:36 on a scratch copy of the lx state (all 9 files): the six kits applied in order a-f, all JSON parse, all reverted byte-identical. RE-RUN 15:59 against the REAL tree twins (lx is live since 15:41): dry-run of all six kits clean, scratch copy of the real files: apply a-f, parse, byte-identical revert.
## f5plain PROOF (QA copy of the new build, inside a FOCUS window; own pid only)
 powershell -File _agents\laneLG\bundle\plain_proof.ps1 -Build <QA copy>\HadesSlice.exe [-Seconds 70]
 It launches the exe with NO behaviour flags (only -savePath sandbox + window size + -logFile), then PASS/FAILs: no '[Zones] launcher flag' line (the DATA turned zones on), '[Zones] on: zone pit_yard spot arrive_start', '[ZoneSpan] bound: 7 zones', [Hub]/[Market] lines, '[SavePaths] save dir redirected', an engage/bandit line, 0 'Exception'. If any FAIL: revert ONLY f5plain (f5 f5plain revert --go) and ship the rest. Also one opening frame vs a PLAY.bat launch of the same build. Defaults already match the launcher: quality_tiers defaultTier ultra2k (launcher passes -quality:ultra2k); a plain launch is a borderless window at native, the launcher is windowed 2560x1600.
## 3. CODE build into a fresh tag (rollback = PLAY_PREV)
## 4. Proofs (one each)
 - Backspace in the first fight: log '[Cover] reload cover held X s (load Y s + N frames)' + a frame at the first lit frame after the restart with the armour on Joan
 - Menu: Esc opens Pause; Graphics shows Render scale + Upscaler; Controls panel shows the three new rows (pick up / equipment screen / horse); pad D-pad moves the menu cursor (needs a pad)
 - Tab equip screen on a weapon row: Damage / Speed / Reach lines; pad Start no longer opens pause AND equip at once
 - Plain launch: card gone no later than the StartupWarm cover (no black gap), armour on Joan at the first lit frame
## HELD, not in this list
 - f5plain (hub/chase/zone_span enabled: a plain exe launch becomes the PLAY.bat game): needs a Lead yes
 - f5start (zones.json start pos 8.6, moves the store-page frame): frame check first
 - mkt_z7 (laneF4, market stock) and t_curve (numbers): Dom's
 - f1warm / f1gear (replaced by f1bundle), f3lw_ui/f4ui (replaced by lw_loot)
Notes: the cold first launch stays ~19.5 s (f1bundle only holds the cover for gear on RELOAD and a normal-length parse); unity-dev builds into a fresh tag.



