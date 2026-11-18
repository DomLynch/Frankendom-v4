@echo off
rem Dom: WALK zones 5 > 6 > 7 without killing the bosses first (v109q4). You start in ZONE 5 at the arrival, gates 5>6 and 6>7 are open. Walk straight ahead (forward, +Z); a boss that is chasing you shuts the way, so kill it or get 26 m away. Your real save is not touched (own save folder). Add RESET as an argument to start over in zone 5: PLAY_ALL_ZONES.bat RESET
if /I "%~1"=="RESET" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\_agents\unitydev\play_all_zones.ps1" -Build "%~dp0HDRP_v109tz\HadesSlice.exe" -Reset
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\_agents\unitydev\play_all_zones.ps1" -Build "%~dp0HDRP_v109tz\HadesSlice.exe"
)

