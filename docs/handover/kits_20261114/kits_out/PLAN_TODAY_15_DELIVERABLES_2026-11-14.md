# Today's plan v2: 5 strategic deliverables per game (Dom, 14 Nov 2026)

Dom ~12:45: "we're circling and going in some micro details ... bigger picture, strategic stuff ... next 5 things across the 3 devs, so 15 in total ... strategic deliverables today".
Dom ~13:00 to z-Strategy: "get this plan on track, modify it but strategic big ticket stuff, and player engagement, max graphics and gameplay etc, 10-15 min data layer releases... inform coo and the relevant devs".

v2 by z-Strategy on the COO's v1 (COO's amendments kept: F1 targets, A1 ships with the cloud-surround build, disk gate). Same 15 slots and the same order. What changed: each deliverable names the player payoff (why someone keeps playing, or what makes them say wow), a few are widened to the big-ticket version, and each game has ONE graphics deliverable whose proof is a store-page frame.

## Rules for all three games
- **Order:** work the five in order. DONE = Dom can see it in the live game AND there is proof (frame, clip or log line).
- **Cadence:** a release every 10-15 minutes, data layer first, and each one moves a numbered deliverable forward. Every capture window ends in a flip (a sure thing plus the risky item).
- **Off-list:** no polish or look tweaks outside the list. A look item is frozen once its deliverable closes.
- **Big-ticket test:** before starting any task ask "would a player notice this in the first ten minutes, or would it make them play again?" If no, it is not today's work.
- **Parallel track:** file-only agents and the Tripo / OpenTrident lanes prepare the NEXT deliverable's data while the Unity dev ships the current one. The Unity dev never waits on a kit.
- **Status:** each Lead posts "D# DONE <proof path>" as each closes. The COO audits against this file every sweep; z-Strategy audits on Dom's call.
- **Disk gate:** no new Editor build while C: has under 80 GB free; keep it over 100 GB by pruning to live + PREV + one staging build per game. Editor builds need 20 GB RAM free at start.

## Frankendom: first alpha (the graphics benchmark; now make it a game people finish)
| # | Deliverable | Player payoff | Done when |
|---|---|---|---|
| F1 | **Zone arrival works** | No dead time, no cheap deaths | Zone-to-zone arrival is a lit, playable frame in under 5 s; first launch shows a loading card (never a black screen) and is playable by about 15 s; raiders respect the arrival grace. Proof: 7 arrival frames + log times. |
| F2 | **Recorded clean full run** | The whole game holds together | Fresh save, zones 1-7, all 5 bosses killed, gates open, ends on the tease gate, no soft-locks. Proof: the recording + run log. |
| F3 | **Fair bosses worth beating** | The fights are the game | Each of the 5 bosses has readable tells and one phase change; death means a retry in seconds; AND each kill gives a signature reward (a named weapon or ability usable at once). Saves tested on a copy of Dom's real save. |
| F4 | **Gear loop you can see** | A reason to keep going | Drops, buy and sell in both markets, gear visibly changes Joan, and one clear power step between zone 1 and zone 7. Proof: a frame per step. |
| F5 | **First five minutes + alpha look freeze** | First impression | Menu, settings and controller work; the opening 60 seconds at the Hades gate is the store-page frame; 30 fps native, no start stall, no crash in the full run. Then the look is frozen for alpha. |

**Parked for alpha:** riding polish, finishers, bow, new zones.
**Parked by Dom (14:31):** a ~25 s showcase video (about 5 s per boss, all 5 bosses), "once we have the sound ready". Keep any raw F2 recording; no video work until there is sound.

