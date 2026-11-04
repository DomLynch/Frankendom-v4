using UnityEngine; using System.Text;
public static class HdrpBoulderCheck2 { public static void Run() { var sb = new StringBuilder();
 foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)) { string n = r.name.ToLower(); if (n.Contains("water") || n.Contains("lake") || n.Contains("ground") || n.Contains("terrain") || n.Contains("river") || n.Contains("sea")) sb.AppendLine(r.name + " y=" + r.bounds.center.y.ToString("F2") + " top=" + r.bounds.max.y.ToString("F2") + " size=" + r.bounds.size); }
 System.IO.File.WriteAllText("Shots/_water.txt", sb.ToString()); } }
