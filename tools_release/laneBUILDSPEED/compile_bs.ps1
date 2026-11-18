# laneBUILDSPEED compile check: Roslyn of Unity 6000.6.7f1 over BuildSpeed.cs + a stub file that has the exact call shapes of the 6 hunks (void and int-returning lambdas, Action argument). Writes only under out\.
$Project = $env:FK_ROOT; $U = 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Data'; $Lane = "$Project\_agents\laneBUILDSPEED"; $Out = "$Lane\out"
$csc = "$U\DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll"; if (-not (Test-Path $csc)) { $csc = (Get-ChildItem "$U\DotNetSdk" -Recurse -Filter csc.dll | Select-Object -First 1).FullName }
$stub = "$Out\bs_stub.cs"
Set-Content $stub @'
using System;
public static class AttackClipSetup { public static void Run() { } }
public static class DodgeClipSetup { public static void Run() { } }
public static class EnemySwap { public static void BuildScenesMenu() { } }
public static class ZoneCreaturePrefab { public static int SaveAll() { return 0; } }
public static class Shapes {
  static void Step(bool on, string name, System.Action a) { if (!on) return; UnityEngine.Debug.Log("PRODUCTION step " + name); BuildTimer.Step(name, a); }
  public static void Run() {
    BuildTimer.Step("AttackClipSetup", () => AttackClipSetup.Run());
    BuildTimer.Step("DodgeClipSetup", () => DodgeClipSetup.Run());
    BuildTimer.Step("EnemySwap.BuildScenesMenu", () => EnemySwap.BuildScenesMenu());
    int miss = BuildTimer.Step("ZoneCreaturePrefab.SaveAll", () => ZoneCreaturePrefab.SaveAll());
    foreach (var row in new string[] { "a" }) { if (row == null) continue; if (Zone2BuildInc.Skip(row)) continue; }
    Step(true, "x", () => AttackClipSetup.Run());
  }
}
'@
$refs = @("-r:$U\NetStandard\ref\2.1.0\netstandard.dll"); foreach ($d in Get-ChildItem "$U\Managed\UnityEngine" -Filter *.dll) { $refs += "-r:$($d.FullName)" }
$args = @('-nologo', '-target:library', '-langversion:latest', '-nostdlib', "-out:$Out\bs.dll") + $refs + @("$Lane\stage\Assets\Editor\BuildSpeed.cs", $stub)
$rsp = "$Out\bs.rsp"; Set-Content $rsp (($args | ForEach-Object { '"' + $_ + '"' })) -Encoding UTF8
$o = & dotnet $csc "@$rsp" 2>&1; $err = @($o | Where-Object { $_ -match 'error CS' })
Write-Host ("BuildSpeed.cs + call-shape stub: errors " + $err.Count); $err | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
