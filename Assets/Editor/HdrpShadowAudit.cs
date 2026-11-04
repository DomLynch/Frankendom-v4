using UnityEngine; using UnityEngine.Rendering.HighDefinition; using System.Text;
public static class HdrpShadowAudit { public static void Run() { var sb = new StringBuilder();
 foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)) { var hd = l.GetComponent<HDAdditionalLightData>(); sb.AppendLine(l.name + " type=" + l.type + " enabled=" + l.enabled + " shadows=" + l.shadows + " res=" + (hd != null ? hd.shadowResolution.@override.ToString() : "-") + " update=" + (hd != null ? hd.shadowUpdateMode.ToString() : "-") + " layers=" + (hd != null ? hd.lightlayersMask.ToString() : "-")); }
 System.IO.File.WriteAllText("Shots/_shadow_audit.txt", sb.ToString()); } }
