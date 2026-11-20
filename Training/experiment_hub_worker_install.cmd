@echo off
setlocal DisableDelayedExpansion
echo [Experiment Hub] Preparing the Windows worker installer...
where powershell.exe >nul 2>&1
if errorlevel 1 (
    echo [Experiment Hub] Windows PowerShell is required.
    set "HUB_WORKER_EXIT=1"
    goto HUB_WORKER_DONE
)
set "HUB_WORKER_SELF=%~f0"
set "HUB_WORKER_SCRIPT=%TEMP%\experiment-hub-worker-%RANDOM%-%RANDOM%.ps1"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command ^
 "$raw=[IO.File]::ReadAllText($env:HUB_WORKER_SELF); $tag=[regex]::Match($raw,'(?m)^:__EXPERIMENT_HUB_POWERSHELL_PAYLOAD__\r?\n'); if(-not $tag.Success){throw 'Embedded worker payload is missing.'}; $payload=$raw.Substring($tag.Index+$tag.Length); $tokens=$null; $errors=$null; $null=[System.Management.Automation.Language.Parser]::ParseInput($payload,[ref]$tokens,[ref]$errors); if($errors.Count){foreach($item in $errors){[Console]::Error.WriteLine($item)};exit 2}; $bytes=([System.Text.UTF8Encoding]::new($false)).GetBytes($payload); $stream=[IO.File]::Open($env:HUB_WORKER_SCRIPT,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None); try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}"
