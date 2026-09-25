param(
    [int]$Envs=0,
    [string]$InstallRoot='',
    [string]$TorchDevice=''
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

$DefaultInstallRoot='__BEES_INSTALL_ROOT__'
$DefaultTorchDevice='__BEES_TORCH_DEVICE__'
$TailnetLearner='__BEES_TAILNET_LEARNER__'
$TailnetBootstrapPort='__BEES_TAILNET_BOOTSTRAP_PORT__'
$GameplayPort='__BEES_GAMEPLAY_PORT__'
$ControlPort='__BEES_CONTROL_PORT__'
$BrokerPort='__BEES_BROKER_PORT__'
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
    @($GameplayPort,'GameplayPort'),
    @($ControlPort,'ControlPort'),
    @($BrokerPort,'BrokerPort'),
    @($BundledTailnetBridge,'TailnetBridgeFile'),
    @($TailnetBridgeSha256,'TailnetBridgeSha256'),
    @($BootstrapToken,'BootstrapToken')
)){ Require-GeneratedValue ([string]$item[0]) ([string]$item[1]) }

$TailnetBootstrapPort=[int]$TailnetBootstrapPort
$GameplayPort=[int]$GameplayPort
$ControlPort=[int]$ControlPort
$BrokerPort=[int]$BrokerPort
if(-not $InstallRoot){$InstallRoot=[Environment]::ExpandEnvironmentVariables($DefaultInstallRoot)}
if(-not $TorchDevice){$TorchDevice=$DefaultTorchDevice}
if($Envs -lt 0 -or $Envs -gt 64){throw 'Envs must be in 1-64 when specified.'}
foreach($port in @($TailnetBootstrapPort,$GameplayPort,$ControlPort,$BrokerPort)){
    if($port -lt 1 -or $port -gt 65535){throw 'Configured Bees ports must be in 1-65535.'}
}

function Resolve-Exe([string]$Name){
    $cmd=Get-Command $Name -ErrorAction SilentlyContinue
    if($null -eq $cmd){return $null}
    $cmd.Source
}

Write-Host '[Bees remote] Stage 1/5: preparing local worker files...'
$InstallRoot=[IO.Path]::GetFullPath($InstallRoot)
$RuntimeRoot=Join-Path $InstallRoot 'Runtime'
$SecretsRoot=Join-Path $InstallRoot 'Secrets'
$DownloadsRoot=Join-Path $InstallRoot 'Downloads'
$VenvRoot=Join-Path $InstallRoot '.venv'
$TailnetRoot=Join-Path $InstallRoot 'Tailnet'
$TailnetState=Join-Path $TailnetRoot 'State'
foreach($path in @($InstallRoot,$RuntimeRoot,$SecretsRoot,$DownloadsRoot,$TailnetRoot,$TailnetState)){
    $null=New-Item -ItemType Directory -Force -Path $path
}

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
    $launcher=Resolve-PythonLauncher
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
if($currentStamp -ne $requirementsHash){
    Write-Host 'Installing/updating remote worker Python dependencies...'
    & $venvPython -m pip install --upgrade pip
    if($LASTEXITCODE -ne 0){throw 'pip upgrade failed.'}
    & $venvPython -m pip install -r $requirements
    if($LASTEXITCODE -ne 0){throw 'Remote worker dependency installation failed.'}
    $requirementsHash | Set-Content -LiteralPath $requirementsStamp -NoNewline -Encoding ASCII
}

$worker=Join-Path $RuntimeRoot 'bees_managed_remote_worker.py'
$workerArgs=@(
    $worker,
    '--tailnet-bridge',$tailnetBridge,
    '--tailnet-state',$TailnetState,
    '--tailnet-hostname',$workerHostname,
    '--tailnet-target',$TailnetLearner,
    '--gameplay-port',[string]$GameplayPort,
    '--control-port',[string]$ControlPort,
    '--bootstrap-port',[string]$TailnetBootstrapPort,
    '--broker-port',[string]$BrokerPort,
    '--install-root',$InstallRoot,
    '--runtime-archive',$runtimeZip,
    '--bootstrap-token-file',$bootstrapTokenPath,
    '--worker-token-file',$workerToken,
    '--wan-token-file',$wanToken,
    '--torch-device',$TorchDevice
)
if($Envs -gt 0){$workerArgs+=@('--envs',[string]$Envs)}

Write-Host ''
Write-Host '[Bees remote] Stage 5/5: starting managed training worker...'
if($Envs -gt 0){
    Write-Host "Starting Bees remote worker with $Envs environments."
}else{
    Write-Host 'Starting Bees remote worker; environment count defaults to 4x available CPU threads (maximum 64).'
}
Write-Host 'Private transport, control, build updates, and WAN rollouts are automatic. Ctrl+C stops this worker.'
& $venvPython -u @workerArgs
exit $LASTEXITCODE
