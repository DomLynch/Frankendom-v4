# F4 "gear loop you can SEE": audit (F4 agent, file-only, nothing applied, 2026-11-14)
Base = live HDRP_v109lu data. Twins checked byte-identical to the build: gear.json ca98899989, market.json 4b8d2aa597, hub.json e18c6356d4, zones.json 185898dbcc, loot_ui.json f28ab4016c, abilities.json 47755302f5, consumables.json c7daad2b6a. All paths below are under Assets\StreamingAssets (= Assets\Data). The launchers pass -zones, which turns hub.json / market.json on even though both files say `enabled: false` (ZoneFlag.Allowed in HubCfg.cs:70, MarketCfg.cs:77).
Zone order in zones.json: z1 pit_yard (Pitborn), z2 sobek_reach (Sobek), **z3 hub_camp = market 1**, z4 grendel, z5 z5_humbaba, z6 z6_erlking, **z7 z7_market2 = market 2**.

## Step by step: does the data path exist and work?
| step | verdict | evidence |
|---|---|---|
| 1 DROP | WORKS for 5 bosses, one item each | gear.json items[].dropFrom/dropChance: pitborn_maul [Pitborn] 1.0, sobek_trident [Sobek], joan_tabard [Grendel], pitborn_pauldron_joan [Humbaba], cap_joan [Erlking], all 1.0. `drop.maxPerKillBy` (gear.json:315) 1 each, `skipIfOwned` true (:310). Roll = GearDrop.cs `Roll`/`OnKilled` -> LootGearAdapter -> LootBridge pillar. Zones z1/z2/z4/z5/z6 spawn those bosses (zones.json spawns z1_pitborn.. z6_erlking). **Gaps:** erlking_axe has `dropFrom: []` (gear.json:1654); pitborn_chest (:913) has no dropFrom although the `_maxPerKillBy` note says "Pitborn drops 3"; bandits drop nothing (only silver, boss_silver.json). |
| 2 PICK UP / EQUIP | WORKS, manual by design | `drop.autoCollect` false (gear.json:309): E on the pillar adds to LootSave `owned` (LootUI.cs:191 log "[LootUI] picked up"), then Tab/Start equip screen -> GearSlots.Equip. Buying also only adds to `owned` ("Open gear (Tab / Start) to wear it", hub.json stall.buy.bought). Items are one copy per id (LootSave.Add / ApplyBuy refuse a second). **Gap (content):** gear.json items carry no `rarity`, so every pillar/label/equip row is the same ink colour (LootCfg.RarityColor falls back to ink). **Gap (content):** equip screen stats come only from stats{armor,poise,weight}; weapon damage/speed/reach live in weapon{} and are not shown (LootContracts.cs:150-156 reads `statList`, which no item has), so a weapon step is invisible in the UI. |
| 3 BUY, hub market (z3) | WORKS (data), stock is thin | market.json stalls hub_trade_a Armourer / _c Stablemaster / _d Apothecary / _e Bric-a-brac (:480-566) + main stall hub_trade (:249) whose buy list is hub.json stall.buy.items = knight_sword, war_axe, iron_maul, boar_spear (hub.json:399). rules.buyEnabled true (market.json:24), startCoins 200 (:12). Buy = LocalMarket.Buy -> LootSave.CommitBuy, one write. **Dead rows:** the Armourer sells cuirass_joan, arms_joan, legarmour_joan, joan_shield, which Joan already wears (roster.json loadout) -> "Owned", greyed. Useful rows: joan_helmet (head 5/3, the head slot is EMPTY at start) and bascinet_joan; bracers/greaves are downgrades (3<4, 4<5). |
| 4 SELL | WORKS | rules.sellRatio 0.33, confirmSell, rows[] per item (market.json). Sell price = round(buyValue x 0.33). Every item that can drop has a row EXCEPT erlking_axe (rows[] ends at sobek_trident, :221). **Gap (content):** the moment Erlking drops the axe (sigdrops kit) the trader answers "not-sellable". |
| 5 BUY / SELL, z7 market2 | WORKS (data), but z7 sells exactly what the hub sells | market.json z7_trade_a..e (:593-703): Armourer = same 8 ids as the hub, Weaponsmith = the same 4 weapons, Stablemaster mounts, Apothecary potions, Bric-a-brac tabard/boots/cap. Anchors resolve through zones.json z7_market2 spots stall_1/vendor_2/vendor_3 (x,z fallbacks agree to ~1.6 m; z7_trade_d/e use fixed coords, the spot vendor_5 is unused by them). zone7_vendors.json is `enabled:false`, no reader: its "best tier" brief ("content: Dom") is not implemented. Coins are global (LootSave), so a z7 purchase is there in every zone. **Gap:** no better tier at z7 than at the hub; pitborn_chest (10/8, row buyValue 120, glb present) is in no drop table and no stall = unobtainable. |
| 6 GEAR VISIBLE ON JOAN | WORKS | slots head/chest/arms/legs `visual:true` (gear.json:12-27), glb/glbMirror per item, all 25 referenced glbs exist under StreamingAssets\Gear (checked, sizes 0.8-34 MB; knight_sword/hunting_bow have no glb = default sword, intended). Weapons via WeaponSwap (slot mainHand), shield via ShieldMount. Proof lines exist only with `gear.json debugLog:true` ("[Gear] <name> equips <id> in <slot>", "<id> visual ok"); without it use frames. Unused art: pitborn_gorget_joan / pitborn_greave_joan / pitborn_vambrace_joan .glb have no item rows. |

