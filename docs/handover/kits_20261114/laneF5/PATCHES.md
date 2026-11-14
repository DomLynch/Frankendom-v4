# F5 PATCHES: menu, settings, controller, first-minute (file-only, 2026-11-14 ~15:35)
Nothing here is applied. The tree, Assets\Data / StreamingAssets twins and every build are untouched (twins re-checked: all equal to live lv, `twins.py`). Everything lives under `_agents\laneF5\`.
Rules kept: no hard-coded tunables (every new behaviour reads a menu.json / loading_card.json / loot_ui.json / quality_tiers.json key and every new key is OFF by default in code); difficulty and economy are not touched; tree twins stay byte-identical (kits write both, `--target` writes one scratch dir); ONE code build: all five CODE patches below are small, independent, and meant to ride in the same build (58 added and 14 replaced lines in 6 files, `patches\*.diff`).
**Not compiled** (no Unity here, no `csc`): hand-reviewed against the surrounding code; each diff applies cleanly to the tree and the five stacked equal the `.cs.patched` files byte for byte (proved with `patch --dry-run` and `cmp` on a scratch copy). The Unity dev compiles it in the next code build.
Note: `f5check.py` in this folder is not mine (dropped here by another agent at 15:16, read-only data checker); I did not use or edit it.

## Summary
| # | Patch | Kind | Fixes (what a player notices) | Files | Default | Needs |
|---|---|---|---|---|---|---|
| P1 | `f5card` + `cardwait` | DATA now (interim half) + CODE | 7 s of title-less black between the card giving up (4 s) and the cover lifting (11 s) | `loading_card.json`; `HUD\LoadingCard.cs` | code off until the key is on | DATA half works today; CODE half in the next build |
| P2 | `f5plain` | DATA (hot) | a plain exe launch is not the game: camp, chase, zone span dark without `-zones` | `hub.json`, `chase.json`, `zone_span.json` | n/a | **Lead yes** (changes plain-launch behaviour; PLAY*.bat unaffected) |
| P3 | `f5gfx` + `f5menu` + `userscale` | DATA + CODE | "Ultra Native" is not native (67% FSR 2); no Render scale / Upscaler choice for a 15 fps native vs 40 fps default machine | `quality_tiers.json`, `game_settings.json`, `menu.json`; `QualityTiers.cs` | rows hidden until both data kits | rows need CODE `userscale` to show the true default |
| P4 | `dpadnav` + `f5menu` | CODE + DATA | the D-pad does nothing in menus | `MenuCfg.cs`, `MenuUI.cs`; `menu.json` | off (empty axis names) | CODE |
| P5 | `lootpad` + `f5loot` | CODE + DATA | pad Start can open pause AND the equip screen on one press | `LootCfg.cs`, `LootScreen.cs`; `loot_ui.json` | off | CODE; open product question below |
| P6 | `bootmenu` + `f5menuboot` | CODE + DATA | no title / Continue screen: the player is dropped into the world | `MenuCfg.cs`, `MenuUI.cs`; `menu.json` | off (`boot.enabled false`) | CODE; Dom's call |
| P7 | `f5start` | DATA (hot) | the first fight begins on frame 1 at the Hades gate (Pitborn 3.6 m away, aggro 5.5) | `zones.json` | n/a | **moves the store-page frame**: frame check before applying |

DATA = hot (copy to both twins + deploy_data). CODE = next code build. Order that works with no code build waiting: `f5card` -> (frame check) ... the rest of the DATA kits are inert until their CODE is built (JsonUtility ignores unknown keys; the new menu rows only appear when `userScale.enabled` is true).

## Kits (JSON, `f5kit.py`, lgkit style)
`python -I f5kit.py <kit> make | show | apply [--target DIR] [--go] | revert [--target DIR] [--go]`. Dry run by default; `apply/revert` refuse a target inside a build named by `Builds\PLAY*.bat` (proved: lv refused); no `--target` = both tree twins. Base = tree = live lv. md5 (10 chars):

| kit | file(s): base -> after | edits |
|---|---|---|
| `f5card` | loading_card.json 81ca419b34 -> 1d7e6b6130 | `maxSeconds` 4.0 -> 14.0; new `waitForStartupWarm: true` (inert until `cardwait`) |
| `f5plain` | hub.json e18c6356d4 -> af5764211a; chase.json e14b414b9e -> 1b54a5e59c; zone_span.json 06af47e058 -> 98569614a0 | top-level `enabled` false -> true, three lines |
| `f5gfx` | quality_tiers.json 15ea65a016 -> 31eb54ad88; game_settings.json 56d314772c -> d5758efc6e | `userScale.enabled` false -> true; ultra2k label "Ultra Native" -> "Ultra 2K (upscaled)"; graphics.tier hint rewritten to say 67% + FSR 2 |
| `f5menu` | menu.json 5da8a0510d -> f9e3a880fd | Graphics tab gets `graphics.renderScale` and `graphics.upscaler` rows after the tier row; `input.dpadAxisX/Y` "DPadX"/"DPadY" |
| `f5menuboot` | menu.json 5da8a0510d -> 53d539a30d | everything in `f5menu` + `boot{enabled true, title FRANKENDOM, resumeLabel Continue, footer, afterCardS 0.3, maxWaitS 60}`. Same base as `f5menu`: pick ONE (the second refuses until the first is reverted: proved) |
| `f5loot` | loot_ui.json cd33e731da -> 1526dcd16a | `screen.blockWhileMenu: true` |
| `f5start` | zones.json 185898dbcc -> 390b6d8d5d | `pit_yard` spot `arrive_start` pos `[]` -> `[0.0, 8.6]` |
Proof (`PROOF.txt`, script `prove_kits.py`): on a scratch copy of the nine StreamingAssets files each kit was dry-run (nothing written), applied, reverted (dry then `--go`), and the scratch was **byte-identical to the tree twin after every revert** (7 kits x 9 files); a revert refuses a file that is neither the kit base nor its after; the live-build dir was refused; `f5menu` then `f5menuboot` refused. Every kit `make` asserts after == base + exactly the listed values (JSON equality) and that the twins are equal and still on the staged base md5.

## CODE patches (`patches\`)
Each has `<name>.diff` (vs the tree, `patch -p1`), and the six touched files have `<File>.cs.patched` (all five patches stacked) and `<File>.cs.diff`. `make_patches.py` regenerates them from the tree (anchors must match exactly once; it aborts if the tree moved).

### cardwait (`HUD\LoadingCard.cs`, 2 lines)
`Cfg.waitForStartupWarm` (default false). When true the card does not start its fade until `StartupWarm.Done`, so the title stays up until the cover lifts. `maxSeconds` still caps it (a disabled StartupWarm never sets Done: the cap ends it). With `f5card` the card holds to calm frames or 14 s. Risk: if the card then outlasts the world being ready (calm never reached) the player waits to 14 s; set `maxSeconds` lower. Test: plain launch log must show `[LoadingCard] ... fading out` after `[StartupWarm] done`.

### dpadnav (`Menu\MenuCfg.cs`, `Menu\MenuUI.cs`, about 15 lines)
`input.dpadAxisX/Y` (empty = off). The D-pad axes navigate the menu like the left stick, same threshold (`axisThreshold` 0.6) and repeat; own `dpadBroken` flag so a bad axis name cannot disable the stick. Up = +1 on XInput. Test with a real pad: up/down moves the pause list and settings rows, left/right changes a value; if a pad reports DPadY flipped there is no sign key to fix it in data: add a `dpadInvertY` row then (not in this patch).

### bootmenu (`Menu\MenuCfg.cs`, `Menu\MenuUI.cs`, about 40 lines)
`menu.json boot{}` (class `BootC`, `enabled` false by default). After the loading card is cleared and `StartupWarm.Done`, plus `afterCardS`, `MenuUI` opens the pause menu once (same code path as Esc: time stopped, cursor free, audio paused), with the title, the Resume row relabelled and the footer line from data. Closing it (Continue / Esc / Start) clears the mode and the game runs. Skipped by `skipArgs` (all proof and capture flags: `-camscene -camtrace -dirproof -zoneshot -routebot -lightdump -lightoff -resgate -record -flickrec`) and by `MenuCfg.Harness`; gives up after `maxWaitS`. The DirectionPrompt already waits while the menu is open (`pauseWhileMenuOpen`). This is the cheapest honest title screen (no art, no scene): the world is dimmed behind FRANKENDOM / Continue / Settings / Controls / New game / Quit. Risks: it opens in a world that has been live behind the card (the sim hold only covers the cover): if the player's save stands in an aggro radius a fight may already be running (`[Zones] engage` fired before `[LoadingCard] gone` in Player.log); "Restart fight" stays in the list. Whether a title is wanted at all is Dom's call (alpha plan could treat the card as the title); a real title screen (Play / Continue art) is larger work and not attempted.

### userscale (`QualityTiers.cs`, 4 lines)
`UserScale` / `UserUpscaler` default to the ACTIVE tier's own `renderScale` / `upscaler` (read from `FlagsJson`) when the player has no saved choice, instead of 100 / `defaultUpscaler`. Without it, switching on the rows shows "Native 100%" while ultra2k renders 67% FSR 2, and the first click in the list lands the player on 15 fps. Tiers whose scale is not in `userScale.scales` (low 50, medium 59) show the raw value. Saved choices and `-qset:` / `-renderscale:` behave as before.

### lootpad (`Loot\LootCfg.cs`, `Loot\LootScreen.cs`, 2 lines)
`loot_ui.json screen.blockWhileMenu` (default false): `LootScreen.CanOpen` returns false while `MenuUI.Blocking`. Fixes the pad Start double open (UNPROVEN, needs a pad: both `menu.json openKeys` and `loot_ui.json toggle` carry `JoystickButton7`; MenuUI runs first at order -450). **Open product question**: with it on, pad Start only pauses, and a pad player has no button for the equip screen (every face/shoulder button is in use, `controls.json`). Options for Dom/Lead: (a) a pause row "Equipment" (small CODE, not written because the pause/loot freeze hand-over needs a test), (b) loot toggle on a LB + button chord, (c) accept keyboard-only equip until after alpha. Until decided, leave `f5loot` unapplied; the double open is the thing to confirm first on a real pad.

## Not written (and why)
* A real title screen with art / Play-Continue-New game: needs a design and Dom's yes; `bootmenu` is the 80% version.
* Pad rebinding (buttons are fixed by design, `InputMap.padFixed`); pad stick sensitivity / invert Y / pad dead zone rows (`MenuSettings.GetF/SetF` is a hard switch on `camSens` and `deadZone`: needs a code change to take more keys; the data lives in `pad_extras.json look` and `controls.json pad.deadZone`).
* Pad hint panel (F1 is keyboard only and no pad button is free).
* Hub NPC glbs into `perf_fixes.json C3.eagerFiles` (boot dev's lane; `laneBOOT\README.md`), `f1warm` (gearCapSeconds 25 -> 3 would reveal Joan without her gear: decide with the card).
* No fps number: needs one quiet-slot measurement of the shipped default (STABILITY.md section 5).

## How to apply, in order (suggestion for the Lead)
1. Now, hot, zero risk: `f5card` (interim half). Look at the first 20 s of a plain launch.
2. Lead yes: `f5plain` (plain exe = the game). Frame check + Dom's yes: `f5start`.
3. With the next code build (one hour budget): apply all five `.diff`s (`cardwait`, `dpadnav`, `bootmenu`, `userscale`, `lootpad`), then data: `f5gfx` + `f5menu` (or `f5menuboot` if Dom wants the title), `f5loot` only after the product question. Check on the laptop with a pad: pause on Start, D-pad nav, Start in the hub (one or two windows), Render scale row shows 67% at first open, a settings round trip across a relaunch (settings.json must appear), boot title opens once and `-camscene` runs stay untouched.
