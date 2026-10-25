#requires -Version 5.1
<#
  One-shot provisioning of the Maldex analysis guest. Runs as the local administrator `analyst` from the
  provisioning CD (label MALDEXPROV) via Autounattend FirstLogonCommands, in the same boot as Setup.

  Critical steps record a failure and provisioning carries on. Best-effort steps (Defender, SmartScreen, Windows Update,
  .ps1 association, Edge, power plan) warn and continue. Office is critical when office-config.xml is on the CD.
  The Maldex agent is installed and started right after the virtio tools, so the host can read the result even when a
  later step fails. Everything is logged to C:\ProgramData\Maldex\provision.log, and the outcome is written last to
  C:\ProgramData\Maldex\provision-result.json as {"ok":true|false,"failedSteps":[...]}.

  This script never shuts the guest down and never clears event logs: the host powers the VM off after it has read the
  result, and sealing clears the logs.
#>
$ErrorActionPreference = 'Stop'

$LogDir = 'C:\ProgramData\Maldex'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$LogFile = Join-Path $LogDir 'provision.log'
$ResultFile = 'C:\ProgramData\Maldex\provision-result.json'
Start-Transcript -Path $LogFile -Append | Out-Null

$ProvRoot = $PSScriptRoot
$ProvDrive = [System.IO.Path]::GetPathRoot($ProvRoot)
$AgentDir = 'C:\Program Files\Maldex'
$AnalystUser = 'analyst'
$AnalystPassword = 'Maldex-Analyst-1!'   # fixed throwaway value for a disposable guest; see Autounattend.xml
$script:failed = $false
$script:failedSteps = @()

function Step([string]$Name, [scriptblock]$Body, [switch]$BestEffort) {
    Write-Host "=== $Name"
    try { & $Body }
    catch {
        if ($BestEffort) { Write-Warning "$Name failed (continuing): $_" }
        else { Write-Host "ERROR: $Name failed: $_"; $script:failed = $true; $script:failedSteps += $Name }
    }
}

function Find-Drive([string]$RelativePath) {
    Get-PSDrive -PSProvider FileSystem |
        Where-Object { Test-Path (Join-Path $_.Root $RelativePath) } |
        Select-Object -First 1 -ExpandProperty Root
}

