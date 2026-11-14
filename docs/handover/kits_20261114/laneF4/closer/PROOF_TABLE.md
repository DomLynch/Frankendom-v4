# F4 closer: PROOF_TABLE (file-only, 2026-11-14, live = HDRP_v109lx)
Paths under C:\Users\dom12\Documents\unity_test_hdrp\_agents\ . Every frame below was opened; descriptions are what is on screen.
Live lx data checked: gear.json d1fb8581f4 (21 rarity, 7 statList), market.json 8970152b3e (= mkt_z7: z7_trade_a lists pitborn_chest, row erlking_axe 45), loot_ui.json 06c3a54c00 (rarities common/fine/rare/relic; stat rows armor..reach).

| item | best proof | what it shows | verdict |
|---|---|---|---|
| (a) DROP | laneFULLRUN\f2_seg1\run\trace\frames\f_000081.jpg, f_000083, f_000086, f_000090 (build v109lc; game.log:1399 `[LootUI] marker 'pitborn_maul' at (-0.04,0,14.06)`); f2_seg1\key_sheet2.png bottom right (Sobek, label "Sobek's trident" over a pale-teal pillar). Other markers: f2_seg2 game.log:1735 joan_tabard, f2_seg3 :2323 pitborn_pauldron_joan, seg4_final :2477 cap_joan | Pitborn's body lies dead in the yard, label "Pitborn's maul", tiny sub-line, orange "[E/X] TAKE" prompt, faint pillar under the label, Joan next to it. Label ink is the old uniform pale colour (pre-rarity build lc) | PRESENT for 5/5 bosses by log + frames, but NOT proven in rarity colour on lx (Rare = orange 0.95,0.52,0.2). Drop-in-colour = MISSING |
| (b) BUY | laneHOTSHIP\gatelx\lx_sheet1.png (row 2 mid + right, row 3 left); gatelx\run2\game.log `[CamScene] trade buy pitborn_chest at z7_trade_a: ok, coins 418 -> 298`; gatelx\loot_save_after_run2.json (owned has pitborn_chest, coins 248 after a 50 horse; ledger rows coins -120 / -50, ids named "sell-N" even for buys, cosmetic) | Armourer panel, "418 silver", rows gauntlet arms / leg harness / shield greyed OWNED, bascinet 49, helm 54, bracers 27; detail line "Pitborn cuirass, Armour 10 Poise 8, The trader asks 120, Press again to buy for 120"; next frame HUD "298 silver"; Stablemaster Warhorse 50 then "248 silver" | PRESENT (best) |
| (c) SELL | log only: unitydev\item5_r1\game.log:1848 `trade sell joan_bracers at hub_trade_a: ok, coins 103 -> 112` (+ :1822 sell grendel_blade_R refused "no market.json row"); same in item5_r1\trace\camscene_done.txt. Build v109gp, no frames, old data | nothing visible: no frame of any Sell tab exists; no sale under lx; erlking_axe row (45, sells 15) never exercised | LOG ONLY, frame MISSING |
| (d) GEAR ON JOAN | laneHOTSHIP\gatelx\run3\run3_sheet.png and run3\f\f_074_t30.7.jpg: Joan with a huge war axe (bronze blade, bone head) at her side, round shield on the back, red tabard skirt, grey cuirass, pauldrons; gatelw\run1\tabard_joan.png: Joan from behind with red-black tabard skirt, shield, sword. Inv log `worn [chest=cuirass_joan,tabard=joan_tabard,arms,legs,mainHand=war_axe,offHand=joan_shield]`. Tab screen gatelw\run2\tab_sheet.png and gatelx run3 t36: EQUIPMENT lists Old iron cuirass, Heraldic tabard (blue = fine), War axe (blue) | PRESENT (axe + tabard + shield). The bought pitborn_chest is owned but never worn on camera (optional). Tab frames sit on the HEAD row: stat rows not seen |

## Missing items: smallest runs for unity-dev
No data gap, so no kit: lx already holds the rows (market row erlking_axe, rarity, stat rows). Both gaps are harness runs. No armor/damage/price number is touched.

### M1 SELL frame (c). One run, about 40 s, gatelx.ps1 run-2 pattern on a QA copy of lx (sandbox save only)
- Flags: `-zones -quality:ultra2k -screen-width 2560 -screen-height 1600 -screen-fullscreen 0 -silent-crashes -savePath <new sandbox> -camscene -camtrace:<out>\trace -logFile <out>\game.log`
- Sandbox loot_save.json: gatelx $lsBase with "erlking_axe" and "pitborn_chest" appended to owned, coins 418, sales []; zones_save.json zone hub_camp spot arrive_from_prev (as run3 NewSave).
- camtrace.json: frames.enabled true, hz 4.0, width 0; scene.steps = ready(run), hold 2, inv, trade{action:sell,item:erlking_axe,stall:hub_trade_a,timeoutS:40,run:true}, inv, trade{action:sell,item:pitborn_chest,stall:hub_trade_a,run:true}, inv, hold 2.
- Fallback if the log says not-listed at hub_trade_a: restart step to z7_market2 / arrive_from_prev and use stall z7_trade_a.
- No key presses needed (the step drives MarketUI). For a keyboard run: hold each key 0.35 s (down, sleep 350 ms, up), 0.8 s between keys; the 140 ms taps in gatelx run3 did not move the equip cursor.
- Frame: trace frame with the market panel on the Sell tab, row "Erlking's antler great-axe ... 15" selected ("Press again to sell"); the next frame with HUD 433 silver. Pass: log `trade sell erlking_axe ... ok, coins 418 -> 433`; loot_save.json owned lacks erlking_axe; sales row coins +15 (then the chest +40 = 473).

### M2 DROP in rarity colour (a). Same boot is fine, or separately
- laneFULLRUN\fullrun_z1_z7.ps1 -Segment 1 against base HDRP_v109lx, or short: camscene steps ready, fight z1 Pitborn (boss z1_pitborn, killAfter 1.0, as F2 seg1 z1_p2/z1_kill), pickup (timeoutS 15), hold 3. The pillar appears pickup.zoneDropDelayS after the kill (F2 frames 81-90 = a 4.5 s window).
- Frame: first trace frame after `[LootUI] marker 'pitborn_maul'`: label "Pitborn's maul" with "Rare" in ORANGE, not pale. Pass: that frame + the marker line + `[CamScene] pickup pitborn_maul: ok`.
- Optional: an `equip pitborn_chest` step after the buy in gatelx run 2, grab a frame 2 s later, so (d) also shows the chest.

## Verdict
F4 can be called DONE after ONE combined run (M2 z1 kill + pickup, then restart to hub_camp for the M1 sells, about 2 minutes, one boot). Today (b) buy and (d) gear on Joan are solidly proven on lx frames; (a) is proven by five log markers and a pale-pillar frame on lc but not in rarity colour; (c) has only a log from an old build and no frame, and the lx erlking_axe row is untested.

## Self-audit
Pass 1: re-read every citation. Corrections: the sale log lives in item5_r1 (v109gp), not gatelw; the ledger "sell-" ids with negative coins are the buys; erlking_axe is not dropped (dropFrom []; Erlking drops cap_joan and bascinet_joan), so no axe pillar is expected; lx stat rows were not seen (menu stayed on HEAD).
Pass 2: checked the recipe against ZoneCamScene.cs Trade / Pickup / Inv (sell takes action, item, stall; Pickup walks to the LootPickup and calls Collect). Unverified: that hub_trade_a lists erlking_axe on the Sell tab (code lists any owned item with a market row, so expected OK; hub sell is proven by item5_r1).
