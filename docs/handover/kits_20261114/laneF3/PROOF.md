# F3 PROOF: the one artefact per boss that shows the reward (paths under _agents\)
1. Pitborn: laneFULLRUN\f2_seg1\key\z1_kill_boss_death.jpg shows the pillar "Pitborn's maul [E/X] take"; usable: laneHOTSHIP\gatesig\sig\game.log:575 `[ability] Joan weapon '' -> 'pitborn_maul'` + sigabil_sheet.png HUD "Ground Slam / Smash".
2. Sobek: laneFULLRUN\f2_seg1\key\z2_kill_boss_death.jpg shows "Sobek's trident"; usable: laneHOTSHIP\gatesig\sig\game.log:717 `[ability] Joan weapon 'pitborn_maul' -> 'sobek_trident'` + HUD "Lunge / Sweep".
3. Grendel: laneFULLRUN\f2_seg2\key\z4_kill_boss_death.jpg shows the "Heraldic tabard" pillar (lc build, chest slot; worn-over-cuirass frame on lx still MISSING).
4. Humbaba: laneFULLRUN\f2_seg3\key\z5_kill_boss_death.jpg shows "Pitborn pauldrons  ARMS  [E/X] take" (frame is a wave moment after his death; kill line f2_seg3\run\game.log:2294 `[BossSilver] Humbaba paid 38 silver`).
5. Erlking: laneFULLRUN\seg4_final\key\z6_dead_after_death.jpg shows "Steel kettle cap  HEAD  [E/X] take" (kill line seg4_final\run\game.log:2439 `[BossSilver] Erlking paid 48 silver`); the axe has no proof because it is not dropped yet (kit sigaxe, HOLD).
6. Phase change lines (events.txt): Pitborn f2_seg1:31, Sobek f2_seg1:77, Grendel f2_seg2:52, Humbaba f2_seg3:84 `[BossPhase] <Boss> enters its phase`; Erlking MISSING (seg4 count 0).
7. 2 s retry: Assets\StreamingAssets\zones.json respawn delayS 2.0 / foeResetDelayS 2.0; run proof `[Zones] revive` lines in f2_seg3\events.txt (3 revives, 0 exceptions).
8. Still to capture on lx (one QA run, real pickup via E, Tab equip): maul, trident, tabard-over-cuirass, pauldrons, cap/bascinet worn, then C with the trident after sigbash.
9. Kit proof: laneF3\PROOF_KITS.txt (apply -> parse -> revert -> md5 equal, twice, scratch copy).
10. No log line proves a drop (loot_ui debugLog false): the pillar frame is the proof.