if errorlevel 1 (
    echo [Experiment Hub] Installer extraction or PowerShell syntax validation failed.
    set "HUB_WORKER_EXIT=1"
    goto HUB_WORKER_DONE
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%HUB_WORKER_SCRIPT%" %*
set "HUB_WORKER_EXIT=%ERRORLEVEL%"
del /f /q "%HUB_WORKER_SCRIPT%" >nul 2>&1
:HUB_WORKER_DONE
if "%HUB_WORKER_EXIT%"=="223" exit /b 0
if not "%HUB_WORKER_EXIT%"=="0" echo [Experiment Hub] Installer failed, exit code %HUB_WORKER_EXIT%.
if "%HUB_WORKER_EXIT%"=="0" echo [Experiment Hub] Installer finished successfully.
echo [Experiment Hub] Review the result above. Press any key to close this window.
pause >nul
exit /b %HUB_WORKER_EXIT%
:__EXPERIMENT_HUB_POWERSHELL_PAYLOAD__
# One machine-local Hub agent for independent TinyBrains/Courier CPU collectors.
# Run from elevated PowerShell. Original Bees actor installers are unchanged.
[CmdletBinding()]
param(
    [ValidateSet('install','update','status')][string]$Command='install',
    [string]$Projects='',
    [string]$InstallRoot='C:\ProgramData\BeesExperimentHubWorker',
    [string]$MachineId='',
    [string]$HubTailnetIp='100.97.43.98',
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
# Download immutable private software from Halifax after tailnet enrollment.
# Initial installation never needs private-repository Git credentials.
function Install-HubRelease([string]$Component,[string]$Name) {
    if($null -eq $Enrollment){throw 'Worker bootstrap must complete before downloading software.'}
    $destination=Join-Path $SourceRoot $Name
    $headers=@{ 'x-hub-agent-token'=[string]$Enrollment.agent_token }
    $base="http://$($HubTailnetIp):7149"
    $manifest=Invoke-RestMethod -Uri "$base/api/hub/agent/releases?machine_id=$MachineId" -Headers $headers
    $entry=$manifest.components.PSObject.Properties[$Component].Value
    if($null -eq $entry -or $entry.activation_ready -ne $true){
        throw "Hub has not published an active $Component release."
    }
    $hash=[string]$entry.sha256
    $bytes=[long]$entry.bytes
    if([string]$entry.version -cnotmatch '^[0-9a-f]{40}$' -or
        $hash -cnotmatch '^[0-9a-f]{64}$' -or $bytes -le 0 -or $bytes -gt 536870912){
        throw 'Invalid approved software release manifest.'
    }
    # A rerun after a completed source extraction must not require cleanup.
    if(Test-Path -LiteralPath $destination){
        $marker=Join-Path $destination '.hub-release-version'
        if((Test-Path -LiteralPath $marker -PathType Leaf) -and
           ((Get-Content -LiteralPath $marker -Raw).Trim() -ceq [string]$entry.version)){
            Write-Host "Resuming $Component from the previously installed release."
            return $destination
        }
        throw "Refusing to overwrite unexpected worker source: $destination"
    }
    $staging=Join-Path $SourceRoot ('.'+$Name+'.staging-'+[guid]::NewGuid().ToString('N'))
    $archive=Join-Path $Private "$Component-source.tar"
    try {
        $bundle="$base/api/hub/agent/releases/bundle?machine_id=$MachineId&component=$Component&sha256=$hash"
        Invoke-WebRequest -UseBasicParsing -Uri $bundle -Headers $headers -OutFile $archive
        if((Get-Item -LiteralPath $archive).Length -ne $bytes -or
           (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash){
            throw "$Component release SHA-256 or length did not match Halifax."
        }
        $names=@(& tar.exe -tf $archive)
        Assert-Exit 'Inspect downloaded release paths'
        $details=@(& tar.exe -tvf $archive)
        Assert-Exit 'Inspect downloaded release types'
        if(-not $names.Count -or
            @($names | Where-Object { $_ -match '(^/|(^|/)\.\.(/|$)|\\|:)' }).Count -ne 0 -or
            @($details | Where-Object { $_ -match '^[lh]' }).Count -ne 0){
            throw 'Unsafe software release archive.'
        }
        # Keep incomplete releases invisible to subsequent installer attempts.
        New-Item -ItemType Directory -Path $staging -ErrorAction Stop | Out-Null
        & tar.exe -xf $archive -C $staging
        Assert-Exit "Extract $Component release"
        if($Component -eq 'worker' -and
            -not(Test-Path (Join-Path $staging 'Training\experiment_hub_agent.js'))){
            throw 'Published Hub worker release has no agent entrypoint.'
        }
        if($Component -eq 'tinybrains' -and
            -not(Test-Path (Join-Path $staging 'Ants-MAPOCA\pyproject.toml'))){
            throw 'Published TinyBrains release has no simulation project.'
        }
        if($Component -eq 'courier' -and
            -not(Test-Path (Join-Path $staging 'requirements.txt'))){
            throw 'Published Courier release has no dependency contract.'
        }
        [IO.File]::WriteAllText(
            (Join-Path $staging '.hub-release-version'),[string]$entry.version)
        Move-Item -LiteralPath $staging -Destination $destination -ErrorAction Stop
    } finally {
        if(Test-Path -LiteralPath $archive){Remove-Item -LiteralPath $archive -Force}
        if(Test-Path -LiteralPath $staging){
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
    return $destination
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
    # The self-contained CMD launcher provides its own path; double-clicking
    # the installer raises the normal Windows UAC prompt automatically.
    if(-not $env:HUB_WORKER_SELF -or
        -not(Test-Path -LiteralPath $env:HUB_WORKER_SELF -PathType Leaf)){
        throw 'Run the self-contained CMD worker installer.'
    }
    Write-Host 'Requesting administrator permission to install the native worker...'
    # Forward explicit installer options, including a custom installation root.
    # ShellExecute receives arguments as one quoted string; never evaluate
    # user-provided option values as PowerShell or CMD instructions.
    $forward=[System.Collections.Generic.List[string]]::new()
    foreach($key in $PSBoundParameters.Keys) {
        $value=$PSBoundParameters[$key]
        if($value -is [System.Management.Automation.SwitchParameter]) {
            if($value.IsPresent){$forward.Add("-$key")}
        }else{
            $text=[string]$value
            if($text -match '["\r\n]'){throw "Unsupported character in installer option -$key."}
            $forward.Add("-$key")
            $forward.Add('"'+$text+'"')
        }
    }
    $launch=@{FilePath=$env:HUB_WORKER_SELF;Verb='RunAs'}
    if($forward.Count){$launch.ArgumentList=$forward.ToArray()}
    Start-Process @launch | Out-Null
    # This is only the unelevated handoff. Let the elevated CMD keep its
    # own result visible; do not display a second completion prompt here.
    exit 223
}
if(-not [IO.Path]::IsPathRooted($InstallRoot) -or $InstallRoot.StartsWith('\\')){
    throw 'InstallRoot must be an absolute local disk directory.'
}
$InstallRoot=[IO.Path]::GetFullPath($InstallRoot)
$Private=Join-Path $InstallRoot 'Private'
$SourceRoot=Join-Path $InstallRoot 'Sources'
$NativeRoot=Join-Path $InstallRoot 'NativeState'
$ServiceState=Join-Path $InstallRoot 'ServiceState'
$CacheRoot=Join-Path $InstallRoot 'Cache'
$EnvFile=Join-Path $Private 'worker.env'

function Install-UpdateTask([string]$NodeExecutable,[string]$UpdaterScript,[string]$WorkerRoot) {
    # Supply executable and arguments as separate Task Scheduler fields:
    # schtasks.exe /TR loses embedded quotes around Program Files paths.
    $scheduler=New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $task=$scheduler.NewTask(0)
    $task.RegistrationInfo.Description='Authenticated Experiment Hub worker software updater'
    $task.Settings.Enabled=$true
    $task.Settings.StartWhenAvailable=$true
    $task.Settings.MultipleInstances=2
    $task.Settings.ExecutionTimeLimit='PT4H'
    $task.Principal.UserId='SYSTEM'
    $task.Principal.LogonType=5
    $task.Principal.RunLevel=1
    $trigger=$task.Triggers.Create(1)
    $trigger.StartBoundary=(Get-Date).AddMinutes(1).ToString('yyyy-MM-ddTHH:mm:ss')
    $trigger.Repetition.Interval='PT2M'
    $action=$task.Actions.Create(0)
    $action.Path=$NodeExecutable
    $action.Arguments='"'+$UpdaterScript+'" --root "'+$WorkerRoot+'"'
    $null=$scheduler.GetFolder('\').RegisterTaskDefinition(
        'Bees Experiment Hub Worker Updates',$task,6,'SYSTEM',$null,5)
}
if($Command -eq 'status'){
    Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if(Test-Path -LiteralPath $EnvFile){Write-Host "Private agent configuration: $EnvFile"}
    return
}
$existing=Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$hasEnrollment=Test-Path -LiteralPath $EnvFile -PathType Leaf
if($Command -eq 'install' -and $hasEnrollment -and $existing) {
    # Finish only the update task after a partially successful installation.
    # Preserve machine token, running service and any native job ownership.
    $configFile=Join-Path $Private 'agent-service.json'
    $updater=Join-Path $InstallRoot 'Update\experiment_hub_worker_updater.js'
    $serviceBinary=Join-Path $InstallRoot 'Binaries\Service\ExperimentHubWindowsService.exe'
    $service=Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
    if(-not $service -or
        -not ([string]$service.PathName).StartsWith(
            '"'+$serviceBinary+'" --service "',
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $configFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $updater -PathType Leaf)) {
        throw 'Existing worker installation is incomplete or belongs to another root. No changes were made.'
    }
    $configuration=Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
    $saved=@{}
    foreach($entry in (Get-Content -LiteralPath $EnvFile)){
        $pair=$entry.Split('=',2)
        if($pair.Count -eq 2){$saved[$pair[0]]=$pair[1]}
    }
    if($configuration.Name -ne $ServiceName -or
        $configuration.EnvironmentFile -ne $EnvFile -or
        $configuration.TrainingDirectory -ne (Join-Path $SourceRoot 'Bees-Assets\Training') -or
        $saved['EXPERIMENT_HUB_LOCAL_ROOT'] -ne $NativeRoot -or
        $saved['EXPERIMENT_HUB_AGENT_TOKEN'] -cnotmatch '^[0-9a-f]{64}$' -or
        -not (Test-Path -LiteralPath ([string]$configuration.NodePath) -PathType Leaf)) {
        throw 'Existing worker configuration does not match this root. No changes were made.'
    }
    Install-UpdateTask ([string]$configuration.NodePath) $updater $InstallRoot
    $existing.Refresh()
    Write-Host "Worker setup completed; enrollment preserved. Agent service: $($existing.Status)."
    Write-Host 'The software updater task is registered to run every two minutes.'
    return
}
if($Command -eq 'install' -and ($hasEnrollment -or $existing)){
    throw 'Existing worker identity is incomplete or belongs to another installation. No enrollment was overwritten.'
}
if($Command -eq 'update' -and -not $hasEnrollment){
    throw 'No installed Hub agent; run -Command install first.'
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
    $Projects=[string]$saved['EXPERIMENT_HUB_WORKER_PROJECTS']
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
    if($Projects){throw 'Install project software through Experiment Hub, not the base worker installer.'}
    Ensure-Winget 'node.exe' 'OpenJS.NodeJS.LTS'
    Ensure-Winget 'dotnet.exe' 'Microsoft.DotNet.SDK.8'
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
        Write-Host 'Tailscale is installed. Follow its one-time browser sign-in link.'
        & $tailscaleCli up
        Assert-Exit 'Start Tailscale sign-in'
        for($attempt=0;$attempt -lt 120 -and -not $connected;$attempt++){
            $ipLines=@(& $tailscaleCli ip -4 2>$null)
            if($ipLines.Count){
                try {
                    Assert-TailnetIP (([string]$ipLines[0]).Trim())
                    $connected=$true
                }catch { $connected=$false }
            }
            if(-not $connected){Start-Sleep -Seconds 2}
        }
        if(-not $connected){throw 'Tailscale login did not finish; worker not installed.'}
    }
}
# Complete the Tailscale browser login once; Halifax verifies this node
# belongs to the same Tailscale owner and returns machine-scoped credentials.
$Enrollment=$null
if($Command -eq 'install'){
    $stats=Get-CimInstance -ClassName Win32_ComputerSystem
    $payload=@{
        projects=@()
        cpus=[Environment]::ProcessorCount
        ram_mb=[int][Math]::Floor([double]$stats.TotalPhysicalMemory/1MB)
    } | ConvertTo-Json -Compress
    $Enrollment=Invoke-RestMethod -Method Post -ContentType 'application/json' `
        -Uri "http://$($HubTailnetIp):7149/api/hub/agent/bootstrap" -Body $payload
    $MachineId=[string]$Enrollment.machine_id
    if($MachineId -cnotmatch '^[a-z][a-z0-9_-]{0,47}$' -or
        [string]$Enrollment.agent_token -cnotmatch '^[0-9a-f]{64}$'){
        throw 'Invalid authenticated Hub enrollment response.'
    }
}
foreach($tool in @('node.exe','dotnet.exe')){
    $null=Tool $tool
}
$node=Tool 'node.exe'
$major=& $node -p 'parseInt(process.versions.node)'
Assert-Exit 'Node runtime inspection'
if([int]$major -lt 22){throw 'The Windows native Hub agent service requires Node.js 22 or newer.'}
foreach($dir in @($InstallRoot,$Private,$SourceRoot,$NativeRoot,$ServiceState,
    $CacheRoot)){
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
Private-Permissions $InstallRoot
Service-Permissions $NativeRoot
Service-Permissions $ServiceState
Service-Permissions $CacheRoot
# Python and uv are installed by the machine-local updater only when
# a specific TinyBrains or Courier simulation is requested.
$assets=if($Command -eq 'install'){Install-HubRelease 'worker' 'Bees-Assets'}
    else {Join-Path $SourceRoot 'Bees-Assets'}
$Training=Join-Path $assets 'Training'
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
    $token=[string]$Enrollment.agent_token

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
    if($Projects){Add-Line $lines 'EXPERIMENT_HUB_WORKER_PROJECTS' $Projects}
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
if($Start -or $Command -eq 'install'){
    $current=Get-Service -Name $ServiceName
    if($current.Status -eq 'Running'){
        throw 'Agent is already running; stop any active Hub jobs before a restart.'
    }
    Set-Service -Name $ServiceName -StartupType Automatic
    Start-Service -Name $ServiceName
}
# A SYSTEM scheduled task must outlive the native agent to activate an
# update. It cannot depend on the interactive operator's Git credentials.
$updateDir=Join-Path $InstallRoot 'Update'
New-Item -ItemType Directory -Path $updateDir -Force | Out-Null
$updater=Join-Path $updateDir 'experiment_hub_worker_updater.js'
Copy-Item -LiteralPath (Join-Path $Training 'experiment_hub_worker_updater.js') -Destination $updater -Force
& "$env:SystemRoot\System32\icacls.exe" $updateDir /grant 'NT AUTHORITY\SYSTEM:(OI)(CI)F' | Out-Null
Assert-Exit 'Set updater service permissions'
Install-UpdateTask $node $updater $InstallRoot
Write-Host "Hub worker installed. Service: $ServiceName (automatic startup when -Start was selected)."
Write-Host 'Simulation software is installed separately by Experiment Hub when required.'
Write-Host 'Automatically enrolled via Tailscale; Hub Machines will display native capabilities.'
