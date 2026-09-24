param(
    [string]$Learner='',
    [int]$ActorId=-1,
    [int]$Envs=0,
    [int]$SshPort=0,
    [string]$InstallRoot='',
    [string]$TorchDevice=''
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

$DefaultLearner='__BEES_LEARNER__'
$DefaultActorId='__BEES_ACTOR_ID__'
$DefaultEnvs='__BEES_ENVS__'
$DefaultSshPort='__BEES_SSH_PORT__'
$DefaultInstallRoot='__BEES_INSTALL_ROOT__'
$DefaultTorchDevice='__BEES_TORCH_DEVICE__'
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
    @($DefaultActorId,'ActorId'),
    @($DefaultEnvs,'Envs'),
    @($DefaultSshPort,'SshPort'),
    @($DefaultInstallRoot,'InstallRoot'),
    @($DefaultTorchDevice,'TorchDevice'),
    @($RemoteRuntimePath,'RemoteRuntimePath'),
    @($RemoteWorkerTokenPath,'RemoteWorkerTokenPath'),
    @($RemoteWanTokenPath,'RemoteWanTokenPath')
)){ Require-GeneratedValue ([string]$item[0]) ([string]$item[1]) }

if(-not $Learner){$Learner=$DefaultLearner}
if($ActorId -lt 0){$ActorId=[int]$DefaultActorId}
if($Envs -le 0){$Envs=[int]$DefaultEnvs}
if($SshPort -le 0){$SshPort=[int]$DefaultSshPort}
if(-not $InstallRoot){$InstallRoot=[Environment]::ExpandEnvironmentVariables($DefaultInstallRoot)}
if(-not $TorchDevice){$TorchDevice=$DefaultTorchDevice}

if($ActorId -lt 0 -or $ActorId -gt 11){throw 'ActorId must be in 0-11.'}
if($Envs -lt 1 -or $Envs -gt 64){throw 'Envs must be in 1-64.'}
if($SshPort -lt 1 -or $SshPort -gt 65535){throw 'SshPort must be in 1-65535.'}

function Resolve-Exe([string]$Name){
    $cmd=Get-Command $Name -ErrorAction SilentlyContinue
    if($null -eq $cmd){return $null}
    $cmd.Source
}

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
        throw "OpenSSH Client is required and automatic installation failed. Run this launcher once as Administrator or install the Windows OpenSSH Client optional feature."
    }
}
if(-not $ssh -or -not $scp){throw 'OpenSSH Client installation completed but ssh/scp could not be located.'}

$InstallRoot=[IO.Path]::GetFullPath($InstallRoot)
$RuntimeRoot=Join-Path $InstallRoot 'Runtime'
$SecretsRoot=Join-Path $InstallRoot 'Secrets'
$DownloadsRoot=Join-Path $InstallRoot 'Downloads'
$VenvRoot=Join-Path $InstallRoot '.venv'
foreach($path in @($InstallRoot,$RuntimeRoot,$SecretsRoot,$DownloadsRoot)){
    $null=New-Item -ItemType Directory -Force -Path $path
}

$runtimeZip=Join-Path $DownloadsRoot 'bees-remote-runtime.zip'
$workerToken=Join-Path $SecretsRoot 'training-worker.token'
$wanToken=Join-Path $SecretsRoot 'wan.token'
$scpArgs=@('-o','StrictHostKeyChecking=accept-new')
if($SshPort -ne 22){$scpArgs+=@('-P',[string]$SshPort)}

function Copy-Remote([string]$RemotePath,[string]$LocalPath){
    Write-Host "Fetching $RemotePath"
    & $scp @scpArgs "${Learner}:$RemotePath" $LocalPath
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
Write-Host ""
Write-Host "Starting Bees remote actor $ActorId with $Envs environments via $Learner."
Write-Host 'Leave this window open. Ctrl+C stops this remote worker.'
& $venvPython $worker --learner $Learner --ssh-port $SshPort --actor-id $ActorId --envs $Envs --install-root $InstallRoot --worker-token-file $workerToken --wan-token-file $wanToken --torch-device $TorchDevice
exit $LASTEXITCODE
