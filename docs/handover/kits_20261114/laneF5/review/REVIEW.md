# F5 bundle review (file-only), 2026-11-14

Method: md5 of all 8 tree files = BUNDLE.md base (check_bundle.py: 8/8 APPLIES-CLEAN). Then a REAL compile: Unity's own Roslyn csc (Unity 6000.6.7f1 DotNetSdk 8.0.318) over all 408 non-Editor .cs under Assets (excluding `~` dirs) against UnityEngine modules + Library\ScriptAssemblies package dlls.
- Baseline tree: 0 errors.
- Tree + the 8 .patched files as delivered: **3 errors** (all LootUI.cs:278, from LootCfg.cs.patched).
- Tree + 8 files with my two .reviewed files swapped in: **0 errors**.
Every cited line re-read twice. Data kits diffed base vs after and checked against the code.

## GO / NO-GO: NO-GO as delivered. GO once LootCfg.cs.reviewed replaces LootCfg.cs.patched (QualityTiers.cs.reviewed recommended).

## 1. LootCfg.cs  - FIX NEEDED (compile error, blocks the whole build)
LootCfg.cs.patched line 60: the new `// F5 LOOTPAD ...` comment sits in the middle of the line and swallows `public float equipHintBottom = 96f; public int equipHintSize = 15;`. Those fields vanish, so Loot\LootUI.cs:278 fails (CS1061 x3: equipHintBottom, equipHintSize x2).
Fix (LootCfg.cs.reviewed, same line 60):
```
public bool openInFight = false, showEquipHint = true, blockWhileMenu = false; public float equipHintBottom = 96f; public int equipHintSize = 15;   // F5 LOOTPAD: blockWhileMenu true = ...
```
Default blockWhileMenu=false = default-off. JSON key `screen.blockWhileMenu` matches (loot_ui.json after: true, inside "screen").
Note: the F5 agent's make_patches.py / lootpad.diff / LootCfg.cs.diff carry the same bug; fix the source there too if re-generated.

## 2. LootScreen.cs - OK
Line 40 `S.blockWhileMenu && MenuUI.Blocking` (MenuUI.Blocking is public static, MenuUI.cs:19). Compiles; off by default.

## 3. StartupWarm.cs (RELOADCOVER) - OK to build; one behaviour note
Compiles clean. ReArm/ReloadCover logic read: sceneLoaded subscribe happens inside StartCoroutine before LoadScene is called (no missed event), 20 s hard cap on the load wait, 4 s default cap on the hold, harness/Done/reloading guards present, OnGUI (line 115-118) draws while `cover` is true.
NOTE (not a compile issue): `CoverC.onReload = true` (line 16) is default-ON, so with the key absent a reload now raises a black cover (<= 20 s load + 4 s). BUNDLE says "default on" deliberately, and the f1bundle kit sets onReload true anyway. If you want strictly default-off code, change the default to `onReload = false` (the kit still turns it on). Not required.

## 4. GameLoop.cs (RELOADCOVER) - OK
One line, GameLoop.cs:164 `StartupWarm.ReArm();` at the top of static Reload(). ReArm is public static; both LoadScene paths (172, 176) follow. No-op in harness / before first cover done / onReload false.

## 5. LoadingCard.cs - OK, one behaviour note
Compiles. New field `waitForStartupWarm=false` (default-off), key matches loading_card.json after. Note: with the kit on, if StartupWarm never runs (-nostartupwarm, startup_warm.json enabled false, batch, harness-with-warmRenders off) StartupWarm.Done stays false and the card holds until maxSeconds (14 s in the kit). Acceptable; the cap bounds it.

## 6. QualityTiers.cs - OK to build; FIX RECOMMENDED (logic)
Compiles. TierScale() parses FlagsJson with JsonUtility (unknown keys ignored), so UserScale / UserUpscaler default to the tier's own 67 / fsr2 (tree ultra2k already has rs 67, upscaler fsr2).
Logic gap (line 99): `if (UserScaleOn && PlayerPrefs.HasKey(scaleKey))` - a player who changes ONLY the Upscaler row stores gfx.upscaler but no gfx.scale key, so the choice is ignored until they also touch Render scale. Fix in QualityTiers.cs.reviewed:
```
if (UserScaleOn && (PlayerPrefs.HasKey(head.userScale.scaleKey) || PlayerPrefs.HasKey(head.userScale.upscalerKey)))
```
(Safe: UserScale then returns the tier default 67 for the missing key.) Minor: UserScale/UserUpscaler re-parse the flags JSON on every getter call (menu row Get delegates), small GC only.
Default-off: userScale.enabled stays false in code; kit f5gfx turns it on.

## 7. MenuCfg.cs - OK
Compiles. BootC default enabled=false; `dpadAxisX/Y` default "" (off). Fix() null-guards boot and skipArgs. JSON keys match menu.json after (input.dpadAxisX/Y = DPadX/DPadY; both axes exist in ProjectSettings\InputManager.asset lines 473/489). Kit f5menu does not set boot (f5menuboot is the alternative, not in bundle).

## 8. MenuUI.cs - OK
Compiles. ArmBoot/BootTick only run when boot.enabled; PadAxis has its own broken flag; bootMode reset in Close/ForceClose; Allowed()/ClockReady()/OpenMenu() all exist. D-pad only adds when the axis names are non-empty.

## Data kits (checked against code)
- f5loot loot_ui.json: +screen.blockWhileMenu true. OK (needs LootCfg fix).
- f5card loading_card.json: maxSeconds 14, waitForStartupWarm true. OK.
- f5gfx quality_tiers.json userScale.enabled true + label "Ultra 2K (upscaled)"; game_settings hint text. OK. Behaviour: Render scale/Upscaler rows appear (GameSettings.cs:102-114).
- f5menu menu.json: input.dpadAxisX/Y, two gs rows (graphics.renderScale, graphics.upscaler), 3 controls rows with action/keys/pad (match KeyRowC). OK.
- f1bundle startup_warm.json: holdFramesAfter 12, gearCapSeconds 8, gearAttachFrames 100, onReload/reloadHoldFrames/reloadCapSeconds/reloadHoldSim: names match CoverC. OK. (waitForGear itself is not changed by this kit; it is whatever the base has.)
- All base md5s of the data twins on the tree equal the kit bases (menu 351724f3d7, loot_ui 06c3a54c00, loading_card 81ca419b34, quality_tiers 15ea65a016, game_settings 56d314772c, startup_warm 0f1d81d0a1).
- Not reviewed: f5plain, f5start (held per BUNDLE).

Files here: LootCfg.cs.reviewed (required), QualityTiers.cs.reviewed (recommended). Originals untouched.
