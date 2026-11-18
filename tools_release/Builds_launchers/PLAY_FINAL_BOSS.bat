@echo off
rem Dom: drop straight into ZONE 6 = the FINAL BOSS ERLKING (The Hunting Ground), all boss gates open, own save folder (your real save is untouched). Starts fresh each time (-Reset).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\_agents\unitydev\play_all_zones.ps1" -Build "%~dp0HDRP_v109tz\HadesSlice.exe" -Zone z6_erlking -Spot arrive_from_prev -Reset