## Armagedom: The Ascent, a run people talk about (devour-and-morph is the hook, the look is the identity)
| # | Deliverable | Player payoff | Done when |
|---|---|---|---|
| A1 | **Devour and morph** | The hook: you become what you eat | Growth stages S / M / L clearly visible in play (Ascent10); devouring the horned mid-boss gives the hero horns and one new power; trait table in data with three morphs ready (horns, wings, tail blade). The finished cloud-surround build goes live with it. Proof: before/after frames at the gameplay camera and close up. |
| A2 | **Ten bosses on a bigger map** | Variety and pacing | 5 big bosses (4 angels + Pitborn at the breach) with arenas and 5 named mid-bosses roaming with the packs, all met in one run ("only the first boss fires" fixed); the +50% layout (three added islands) gives them room. Angels are the large majority; everything attacks the hero. Proof: run log with ten boss lines + a frame each. |
| A3 | **Combat feel from Frankendom** | Hits feel heavy, bosses fight back | Poise and stagger (no stun-lock) plus hit feel; one system per release, data-driven. Proof: a clip per system. |
| A4 | **Run loop with a reason to replay** | "One more run" | Start, islands, bosses, end screen with score; 15-20 minutes; no soft-locks; the morphs chosen shape the next run (different traits offered, or one carried over). Proof: two runs that play differently. |
| A5 | **The store-page look, then frozen** | The first screenshot sells it | Sun-shaded cloud sea, light shafts (via the Frankendom settings diff or shaft cards), the white-gold island standing out from the cloud; ONE hero shot per island judged against Dom's mood board. Runs as the PARALLEL TRACK all day (designer assets, file-only agent, hot data), at most one Unity window in three, and closes by end of day. Then the look is frozen. Desktop move checklist ready. |

## FrontierDom: the core loop (lighting FROZEN at fd-55 per Dom; the enemies are the identity)
| # | Deliverable | Player payoff | Done when |
|---|---|---|---|
| D1 | **Enemies fight back, three kinds** | Danger, and reading the fight | A rusher, a shooter and a heavy, each with a readable tell and hit feedback on the player; you can die. Proof: a clip of taking and dealing damage from each. |
| D2 | **The loop with choices** | A reason for the next wave | Wave, cash, then a pick of upgrades between waves that changes how you play (not only bigger numbers), then the next wave; death restarts cleanly. Proof: two different upgrade paths in frames. |
| D3 | **Horde set piece** | The moment people clip | 4+ escalating waves ending in a champion with an intro beat (alarm, the street fills, the champion arrives). Wave 4 is already live; this adds the staging. Proof: one 20-second clip. |
| D4 | **Three guns done properly** | The five-second feel | Pistol, rifle and shotgun: hands on the gun, recoil, hit feedback, reload, clear damage roles. The other guns stay as they are. Proof: a hip, fire and reload frame per gun. |
| D5 | **HUD + first five minutes** | A new player gets it | Menu, settings, a clear objective; a new player understands the loop in under 1 minute. No look changes. |

**Parked:** sky and look work, the v7 animation clips until their frames pass, new guns.

## How the day is judged
At each audit: which numbered deliverable did each game's last release move, and is the parallel track ready with the next one. A release that moves no number is a miss, however good it looks.

---
# PATH TO COMPLETION (z-Strategy for Dom, 13:49). How the remaining deliverables get done in parallel.

Dom: "fast track these ... make the path to completion, fast-track efficient effective so the devs have a clear plan and work in parallel, they can use agents if needed.. sonnet 5.5 medium".

## How every game runs from now
- **Three lanes per game, all moving at once.** LANE 1 the Unity dev: applies kits, builds, captures, flips. Nothing else. LANE 2 file-only agents: each owns ONE deliverable and hands the Unity dev a finished kit. LANE 3 the asset lanes (kits dev, OpenTrident, Tripo): bodies, clips, parts for the deliverable after next.
- **Agents:** Sonnet 5.5, medium effort, file-only (no Editor, no player, no worktree). At most three per Lead at a time. One agent, one deliverable, one set of files: the Lead assigns files so no two agents edit the same one.
- **What an agent hands over:** a kit = the data files (both twins where the game has twins), APPLY and REVERT that dry-run by default, the md5 before and after, and a five-line README naming the ONE frame, clip or log line that proves it. Code goes in as patch files; the Lead batches all waiting patches into ONE build per hour.
- **The Unity dev never waits.** If its queue is empty the Lead has failed to stage; the Lead keeps two kits ahead at all times.
- **Each window:** a sure thing plus one risky item, ends in a flip, LIVE line names the deliverable number.
- **A deliverable closes** with "X# DONE <proof path>" on the board. Then its files are frozen.

