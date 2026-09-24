param(
    [int]$Envs=0,
    [string]$InstallRoot='',
    [string]$TorchDevice=''
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

$DefaultLearner='__BEES_LEARNER__'
$DefaultSshPort='__BEES_SSH_PORT__'
$DefaultInstallRoot='__BEES_INSTALL_ROOT__'
$DefaultTorchDevice='__BEES_TORCH_DEVICE__'
$TailnetLearner='__BEES_TAILNET_LEARNER__'
$TailnetPort='__BEES_TAILNET_PORT__'
$TailnetLocalPort='__BEES_TAILNET_LOCAL_PORT__'
$TailnetBridgeBase64='__BEES_TAILNET_BRIDGE_B64__'
$TailnetBridgeSha256='__BEES_TAILNET_BRIDGE_SHA256__'
$RemoteRuntimePath='__BEES_RUNTIME_REMOTE_PATH__'
$RemoteWorkerTokenPath='__BEES_WORKER_TOKEN_REMOTE_PATH__'
$RemoteWanTokenPath='__BEES_WAN_TOKEN_REMOTE_PATH__'

function Require-GeneratedValue([string]$Value,[string]$Name){
    if($Value -match '^__BEES_'){
        throw "$Name is not configured. Copy a generated worker launcher from the learner's B:\Bees\Remote directory after running bees.ps1 start."
    }
}

foreach($item in @(
    @($DefaultLearner,'Learner'),
    @($DefaultSshPort,'SshPort'),
    @($DefaultInstallRoot,'InstallRoot'),
    @($DefaultTorchDevice,'TorchDevice'),
    @($TailnetLearner,'TailnetLearner'),
    @($TailnetPort,'TailnetPort'),
    @($TailnetLocalPort,'TailnetLocalPort'),
    @($TailnetBridgeBase64,'TailnetBridge'),
    @($TailnetBridgeSha256,'TailnetBridgeSha256'),
    @($RemoteRuntimePath,'RemoteRuntimePath'),
    @($RemoteWorkerTokenPath,'RemoteWorkerTokenPath'),
    @($RemoteWanTokenPath,'RemoteWanTokenPath')
)){ Require-GeneratedValue ([string]$item[0]) ([string]$item[1]) }

$Learner=$DefaultLearner
$SshPort=[int]$DefaultSshPort
$TailnetPort=[int]$TailnetPort
$TailnetLocalPort=[int]$TailnetLocalPort
if(-not $InstallRoot){$InstallRoot=[Environment]::ExpandEnvironmentVariables($DefaultInstallRoot)}
if(-not $TorchDevice){$TorchDevice=$DefaultTorchDevice}

if($Envs -lt 0 -or $Envs -gt 64){throw 'Envs must be in 1-64 when specified.'}
if($SshPort -lt 1 -or $SshPort -gt 65535 -or $TailnetPort -lt 1 -or $TailnetPort -gt 65535 -or $TailnetLocalPort -lt 1 -or $TailnetLocalPort -gt 65535){
    throw 'Configured SSH/tailnet ports must be in 1-65535.'
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
foreach($path in @($InstallRoot,$RuntimeRoot,$SecretsRoot,$DownloadsRoot,$TailnetRoot,$TailnetState)){
    $null=New-Item -ItemType Directory -Force -Path $path
}

$tailnetBridge=Join-Path $TailnetRoot 'bees-tailnet-bridge.exe'
$writeBridge=$true
if(Test-Path -LiteralPath $tailnetBridge){
    $existing=(Get-FileHash -LiteralPath $tailnetBridge -Algorithm SHA256).Hash.ToLowerInvariant()
    $writeBridge=$existing -ne $TailnetBridgeSha256
}
if($writeBridge){
    Write-Host '[Bees remote] extracting bundled tailnet runtime...'
    [IO.File]::WriteAllBytes($tailnetBridge,[Convert]::FromBase64String($TailnetBridgeBase64))
}
$actualBridgeSha=(Get-FileHash -LiteralPath $tailnetBridge -Algorithm SHA256).Hash.ToLowerInvariant()
if($actualBridgeSha -ne $TailnetBridgeSha256){ throw 'Bundled tailnet runtime failed SHA-256 verification.' }

$workerHostname=("bees-worker-" + $env:COMPUTERNAME.ToLowerInvariant())
Write-Host '[Bees remote] checking embedded tailnet identity.'
Write-Host '[Bees remote] on first use, open the Tailscale login URL printed below; no Tailscale installation is required.'
& $tailnetBridge auth --state $TailnetState --hostname $workerHostname
if($LASTEXITCODE -ne 0){ throw "Embedded tailnet authentication failed with exit code $LASTEXITCODE." }

$tailnetOut=Join-Path $TailnetRoot 'bridge.out.log'
$tailnetErr=Join-Path $TailnetRoot 'bridge.err.log'
$tailnetArgs=@(
    'forward',
    '--state',$TailnetState,
    '--hostname',$workerHostname,
    '--listen',("127.0.0.1:" + $TailnetLocalPort),
    '--target',($TailnetLearner + ':' + $TailnetPort)
)
$tailnetProcess=Start-Process -FilePath $tailnetBridge -ArgumentList $tailnetArgs -RedirectStandardOutput $tailnetOut -RedirectStandardError $tailnetErr -WindowStyle Hidden -PassThru

$workerExitCode=1
try {
    $ready=$false
    for($i=0;$i -lt 120;$i++){
        if($tailnetProcess.HasExited){ throw "Embedded tailnet bridge exited during startup. Check $tailnetErr" }
        try {
            $tcp=New-Object Net.Sockets.TcpClient
            $async=$tcp.BeginConnect('127.0.0.1',$TailnetLocalPort,$null,$null)
            if($async.AsyncWaitHandle.WaitOne(250) -and $tcp.Connected){
                $tcp.EndConnect($async); $tcp.Dispose(); $ready=$true; break
            }
            $tcp.Dispose()
        } catch {}
        Start-Sleep -Milliseconds 250
    }
    if(-not $ready){ throw "Embedded tailnet bridge did not open local port $TailnetLocalPort. Check $tailnetErr" }
    Write-Host ("[Bees remote] private tailnet path ready: 127.0.0.1:{0} -> {1}:{2}" -f $TailnetLocalPort,$TailnetLearner,$TailnetPort)

    $ssh=Resolve-Exe 'ssh'
    $scp=Resolve-Exe 'scp'
    if(-not $ssh -or -not $scp){
        try {
            Write-Host 'OpenSSH Client was not found. Attempting to install the Windows optional feature...'
            $null=Add-WindowsCapability -Online -Name 'OpenSSH.Client~~~~0.0.1.0' -ErrorAction Stop
            $ssh=Resolve-Exe 'ssh'
            $scp=Resolve-Exe 'scp'
            if(-not $ssh -and (Test-Path -LiteralPath "$env:WINDIR\System32\OpenSSH\ssh.exe")){$ssh="$env:WINDIR\System32\OpenSSH\ssh.exe"}
            if(-not $scp -and (Test-Path -LiteralPath "$env:WINDIR\System32\OpenSSH\scp.exe")){$scp="$env:WINDIR\System32\OpenSSH\scp.exe"}
        } catch {
            throw "OpenSSH Client is required and automatic installation failed. Run this launcher once as Administrator."
        }
    }
    if(-not $ssh -or -not $scp){throw 'OpenSSH Client installation completed but ssh/scp could not be located.'}

    $runtimeZip=Join-Path $DownloadsRoot 'bees-remote-runtime.zip'
    $workerToken=Join-Path $SecretsRoot 'training-worker.token'
    $wanToken=Join-Path $SecretsRoot 'wan.token'
    $scpArgs=@('-o','StrictHostKeyChecking=accept-new')
    if($SshPort -ne 22){$scpArgs+=@('-P',[string]$SshPort)}

    function Copy-Remote([string]$RemotePath,[string]$LocalPath){
        Write-Host "Fetching $RemotePath"
        & $scp @scpArgs ($Learner + ':' + $RemotePath) $LocalPath
        if($LASTEXITCODE -ne 0){throw "scp failed while fetching $RemotePath"}
    }

    Copy-Remote $RemoteRuntimePath $runtimeZip
    Copy-Remote $RemoteWorkerTokenPath $workerToken
    Copy-Remote $RemoteWanTokenPath $wanToken

    if(Test-Path -LiteralPath $RuntimeRoot){Remove-Item -LiteralPath $RuntimeRoot -Recurse -Force}
    $null=New-Item -ItemType Directory -Force -Path $RuntimeRoot
    Expand-Archive -LiteralPath $runtimeZip -DestinationPath $RuntimeRoot -Force

    function Test-Python310([string]$Exe,[string[]]$Prefix=@()){
        if(-not $Exe -or -not(Test-Path -LiteralPath $Exe)){return $false}
        & $Exe @Prefix -c "import sys; assert sys.version_info[:2] == (3,10)" *> $null
        $LASTEXITCODE -eq 0
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
            & $winget install --id Python.Python.3.10 -e --accept-package-agreements --accept-source-agreements --silent
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
        '--learner',$Learner,
        '--ssh-port',[string]$SshPort,
        '--install-root',$InstallRoot,
        '--worker-token-file',$workerToken,
        '--wan-token-file',$wanToken,
        '--torch-device',$TorchDevice
    )
    if($Envs -gt 0){$workerArgs+=@('--envs',[string]$Envs)}
    Write-Host ""
    if($Envs -gt 0){
        Write-Host "Starting Bees remote worker with $Envs environments."
    }else{
        Write-Host 'Starting Bees remote worker; environment count defaults to 4x available CPU threads (maximum 64).'
    }
    Write-Host 'The learner assigns the actor slot automatically. Leave this window open; Ctrl+C stops this remote worker.'
    & $venvPython @workerArgs
    $workerExitCode=$LASTEXITCODE
} finally {
    if($null -ne $tailnetProcess -and -not $tailnetProcess.HasExited){
        Stop-Process -Id $tailnetProcess.Id -Force -ErrorAction SilentlyContinue
        try{$tailnetProcess.WaitForExit(5000)}catch{}
    }
}
exit $workerExitCode
