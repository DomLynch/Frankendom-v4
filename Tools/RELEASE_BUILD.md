# Release build (FIX2 v1)

One command ships a build without the test / proof / capture harnesses (`FRANKEN_NO_HARNESS`, about 25 % smaller `Assembly-CSharp.dll`).
The dev build is unchanged: `Tools\build_and_gate.ps1` and the `build_vNNN.ps1` scripts still build with the harnesses in.

    powershell -File Tools\build_release.ps1 -v v110            # real run
    powershell -File Tools\build_release.ps1 -v v110 -DryRun    # prints every command, launches nothing

Steps (each is a hard stop, exit code 1 and a `RELEASE FAIL:` line):
1. `hc_gate.py --check` (new literals in a hot category), `data_gates.py`, `missing_keys.py`, `json_keys_gate.py --max-unknown 114` (the ratchet: lower it, never raise it).
2. `compile_check_defs.ps1` twice, harness ON and `-NoHarness` (Roslyn, CPU only): both must be 0 errors.
3. Unity batch `-executeMethod HdrpBatch.V51 -noharness -releasebuild -buildDir Builds/HDRP_<v> -buildTag <v>` (same chain as the dev build, plus the define). The script never deletes: an existing build folder stops it.
4. Verify: fresh exe, no `Result: Failure`, the log has `BUILD FIXHARNESS` (the define really went in).
5. md5: every `Assets\Data\*.json` (minus `build.editorOnlyData`) equals the file in `<exe>_Data\StreamingAssets`.

What a release build cannot do: any `-looptest`, `-physproof`, `-balanceauto`, run-through, camscene flag (compiled out). Proofs run on the dev build of the same tree. Boot-check the release exe by hand.

`hc_gate.py` since FIX2 v1: a bare run prints the report, then `hardcode gate: PASS` or `FAIL n` and exits 1 on FAIL. `--report` keeps the old report-only run. `--check` / `--vs` / `--quiet` are unchanged.

Switches: `-SkipStatic` (gates already run for this tree), `-SkipMd5`, `-Force` (build while Unity is running: not advised), `-Method BuildPlayerCmd.Run` (player only, no look / world steps).
`Tools\build_profile.txt`, `build_dev.txt`, `build_gpupass.txt` change the player build; the script prints a NOTE when one exists and does not delete it.
