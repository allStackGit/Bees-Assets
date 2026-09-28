param(
    [ValidateSet('start','stop')]
    [string]$Command='start',
    [int]$Envs=0,
    [string]$InstallRoot='',
    [string]$TorchDevice='',
    [switch]$NoAutostart
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

$DefaultInstallRoot='__BEES_INSTALL_ROOT__'
$DefaultTorchDevice='__BEES_TORCH_DEVICE__'
$TailnetLearner='__BEES_TAILNET_LEARNER__'
$TailnetBootstrapPort='__BEES_TAILNET_BOOTSTRAP_PORT__'
$ControlPort='__BEES_CONTROL_PORT__'
$BrokerPort='__BEES_BROKER_PORT__'
$GameplayPort='__BEES_GAMEPLAY_PORT__'
$BundledTailnetBridge='__BEES_TAILNET_BRIDGE_FILE__'
$TailnetBridgeSha256='__BEES_TAILNET_BRIDGE_SHA256__'
$BootstrapToken='__BEES_BOOTSTRAP_TOKEN__'

function Require-GeneratedValue([string]$Value,[string]$Name){
    if($Value -match '^__BEES_'){
        throw "$Name is not configured. Copy a generated worker launcher from B:\Bees\Remote after running bees.ps1 start."
    }
}

foreach($item in @(
    @($DefaultInstallRoot,'InstallRoot'),
    @($DefaultTorchDevice,'TorchDevice'),
    @($TailnetLearner,'TailnetLearner'),
    @($TailnetBootstrapPort,'TailnetBootstrapPort'),
    @($ControlPort,'ControlPort'),
    @($BrokerPort,'BrokerPort'),
    @($GameplayPort,'GameplayPort'),
    @($BundledTailnetBridge,'TailnetBridgeFile'),
    @($TailnetBridgeSha256,'TailnetBridgeSha256'),
    @($BootstrapToken,'BootstrapToken')
)){ Require-GeneratedValue ([string]$item[0]) ([string]$item[1]) }

$TailnetBootstrapPort=[int]$TailnetBootstrapPort
$ControlPort=[int]$ControlPort
$BrokerPort=[int]$BrokerPort
$GameplayPort=[int]$GameplayPort
if(-not $InstallRoot){$InstallRoot=[Environment]::ExpandEnvironmentVariables($DefaultInstallRoot)}
if(-not $TorchDevice){$TorchDevice=$DefaultTorchDevice}
if($Envs -lt 0 -or $Envs -gt 64){throw 'Envs must be in 1-64 when specified.'}
foreach($port in @($TailnetBootstrapPort,$ControlPort,$BrokerPort,$GameplayPort)){
    if($port -lt 1 -or $port -gt 65535){throw 'Configured Bees ports must be in 1-65535.'}
}

function Resolve-Exe([string]$Name){
    $cmd=Get-Command $Name -ErrorAction SilentlyContinue
    if($null -eq $cmd){return $null}
    $cmd.Source
}

$InstallRoot=[IO.Path]::GetFullPath($InstallRoot)
$RuntimeRoot=Join-Path $InstallRoot 'Runtime'
$SecretsRoot=Join-Path $InstallRoot 'Secrets'
$DownloadsRoot=Join-Path $InstallRoot 'Downloads'
$VenvRoot=Join-Path $InstallRoot '.venv'
$TailnetRoot=Join-Path $InstallRoot 'Tailnet'
$TailnetState=Join-Path $TailnetRoot 'State'
$LogsRoot=Join-Path $InstallRoot 'Logs'
$SupervisorPidFile=Join-Path $InstallRoot 'remote-worker.pid'
$ShutdownRequestFile=Join-Path $InstallRoot 'remote-worker.stop'
$SupervisorOutLog=Join-Path $LogsRoot 'remote-supervisor.out.log'
$SupervisorErrLog=Join-Path $LogsRoot 'remote-supervisor.err.log'
foreach($path in @($InstallRoot,$RuntimeRoot,$SecretsRoot,$DownloadsRoot,$TailnetRoot,$TailnetState,$LogsRoot)){
    $null=New-Item -ItemType Directory -Force -Path $path
}

$StartupRoot=[Environment]::GetFolderPath('Startup')
$AutostartFile=if($StartupRoot){Join-Path $StartupRoot 'BeesTrainingRemoteWorker.cmd'}else{''}
$AutostartMarker=Join-Path $InstallRoot 'remote-autostart.enabled'
$AutostartMonitor=Join-Path $InstallRoot 'Launcher\bees-remote-monitor.ps1'
$AutostartMonitorPidFile=Join-Path $InstallRoot 'remote-monitor.pid'
$AutostartChild=([string]$env:BEES_AUTOSTART_CHILD -eq '1')

function Get-LiveAutostartMonitor {
    if(-not(Test-Path -LiteralPath $AutostartMonitorPidFile)){return $null}
    $value=(Get-Content -LiteralPath $AutostartMonitorPidFile -Raw -ErrorAction SilentlyContinue).Trim()
    $monitorPid=0
    if(-not [int]::TryParse($value,[ref]$monitorPid) -or $monitorPid -le 0){return $null}
    try {
        $record=Get-CimInstance Win32_Process -Filter "ProcessId = $monitorPid" -ErrorAction Stop
        $commandLine=[string]$record.CommandLine
        if(
            [string]::IsNullOrWhiteSpace($commandLine) -or
            $commandLine.IndexOf('bees-remote-monitor.ps1',[StringComparison]::OrdinalIgnoreCase) -lt 0 -or
            $commandLine.IndexOf($InstallRoot,[StringComparison]::OrdinalIgnoreCase) -lt 0
        ){
            return $null
        }
        Get-Process -Id $monitorPid -ErrorAction SilentlyContinue
    } catch {
        return $null
    }
}

function Remove-RemoteAutostart {
    Remove-Item -LiteralPath $AutostartMarker -Force -ErrorAction SilentlyContinue
    $monitor=Get-LiveAutostartMonitor
    if($null -ne $monitor){
        Stop-Process -Id $monitor.Id -Force -ErrorAction SilentlyContinue
        try {$monitor.WaitForExit(5000)} catch {}
    }
    Remove-Item -LiteralPath $AutostartMonitorPidFile -Force -ErrorAction SilentlyContinue
    if($AutostartFile){
        Remove-Item -LiteralPath $AutostartFile -Force -ErrorAction SilentlyContinue
    }
}

function Escape-PowerShellSingleQuoted([string]$Value){
    $Value.Replace("'","''")
}

function Install-RemoteAutostart {
    if($AutostartChild){return}
    if($NoAutostart){
        Remove-RemoteAutostart
        return
    }
    $launcherPath=[string]$env:BEES_REMOTE_LAUNCHER_PATH
    if([string]::IsNullOrWhiteSpace($launcherPath) -or -not(Test-Path -LiteralPath $launcherPath)){
        Write-Warning '[Bees remote] copied launcher path is unavailable; reboot autostart could not be registered.'
        return
    }
    if(-not $AutostartFile){
        Write-Warning '[Bees remote] Windows Startup folder is unavailable; reboot autostart could not be registered.'
        return
    }

    $null=New-Item -ItemType Directory -Force -Path (Split-Path -Parent $AutostartMonitor)
    'enabled' | Set-Content -LiteralPath $AutostartMarker -NoNewline -Encoding ASCII
    $monitorBody=@'
$ErrorActionPreference='Continue'
$Marker='__MARKER__'
$Launcher='__LAUNCHER__'
$InstallRoot='__INSTALL_ROOT__'
$TorchDevice='__TORCH_DEVICE__'
$MonitorPidFile='__PID_FILE__'
$Envs=__ENVS__

$existingPid=0
if(Test-Path -LiteralPath $MonitorPidFile){
    $raw=(Get-Content -LiteralPath $MonitorPidFile -Raw -ErrorAction SilentlyContinue).Trim()
    [void][int]::TryParse($raw,[ref]$existingPid)
}
if($existingPid -gt 0){
    try {
        $record=Get-CimInstance Win32_Process -Filter "ProcessId = $existingPid" -ErrorAction Stop
        if(([string]$record.CommandLine).IndexOf('bees-remote-monitor.ps1',[StringComparison]::OrdinalIgnoreCase) -ge 0){
            exit 0
        }
    } catch {}
}
$PID | Set-Content -LiteralPath $MonitorPidFile -NoNewline -Encoding ASCII
$env:BEES_AUTOSTART_CHILD='1'

function Test-SupervisorAlive {
    $pidFile=Join-Path $InstallRoot 'remote-worker.pid'
    if(-not(Test-Path -LiteralPath $pidFile)){return $false}
    $raw=(Get-Content -LiteralPath $pidFile -Raw -ErrorAction SilentlyContinue).Trim()
    $workerPid=0
    if(-not [int]::TryParse($raw,[ref]$workerPid) -or $workerPid -le 0){return $false}
    try {
        $record=Get-CimInstance Win32_Process -Filter "ProcessId = $workerPid" -ErrorAction Stop
        $commandLine=[string]$record.CommandLine
        return (
            -not [string]::IsNullOrWhiteSpace($commandLine) -and
            $commandLine.IndexOf('bees_managed_remote_worker.py',[StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $commandLine.IndexOf($InstallRoot,[StringComparison]::OrdinalIgnoreCase) -ge 0
        )
    } catch {
        return $false
    }
}

try {
    while(Test-Path -LiteralPath $Marker){
        if(-not(Test-SupervisorAlive)){
            $command='call "' + $Launcher.Replace('"','""') + '" start -InstallRoot "' +
                $InstallRoot.Replace('"','""') + '" -TorchDevice "' +
                $TorchDevice.Replace('"','""') + '"'
            if($Envs -gt 0){$command+=' -Envs ' + [string]$Envs}
            try {& $env:COMSPEC /d /c $command *> $null} catch {}
        }
        Start-Sleep -Seconds 10
    }
} finally {
    try {
        $recorded=(Get-Content -LiteralPath $MonitorPidFile -Raw -ErrorAction SilentlyContinue).Trim()
        if($recorded -eq [string]$PID){
            Remove-Item -LiteralPath $MonitorPidFile -Force -ErrorAction SilentlyContinue
        }
    } catch {}
}
'@
    $monitorBody=$monitorBody.Replace('__MARKER__',(Escape-PowerShellSingleQuoted $AutostartMarker))
    $monitorBody=$monitorBody.Replace('__LAUNCHER__',(Escape-PowerShellSingleQuoted $launcherPath))
    $monitorBody=$monitorBody.Replace('__INSTALL_ROOT__',(Escape-PowerShellSingleQuoted $InstallRoot))
    $monitorBody=$monitorBody.Replace('__TORCH_DEVICE__',(Escape-PowerShellSingleQuoted $TorchDevice))
    $monitorBody=$monitorBody.Replace('__PID_FILE__',(Escape-PowerShellSingleQuoted $AutostartMonitorPidFile))
    $monitorBody=$monitorBody.Replace('__ENVS__',[string]$Envs)
    [IO.File]::WriteAllText($AutostartMonitor,$monitorBody,(New-Object Text.UTF8Encoding($false)))

    $line='@echo off' + [Environment]::NewLine +
        'start "" powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' +
        $AutostartMonitor + '"' + [Environment]::NewLine
    [IO.File]::WriteAllText($AutostartFile,$line,(New-Object Text.UTF8Encoding($false)))

    # Re-registration can move persistence from the originally copied launcher to the
    # install-root managed launcher. Replace the live monitor so it immediately adopts the
    # newly written launcher/configuration instead of holding old values until next logon.
    $oldMonitor=Get-LiveAutostartMonitor
    if($null -ne $oldMonitor){
        Stop-Process -Id $oldMonitor.Id -Force -ErrorAction SilentlyContinue
        try {$oldMonitor.WaitForExit(5000)} catch {}
        Remove-Item -LiteralPath $AutostartMonitorPidFile -Force -ErrorAction SilentlyContinue
    }

    $monitorProcess=Start-Process -FilePath 'powershell.exe' -ArgumentList @(
        '-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden',
        '-File',('"' + $AutostartMonitor + '"')
    ) -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 250
    if($monitorProcess.HasExited){
        Write-Warning '[Bees remote] Windows worker watchdog exited during startup; reboot persistence remains registered.'
    }
}

function Get-RecordedSupervisorPid {
    if(-not(Test-Path -LiteralPath $SupervisorPidFile)){return 0}
    $value=(Get-Content -LiteralPath $SupervisorPidFile -Raw -ErrorAction SilentlyContinue).Trim()
    $pidValue=0
    if(-not [int]::TryParse($value,[ref]$pidValue)){return 0}
    $pidValue
}

function Get-LiveSupervisorProcess {
    $pidValue=Get-RecordedSupervisorPid
    if($pidValue -le 0){return $null}
    $process=Get-Process -Id $pidValue -ErrorAction SilentlyContinue
    if($null -eq $process){return $null}
    try {
        $record=Get-CimInstance Win32_Process -Filter "ProcessId = $pidValue" -ErrorAction Stop
        $commandLine=[string]$record.CommandLine
        if(
            [string]::IsNullOrWhiteSpace($commandLine) -or
            $commandLine.IndexOf('bees_managed_remote_worker.py',[StringComparison]::OrdinalIgnoreCase) -lt 0 -or
            $commandLine.IndexOf($InstallRoot,[StringComparison]::OrdinalIgnoreCase) -lt 0
        ){
            return $null
        }
    } catch {
        return $null
    }
    $process
}

if($Command -eq 'stop'){
    Remove-RemoteAutostart
    $process=Get-LiveSupervisorProcess
    if($null -eq $process){
        Remove-Item -LiteralPath $SupervisorPidFile,$ShutdownRequestFile -Force -ErrorAction SilentlyContinue
        Write-Host '[Bees remote] worker is not running.'
        exit 0
    }

    'stop' | Set-Content -LiteralPath $ShutdownRequestFile -NoNewline -Encoding ASCII
    Write-Host "[Bees remote] stop requested for worker PID $($process.Id); waiting for managed cleanup..."
    $deadline=[DateTime]::UtcNow.AddSeconds(45)
    while([DateTime]::UtcNow -lt $deadline){
        if($null -eq (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)){
            Remove-Item -LiteralPath $SupervisorPidFile,$ShutdownRequestFile -Force -ErrorAction SilentlyContinue
            Write-Host '[Bees remote] worker stopped.'
            exit 0
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Remote worker PID $($process.Id) did not stop within 45 seconds. It was not force-killed."
}

$existingProcess=Get-LiveSupervisorProcess
if($null -ne $existingProcess){
    Install-RemoteAutostart
    Write-Host "[Bees remote] worker is already running in the background (PID $($existingProcess.Id))."
    Write-Host '[Bees remote] use bees-remote-worker.cmd stop to stop it.'
    exit 0
}
Remove-Item -LiteralPath $SupervisorPidFile,$ShutdownRequestFile -Force -ErrorAction SilentlyContinue

Write-Host '[Bees remote] Stage 1/5: preparing local worker files...'

$bundledBridgePath=Join-Path $PSScriptRoot $BundledTailnetBridge
if(-not(Test-Path -LiteralPath $bundledBridgePath)){
    throw "Bundled tailnet runtime is missing: $bundledBridgePath. Copy the generated Windows remote bundle files together."
}
$bundledBridgeSha=(Get-FileHash -LiteralPath $bundledBridgePath -Algorithm SHA256).Hash.ToLowerInvariant()
if($bundledBridgeSha -ne $TailnetBridgeSha256){ throw 'Bundled tailnet runtime failed SHA-256 verification.' }

$tailnetBridge=Join-Path $TailnetRoot 'bees-tailnet-bridge.exe'
$writeBridge=$true
if(Test-Path -LiteralPath $tailnetBridge){
    $existing=(Get-FileHash -LiteralPath $tailnetBridge -Algorithm SHA256).Hash.ToLowerInvariant()
    $writeBridge=$existing -ne $TailnetBridgeSha256
}
if($writeBridge){
    Write-Host '[Bees remote] installing bundled private-network runtime...'
    Copy-Item -LiteralPath $bundledBridgePath -Destination $tailnetBridge -Force
}
$actualBridgeSha=(Get-FileHash -LiteralPath $tailnetBridge -Algorithm SHA256).Hash.ToLowerInvariant()
if($actualBridgeSha -ne $TailnetBridgeSha256){ throw 'Installed tailnet runtime failed SHA-256 verification.' }

$bootstrapTokenPath=Join-Path $TailnetRoot 'bootstrap.token'
$BootstrapToken | Set-Content -LiteralPath $bootstrapTokenPath -NoNewline -Encoding ASCII
$workerHostname=("bees-worker-" + $env:COMPUTERNAME.ToLowerInvariant())

Write-Host '[Bees remote] Stage 2/5: checking private-network identity...'
Write-Host '[Bees remote] on first use, open the Tailscale login URL printed below; no VPN installation is required.'
& $tailnetBridge auth --state $TailnetState --hostname $workerHostname
if($LASTEXITCODE -ne 0){ throw "Embedded tailnet authentication failed with exit code $LASTEXITCODE." }

$runtimeZip=Join-Path $DownloadsRoot 'bees-remote-runtime.zip'
$workerToken=Join-Path $SecretsRoot 'training-worker.token'
$wanToken=Join-Path $SecretsRoot 'wan.token'
Write-Host '[Bees remote] Stage 3/5: fetching the current Bees worker runtime over the private tailnet...'
$fetchArgs=@(
    'fetch',
    '--state',$TailnetState,
    '--hostname',$workerHostname,
    '--target',($TailnetLearner + ':' + $TailnetBootstrapPort),
    '--token-file',$bootstrapTokenPath,
    '--runtime-out',$runtimeZip,
    '--worker-token-out',$workerToken,
    '--wan-token-out',$wanToken
)
& $tailnetBridge @fetchArgs
if($LASTEXITCODE -ne 0){ throw "Private bootstrap fetch failed with exit code $LASTEXITCODE." }

if(Test-Path -LiteralPath $RuntimeRoot){Remove-Item -LiteralPath $RuntimeRoot -Recurse -Force}
$null=New-Item -ItemType Directory -Force -Path $RuntimeRoot
Expand-Archive -LiteralPath $runtimeZip -DestinationPath $RuntimeRoot -Force

function Test-Python310 {
    param(
        [string]$Exe,
        [string[]]$Prefix=@()
    )

    if([string]::IsNullOrWhiteSpace($Exe)){return $false}
    if(-not(Test-Path -LiteralPath $Exe)){return $false}

    $previousErrorAction=$ErrorActionPreference
    try {
        # A failed executable, Microsoft Store alias, or wrong Python version is simply not a
        # usable Python 3.10 installation. Suppress probe errors and fall through to installation.
        $ErrorActionPreference='SilentlyContinue'
        & $Exe @Prefix -c 'import sys; raise SystemExit(0 if sys.version_info[:2] == (3,10) else 1)' *> $null
        return ($LASTEXITCODE -eq 0)
    } catch {
        return $false
    } finally {
        $ErrorActionPreference=$previousErrorAction
    }
}

function Resolve-PythonLauncher {
    $py=Resolve-Exe 'py'
    if($py -and (Test-Python310 $py @('-3.10'))){return @($py,'-3.10')}
    $python=Resolve-Exe 'python'
    if($python -and (Test-Python310 $python)){return @($python)}
    foreach($candidate in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python310\python.exe'),
        'C:\Program Files\Python310\python.exe'
    )){
        if(Test-Python310 $candidate){return @($candidate)}
    }
    $winget=Resolve-Exe 'winget'
    if($winget){
        Write-Host 'Python 3.10 was not found. Installing it with winget...'
        $wingetArgs=@(
            'install',
            '--id','Python.Python.3.10',
            '-e',
            '--source','winget',
            '--accept-package-agreements',
            '--accept-source-agreements',
            '--disable-interactivity',
            '--silent'
        )
        $installProcess=Start-Process -FilePath $winget -ArgumentList $wingetArgs -Wait -PassThru -NoNewWindow
        if($installProcess.ExitCode -ne 0){
            throw "winget failed to install Python 3.10 (exit code $($installProcess.ExitCode))."
        }

        foreach($candidate in @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python310\python.exe'),
            'C:\Program Files\Python310\python.exe'
        )){
            if(Test-Python310 $candidate){return @($candidate)}
        }
        $py=Resolve-Exe 'py'
        if($py -and (Test-Python310 $py @('-3.10'))){return @($py,'-3.10')}
    }
    throw 'Python 3.10 is required and could not be installed automatically.'
}

Write-Host '[Bees remote] Stage 4/5: preparing Python 3.10 worker environment...'
if(-not(Test-Path -LiteralPath (Join-Path $VenvRoot 'Scripts\python.exe'))){
    # PowerShell unrolls a single-item array returned by a function into a scalar.
    # Normalize explicitly so StrictMode-safe .Count/indexing works for both
    # python.exe-only launchers and py.exe + -3.10 launchers.
    $launcher=@(Resolve-PythonLauncher)
    $launcherExe=$launcher[0]
    $launcherArgs=@()
    if($launcher.Count -gt 1){$launcherArgs=@($launcher[1..($launcher.Count-1)])}
    Write-Host "Creating remote worker Python environment at $VenvRoot"
    & $launcherExe @launcherArgs -m venv $VenvRoot
    if($LASTEXITCODE -ne 0){throw 'Failed to create the Python virtual environment.'}
}

$venvPython=Join-Path $VenvRoot 'Scripts\python.exe'
$requirements=Join-Path $RuntimeRoot 'bees_remote_requirements.txt'
$requirementsHash=(Get-FileHash -Algorithm SHA256 -LiteralPath $requirements).Hash.ToLowerInvariant()
$requirementsStamp=Join-Path $VenvRoot 'bees-requirements.sha256'
$currentStamp=if(Test-Path -LiteralPath $requirementsStamp){(Get-Content -LiteralPath $requirementsStamp -Raw).Trim()}else{''}

function Test-RemotePythonDependencies {
    param([string]$PythonExe)

    $previousErrorAction=$ErrorActionPreference
    try {
        # Missing imports are an expected probe result before first-time dependency installation.
        # Windows PowerShell 5.1 can promote native stderr to a terminating NativeCommandError
        # under ErrorActionPreference=Stop, so suppress the probe and decide from its exit code.
        $ErrorActionPreference='SilentlyContinue'
        & $PythonExe -c "import pkg_resources, mlagents, torch, numpy" *> $null
        return ($LASTEXITCODE -eq 0)
    } catch {
        return $false
    } finally {
        $ErrorActionPreference=$previousErrorAction
    }
}

$dependenciesOk=Test-RemotePythonDependencies -PythonExe $venvPython
if($currentStamp -ne $requirementsHash -or -not $dependenciesOk){
    Write-Host 'Installing/updating remote worker Python dependencies...'
    & $venvPython -m pip install --upgrade pip
    if($LASTEXITCODE -ne 0){throw 'pip upgrade failed.'}
    & $venvPython -m pip install -r $requirements
    if($LASTEXITCODE -ne 0){throw 'Remote worker dependency installation failed.'}
    if(-not (Test-RemotePythonDependencies -PythonExe $venvPython)){
        throw 'Remote Python dependency validation failed after installation.'
    }
    $requirementsHash | Set-Content -LiteralPath $requirementsStamp -NoNewline -Encoding ASCII
}

$worker=Join-Path $RuntimeRoot 'bees_managed_remote_worker.py'
$workerArgs=@(
    $worker,
    '--tailnet-bridge',$tailnetBridge,
    '--tailnet-state',$TailnetState,
    '--tailnet-hostname',$workerHostname,
    '--tailnet-target',$TailnetLearner,
    '--control-port',[string]$ControlPort,
    '--bootstrap-port',[string]$TailnetBootstrapPort,
    '--broker-port',[string]$BrokerPort,
    '--gameplay-port',[string]$GameplayPort,
    '--install-root',$InstallRoot,
    '--runtime-archive',$runtimeZip,
    '--launcher-path',$env:BEES_REMOTE_LAUNCHER_PATH,
    '--bootstrap-token-file',$bootstrapTokenPath,
    '--worker-token-file',$workerToken,
    '--wan-token-file',$wanToken,
    '--torch-device',$TorchDevice
)
if($Envs -gt 0){$workerArgs+=@('--envs',[string]$Envs)}
if($NoAutostart){$workerArgs+='--no-autostart'}

function Quote-ProcessArgument([string]$Value){
    if($Value -notmatch '[\s"]'){return $Value}
    '"' + ($Value.Replace('"','\"')) + '"'
}

Write-Host ''
Write-Host '[Bees remote] Stage 5/5: starting managed training worker in the background...'
if($Envs -gt 0){
    Write-Host "Starting Bees remote worker with $Envs environments."
}else{
    Write-Host 'Starting Bees remote worker with BeesServer environment auto-optimization (CPU-derived start, RAM-capped maximum 64).'
}
Remove-Item -LiteralPath $ShutdownRequestFile -Force -ErrorAction SilentlyContinue
$argumentString=(@('-u') + $workerArgs | ForEach-Object { Quote-ProcessArgument ([string]$_) }) -join ' '
$startupUtc=[DateTime]::UtcNow
$process=Start-Process -FilePath $venvPython -ArgumentList $argumentString -WorkingDirectory $InstallRoot -WindowStyle Hidden -RedirectStandardOutput $SupervisorOutLog -RedirectStandardError $SupervisorErrLog -PassThru
$process.Id | Set-Content -LiteralPath $SupervisorPidFile -NoNewline -Encoding ASCII

function Get-FreshSupervisorTail([string]$Path,[DateTime]$SinceUtc){
    if(-not(Test-Path -LiteralPath $Path)){return @()}
    $item=Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if($null -eq $item -or $item.LastWriteTimeUtc -lt $SinceUtc){return @()}
    @(Get-Content -LiteralPath $Path -Tail 80 -ErrorAction SilentlyContinue)
}

$startupDeadline=[DateTime]::UtcNow.AddSeconds(10)
$startupState='supervisor alive; waiting for first connectivity signal'
while([DateTime]::UtcNow -lt $startupDeadline){
    $process.Refresh()
    if($process.HasExited){
        Remove-Item -LiteralPath $SupervisorPidFile -Force -ErrorAction SilentlyContinue
        $outTail=(Get-FreshSupervisorTail $SupervisorOutLog $startupUtc) -join [Environment]::NewLine
        $errTail=(Get-FreshSupervisorTail $SupervisorErrLog $startupUtc) -join [Environment]::NewLine
        if($outTail){Write-Host $outTail}
        if($errTail){Write-Warning $errTail}
        throw "Remote supervisor exited during background startup with code $($process.ExitCode). Check $SupervisorOutLog and $SupervisorErrLog."
    }

    $outText=(Get-FreshSupervisorTail $SupervisorOutLog $startupUtc) -join [Environment]::NewLine
    $errText=(Get-FreshSupervisorTail $SupervisorErrLog $startupUtc) -join [Environment]::NewLine
    $combined=$outText + [Environment]::NewLine + $errText

    if($combined -match 'status: learner=connected'){
        $startupState='learner connected and trainer registered'
        break
    }
    if($combined -match 'waiting for matching Training runtime before rollout'){
        $startupState='learner connected; waiting for matching Training runtime'
        break
    }
    if($combined -match 'private transport ready'){
        $startupState='private transport ready; trainer startup continuing in background'
        break
    }
    if($combined -match 'private transport failed to reach learner control'){
        $startupState='supervisor running, but learner private transport is not reachable yet'
        break
    }
    Start-Sleep -Milliseconds 250
}

Install-RemoteAutostart
Write-Host "[Bees remote] supervisor started in the background (PID $($process.Id))."
if($startupState -like '*not reachable*'){
    Write-Warning "[Bees remote] startup state: $startupState"
}else{
    Write-Host "[Bees remote] startup state: $startupState"
}
Write-Host "[Bees remote] logs: $SupervisorOutLog and $SupervisorErrLog"
Write-Host '[Bees remote] close this shell freely; use bees-remote-worker.cmd stop to stop the worker.'
exit 0
