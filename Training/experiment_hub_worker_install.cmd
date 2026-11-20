@echo off
setlocal DisableDelayedExpansion
echo [Experiment Hub] Preparing the Windows worker installer...
where powershell.exe >nul 2>&1
if errorlevel 1 (
    echo [Experiment Hub] Windows PowerShell is required.
    exit /b 1
)
set "HUB_WORKER_SELF=%~f0"
set "HUB_WORKER_SCRIPT=%TEMP%\experiment-hub-worker-%RANDOM%-%RANDOM%.ps1"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command ^
 "$raw=[IO.File]::ReadAllText($env:HUB_WORKER_SELF); $tag=[regex]::Match($raw,'(?m)^:__EXPERIMENT_HUB_POWERSHELL_PAYLOAD__\r?\n'); if(-not $tag.Success){throw 'Embedded worker payload is missing.'}; $payload=$raw.Substring($tag.Index+$tag.Length); $tokens=$null; $errors=$null; $null=[System.Management.Automation.Language.Parser]::ParseInput($payload,[ref]$tokens,[ref]$errors); if($errors.Count){foreach($item in $errors){[Console]::Error.WriteLine($item)};exit 2}; $bytes=([System.Text.UTF8Encoding]::new($false)).GetBytes($payload); $stream=[IO.File]::Open($env:HUB_WORKER_SCRIPT,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None); try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}"
