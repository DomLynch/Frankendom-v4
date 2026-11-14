# F3 signature reward + F4 power step: audit (bandits dev, file-only, nothing applied). Base = live ls twins (gear.json ca98899989, abilities.json 47755302f5).
## What each boss drops TODAY (gear.json items[].dropFrom, drop.maxPerKillBy 1 each) vs what Joan can use at once
| zone / boss | drops (stats) | visible on Joan | signature ability | missing |
|---|---|---|---|---|
| z1 Pitborn | pitborn_maul (two-hand, dmg x1.5, speed x0.75, reach x0.9, posture x1.8, poise 3, weight 7) | glb yes (13 MB) | NONE: maul_slam / maul_smash list only iron_maul | ability for pitborn_maul; pitborn_chest has no dropFrom (maxPerKillBy comment says Pitborn drops 3) |
| z2 Sobek | sobek_trident (dmg x1.0, speed x1.08, reach x1.768) | glb yes | NONE: spear_lunge / spear_sweep list only boar_spear | ability for sobek_trident |
| z4 Grendel | joan_tabard (chest, armor 3, poise 0) | glb yes | none | a chest tabard (3) is BELOW the starting cuirass (6): no power step; Grendel blades stay undroppable (Dom: claws only) |
| z5 Humbaba | pitborn_pauldron_joan (arms, armor 8, poise 6; named Pitborn's, dropped by Humbaba) | glb pair yes | none | no weapon, no ability |
| z6 Erlking | cap_joan (head, armor 3, poise 1) | glb yes | none | erlking_axe (Dom's antler great-axe, glb 26 MB, twoHanded, weight 9) has dropFrom [] and no abilities |
| z7 market | no boss; stalls sell knight_sword, war_axe, iron_maul, boar_spear, mounts | glbs are stand-ins | war_axe / iron_maul / boar_spear have abilities | none |
Power step now: start armor 6+4+5 = 15 (cuirass, arms, legs); z1 weapon dmg x1.5 but slower; z2 reach; z4 sidegrade; z5 +4 armor / +4 poise on arms; z6 +3 head: the curve flattens and drops at z6.
## Kits (python -I _agents\laneLG\lgkit.py <kit> apply --go; no --go = dry run; revert --go; QA copy: --target <copy StreamingAssets>)
- `sigabil` (abilities.json 47755302f5 -> 349b473f51), CONTENT (Dom's loot design, not numbers): the boss weapons get the family abilities at once: pitborn_maul -> maul_slam + maul_smash, sobek_trident -> spear_lunge + spear_sweep, erlking_axe -> axe_cleave + axe_chop (Joan only, existing cooldown/stamina numbers untouched). Unproven: assumes AbilityTable matches weapons[] against the equipped item id (abilities.json _about says weapon ids = WeaponStats.id).
- `sigdrops` (gear.json ca98899989 -> 0e1d13ee68), CONTENT: Erlking also drops erlking_axe (dropChance 1.0, maxPerKillBy Erlking 1 -> 2). It is Dom's designed axe and was monster-only (no weapons on monsters rule): his call to hand it to Joan.
- `sigfull` (gear.json -> 23afcf0492) = sigdrops + TUNING: cap_joan armor 3 -> 7, poise 1 -> 3 so z6 is not the weakest step. TUNING = Dom's numbers after he plays: apply sigdrops, not sigfull, until then. gear.json kits do not stack: one of sigdrops / sigfull only.
## Data vs code
Data: everything above. Visible-on-Joan is already driven by gear.json glb + zones (all drop glbs exist). CODE (not staged): no per-boss kill reward toast / "named weapon" banner beyond loot_ui.json text; weapon family ability matching if it turns out to read the weapon family, not the item id; a chest-slot tabard replacing the cuirass if both equip the same slot.
