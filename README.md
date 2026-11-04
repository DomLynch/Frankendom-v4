# unity_test - Hades gate flat-plate test

Unity 6000.6.7f1, built-in render pipeline, no extra packages.

## Open
Unity Hub > Add > select this folder (Documents\unity_test) > open with 6000.6.7f1.

## Run
1. Menu Factory > Build Hades Scene. Creates and saves Assets/Scenes/Hades.unity (ground, backdrop, gate billboard, light, fog, camera).
2. Copy vampire.glb into Assets/Characters, add the glTFast package (see below), then Factory > Add Vampire.

## glTFast
Window > Package Manager > + > Add package by name: com.unity.cloud.gltfast
Without it, Add Vampire only logs an error.

## Limits
- Flat-plate environment test only: the gate and backdrop are textured quads, not 3D props.
- Textures are the 6k upscales (gate, backdrop) and the 1024 causeway tile.
- Untested: the project has never been opened in Unity.
- If materials look pink, the project was switched to URP: shaders used are Standard and Unlit/Texture (built-in).