## Frankendom (F1 DONE 13:50). Target: alpha-ready by about 18:00
The five-segment discovery run already served as F2's dry run, so the final RECORDED run moves after F3 and F4 land, otherwise it has to be recorded twice.
| When | Lane 1 Unity dev | Lane 2 agents (Sonnet medium) | Lane 3 kits dev |
|---|---|---|---|
| now to 14:45 | F3: ship boss rewards and any missing tell or phase line, one boss per window | Agent F4: gear loop audit and kit (drops, buy and sell in both markets, gear shown on Joan, power step zone 1 to 7) | F3 signature-reward kits per boss (audit already done: fix the two rewards that are worse than starting gear) |
| 14:45 to 15:45 | F4: ship the gear loop, one step per window, a frame per step | Agent F5: menu, settings, controller audit and patch files; list of crashes or stalls from the run logs | F4 market and drop rows |
| 15:45 to 16:30 | F2: ONE continuous recorded run, fresh save, zones 1 to 7. Book it on the board as a quiet slot; the other two games hold players for it | Agent F2: reads the run log live and lists any soft-lock or snag with its zone and time | staged fixes for whatever the recording finds |
| 16:30 to 18:00 | F5: one code build with the menu/settings/controller patches, then the opening 60 seconds captured as the store-page frame; look frozen | none | none |

## Armagedom. Target: a complete replayable run by about 19:00
| When | Lane 1 unity-dev | Lane 2 agents (Sonnet medium) | Lane 3 assets |
|---|---|---|---|
| now to 14:15 | Close A1: mid-boss build live, then the morph (grant the wings directly first, then wire to devour) | Agent A2-roster: ten-boss table (5 big with arenas, 5 named mid-bosses roaming), kill-count step-in rows, which bodies need rigs. Agent A2-map: the +50% layout (three islands) through the generator, checks and probe passing | OpenTrident: rig the mid-boss bodies the roster names; sockets for horns, wings, tail blade |
| 14:15 to 15:30 | A2: ONE bake with the roster and the bigger map; proof run with ten boss lines | Agent A3: Frankendom-to-Armagedom combat port, poise and stagger first, as patch files with data tables. Agent A5: cloud-sea layers, the Frankendom fog-settings diff for light shafts, as hot data | Tripo: any mid-boss body still missing |
| 15:30 to 17:00 | A3: one code build per system (poise/stagger, then hit feel), a clip each. A5 hot data in every third window | Agent A4: run loop data: end screen and score, extraction on the last boss, morphs shaping the next run | OpenTrident: clips the combat port needs |
| 17:00 to 19:00 | A4: run loop build, two runs that play differently. A5: one hero shot per island, then look frozen | none | none |

## FrontierDom (D3 DONE). Target: the full loop by about 18:00. Lighting frozen at fd-55
First fix the capture that photographs the pause menu (no pause on focus loss in capture runs). Build everything from a clean fd-55 base.
| When | Lane 1 Lead (build, capture, promote) | Lane 2 agents (Sonnet medium) | Lane 3 OpenTrident |
|---|---|---|---|
| now to 14:45 | D1: one enemy kind per window live (rusher, then shooter, then heavy), each with its tell and a hit on the player | Agent D2: the between-wave upgrade pick. The wave perk card already exists in this code base from its origin; reuse it with cash cost and choices that change play. Agent D4: gun rows for pistol, rifle, shotgun (recoil, hit feedback, reload, damage roles) | Hands for pistol, rifle, shotgun from the grip rows; a still per gun |
| 14:45 to 15:45 | D2: ship the upgrade pick; two upgrade paths in frames | Agent D5: menu and HUD text audit (remove Westminster wording), objective clarity, first-minute prompts, as data | D3 staging clips if needed (champion entrance) |
| 15:45 to 17:00 | D4: one gun per window, hip, fire and reload frame each. D3 staging beat (alarm, street fills, champion arrives) as data | none | none |
| 17:00 to 18:00 | D5: HUD and first five minutes; one new-player run recorded | none | none |

## What the COO checks each sweep
Per game: which lane is idle (that is the bottleneck, not the clock), how many kits are staged ahead of the Unity dev (should be two), which deliverable the last release moved, and whether the time targets above are slipping by more than 30 minutes. A slip gets one line to z-Strategy with the cause.