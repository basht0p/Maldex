# Maldex E2E sample: harmless persistence-style registry write. No network, no payload.
# Writes a text file under %TEMP% and a Run-key value that points at notepad.exe.
$ErrorActionPreference = 'Stop'
Set-Content -Path (Join-Path $env:TEMP 'maldex-regwrite.txt') -Value 'maldex e2e harmless marker'
New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'MaldexTest' -Value 'C:\Windows\System32\notepad.exe' -PropertyType String -Force | Out-Null
