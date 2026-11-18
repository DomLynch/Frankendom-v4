param([string]$Root="$env:FK_ROOT\_agents\laneINT107\scratch\x",[switch]$Live,[switch]$Editor,[string]$Name='',[string]$Out="$env:FK_ROOT\_agents\laneINT107\out")
# laneINT107 compile: Roslyn of Unity 6000.6.7f1 (INT103 pattern). Default = scratch copy $Root; -Live = live project Assets (baseline, read-only); -Editor adds Assets\Editor + UnityEditor refs.
# The live Library\ScriptAssemblies DLLs and Assets\MetaHumanHead scripts are only READ. Output goes to laneINT107\out.
$Project=$env:FK_ROOT; $U='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Data'
$csc="$U\DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll"; if(-not(Test-Path $csc)){$csc=(Get-ChildItem "$U\DotNetSdk" -Recurse -Filter csc.dll|Select -First 1).FullName}
New-Item -ItemType Directory -Force $Out|Out-Null
$A=$(if($Live){"$Project\Assets"}else{"$Root\Assets"})
$files=@(Get-ChildItem "$A\Scripts" -Recurse -Filter *.cs|%{$_.FullName})+@(Get-ChildItem "$Project\Assets\MetaHumanHead" -Recurse -Filter *.cs -EA SilentlyContinue|%{$_.FullName})
if($Editor){$files+=@(Get-ChildItem "$A\Editor" -Filter *.cs -Recurse|%{$_.FullName})}
$sa=Get-ChildItem "$Project\Library\ScriptAssemblies" -Filter *.dll|?{$_.Name -notmatch '^Assembly-CSharp|Tests|Editor\.dll$'}
$defs='UNITY_STANDALONE;UNITY_STANDALONE_WIN;UNITY_6000_0_OR_NEWER;UNITY_2022_3_OR_NEWER;ENABLE_INPUT_SYSTEM;ENABLE_LEGACY_INPUT_MANAGER'; if($Editor){$defs+=';UNITY_EDITOR'}
$refs=@("-r:$U\NetStandard\ref\2.1.0\netstandard.dll"); foreach($d in Get-ChildItem "$U\Managed\UnityEngine" -Filter *.dll){$refs+="-r:$($d.FullName)"}; foreach($d in $sa){$refs+="-r:$($d.FullName)"}
if($Editor){foreach($d in (Get-ChildItem "$U\Managed" -Filter UnityEditor*.dll -EA SilentlyContinue | ?{$_.Name -ne "UnityEditor.dll"})){$refs+="-r:$($d.FullName)"}}
$base=$(if($Name){$Name}elseif($Live){'live'}else{'int107'})
$name=$base+$(if($Editor){'-editor'}else{''})
$common=@('-nologo','-target:library','-langversion:latest','-nostdlib','-nowarn:CS0618,CS0612,CS0649,CS0169,CS0414,CS0219,CS0168,CS0108,CS0114,CS8981,CS0659,CS0661,CS1591',"-define:$defs")
$lines=((@($common)+$refs+@("-out:$Out\$name.dll"))|%{'"'+$_+'"'})+($files|%{'"'+$_+'"'}); $rsp="$Out\$name.rsp"; Set-Content $rsp $lines -Encoding UTF8
$o=& dotnet $csc "@$rsp" 2>&1; $err=$o|?{$_ -match 'error CS'}; Set-Content "$Out\$name.log" $o
Write-Host ("{0}: {1} files, errors: {2}" -f $name,$files.Count,@($err).Count); $err|Select -First 40|%{Write-Host "  $_"}
