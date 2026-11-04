using UnityEngine;

// Scene-held references to the Unreal-baked ClashFx flipbook sheets so a player build includes them (HdrpFixes.BuildClashSheets creates one
// "ClashFxSheets" object from the paths in Assets/Data/clash_fx.json "sheets"). ClashFx finds it at boot; with none it tries the editor
// AssetDatabase path and then Resources, and finally falls back to the procedural sparks / dust.
public class ClashFxSheetRefs : MonoBehaviour
{
    public Texture2D sparks, sparksHeavy, dust, dustMotion;
}
