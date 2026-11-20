# One machine-local Hub agent for independent TinyBrains/Courier CPU collectors.
# Run from elevated PowerShell. Original Bees actor installers are unchanged.
[CmdletBinding()]
param(
    [ValidateSet('install','update','status')][string]$Command='install',
    [ValidateSet('tinybrains','courier','tinybrains,courier','courier,tinybrains')]
    [string]$Projects='tinybrains,courier',
    [string]$InstallRoot='C:\ProgramData\BeesExperimentHubWorker',
    [string]$MachineId='',
    [string]$HubSshTarget='',
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
function Exec-Git([string[]]$Args) {
    & (Tool 'git.exe') @Args
    Assert-Exit 'Git'
}
function Sync-Repo([string]$Name,[string]$Remote,[string]$Ref) {
    $dest=Join-Path $SourceRoot $Name
    if(-not (Test-Path -LiteralPath (Join-Path $dest '.git'))) {
        Exec-Git @('clone','--filter=blob:none','--depth=1','--branch',$Ref,$Remote,$dest)
    }else{
        $dirty=& (Tool 'git.exe') -C $dest status --porcelain
        Assert-Exit 'Git status'
        if($dirty){throw "Refusing to overwrite local changes at $dest"}
        $branchName=& (Tool 'git.exe') -C $dest branch --show-current
        Assert-Exit 'Git branch'
        if($branchName -ne $Ref){throw "Refusing unexpected branch at $dest"}
        Exec-Git @('-C',$dest,'pull','--ff-only','origin',$Ref)
    }
    return $dest
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
if($Command -eq 'update' -and $existing -and $existing.Status -eq 'Running'){
    throw 'A running Hub agent may own live jobs. Stop the jobs through the Hub before updating and restarting.'
}

if($Command -eq 'install'){
    Ensure-Winget 'git.exe' 'Git.Git'
    Ensure-Winget 'node.exe' 'OpenJS.NodeJS.LTS'
    Ensure-Winget 'dotnet.exe' 'Microsoft.DotNet.SDK.8'
    Ensure-Winget 'py.exe' 'Python.Python.3.11'
    Ensure-Winget 'tailscale.exe' 'Tailscale.Tailscale'
}
foreach($tool in @('git.exe','node.exe','dotnet.exe','ssh.exe','ssh-keygen.exe','ssh-keyscan.exe')){
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
$assets=Sync-Repo 'Bees-Assets' 'https://github.com/allStackGit/Bees-Assets.git' $Branch
$Training=Join-Path $assets 'Training'
$selected=$Projects.Split(',')
if('tinybrains' -in $selected){
    if(-not $TinyBrainsRoot){
        $tiny=Sync-Repo 'TinyBrains' 'https://github.com/allStackGit/TinyBrains.git' $TinyBrainsBranch
        $TinyBrainsRoot=Join-Path $tiny 'Ants-MAPOCA'
    }
    if(-not(Test-Path (Join-Path $TinyBrainsRoot 'pyproject.toml'))){throw 'TinyBrains project root missing.'}
    $tbEnv=Join-Path $VenvRoot 'tinybrains'
    & (Tool 'py.exe') -3.11 -m venv $tbEnv
    Assert-Exit 'Create TinyBrains virtual environment'
    $tbPython=Join-Path $tbEnv 'Scripts\python.exe'
    & $tbPython -m pip install --disable-pip-version-check --index-url https://download.pytorch.org/whl/cpu 'torch==2.6.0'
    Assert-Exit 'Install TinyBrains CPU PyTorch'
    & $tbPython -m pip install --disable-pip-version-check -e $TinyBrainsRoot
    Assert-Exit 'Install TinyBrains runtime'
    & $tbPython -B -c 'import ants_mapoca.remote_rollout'
    Assert-Exit 'TinyBrains remote protocol import'
}
if('courier' -in $selected){
    if(-not $CourierRoot){
        $CourierRoot=Sync-Repo 'Basic-Training' 'https://github.com/allStackGit/Basic-Training.git' $CourierBranch
    }
    if(-not(Test-Path (Join-Path $CourierRoot 'requirements.txt'))){throw 'Courier project root missing.'}
    $courierEnv=Join-Path $VenvRoot 'courier'
    & (Tool 'py.exe') -3.11 -m venv $courierEnv
    Assert-Exit 'Create Courier virtual environment'
    $courierPython=Join-Path $courierEnv 'Scripts\python.exe'
    & $courierPython -m pip install --disable-pip-version-check -r (Join-Path $CourierRoot 'requirements.txt')
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
    if($HubSshTarget -notmatch '^[A-Za-z_][A-Za-z0-9_.-]*@100\.'){
        throw 'Supply -HubSshTarget user@Halifax-Tailscale-IP.'
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

    $sshKey=Join-Path $Private 'ssh_ed25519'
    $hosts=Join-Path $Private 'known_hosts'
    if(-not(Test-Path $sshKey)){
        & (Tool 'ssh-keygen.exe') -q -t ed25519 -N '' -f $sshKey
        Assert-Exit 'Generate worker-only private SSH identity'
    }
    if(-not(Test-Path $hosts)){
        $peer=$HubSshTarget.Split('@')[-1]
        $candidate=Join-Path $Private 'known_hosts.candidate'
        & (Tool 'ssh-keyscan.exe') -T 10 -t ed25519 $peer 2>$null |
            Set-Content -LiteralPath $candidate -Encoding ascii
        if(-not (Test-Path $candidate) -or (Get-Item $candidate).Length -eq 0){
            throw 'Halifax SSH host key unavailable; enable tailnet SSH access before continuing.'
        }
        Write-Host 'Compare fingerprint with ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub on Halifax:'
        & (Tool 'ssh-keygen.exe') -lf $candidate
        if((Read-Host 'Type VERIFIED after matching the fingerprint') -cne 'VERIFIED'){
            Remove-Item -LiteralPath $candidate -Force; throw 'Host key verification was not approved.'
        }
        Move-Item -LiteralPath $candidate -Destination $hosts
    }
    Write-Host 'Authorizing this worker public SSH key on Halifax (may request an SSH password):'
    Get-Content -LiteralPath "$sshKey.pub" |
        & (Tool 'ssh.exe') -o "UserKnownHostsFile=$hosts" -o StrictHostKeyChecking=yes `
            $HubSshTarget 'umask 077; mkdir -p ~/.ssh; cat >> ~/.ssh/authorized_keys'
    Assert-Exit 'Authorize worker SSH key'
    & (Tool 'ssh.exe') -o BatchMode=yes -o StrictHostKeyChecking=yes `
        -o "UserKnownHostsFile=$hosts" -i $sshKey $HubSshTarget true
    Assert-Exit 'Check unattended SSH authentication'

    $tailscale=if(Test-Path "$env:ProgramFiles\Tailscale\tailscale.exe"){
        "$env:ProgramFiles\Tailscale\tailscale.exe"
    }else{Tool 'tailscale.exe'}
    $tailnetIp=(& $tailscale ip -4 | Select-Object -First 1).Trim()
    if($tailnetIp -notmatch '^100\.'){
        throw 'Sign Exeter into Tailscale and rerun this installer.'
    }
    $lines=[System.Collections.Generic.List[string]]::new()
    Add-Line $lines 'EXPERIMENT_HUB_MACHINE_ID' $MachineId
    Add-Line $lines 'EXPERIMENT_HUB_AGENT_TOKEN' $token
    Add-Line $lines 'EXPERIMENT_HUB_ENABLE_EXECUTION' '1'
    Add-Line $lines 'EXPERIMENT_HUB_LOCAL_ADAPTERS' (Join-Path $Training 'experiment_hub_worker_adapters.js')
    Add-Line $lines 'EXPERIMENT_HUB_LOCAL_ROOT' $NativeRoot
    Add-Line $lines 'EXPERIMENT_HUB_WINDOWS_HELPER' $owner
    Add-Line $lines 'EXPERIMENT_HUB_WINDOWS_HELPER_SHA256' $ownerSha
    Add-Line $lines 'EXPERIMENT_HUB_URL' 'http://127.0.0.1:17149'
    Add-Line $lines 'EXPERIMENT_HUB_SSH_TARGET' $HubSshTarget
    Add-Line $lines 'EXPERIMENT_HUB_SSH_KEY' $sshKey
    Add-Line $lines 'EXPERIMENT_HUB_SSH_KNOWN_HOSTS' $hosts
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
    foreach($line in $lines){
        if($line.StartsWith('EXPERIMENT_HUB_WINDOWS_HELPER_SHA256=')){
            $new.Add("EXPERIMENT_HUB_WINDOWS_HELPER_SHA256=$ownerSha")
        }else{$new.Add($line)}
    }
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