try {
    Step 'virtio-win guest tools' {
        $virtio = Find-Drive 'virtio-win-guest-tools.exe'
        if (-not $virtio) { throw 'virtio-win CD not found' }
        $p = Start-Process -FilePath (Join-Path $virtio 'virtio-win-guest-tools.exe') -ArgumentList '/quiet', '/norestart' -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { throw "virtio-win-guest-tools exit code $($p.ExitCode)" }
    }

    Step 'Maldex agent (scheduled task at logon of analyst, interactive session; started now)' {
        New-Item -ItemType Directory -Force -Path $AgentDir | Out-Null
        $exe = Join-Path $AgentDir 'Maldex.Agent.exe'
        Copy-Item -Force (Join-Path $ProvRoot 'Maldex.Agent.exe') $exe

        $user = "$env:COMPUTERNAME\$AnalystUser"
        $action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $AgentDir
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
            -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName 'MaldexAgent' -Action $action -Trigger $trigger -Principal $principal `
            -Settings $settings -Force | Out-Null
        if (-not (Get-ScheduledTask -TaskName 'MaldexAgent')) { throw 'MaldexAgent task was not registered' }
        # Start it now (analyst is logged on): the host sees the agent come up and reads the result marker through it.
        Start-ScheduledTask -TaskName 'MaldexAgent'
    }

    Step 'Sysmon' {
        $p = Start-Process -FilePath (Join-Path $ProvRoot 'Sysmon64.exe') `
            -ArgumentList '-accepteula', '-i', (Join-Path $ProvRoot 'sysmon-config.xml') -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "Sysmon64 exit code $($p.ExitCode)" }
        if ((Get-Service -Name Sysmon64 -ErrorAction Stop).Status -ne 'Running') { throw 'Sysmon64 service is not running' }
    }

    Step 'Autologon for analyst (so the agent task fires at every boot)' {
        $wl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        Set-ItemProperty -Path $wl -Name AutoAdminLogon -Value '1' -Type String
        Set-ItemProperty -Path $wl -Name DefaultUserName -Value $AnalystUser -Type String
        Set-ItemProperty -Path $wl -Name DefaultPassword -Value $AnalystPassword -Type String
        Set-ItemProperty -Path $wl -Name DefaultDomainName -Value $env:COMPUTERNAME -Type String
        Remove-ItemProperty -Path $wl -Name AutoLogonCount -ErrorAction SilentlyContinue
    }

    Step 'Trust the Maldex mitmproxy CA (LocalMachine\Root)' {
        $cer = Join-Path $ProvRoot 'maldex-ca.cer'
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer
        Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
        if (-not (Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $cert.Thumbprint)) { throw 'CA not in Root store' }
    }

    Step 'Defender real-time, SmartScreen and Windows Update off' -BestEffort {
        # Policy keys are also set by Autounattend. Tamper Protection can block runtime changes; report the outcome
        # (Defender is never forced off here: the admin does it in the Configuring console).
        $pol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender'
        New-Item -Path "$pol\Real-Time Protection" -Force | Out-Null
        Set-ItemProperty -Path $pol -Name DisableAntiSpyware -Value 1 -Type DWord
        Set-ItemProperty -Path "$pol\Real-Time Protection" -Name DisableRealtimeMonitoring -Value 1 -Type DWord
        try { Set-MpPreference -DisableRealtimeMonitoring $true -DisableIOAVProtection $true -DisableBehaviorMonitoring $true -MAPSReporting Disabled -SubmitSamplesConsent NeverSend }
        catch { Write-Warning "Set-MpPreference: $_" }
        try {
            $rt = (Get-MpComputerStatus).RealTimeProtectionEnabled
            Write-Host "Defender RealTimeProtectionEnabled = $rt"
            if ($rt) { Write-Warning 'Defender real-time protection is STILL ON (Tamper Protection?). Disable it in the Configuring console.' }
        } catch { Write-Warning "Get-MpComputerStatus: $_" }

        Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' -Name SmartScreenEnabled -Value 'Off' -Type String
        New-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Force | Out-Null
        Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Name EnableSmartScreen -Value 0 -Type DWord
        foreach ($svc in 'wuauserv', 'UsoSvc') {
            Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue
            Set-Service -Name $svc -StartupType Disabled -ErrorAction SilentlyContinue
        }
    }

    Step '.ps1 opens with PowerShell, not Notepad' -BestEffort {
        # The agent opens samples with ShellExecute; Windows 11 associates .ps1 with Notepad unless told otherwise.
        $key = 'HKLM:\SOFTWARE\Classes\Microsoft.PowerShellScript.1\Shell'
        New-Item -Path "$key\Open\Command" -Force | Out-Null
        Set-ItemProperty -Path $key -Name '(default)' -Value 'Open'
        Set-ItemProperty -Path "$key\Open\Command" -Name '(default)' -Value '"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%1"'
    }

    Step 'Edge: no Startup Boost, no background mode, no first-run page' -BestEffort {
        # The agent launches msedge.exe for URL runs; with Startup Boost or background mode (Windows 11 defaults) an Edge
        # already running outside the sample's process tree takes the URL over, and its downloads would not be collected.
        # BackgroundModeEnabled=0 also covers "Continue running background extensions and apps when Microsoft Edge is closed".
        $edge = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
        New-Item -Path $edge -Force | Out-Null
        Set-ItemProperty -Path $edge -Name StartupBoostEnabled -Value 0 -Type DWord
        Set-ItemProperty -Path $edge -Name BackgroundModeEnabled -Value 0 -Type DWord
        # No welcome page: it would cover the URL a run opens.
        Set-ItemProperty -Path $edge -Name HideFirstRunExperience -Value 1 -Type DWord
        # Resolve through Windows (no built-in DNS client, no DNS over HTTPS): Sysmon only sees DnsQuery lookups, and the
        # run's dnsmasq/INetSim must answer every name.
        Set-ItemProperty -Path $edge -Name BuiltInDnsClientEnabled -Value 0 -Type DWord
        Set-ItemProperty -Path $edge -Name DnsOverHttpsMode -Value 'off' -Type String
    }

    Step 'Power plan: never sleep' -BestEffort {
        powercfg /x -standby-timeout-ac 0
        powercfg /x -standby-timeout-dc 0
        powercfg /x -monitor-timeout-ac 0
        powercfg /x -monitor-timeout-dc 0
        powercfg /x -hibernate-timeout-ac 0
        powercfg /hibernate off
    }

    # Critical when the host put office-config.xml on the CD: the admin asked for Office.
    Step 'Office' {
        $config = Join-Path $ProvRoot 'office-config.xml'
        if (-not (Test-Path $config)) { Write-Host 'No office-config.xml on the provisioning CD; skipping Office.'; return }
        # The Office media is the other CD that has setup.exe and an Office\ folder.
        $src = Get-PSDrive -PSProvider FileSystem |
            Where-Object { $_.Root -ne $ProvDrive -and (Test-Path (Join-Path $_.Root 'setup.exe')) -and (Test-Path (Join-Path $_.Root 'Office')) } |
            Select-Object -First 1 -ExpandProperty Root
        if (-not $src) { throw 'Office media not mounted' }
        $cfg = Join-Path $env:TEMP 'office-config.xml'
        (Get-Content -Raw $config).Replace('@OFFICE_SOURCE@', $src.TrimEnd('\')) | Set-Content -Path $cfg -Encoding UTF8
        $p = Start-Process -FilePath (Join-Path $src 'setup.exe') -ArgumentList '/configure', $cfg -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "Office setup exit code $($p.ExitCode)" }

        # Macro security "enable all" and Protected View off, for the analysis profile (HKCU of analyst).
        foreach ($app in 'Word', 'Excel', 'PowerPoint') {
            $sec = "HKCU:\Software\Microsoft\Office\16.0\$app\Security"
            New-Item -Path "$sec\ProtectedView" -Force | Out-Null
            Set-ItemProperty -Path $sec -Name VBAWarnings -Value 1 -Type DWord
            Set-ItemProperty -Path $sec -Name AccessVBOM -Value 1 -Type DWord
            Set-ItemProperty -Path "$sec\ProtectedView" -Name DisableInternetFilesInPView -Value 1 -Type DWord
            Set-ItemProperty -Path "$sec\ProtectedView" -Name DisableAttachementsInPView -Value 1 -Type DWord
            Set-ItemProperty -Path "$sec\ProtectedView" -Name DisableUnsafeLocationsInPView -Value 1 -Type DWord
        }
    }

}
catch { Write-Host "FATAL: $_"; $script:failed = $true; $script:failedSteps += 'unexpected error' }
finally {
    if ($script:failed) { Write-Host "PROVISIONING FAILED: $($script:failedSteps -join '; ')." }
    else { Write-Host 'Provisioning complete.' }
    # The host reads this marker through the agent. UTF-8 without BOM, written to .tmp and renamed so a reader never sees a partial file.
    # Written before the transcript stops, so a failure to write it is still in provision.log.
    try {
        $json = (@{ ok = (-not $script:failed); failedSteps = @($script:failedSteps) } | ConvertTo-Json -Compress)
        $tmp = "$ResultFile.tmp"
        [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
        Move-Item -Force -Path $tmp -Destination $ResultFile
    } catch { Write-Host "Could not write ${ResultFile}: $_" }
    try { Stop-Transcript | Out-Null } catch { }
}
exit ([int]$script:failed)
