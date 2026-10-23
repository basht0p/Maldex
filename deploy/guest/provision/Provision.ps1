#requires -Version 5.1
<#
  One-shot provisioning of the Maldex analysis guest. Runs as the local administrator `analyst` from the
  provisioning CD (label MALDEXPROV) via Autounattend FirstLogonCommands, then powers the VM off.

  Critical steps record a failure and provisioning still powers off (the host-side boot check then fails because
  no agent "ready" frame arrives). Best-effort steps (Defender, power plan, Office) warn and continue.
  Everything is logged to C:\ProgramData\Maldex\provision.log.
#>
$ErrorActionPreference = 'Stop'

$LogDir = 'C:\ProgramData\Maldex'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$LogFile = Join-Path $LogDir 'provision.log'
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

    Step 'Maldex agent (scheduled task at logon of analyst, interactive session)' {
        if ($script:failed) { throw "not registering the agent because earlier critical steps failed: $($script:failedSteps -join '; ')" }
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
    }

    Step 'Defender real-time, SmartScreen and Windows Update off' -BestEffort {
        # Policy keys are also set by Autounattend. Tamper Protection can block runtime changes; report the outcome.
        $pol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender'
        New-Item -Path "$pol\Real-Time Protection" -Force | Out-Null
        Set-ItemProperty -Path $pol -Name DisableAntiSpyware -Value 1 -Type DWord
        Set-ItemProperty -Path "$pol\Real-Time Protection" -Name DisableRealtimeMonitoring -Value 1 -Type DWord
        try { Set-MpPreference -DisableRealtimeMonitoring $true -DisableIOAVProtection $true -DisableBehaviorMonitoring $true -MAPSReporting Disabled -SubmitSamplesConsent NeverSend }
        catch { Write-Warning "Set-MpPreference: $_" }
        try {
            $rt = (Get-MpComputerStatus).RealTimeProtectionEnabled
            Write-Host "Defender RealTimeProtectionEnabled = $rt"
            if ($rt) { Write-Warning 'Defender real-time protection is STILL ON (Tamper Protection?). Disable it in the image by hand.' }
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

    Step 'Edge: no Startup Boost, no background mode' -BestEffort {
        # The agent launches msedge.exe for URL runs; with Startup Boost or background mode (Windows 11 defaults) an Edge
        # already running outside the sample's process tree takes the URL over, and its downloads would not be collected.
        # BackgroundModeEnabled=0 also covers "Continue running background extensions and apps when Microsoft Edge is closed".
        $edge = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
        New-Item -Path $edge -Force | Out-Null
        Set-ItemProperty -Path $edge -Name StartupBoostEnabled -Value 0 -Type DWord
        Set-ItemProperty -Path $edge -Name BackgroundModeEnabled -Value 0 -Type DWord
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

    Step 'Office (optional)' -BestEffort {
        $config = Join-Path $ProvRoot 'office-config.xml'
        if (-not (Test-Path $config)) { Write-Host 'No office-config.xml on the provisioning CD; skipping Office.'; return }
        # The Office media is the other CD that has setup.exe and an Office\ folder.
        $src = Get-PSDrive -PSProvider FileSystem |
            Where-Object { $_.Root -ne $ProvDrive -and (Test-Path (Join-Path $_.Root 'setup.exe')) -and (Test-Path (Join-Path $_.Root 'Office')) } |
            Select-Object -First 1 -ExpandProperty Root
        if (-not $src) { Write-Host 'Office media not mounted; skipping Office.'; return }
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
    if ($script:failed) { Write-Host "PROVISIONING FAILED: $($script:failedSteps -join '; '). Powering off; the host boot check will fail." }
    else { Write-Host 'Provisioning complete. Powering off.' }
    try { Stop-Transcript | Out-Null } catch { }

    # Clear every event log last, so the first analysis run starts clean (provision.log stays on disk).
    # Native stderr under 'Stop' would be a terminating error, so relax it here and never let this block shutdown.
    $ErrorActionPreference = 'Continue'
    try { foreach ($log in (wevtutil el)) { wevtutil cl "$log" 2>$null | Out-Null } } catch { }

    shutdown.exe /s /t 0
}
exit ([int]$script:failed)
