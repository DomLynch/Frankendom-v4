# Hades props handover (Hades env session, 2026-11-04)

Source of truth: `C:\Users\dom12\Documents\kits_out\3d\hades_env\processed\<name>\` (LOD0/1/2 GLB with embedded albedo + normal + ORM; PNG maps also there). Files only: nothing here edits a scene. **Scale is real metres: import with scale 1.** glTF is Y-up; origin is bottom-centre on the ground (z=0) for props; weapons have a grip pivot (below).
Extents below are LOD0 bounding boxes measured from the GLBs (x, y-up, z in metres). ORM texture: R = ambient occlusion, G = roughness, B = metallic (use as glTF metallicRoughness + occlusion; in URP pack into a Mask map: R metallic, G AO, A smoothness = 1 - roughness if needed; PNG `_metal_smooth.png` = R metal, A smoothness).

## Status legend
FINAL = approved by Strategy and copied into this folder (`Assets\Env\Props\<name>\`). PENDING = processed but a fix is in progress; use the `processed\` path after it is marked FINAL in `NEXT_STEPS.md` / by message.

| Prop | Status | Folder / files | Size (x, y, z m) | Placement / notes |
|---|---|---|---|---|
| fallen_pillar_3 | FINAL | `Props\fallen_pillar_3\fallen_pillar_3_LOD0/1/2.glb` | 2.9 x 3.0 x 2.2 | Fallen Corinthian column pile: dressing near the gate base / arena edge |
| standing_ruin_pillar_4 | FINAL | `Props\standing_ruin_pillar_4\...` | 3.8 x 5.0 x 3.9 | Tall broken block wall/ruin: near the platform and on the banks |
| broken_oar_6 | FINAL (weathered grey-silver, re-copied after the weathering pass 2026-11-04) | `Props\broken_oar_6\...` | 2.4 x 0.44 x 3.1 (lies flat) | Causeway / shore scatter, random yaw |
| dead_tree_1a | FINAL (Strategy approved; cool charcoal grade, trunk lifted 15% to match 1b, roots stay darker) | `Props\dead_tree_1a\dead_tree_1a_LOD0/1/2.glb` LOD0 700k / LOD1 350k / LOD2 140k tris | 9.2 x 7.0 x 9.2 (root flare sunk 0.3 m below z=0) | Frame both banks, 2-3 per bank at the water edge, LOD0 only within ~12 m, impostor/cull far, fps > 120 with all placed |
| dead_tree_1b | FINAL (Strategy approved; cool charcoal grade) | `Props\dead_tree_1b\dead_tree_1b_LOD0/1/2.glb` | 9.5 x 7.0 x 7.9 | variant of 1a; mix, random yaw, scale 0.8-1.3 |
| brazier_2a | FINAL (Strategy approved; stone plinth built in, rusted iron roughness >= 0.7; LOD0 99.5k / LOD1 20k / LOD2 8k tris) | `Props\brazier_2a\brazier_2a_LOD0/1/2.glb`: 4 primitives/materials: `brazier_iron`, `coal` (separate primitive with an orange emissive map, strength 1.0: drive the fire VFX/emission from it), `plinth_lower`, `plinth_upper` | 1.9 x 2.0 x 2.0 | Two, either side of the stairs; fire VFX on the coal bed |
| skull_pile_5a | FINAL (Strategy approved; '6' mark painted out, verified) | `Props\skull_pile_5a\skull_pile_5a_LOD0/1/2.glb` | 2.5 x 0.55 x 2.4 | Near the causeway edge |
| bone_fragments_7 | FINAL (Strategy approved; skirt cut, mound Z x1.3) | `Props\bone_fragments_7\bone_fragments_7_LOD0/1/2.glb` | 1.2 x 0.22 x 1.2 | Scatter near skulls / shore |
| rock_arch_8 | FINAL (Strategy approved; skirt cut, sparkles gone, roughness floor 0.38) | `Props\rock_arch_8\rock_arch_8_LOD0/1/2.glb` | 8.8 x 6.0 x 8.0 | Left far shore, mid-distance |
| arch_fragment_9a | FINAL (+3% red warm shift applied) | `Props\arch_fragment_9a\arch_fragment_9a_LOD0/1/2.glb` | 7.5 x 4.7 x 5.5 | Far bank dressing: broken arch pier + rubble |
| arch_fragment_9b | FINAL (variant of 9a, same +3% red shift; Strategy asked to keep both) | `Props\arch_fragment_9b\arch_fragment_9b_LOD0/1/2.glb` | ~7.5 wide x 5.5 tall | Variant of 9a, use both on the far bank |
| sword_start | FINAL v3 "chunky broadsword" (Dom's choice, 2026-11-04; see the section at the bottom) | `Assets\Characters\Weapons\sword_start\sword_start_LOD0/1/2.glb` | 0.31 x 1.15 x 0.10 (blade 8.5 cm wide) | Hand socket: grip pivot at the origin (pommel 0.154 m below), +Y to the tip (tip node y = 0.996), nodes `tip` and `grip` |
| maul_start | FINAL (Strategy approved 2026-11-04; head albedo -10%) | `Assets\Characters\Weapons\maul_start\maul_start_LOD0/1/2.glb` | 0.52 x 1.70 x 0.48 | Pivot at the lower wrap (butt end 0.30 m below the origin), +Y toward the head (head top at y = +1.40). Nodes `tip` (head) and `grip` |

## Weapons update (2026-11-04, Strategy ruling)
sword_start and maul_start re-exported: LOD0/1/2 = 40k / 12k / 4k tris, 2K embedded textures (albedo/normal/ORM), maul colour graded (metal x0.8, wood x0.85, judged under a neutral grey rig), sword guard slightly browner. Pivot, size and the `grip`/`tip` nodes unchanged and re-verified in every LOD (sword y -0.154..0.946, tip y 0.944; maul y -0.30..1.40, tip y 1.222). Files in `Assets\Characters\Weapons\<name>\`. Previous 80k/40k/16k, 4K versions kept in `hades_env\processed\<name>\_v1_80k_4k_backup\`.

## Other kits already delivered (earlier messages): `hades_env\blocks_v2\{causeway_v4, balustrade_v2, wall_kit, arena}`, `processed\{rocks, shoreline, debris}`, layouts in `hades_env\unity_export\` (arena_layout.json, setdressing_layout.json, water_fog_reference.json). Causeway step_flight: use the fixed files (back panel removed), copy fresh, the old copy was stale.
Placeholders only (Blender, being replaced by Dom's Meshy/Tripo versions above): broken_oar_a, bone_fragments, rock_arch, arch_fragment (do not import).

## Sword v3 - chunky broadsword (2026-11-04, Dom's choice, Strategy brief)
sword_start was rebuilt from the 0.77 m sword: overall 1.15 m, blade 8.5 cm wide at the base tapering to 6.5 cm before a short broad point (~15% of the blade), thickness x1.4 with the central fuller kept, crossguard arms x1.2, guard centre block widened to seat the blade. Pivot unchanged: grip centre at the origin, +Y to the tip, tip node at y = 0.996 (re-check the hand socket, trail/hit offsets and the weapon-length based reach). LOD0/1/2 = 40k / 12k / 4k tris, 2K embedded textures. Blade material: metallic 1.0, roughness 0.28, brighter edge bevel in the albedo (needs a reflection probe/skybox to shine). Previous 0.77 m files are in `hades_env\processed\sword_start\_v6_installed_0p77m\`. Comparison variants (A 1.00 m, B 1.15 m) in `processed\sword_start\variants\`.
