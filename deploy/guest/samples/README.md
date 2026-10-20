# Harmless E2E samples

These files drive `./build.sh e2e` (`tests/Maldex.Integration.Tests/E2E/RealVmE2ETests.cs`). None of them is malware.

| File | What it is |
|---|---|
| `eicar.txt.b64` | The standard EICAR antivirus test string, base64-encoded so that antivirus on clones, CI or mail gateways does not quarantine the repository. The test decodes it at run time. By hand: `base64 -d eicar.txt.b64 > eicar.txt`. EICAR is inert text; it only makes scanners (here the YARA rule `EICAR_Test_File`) report a detection. |
| `regwrite.ps1` | Writes `%TEMP%\maldex-regwrite.txt` and the value `MaldexTest` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, pointing at `notepad.exe`. Exercises registry telemetry and the `persistence_run_key` heuristic. If you run it on a real machine, delete the value afterwards. |
| `url.txt` | `http://maldex-test.invalid/`. In `Simulated` mode INetSim answers it, and the Suricata dev rule `sid:9000001` matches the Host header. `.invalid` never resolves on the real internet. |

The calc-macro document is not stored here: it needs Office in the image. See "Running the E2E suite" in `../README.md`.