## Zone 1 -> 7 power step (what the data lets Joan reach, today)
Start: head empty (0) + cuirass 6 + arms 4 + legs 5 = **armor 15**, poise 9 (4+2+3), default sword (damage 1.0). Silver: start 200, boss silver 16/22/30/38/48 (boss_silver.json), bandits 1-2 each.
| zone | what you can get | best armor total | best weapon (damage x, speed x, reach x) | step? |
|---|---|---|---|---|
| z1 Pitborn | drop pitborn_maul | 15 | maul 1.5, 0.75, 0.9 (eff. 1.125) | start |
| z2 Sobek | drop sobek_trident | 15 | trident 1.0, 1.08, 1.77 | sidegrade (reach) |
| z3 HUB market (~238 silver) | buy joan_helmet 54, knight_sword 30 | **20** (+5 head) | knight_sword 1.15, 1.0, 1.0 | the one real armour step before z5 |
| z4 Grendel | drop joan_tabard (chest 3/0) | 20 | same | WORSE than the cuirass 6, no step |
| z5 Humbaba | drop pitborn_pauldron_joan (arms 8/6) | **24** (+4 arms), poise 16 | same | step (best in the game) |
| z6 Erlking | drop cap_joan (head 3/1) | 24 | erlking_axe NOT dropped (dropFrom []); with sigdrops 1.0, 0.9, 0.78 | WORSE than the helmet bought at z3: no step |
| z7 market2 | same stock as the hub | 24 | same | no new tier; the curve flattens at z5 and the z1 maul is the weapon peak |
Reading: armor 15 -> 20 -> 24 then flat; weapon damage never exceeds the z1 drop. A clear z1->z7 step needs content that exists but is not handed out (pitborn_chest, erlking_axe) plus Dom's numbers.

## What is missing or broken (and which kit covers it)
| # | gap | class | kit |
|---|---|---|---|
| 1 | erlking_axe has no market row (unsellable once dropped) | CONTENT | `mkt` |
| 2 | z7 sells nothing the hub does not; pitborn_chest is unobtainable | CONTENT (loot design, Dom may veto the stock choice) | `mkt_z7` (alternative to `mkt`, includes its row) |
| 3 | no rarity on any item: all pillars/labels one colour | CONTENT | `gearvis` / `gearvis_sd` |
| 4 | weapon damage/speed/reach/posture invisible in the equip screen | CONTENT | `gearvis` + `uistats` |
| 5 | z4 tabard (3) and z6 cap (3) below what Joan already has; trident/axe equal the base weapon | TUNING | `t_curve` (HOLD) |
| 6 | Erlking drops no axe (Dom's call, kits dev's `sigdrops`) | CONTENT, Dom's decision | not mine; `gearvis_sd` is written to sit on top of it |
| 7 | pitborn_gorget/greave/vambrace glbs have no item rows (a z7 legs piece would need armor numbers) | TUNING | none staged (needs Dom's numbers) |
| 8 | hub Armourer lists 4 pieces Joan wears from the start (greyed "Owned") | design | none |
| 9 | no auto-equip and no "better than worn" nudge after a drop (E then Tab) | CODE | none, patch idea only |
| 10 | proof logs need `gear.json debugLog` / `market.json debugLog` true; loot_save.json (owned, coins, sales ledger) is the no-flag proof | info | n/a |

## Kits (python -I _agents\laneF4\f4kit.py <kit> make|apply|revert|proof [--target DIR] [--go]; dry run unless --go)
All `make` assert json(after) == mutate(json(base)); `proof` runs apply+revert twice on a scratch copy (see <kit>\PROOF.txt, all OK, byte-identical). Live twins were NOT touched.
| kit | class | file | md5 base -> after | does |
|---|---|---|---|---|
| mkt | CONTENT | market.json | 4b8d2aa597 -> 45550549dd | row erlking_axe buyValue 45 (= war_axe's; placeholder like every row) |
| mkt_z7 | CONTENT | market.json | 4b8d2aa597 -> 8970152b3e | mkt + z7 Armourer also stocks pitborn_chest (existing price 120). Do not stack with mkt |
| gearvis | CONTENT | gear.json | ca98899989 -> e9855d48b4 | rarity on 21 items, statList on 7 Joan weapons (numbers copied from weapon{}) |
| gearvis_sd | CONTENT | gear.json | 0e1d13ee68 -> 12f86e84dc | same, for a tree that already has the kits dev's sigdrops. Pick gearvis OR gearvis_sd |
| uistats | CONTENT | loot_ui.json | f28ab4016c -> 80f400a9bb | stat rows damage/speed/reach/posture (labels, decimals) |
| t_curve | **TUNING, HOLD** | gear.json | 12f86e84dc -> cbcd5c2a1e | tabard armor 3->8, cap 3/1->7/3, trident damage 1.0->1.2, axe damage 1.0->1.6 (statList copies move too). Chained after gearvis_sd |
Apply order for the safe set: (sigdrops if Dom agrees) -> gearvis or gearvis_sd -> uistats -> mkt or mkt_z7. Each apply writes both twins; the Unity dev deploys with deploy_data.ps1 (HOT, no Editor).
Not changed anywhere: abilities.json (sigabil covers the boss weapons), roster.json, any C#, anything under Builds.