if errorlevel 1 (
    echo [Experiment Hub] Installer extraction or PowerShell syntax validation failed.
    exit /b 1
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%HUB_WORKER_SCRIPT%" %*
set "HUB_WORKER_EXIT=%ERRORLEVEL%"
del /f /q "%HUB_WORKER_SCRIPT%" >nul 2>&1
if not "%HUB_WORKER_EXIT%"=="0" echo [Experiment Hub] Installer ended with error %HUB_WORKER_EXIT%.
exit /b %HUB_WORKER_EXIT%
:__EXPERIMENT_HUB_POWERSHELL_PAYLOAD__
# One machine-local Hub agent for independent TinyBrains/Courier CPU collectors.
# Run from elevated PowerShell. Original Bees actor installers are unchanged.
[CmdletBinding()]
param(
    [ValidateSet('install','update','status')][string]$Command='install',
    [ValidateSet('tinybrains','courier','tinybrains,courier','courier,tinybrains')]
    [string]$Projects='tinybrains,courier',
    [string]$InstallRoot='C:\ProgramData\BeesExperimentHubWorker',
    [string]$MachineId='',
    [string]$HubTailnetIp='',
    [string]$TinyBrainsKeyFile='',
    [string]$CourierKeyFile='',
    [string]$TinyBrainsRoot='',
    [string]$CourierRoot='',
    [switch]$Start
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$Branch='feat/hub-resource-experiments-20261110'
$TinyBrainsBranch='feat/linux-tinybrains-runtime'
$CourierBranch='feat/hub-independent-courier-ppo-20261119'
$ServiceName='Bees-ExperimentHub-agent'

function Tool([string]$Name) {
    $command=Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if(-not $command){throw "Required tool $Name not found in PATH. Reopen PowerShell after prerequisite installation."}
    return $command.Source
}
function Assert-Exit([string]$Task) {
    if($LASTEXITCODE -ne 0){throw "$Task failed (exit $LASTEXITCODE)."}
}
function Assert-TailnetIP([string]$Value) {
    if($Value -notmatch '^([0-9]{1,3}\.){3}[0-9]{1,3}$') {
        throw 'Hub address must be a literal Tailscale IPv4 in 100.64.0.0/10.'
    }
    $parts=@($Value.Split('.') | ForEach-Object { [int]$_ })
    if($parts[0] -ne 100 -or $parts[1] -lt 64 -or $parts[1] -gt 127 -or
        @($parts | Where-Object { $_ -gt 255 }).Count -ne 0 -or
        ($parts -join '.') -cne $Value) {
        throw 'Hub address must be a literal Tailscale IPv4 in 100.64.0.0/10.'
    }
}
function Exec-Git([string[]]$GitArguments) {
    & (Tool 'git.exe') @GitArguments
    Assert-Exit 'Git'
}
function Sync-Repo([string]$Name,[string]$Remote,[string]$Ref,[string]$SparseDirectory='') {
    $dest=Join-Path $SourceRoot $Name
    if(-not (Test-Path -LiteralPath (Join-Path $dest '.git'))) {
        Exec-Git @('clone','--filter=blob:none','--depth=1','--sparse','--branch',$Ref,$Remote,$dest)
        if($SparseDirectory){
            Exec-Git @('-C',$dest,'sparse-checkout','set',$SparseDirectory)
        }
    }else{
        $dirty=& (Tool 'git.exe') -C $dest status --porcelain --untracked-files=no
        Assert-Exit 'Git status'
        if($dirty){throw "Refusing to overwrite local changes at $dest"}
        $branchName=& (Tool 'git.exe') -C $dest branch --show-current
        Assert-Exit 'Git branch'
        if($branchName -ne $Ref){throw "Refusing unexpected branch at $dest"}
        Exec-Git @('-C',$dest,'pull','--ff-only','origin',$Ref)
    }
    return $dest
}
# Pin the official simulator and baseline code; never substitute a Python
# recreation of game mechanics for the published TinyBrains engine.
function Install-TinyBrainsEngine([string]$Project) {
    $release='https://github.com/Tiny-Brains/cli/releases/download/v0.5.0'
    $archive='tinybrains-x86_64-pc-windows-msvc.zip'
    $downloads=Join-Path $Project 'downloads'
    $cli=Join-Path $Project 'tools\tinybrains-0.5.0\tinybrains.exe'
    New-Item -ItemType Directory -Path $downloads -Force | Out-Null
    $bundle=Join-Path $downloads $archive
    if(-not(Test-Path -LiteralPath $bundle)){
        Invoke-WebRequest "$release/$archive" -OutFile $bundle
    }
    $checks=Join-Path $downloads 'SHA256SUMS'
    Invoke-WebRequest "$release/SHA256SUMS" -OutFile $checks
    $line=@(Get-Content -LiteralPath $checks | Where-Object {
        $_ -match " $([regex]::Escape($archive))$"
    })
    if($line.Count -ne 1){throw 'Official TinyBrains CLI checksum is absent or ambiguous.'}
    $expected=($line[0] -split '\s+')[0].ToLowerInvariant()
    $actual=(Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
    if($actual -cne $expected){throw 'Official TinyBrains CLI archive hash mismatch.'}
    if(-not(Test-Path -LiteralPath $cli)){
        $directory=Split-Path -Parent $cli
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        Expand-Archive -LiteralPath $bundle -DestinationPath $directory
    }
    if(-not(Test-Path -LiteralPath $cli)){throw 'Verified TinyBrains CLI was not installed.'}
    $pinned=@{
        'ants'='54edfab12469b432041658ec80bcdf5931ae23e4'
        'ants-starter'='a9610c693c0559286e4f5a96efec511b024bd573'
    }
    foreach($name in $pinned.Keys){
        $upstream=Join-Path $Project "upstream\$name"
        if(-not(Test-Path -LiteralPath (Join-Path $upstream '.git'))){
            Exec-Git @('clone',"https://github.com/Tiny-Brains/$name.git",$upstream)
            Exec-Git @('-C',$upstream,'checkout','--detach',$pinned[$name])
        }
        $revision=(& (Tool 'git.exe') -C $upstream rev-parse HEAD).Trim()
        Assert-Exit 'Inspect pinned TinyBrains dependency'
        if($revision -cne $pinned[$name]){
            throw "Refusing unexpected official TinyBrains upstream $name version."
        }
    }
}
function Copy-Private([string]$From,[string]$To) {
    if(-not (Test-Path -LiteralPath $From -PathType Leaf)) {
        throw "The approved local secret source does not exist: $From"
    }
    Copy-Item -LiteralPath $From -Destination $To -ErrorAction Stop
}
function Add-Line([System.Collections.Generic.List[string]]$Lines,[string]$Name,[string]$Value) {
    if([string]::IsNullOrWhiteSpace($Value) -or $Value -match '[\r\n"\x00]' ) {
        throw "Invalid environment value: $Name"
    }
    $Lines.Add("$Name=$Value")
}
function Ensure-Winget([string]$ToolName,[string]$Package) {
    if(Get-Command $ToolName -ErrorAction SilentlyContinue){return}
    $winget=Tool 'winget.exe'
    & $winget install --id $Package -e --scope machine --accept-package-agreements --accept-source-agreements
    Assert-Exit "Install $Package"
    $env:PATH=[Environment]::GetEnvironmentVariable('Path','Machine')+';'+
        [Environment]::GetEnvironmentVariable('Path','User')
}
function Private-Permissions([string]$Root) {
    $owner=[System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    & "$env:SystemRoot\System32\icacls.exe" $Root /inheritance:r /grant:r `
        "$($owner):(OI)(CI)F" 'BUILTIN\Administrators:(OI)(CI)F' `
        'NT AUTHORITY\SYSTEM:(OI)(CI)F' 'NT AUTHORITY\LOCAL SERVICE:(OI)(CI)RX' | Out-Null
    Assert-Exit 'Set restricted worker directory permissions'
}
function Service-Permissions([string]$Root) {
    & "$env:SystemRoot\System32\icacls.exe" $Root /grant `
        'NT AUTHORITY\LOCAL SERVICE:(OI)(CI)M' /T | Out-Null
    Assert-Exit 'Grant native agent state ownership'
}

$principal=New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'Use an elevated PowerShell console for installer and native service provisioning.'
}
if(-not [IO.Path]::IsPathRooted($InstallRoot) -or $InstallRoot.StartsWith('\\')){
    throw 'InstallRoot must be an absolute local disk directory.'
}
$InstallRoot=[IO.Path]::GetFullPath($InstallRoot)
$Private=Join-Path $InstallRoot 'Private'
$SourceRoot=Join-Path $InstallRoot 'Sources'
$VenvRoot=Join-Path $InstallRoot 'Venvs'
$NativeRoot=Join-Path $InstallRoot 'NativeState'
$ServiceState=Join-Path $InstallRoot 'ServiceState'
$CacheRoot=Join-Path $InstallRoot 'Cache'
$EnvFile=Join-Path $Private 'worker.env'

if($Command -eq 'status'){
    Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if(Test-Path -LiteralPath $EnvFile){Write-Host "Private agent configuration: $EnvFile"}
    return
}
if($Command -eq 'install' -and (Test-Path -LiteralPath $EnvFile)){
    throw 'Existing enrollment would be overwritten. Use -Command update instead.'
}
if($Command -eq 'update' -and -not (Test-Path -LiteralPath $EnvFile)){
    throw 'No installed Hub agent; run -Command install first.'
}
$existing=Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if($Command -eq 'install' -and $existing){
    throw 'A Hub agent service already exists. Do not enroll a second agent on the same machine.'
}
if($Command -eq 'update' -and $existing -and $existing.Status -eq 'Running'){
    throw 'A running Hub agent may own live jobs. Stop the jobs through the Hub before updating and restarting.'
}
if($Command -eq 'update'){
    # Reuse the installed project contract rather than guessing from
    # installer defaults or moving an active machine's approved source.
    $saved=@{}
    foreach($entry in (Get-Content -LiteralPath $EnvFile)){
        $pair=$entry.Split('=',2)
        if($pair.Count -eq 2){$saved[$pair[0]]=$pair[1]}
    }
    if(-not $saved.ContainsKey('EXPERIMENT_HUB_WORKER_PROJECTS')){
        throw 'Installed agent has no frozen project selection.'
    }
    $Projects=$saved['EXPERIMENT_HUB_WORKER_PROJECTS']
    if(-not $TinyBrainsRoot){
        $TinyBrainsRoot=$saved['EXPERIMENT_HUB_TINYBRAINS_PROJECT_ROOT']
    }
    if(-not $CourierRoot){
        $CourierRoot=$saved['EXPERIMENT_HUB_COURIER_PROJECT_ROOT']
    }
    if(-not $HubTailnetIp){
        $oldUrl=$saved['EXPERIMENT_HUB_URL']
        if($oldUrl -match '^http://(100\.[0-9.]+):7149$'){
            $HubTailnetIp=$Matches[1]
        }else{
            throw 'Supply -HubTailnetIp to migrate the saved SSH-forwarded worker endpoint.'
        }
    }
}
Assert-TailnetIP $HubTailnetIp

if($Command -eq 'install'){
    # Catch missing enrollment and shared rollout-key inputs before costly
    # package installation, runtime setup or native helper compilation.
    if($MachineId -cnotmatch '^[a-z][a-z0-9_-]{0,47}$'){
        throw 'Enroll this worker in Hub Machines and supply its exact -MachineId.'
    }
    foreach($selection in $Projects.Split(',')){
        $source=if($selection -eq 'tinybrains'){$TinyBrainsKeyFile}else{$CourierKeyFile}
        if(-not $source -or -not(Test-Path -LiteralPath $source -PathType Leaf)){
            throw "Supply an existing private $selection rollout key file before installing."
        }
    }
}

if($Command -eq 'install'){
    Ensure-Winget 'git.exe' 'Git.Git'
    Ensure-Winget 'node.exe' 'OpenJS.NodeJS.LTS'
    Ensure-Winget 'dotnet.exe' 'Microsoft.DotNet.SDK.8'
    Ensure-Winget 'py.exe' 'Python.Python.3.11'
    $python311=Get-Command py.exe -ErrorAction SilentlyContinue
    if($python311){
        & $python311.Source -3.11 --version 2>$null
        if($LASTEXITCODE -ne 0){
            & (Tool 'winget.exe') install --id Python.Python.3.11 -e --scope machine --accept-package-agreements --accept-source-agreements
            Assert-Exit 'Install pinned Python 3.11'
        }
    }
    if(-not (Test-Path "$env:ProgramFiles\Tailscale\tailscale.exe")){
        Ensure-Winget 'tailscale.exe' 'Tailscale.Tailscale'
    }
    $tailscaleCli=if(Test-Path "$env:ProgramFiles\Tailscale\tailscale.exe"){
        "$env:ProgramFiles\Tailscale\tailscale.exe"
    }else{Tool 'tailscale.exe'}
    $connected=$false
    try{
        $ipLines=@(& $tailscaleCli ip -4)
        if($ipLines.Count){Assert-TailnetIP (([string]$ipLines[0]).Trim());$connected=$true}
    }catch{$connected=$false}
    if(-not $connected){
        Write-Host 'Tailscale is installed. Complete its one-time sign-in to the Halifax tailnet.'
        & $tailscaleCli up
        Assert-Exit 'Tailscale sign-in'
        $ipLines=@(& $tailscaleCli ip -4)
        if(-not $ipLines.Count){
            throw 'Open Tailscale, sign in to the Halifax tailnet, then rerun this installer.'
        }
        Assert-TailnetIP (([string]$ipLines[0]).Trim())
    }
}
foreach($tool in @('git.exe','node.exe','dotnet.exe')){
    $null=Tool $tool
}
$node=Tool 'node.exe'
$major=& $node -p 'parseInt(process.versions.node)'
Assert-Exit 'Node runtime inspection'
if([int]$major -lt 22){throw 'The Windows native Hub agent service requires Node.js 22 or newer.'}
& (Tool 'py.exe') -3.11 --version
Assert-Exit 'Python 3.11 runtime inspection'

foreach($dir in @($InstallRoot,$Private,$SourceRoot,$VenvRoot,$NativeRoot,$ServiceState,
    $CacheRoot,(Join-Path $CacheRoot 'tinybrains'),(Join-Path $CacheRoot 'courier'))){
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
Private-Permissions $InstallRoot
Service-Permissions $NativeRoot
Service-Permissions $ServiceState
Service-Permissions $CacheRoot
# The Windows service must not depend on a Python installed only inside the
# interactive administrator's user profile. Keep a pinned interpreter and
# virtual environments under this protected machine-local installation root.
$managedTools=Join-Path $InstallRoot 'Tooling'
$uvSource=Join-Path $managedTools 'uv'
$pythonRoot=Join-Path $managedTools 'Python'
New-Item -ItemType Directory -Path $uvSource,$pythonRoot -Force | Out-Null
if(-not (Test-Path (Join-Path $uvSource 'uv\__main__.py'))){
    & (Tool 'py.exe') -3.11 -m pip install --disable-pip-version-check --target $uvSource 'uv==0.13.6'
    Assert-Exit 'Install pinned uv bootstrap'
}
$env:PYTHONPATH=$uvSource
& (Tool 'py.exe') -3.11 -m uv python install 3.11.18 --install-dir $pythonRoot --no-bin
Assert-Exit 'Install pinned Python for LocalService'
$managedCandidates=@(Get-ChildItem -LiteralPath $pythonRoot -Directory |
    Where-Object { $_.Name -like 'cpython-3.11*windows-x86_64-none' })
if($managedCandidates.Count -ne 1){throw 'Expected exactly one pinned Python 3.11 runtime.'}
$managedPython=Join-Path $managedCandidates[0].FullName 'python.exe'
if(-not(Test-Path $managedPython)){throw 'Pinned machine-local Python is missing.'}
$assets=Sync-Repo 'Bees-Assets' 'https://github.com/allStackGit/Bees-Assets.git' $Branch 'Training'
$Training=Join-Path $assets 'Training'
$selected=$Projects.Split(',')
if('tinybrains' -in $selected){
    if(-not $TinyBrainsRoot){
        $tiny=Sync-Repo 'TinyBrains' 'https://github.com/allStackGit/TinyBrains.git' $TinyBrainsBranch 'Ants-MAPOCA'
        $TinyBrainsRoot=Join-Path $tiny 'Ants-MAPOCA'
    }
    if(-not(Test-Path (Join-Path $TinyBrainsRoot 'pyproject.toml'))){throw 'TinyBrains project root missing.'}
    # The official CLI writes verified engine artifacts to ROOT/cache.
    # Do not grant the service write access to source or Python packages.
    $engineCache=Join-Path $TinyBrainsRoot 'cache'
    New-Item -ItemType Directory -Path $engineCache -Force | Out-Null
    Service-Permissions $engineCache
    Install-TinyBrainsEngine $TinyBrainsRoot
    $tbEnv=Join-Path $VenvRoot 'tinybrains'
    & (Tool 'py.exe') -3.11 -m uv venv --python $managedPython $tbEnv
    Assert-Exit 'Create TinyBrains isolated Python 3.11 environment'
    $tbPython=Join-Path $tbEnv 'Scripts\python.exe'
    & (Tool 'py.exe') -3.11 -m uv pip install --python $tbPython `
        --index-url https://download.pytorch.org/whl/cpu 'torch==2.6.0'
    Assert-Exit 'Install TinyBrains CPU PyTorch'
    & (Tool 'py.exe') -3.11 -m uv pip install --python $tbPython `
        -r (Join-Path $TinyBrainsRoot 'requirements-lock.txt')
    Assert-Exit 'Install pinned TinyBrains Python packages'
    & (Tool 'py.exe') -3.11 -m uv pip install --python $tbPython --no-deps `
        -e (Join-Path $TinyBrainsRoot 'upstream\ants\baselines') -e $TinyBrainsRoot
    Assert-Exit 'Install official TinyBrains baseline and runtime'
    Push-Location $TinyBrainsRoot
    try{
        & $tbPython -B -c 'import ants_mapoca.remote_rollout; from ants_mapoca.env import Engine'
        Assert-Exit 'TinyBrains official engine and remote protocol imports'
    }finally{Pop-Location}
}
if('courier' -in $selected){
    if(-not $CourierRoot){
        $CourierRoot=Sync-Repo 'Basic-Training' 'https://github.com/allStackGit/Basic-Training.git' $CourierBranch
    }
    if(-not(Test-Path (Join-Path $CourierRoot 'requirements.txt'))){throw 'Courier project root missing.'}
    $courierEnv=Join-Path $VenvRoot 'courier'
    & (Tool 'py.exe') -3.11 -m uv venv --python $managedPython $courierEnv
    Assert-Exit 'Create Courier isolated Python 3.11 environment'
    $courierPython=Join-Path $courierEnv 'Scripts\python.exe'
    & (Tool 'py.exe') -3.11 -m uv pip install --python $courierPython `
        -r (Join-Path $CourierRoot 'requirements.txt')
    Assert-Exit 'Install Courier CPU dependencies'
    Push-Location $CourierRoot
    try {
        & $courierPython -B -c 'import courier_remote_lineage'
        Assert-Exit 'Courier remote protocol import'
    }finally{Pop-Location}
}

# Build both reviewed native owners locally; never download unpinned
# helper binaries or allow Hub job parameters to pick their executable.
$ownerOut=Join-Path $InstallRoot 'Binaries\Owner'
$serviceOut=Join-Path $InstallRoot 'Binaries\Service'
foreach($dir in @($ownerOut,$serviceOut)){New-Item -ItemType Directory -Path $dir -Force | Out-Null}
& (Tool 'dotnet.exe') publish (Join-Path $Training 'experiment_hub_windows_owner\ExperimentHubWindowsOwner.csproj') -c Release -r win-x64 --self-contained true -o $ownerOut
Assert-Exit 'Build pinned Windows native resource owner'
& (Tool 'dotnet.exe') publish (Join-Path $Training 'experiment_hub_windows_service\ExperimentHubWindowsService.csproj') -c Release -r win-x64 --self-contained true -o $serviceOut
Assert-Exit 'Build Windows agent service launcher'
$owner=Join-Path $ownerOut 'ExperimentHubWindowsOwner.exe'
$launcher=Join-Path $serviceOut 'ExperimentHubWindowsService.exe'
$ownerSha=(Get-FileHash -LiteralPath $owner -Algorithm SHA256).Hash.ToLowerInvariant()

if($Command -eq 'install'){
    if($MachineId -cnotmatch '^[a-z][a-z0-9_-]{0,47}$'){
        throw 'Supply the exact enrolled Hub machine ID using -MachineId.'
    }
    if(('tinybrains' -in $selected) -and -not $TinyBrainsKeyFile){
        throw 'Supply an existing TinyBrains rollout key identical to the Halifax learner key.'
    }
    if(('courier' -in $selected) -and -not $CourierKeyFile){
        throw 'Supply an existing Courier HMAC key identical to the learner key.'
    }
    $secure=Read-Host 'One-time 64-character Hub enrollment token (not echoed)' -AsSecureString
    $ptr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try{$token=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)}
    finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)}
    if($token -cnotmatch '^[0-9a-f]{64}$'){throw 'Invalid token; enroll the machine in the Hub first.'}

    $tailscale=if(Test-Path "$env:ProgramFiles\Tailscale\tailscale.exe"){
        "$env:ProgramFiles\Tailscale\tailscale.exe"
    }else{Tool 'tailscale.exe'}
    $ipLines=@(& $tailscale ip -4 2>$null)
    $tailnetIp=if($ipLines.Count){([string]$ipLines[0]).Trim()}else{''}
    try{Assert-TailnetIP $tailnetIp}catch{throw 'Sign this worker into Tailscale before installing.'}
    $lines=[System.Collections.Generic.List[string]]::new()
    Add-Line $lines 'EXPERIMENT_HUB_MACHINE_ID' $MachineId
    Add-Line $lines 'EXPERIMENT_HUB_AGENT_TOKEN' $token
    Add-Line $lines 'EXPERIMENT_HUB_ENABLE_EXECUTION' '1'
    Add-Line $lines 'EXPERIMENT_HUB_LOCAL_ADAPTERS' (Join-Path $Training 'experiment_hub_worker_adapters.js')
    Add-Line $lines 'EXPERIMENT_HUB_LOCAL_ROOT' $NativeRoot
    Add-Line $lines 'EXPERIMENT_HUB_WINDOWS_HELPER' $owner
    Add-Line $lines 'EXPERIMENT_HUB_WINDOWS_HELPER_SHA256' $ownerSha
    Add-Line $lines 'EXPERIMENT_HUB_URL' "http://$($HubTailnetIp):7149"
    Add-Line $lines 'EXPERIMENT_HUB_TAILNET_IP' $tailnetIp
    Add-Line $lines 'EXPERIMENT_HUB_WORKER_PROJECTS' $Projects
    if('tinybrains' -in $selected){
        Copy-Private $TinyBrainsKeyFile (Join-Path $Private 'tinybrains-rollout.key')
        Add-Line $lines 'EXPERIMENT_HUB_TINYBRAINS_PROJECT_ROOT' ([IO.Path]::GetFullPath($TinyBrainsRoot))
        Add-Line $lines 'EXPERIMENT_HUB_TINYBRAINS_PYTHON' $tbPython
        Add-Line $lines 'EXPERIMENT_HUB_TINYBRAINS_CACHE' (Join-Path $CacheRoot 'tinybrains')
        Add-Line $lines 'EXPERIMENT_HUB_TINYBRAINS_ROLLOUT_KEY' (Join-Path $Private 'tinybrains-rollout.key')
        Add-Line $lines 'EXPERIMENT_HUB_TINYBRAINS_THREAD_LIMIT' ([string][Environment]::ProcessorCount)
    }
    if('courier' -in $selected){
        Copy-Private $CourierKeyFile (Join-Path $Private 'courier-rollout.key')
        Add-Line $lines 'EXPERIMENT_HUB_COURIER_PROJECT_ROOT' ([IO.Path]::GetFullPath($CourierRoot))
        Add-Line $lines 'EXPERIMENT_HUB_COURIER_PYTHON' $courierPython
        Add-Line $lines 'EXPERIMENT_HUB_COURIER_CACHE' (Join-Path $CacheRoot 'courier')
        Add-Line $lines 'EXPERIMENT_HUB_COURIER_ROLLOUT_KEY' (Join-Path $Private 'courier-rollout.key')
    }
    $lines | Set-Content -LiteralPath $EnvFile -Encoding ascii
}else{
    $lines=Get-Content -LiteralPath $EnvFile
    $new=[System.Collections.Generic.List[string]]::new()
    $hadUrl=$false
    foreach($line in $lines){
        if($line.StartsWith('EXPERIMENT_HUB_SSH_')){continue}
        if($line.StartsWith('EXPERIMENT_HUB_URL=')){
            $new.Add("EXPERIMENT_HUB_URL=http://$($HubTailnetIp):7149")
            $hadUrl=$true
        }elseif($line.StartsWith('EXPERIMENT_HUB_WINDOWS_HELPER_SHA256=')){
            $new.Add("EXPERIMENT_HUB_WINDOWS_HELPER_SHA256=$ownerSha")
        }else{$new.Add($line)}
    }
    if(-not $hadUrl){$new.Add("EXPERIMENT_HUB_URL=http://$($HubTailnetIp):7149")}
    $new | Set-Content -LiteralPath $EnvFile -Encoding ascii
}
# Secrets stay readable only by this installer account, administrators,
# SYSTEM and the explicitly chosen Windows service account.
Private-Permissions $Private
Service-Permissions $NativeRoot
Service-Permissions $ServiceState
Service-Permissions $CacheRoot

if(-not $existing){
    & (Join-Path $Training 'experiment_hub_windows_service.ps1') -Role agent `
        -ServiceExecutable $launcher -ConfigurationFile (Join-Path $Private 'agent-service.json') `
        -StateDirectory $ServiceState -TrainingDirectory $Training -NodePath $node `
        -EnvironmentFile $EnvFile -Name $ServiceName -Install
    if(-not $?){throw 'Windows Hub service installation failed.'}
}
if($Start){
    $current=Get-Service -Name $ServiceName
    if($current.Status -eq 'Running'){
        throw 'Agent is already running; stop any active Hub jobs before a restart.'
    }
    Start-Service -Name $ServiceName
}
Write-Host "Hub worker installed. Service: $ServiceName (manual startup unless -Start)."
Write-Host 'Enabled collector ports: TinyBrains 12134/12135, Courier 13134/13135.'
Write-Host 'Review this machine in the Hub Machines tab before authorizing experiments.'
