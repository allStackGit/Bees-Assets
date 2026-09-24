param(
    [Parameter(Mandatory=$true,Position=0)]
    [ValidateSet('build','start','stop','status')]
    [string]$Command,
    [switch]$FullGame,
    [switch]$Force,
    [string[]]$EnvArg,
    [switch]$Once,
    [ValidateRange(1,60)][int]$RefreshSeconds=2,
    [switch]$Server
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

$AssetsRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$BeesRoot=[IO.Path]::GetFullPath((Split-Path -Parent $AssetsRoot))
$BuildsRoot=Join-Path $BeesRoot 'Builds'
$SecretsRoot=Join-Path $BeesRoot 'Secrets'
$RuntimeRoot=Join-Path $BeesRoot 'Runtime'
$LogsRoot=Join-Path $BeesRoot 'Logs'
$TrainingRoot=Join-Path $BeesRoot 'Training'
$RemoteRoot=Join-Path $BeesRoot 'Remote'
$ServerRoot=Join-Path $AssetsRoot 'BeesServer~'
$ConfigPath=Join-Path $AssetsRoot 'Training\bees.cluster.json'
$RemoteBootstrapTemplate=Join-Path $AssetsRoot 'Training\bees_remote_bootstrap.ps1'
$RemoteLinuxBootstrapTemplate=Join-Path $AssetsRoot 'Training\bees_remote_bootstrap.sh'
$RemoteRequirementsPath=Join-Path $AssetsRoot 'Training\bees_remote_requirements.txt'
$LatestReleasePath=Join-Path $BuildsRoot 'latest-training-release.json'
$WorkerTokenPath=Join-Path $SecretsRoot 'training-worker.token'
$AdminTokenPath=Join-Path $SecretsRoot 'training-admin.token'
$WanTokenPath=Join-Path $SecretsRoot 'wan.token'
$ServerPidPath=Join-Path $RuntimeRoot 'bees-server.pid'
$CentralAgentPidPath=Join-Path $RuntimeRoot 'central-training-agent.pid'
$CentralAgentStatePath=Join-Path $RuntimeRoot 'central-training-agent.json'

function Ensure-Directory([string]$Path){ $null=New-Item -ItemType Directory -Force -Path $Path }

function Get-ClusterConfig {
    if(-not(Test-Path -LiteralPath $ConfigPath)){
        throw "Tracked training configuration is missing: $ConfigPath"
    }
    Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
}

function Resolve-CommandPath([string]$Name){
    $x=Get-Command $Name -ErrorAction SilentlyContinue
    if($null -eq $x){ throw "Required executable '$Name' was not found on PATH." }
    $x.Source
}

function Resolve-UnityEditor($Config){
    if($env:BEES_UNITY_EDITOR -and (Test-Path -LiteralPath $env:BEES_UNITY_EDITOR)){ return [IO.Path]::GetFullPath($env:BEES_UNITY_EDITOR) }
    if($Config.unityEditor -and (Test-Path -LiteralPath ([string]$Config.unityEditor))){ return [IO.Path]::GetFullPath([string]$Config.unityEditor) }
    $hub='C:\Program Files\Unity\Hub\Editor'
    if(Test-Path -LiteralPath $hub){
        $candidate=Get-ChildItem -LiteralPath $hub -Directory | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'Editor\Unity.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if($candidate){ return [IO.Path]::GetFullPath($candidate) }
    }
    throw "Unity Editor was not found. Set BEES_UNITY_EDITOR or unityEditor in $ConfigPath."
}

function Resolve-Python($Config){ if($Config.python){ Resolve-CommandPath ([string]$Config.python) } else { Resolve-CommandPath 'python' } }
function Resolve-Node($Config){ if($Config.node){ Resolve-CommandPath ([string]$Config.node) } else { Resolve-CommandPath 'node' } }
function Resolve-Npm { Resolve-CommandPath 'npm' }

function Invoke-Checked([string]$Exe,[string[]]$Args,[string]$WorkingDirectory=$AssetsRoot){
    Push-Location $WorkingDirectory
    try {
        & $Exe @Args
        if($LASTEXITCODE -ne 0){ throw "$Exe exited with code $LASTEXITCODE." }
    } finally { Pop-Location }
}

function Reset-BuildDirectory([string]$Path){
    if(Test-Path -LiteralPath $Path){
        $notEmpty=@(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue).Count -gt 0
        if($notEmpty -and -not $Force){ throw "Build directory is not empty: $Path. Use -Force to replace today's build." }
        if($Force){ Remove-Item -LiteralPath $Path -Recurse -Force }
    }
    Ensure-Directory $Path
}

function Invoke-UnityBuild([string]$Unity,[string]$Method,[string]$Output,[string]$LogName){
    $logRoot=Join-Path $LogsRoot 'Build'; Ensure-Directory $logRoot
    $args=@('-batchmode','-quit','-projectPath',$BeesRoot,'-executeMethod',$Method,'-beesOutput',$Output,'-logFile',(Join-Path $logRoot $LogName))
    Write-Host "Unity: $Method -> $Output"
    Invoke-Checked $Unity $args $BeesRoot
}

function Package-Build([string]$Python,[string]$Source,[string]$Archive,[string]$Entrypoint){
    Ensure-Directory (Split-Path -Parent $Archive)
    if(Test-Path -LiteralPath $Archive){ Remove-Item -LiteralPath $Archive -Force }
    Invoke-Checked $Python @((Join-Path $AssetsRoot 'Training\bees_package_training_build.py'),'--source',$Source,'--output',$Archive,'--entrypoint',$Entrypoint) $AssetsRoot
}

function Get-GitShortSha {
    $git=Resolve-CommandPath 'git'; Push-Location $AssetsRoot
    try {
        $sha=(& $git rev-parse --short=12 HEAD).Trim()
        if($LASTEXITCODE -ne 0 -or -not $sha){ throw 'git rev-parse failed.' }
        $sha
    } finally { Pop-Location }
}

function Invoke-Build {
    $config=Get-ClusterConfig; $unity=Resolve-UnityEditor $config; $python=Resolve-Python $config
    Ensure-Directory $BuildsRoot
    $date=Get-Date -Format 'yyyy-MM-dd'; $time=Get-Date -Format 'HHmmss'; $sha=Get-GitShortSha; $buildId="$date-$time-$sha"
    $win=Join-Path $BuildsRoot "$date RL Windows"; $linux=Join-Path $BuildsRoot "$date RL Linux"; $game=Join-Path $BuildsRoot "$date Full Game Windows"
    Reset-BuildDirectory $win; Reset-BuildDirectory $linux; if($FullGame){ Reset-BuildDirectory $game }
    Invoke-UnityBuild $unity 'BeesCommandLineBuild.BuildWindowsRl' $win "$date-rl-windows.log"
    Invoke-UnityBuild $unity 'BeesCommandLineBuild.BuildLinuxRl' $linux "$date-rl-linux.log"
    if($FullGame){ Invoke-UnityBuild $unity 'BeesCommandLineBuild.BuildWindowsFullGame' $game "$date-full-game-windows.log" }

    $packageRoot=Join-Path (Join-Path $BuildsRoot 'Packages') $buildId
    if(Test-Path -LiteralPath $packageRoot){ if(-not $Force){ throw "Package directory exists: $packageRoot. Use -Force." }; Remove-Item -LiteralPath $packageRoot -Recurse -Force }
    Ensure-Directory $packageRoot
    $winZip=Join-Path $packageRoot 'rl-windows.zip'; $linuxZip=Join-Path $packageRoot 'rl-linux.zip'
    Package-Build $python $win $winZip 'Bees RL Training.exe'
    Package-Build $python $linux $linuxZip 'Bees RL Training.x86_64'
    $artifacts=@(
        [pscustomobject]@{role='dedicated';platform='WindowsPlayer';folder=$win;archive=$winZip;entrypoint='Bees RL Training.exe'},
        [pscustomobject]@{role='dedicated';platform='LinuxPlayer';folder=$linux;archive=$linuxZip;entrypoint='Bees RL Training.x86_64'}
    )
    if($FullGame){
        $gameZip=Join-Path $packageRoot 'full-game-windows.zip'; Package-Build $python $game $gameZip 'Bees.exe'
        $artifacts+=[pscustomobject]@{role='full-game';platform='WindowsPlayer';folder=$game;archive=$gameZip;entrypoint='Bees.exe'}
    }
    [pscustomobject]@{schema_version=1;build_id=$buildId;source_commit=$sha;created_utc=[DateTime]::UtcNow.ToString('o');artifacts=$artifacts} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $LatestReleasePath -Encoding UTF8
    Write-Host ""; Write-Host "Build complete: $buildId"; $artifacts | Format-Table role,platform,folder -AutoSize
}

function New-SecureToken {
    $bytes=New-Object byte[] 32; $rng=[Security.Cryptography.RandomNumberGenerator]::Create()
    try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}
    ([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant()
}

function Ensure-TokenFile([string]$Path){
    Ensure-Directory (Split-Path -Parent $Path)
    if(-not(Test-Path -LiteralPath $Path)){ New-SecureToken | Set-Content -LiteralPath $Path -NoNewline -Encoding ASCII; Write-Host "Created token: $Path" }
    (Get-Content -LiteralPath $Path -Raw).Trim()
}

function Invoke-ControlGet([string]$Url,[string]$Token){ Invoke-RestMethod -Method Get -Uri $Url -Headers @{Authorization="Bearer $Token"} -TimeoutSec 5 }
function Invoke-ControlPost([string]$Url,[string]$Token,$Body){ Invoke-RestMethod -Method Post -Uri $Url -Headers @{Authorization="Bearer $Token"} -ContentType 'application/json' -Body ($Body|ConvertTo-Json -Depth 10 -Compress) -TimeoutSec 30 }
function Test-Control([string]$Base,[string]$Token){ try{$null=Invoke-ControlGet "$Base/v1/status" $Token;$true}catch{$false} }

function Start-BeesServerIfNeeded($Config,[string]$WorkerToken,[string]$AdminToken){
    $base=[string]$Config.controlUrl
    if(Test-Control $base $AdminToken){ return }
    $probeHost=if(([string]$Config.controlHost) -eq '0.0.0.0'){'127.0.0.1'}else{[string]$Config.controlHost}
    $controlPortOpen=Test-NetConnection -ComputerName $probeHost -Port ([int]$Config.controlPort) -InformationLevel Quiet -WarningAction SilentlyContinue
    if($controlPortOpen){ throw "Training-control port $($Config.controlPort) is already in use but did not accept this admin token. Stop/reconfigure the existing server before starting another." }
    if(-not $env:BEES_TLS_KEY_PATH -or -not $env:BEES_TLS_CERT_PATH){ throw 'BeesServer is offline. Set BEES_TLS_KEY_PATH and BEES_TLS_CERT_PATH once on this machine.' }
    $node=Resolve-Node $Config; $npm=Resolve-Npm
    if(-not(Test-Path -LiteralPath (Join-Path $ServerRoot 'node_modules'))){ Write-Host 'Installing BeesServer dependencies...'; Invoke-Checked $npm @('ci') $ServerRoot }
    Ensure-Directory (Join-Path $LogsRoot 'Server'); Ensure-Directory (Join-Path $TrainingRoot 'Control'); Ensure-Directory $RuntimeRoot
    $serverLog=Join-Path $LogsRoot 'Server\bees-server.log'
    $env:BEES_TRAINING_CONTROL_ENABLED='1'; $env:BEES_TRAINING_CONTROL_TOKEN=$WorkerToken; $env:BEES_TRAINING_CONTROL_ADMIN_TOKEN=$AdminToken
    $env:BEES_TRAINING_CONTROL_HOST=[string]$Config.controlHost; $env:BEES_TRAINING_CONTROL_PORT=[string]$Config.controlPort
    $env:BEES_TRAINING_CONTROL_STATE=Join-Path $TrainingRoot 'Control\state.json'; $env:BEES_TRAINING_ARTIFACT_ROOT=Join-Path $TrainingRoot 'Control\Artifacts'
    Push-Location $ServerRoot
    try {
        $output=@(& $node (Join-Path $ServerRoot 'start-server.js') '--background' "--log=$serverLog" 2>&1)
        if($LASTEXITCODE -ne 0){ throw "BeesServer launcher failed: $($output -join [Environment]::NewLine)" }
        $joined=$output -join [Environment]::NewLine; Write-Host $joined
        if($joined -match 'PID\s+(\d+)'){ $Matches[1] | Set-Content -LiteralPath $ServerPidPath -NoNewline }
    } finally { Pop-Location }
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    while([DateTime]::UtcNow -lt $deadline){ if(Test-Control $base $AdminToken){ return }; Start-Sleep -Milliseconds 500 }
    throw "Training control did not become reachable at $base. Check $serverLog."
}

function Get-LatestRelease {
    if(-not(Test-Path -LiteralPath $LatestReleasePath)){ throw "No release exists. Run '.\Assets\bees.ps1 build' first." }
    Get-Content -LiteralPath $LatestReleasePath -Raw | ConvertFrom-Json
}

function Publish-Release($Config,[string]$AdminToken,$Release){
    foreach($a in @($Release.artifacts)){
        if(-not(Test-Path -LiteralPath ([string]$a.archive)){ throw "Release artifact is missing: $($a.archive)" }
        $body=@{role=[string]$a.role;platform=[string]$a.platform;build_id=[string]$Release.build_id;archive_path=[string]$a.archive;entrypoint=[string]$a.entrypoint}
        $null=Invoke-ControlPost "$($Config.controlUrl)/v1/admin/artifact" $AdminToken $body
    }
}

function Quote-Arg([string]$Value){ if($Value -notmatch '[\s"]'){return $Value}; '"' + ($Value.Replace('"','\"')) + '"' }

function Get-StringSha256([string]$Value){
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$bytes=[Text.Encoding]::UTF8.GetBytes($Value);([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
}

function Start-CentralAgentIfNeeded($Config,[string]$Python,[string]$Unity){
    Ensure-Directory $RuntimeRoot; Ensure-Directory (Join-Path $LogsRoot 'Training'); Ensure-Directory (Join-Path $BeesRoot 'ManagedBuilds\central-learner')
    $outLog=Join-Path $LogsRoot 'Training\central-agent.out.log'; $errLog=Join-Path $LogsRoot 'Training\central-agent.err.log'
    $agent=Join-Path $AssetsRoot 'Training\bees_training_worker_agent.py'; $service=Join-Path $AssetsRoot 'Training\bees_continual_elastic_wan_service.py'
    $telemetry=Join-Path $TrainingRoot 'Telemetry'; $models=Join-Path $TrainingRoot 'Models'; Ensure-Directory $telemetry; Ensure-Directory $models
    $args=@($agent,'--server-url',[string]$Config.controlUrl,'--token-file',$WorkerTokenPath,'--trainer-id','central-learner','--role','dedicated','--platform','WindowsPlayer','--install-root',(Join-Path $BeesRoot 'ManagedBuilds\central-learner'),'--',$Python,$service,"--root=$TrainingRoot","--assets-root=$AssetsRoot",'--training-env={env}',"--telemetry-quarantine=$telemetry","--model-distribution-root=$models",'--game-build-version={build_id}',"--unity-editor=$Unity","--unity-project-root=$BeesRoot","--generation-steps=$($Config.generationSteps)","--num-envs=$($Config.numLocalEnvs)",'--platform=WindowsPlayer',"--bees-wan-actors=$($Config.maxRemoteActors)","--bees-wan-min-actors=$($Config.minRemoteActors)","--bees-wan-broker-port=$($Config.brokerPort)","--bees-wan-auth-token-file=$WanTokenPath")
    $argString=($args|ForEach-Object{Quote-Arg ([string]$_)}) -join ' '
    $commandHash=Get-StringSha256 ($Python + [Environment]::NewLine + $argString)
    if(Test-Path -LiteralPath $CentralAgentStatePath){
        try{$existing=Get-Content -LiteralPath $CentralAgentStatePath -Raw|ConvertFrom-Json}catch{$existing=$null}
        if($null -ne $existing -and $existing.pid -and (Get-Process -Id ([int]$existing.pid) -ErrorAction SilentlyContinue)){
            if(([string]$existing.command_hash) -eq $commandHash){ return }
            Write-Host 'Central training configuration changed; restarting the managed central agent.'
            Stop-ProcessTree ([int]$existing.pid)
        }
    } elseif(Test-Path -LiteralPath $CentralAgentPidPath) {
        $legacyPid=0
        [void][int]::TryParse((Get-Content -LiteralPath $CentralAgentPidPath -Raw).Trim(),[ref]$legacyPid)
        if($legacyPid -gt 0 -and (Get-Process -Id $legacyPid -ErrorAction SilentlyContinue)){
            Write-Host 'Restarting the existing central agent under unified command management.'
            Stop-ProcessTree $legacyPid
        }
    }
    Remove-Item -LiteralPath $CentralAgentPidPath -Force -ErrorAction SilentlyContinue
    $p=Start-Process -FilePath $Python -ArgumentList $argString -WorkingDirectory $AssetsRoot -RedirectStandardOutput $outLog -RedirectStandardError $errLog -WindowStyle Hidden -PassThru
    $p.Id | Set-Content -LiteralPath $CentralAgentPidPath -NoNewline
    [pscustomobject]@{pid=$p.Id;command_hash=$commandHash;started_utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content -LiteralPath $CentralAgentStatePath -Encoding UTF8
    Write-Host "Central training agent started with PID $($p.Id)."
}

function Get-EnvironmentArgs($Config){ if($null -ne $EnvArg -and $EnvArg.Count -gt 0){return @($EnvArg)}; if($null -eq $Config.environmentArgs){return @()}; @($Config.environmentArgs|ForEach-Object{[string]$_}) }

function Convert-ToScpPath([string]$Path){
    $value=([IO.Path]::GetFullPath($Path)).Replace('\','/')
    if($value -match '^[A-Za-z]:/'){ return "/$value" }
    $value
}
function Escape-SingleQuoted([string]$Value){ $Value.Replace("'","''") }
function Escape-BashDoubleQuoted([string]$Value){
    if($Value -notmatch '^[A-Za-z0-9_@.:/%~+\-]+$'){
        throw "Remote Linux launcher value contains unsupported shell characters: $Value"
    }
    $Value
}

function Get-RemoteSshTarget($Config){
    if($Config.remoteSshTarget -and ([string]$Config.remoteSshTarget).Trim()){ return ([string]$Config.remoteSshTarget).Trim() }
    "$env:USERNAME@$env:COMPUTERNAME"
}

function Prepare-RemoteBootstrap($Config){
    if(-not(Test-Path -LiteralPath $RemoteBootstrapTemplate)){ throw "Remote Windows bootstrap template is missing: $RemoteBootstrapTemplate" }
    if(-not(Test-Path -LiteralPath $RemoteLinuxBootstrapTemplate)){ throw "Remote Linux bootstrap template is missing: $RemoteLinuxBootstrapTemplate" }
    if(-not(Test-Path -LiteralPath $RemoteRequirementsPath)){ throw "Remote requirements file is missing: $RemoteRequirementsPath" }

    $maxActors=[int]$Config.maxRemoteActors
    if($maxActors -lt 1 -or $maxActors -gt 12){ throw 'maxRemoteActors must be in 1-12 for generated remote launchers.' }
    $defaultEnvs=if($Config.remoteDefaultEnvs){[int]$Config.remoteDefaultEnvs}else{32}
    if($defaultEnvs -lt 1 -or $defaultEnvs -gt 64){ throw 'remoteDefaultEnvs must be in 1-64.' }
    $sshPort=if($Config.remoteSshPort){[int]$Config.remoteSshPort}else{22}
    if($sshPort -lt 1 -or $sshPort -gt 65535){ throw 'remoteSshPort must be in 1-65535.' }
    $installRoot=if($Config.remoteInstallRoot){[string]$Config.remoteInstallRoot}else{'%LOCALAPPDATA%\BeesTraining'}
    $linuxInstallRoot=if($Config.remoteLinuxInstallRoot){[string]$Config.remoteLinuxInstallRoot}else{'.local/share/bees-training'}
    $torchDevice=if($Config.remoteTorchDevice){[string]$Config.remoteTorchDevice}else{'cpu'}
    $learner=Get-RemoteSshTarget $Config

    Ensure-Directory $RemoteRoot
    Ensure-Directory $RuntimeRoot
    $staging=Join-Path $RuntimeRoot 'remote-runtime-staging'
    if(Test-Path -LiteralPath $staging){Remove-Item -LiteralPath $staging -Recurse -Force}
    Ensure-Directory $staging
    try {
        Get-ChildItem -Path (Join-Path $AssetsRoot 'Training\*.py') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $staging }
        Copy-Item -LiteralPath $RemoteRequirementsPath -Destination (Join-Path $staging 'bees_remote_requirements.txt')
        $runtimeZip=Join-Path $RemoteRoot 'bees-remote-runtime.zip'
        if(Test-Path -LiteralPath $runtimeZip){Remove-Item -LiteralPath $runtimeZip -Force}
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $runtimeZip -CompressionLevel Optimal
    } finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }

    $runtimeRemote=Convert-ToScpPath (Join-Path $RemoteRoot 'bees-remote-runtime.zip')
    $workerTokenRemote=Convert-ToScpPath $WorkerTokenPath
    $wanTokenRemote=Convert-ToScpPath $WanTokenPath
    $windowsTemplate=Get-Content -LiteralPath $RemoteBootstrapTemplate -Raw
    $linuxTemplate=Get-Content -LiteralPath $RemoteLinuxBootstrapTemplate -Raw
    $utf8NoBom=New-Object Text.UTF8Encoding($false)
    for($actorId=0;$actorId -lt $maxActors;$actorId++){
        $windowsBody=$windowsTemplate
        $windowsReplacements=@{
            '__BEES_LEARNER__'=(Escape-SingleQuoted $learner)
            '__BEES_ACTOR_ID__'=[string]$actorId
            '__BEES_ENVS__'=[string]$defaultEnvs
            '__BEES_SSH_PORT__'=[string]$sshPort
            '__BEES_INSTALL_ROOT__'=(Escape-SingleQuoted $installRoot)
            '__BEES_TORCH_DEVICE__'=(Escape-SingleQuoted $torchDevice)
            '__BEES_RUNTIME_REMOTE_PATH__'=(Escape-SingleQuoted $runtimeRemote)
            '__BEES_WORKER_TOKEN_REMOTE_PATH__'=(Escape-SingleQuoted $workerTokenRemote)
            '__BEES_WAN_TOKEN_REMOTE_PATH__'=(Escape-SingleQuoted $wanTokenRemote)
        }
        foreach($key in $windowsReplacements.Keys){$windowsBody=$windowsBody.Replace($key,[string]$windowsReplacements[$key])}
        $windowsOutput=Join-Path $RemoteRoot "bees-remote-worker-$actorId.ps1"
        [IO.File]::WriteAllText($windowsOutput,$windowsBody,$utf8NoBom)

        $linuxBody=$linuxTemplate
        $linuxReplacements=@{
            '__BEES_LEARNER__'=(Escape-BashDoubleQuoted $learner)
            '__BEES_ACTOR_ID__'=[string]$actorId
            '__BEES_ENVS__'=[string]$defaultEnvs
            '__BEES_SSH_PORT__'=[string]$sshPort
            '__BEES_LINUX_INSTALL_ROOT__'=(Escape-BashDoubleQuoted $linuxInstallRoot)
            '__BEES_TORCH_DEVICE__'=(Escape-BashDoubleQuoted $torchDevice)
            '__BEES_RUNTIME_REMOTE_PATH__'=(Escape-BashDoubleQuoted $runtimeRemote)
            '__BEES_WORKER_TOKEN_REMOTE_PATH__'=(Escape-BashDoubleQuoted $workerTokenRemote)
            '__BEES_WAN_TOKEN_REMOTE_PATH__'=(Escape-BashDoubleQuoted $wanTokenRemote)
        }
        foreach($key in $linuxReplacements.Keys){$linuxBody=$linuxBody.Replace($key,[string]$linuxReplacements[$key])}
        $linuxBody=$linuxBody.Replace("`r`n","`n")
        $linuxOutput=Join-Path $RemoteRoot "bees-remote-worker-$actorId.sh"
        [IO.File]::WriteAllText($linuxOutput,$linuxBody,$utf8NoBom)
    }
    Write-Host "Remote launchers prepared in $RemoteRoot (actor slots 0-$($maxActors-1), default $defaultEnvs envs each)."
    Write-Host "Windows: copy bees-remote-worker-N.ps1 and run it."
    Write-Host "Linux:   copy bees-remote-worker-N.sh and run 'bash bees-remote-worker-N.sh'."
    $sshd=Get-Service -Name 'sshd' -ErrorAction SilentlyContinue
    if($null -eq $sshd -or $sshd.Status -ne 'Running'){
        Write-Warning "Remote launchers require SSH access to this learner. Windows OpenSSH Server (sshd) is not currently running; install/start it or provide another reachable SSH endpoint for remoteSshTarget."
    }
}

function Invoke-Start {
    $config=Get-ClusterConfig; $python=Resolve-Python $config; $unity=Resolve-UnityEditor $config
    $worker=Ensure-TokenFile $WorkerTokenPath; $admin=Ensure-TokenFile $AdminTokenPath; $null=Ensure-TokenFile $WanTokenPath
    Prepare-RemoteBootstrap $config
    $release=Get-LatestRelease
    Start-BeesServerIfNeeded $config $worker $admin; Publish-Release $config $admin $release; Start-CentralAgentIfNeeded $config $python $unity
    $envArgs=Get-EnvironmentArgs $config
    $desired=Invoke-ControlPost "$($config.controlUrl)/v1/admin/state" $admin @{training_enabled=$true;canonical_build_id=[string]$release.build_id;environment_args=$envArgs}
    Write-Host "Training requested: build=$($desired.canonical_build_id) revision=$($desired.revision)"
    Write-Host "Environment arguments: $(if($envArgs.Count){$envArgs -join ' '}else{'(none; defaults)'})"
    Start-Sleep -Seconds 1; Show-Status $config $admin $true
}

function Stop-ProcessTree([int]$Id){ if($Id -gt 0 -and (Get-Process -Id $Id -ErrorAction SilentlyContinue)){ & taskkill /PID $Id /T /F *> $null } }

function Invoke-Stop {
    $config=Get-ClusterConfig; $admin=Ensure-TokenFile $AdminTokenPath
    if(Test-Control ([string]$config.controlUrl) $admin){
        $desired=Invoke-ControlPost "$($config.controlUrl)/v1/admin/state" $admin @{training_enabled=$false}; Write-Host "Training stop requested at revision $($desired.revision)."
        $deadline=[DateTime]::UtcNow.AddSeconds(30)
        while([DateTime]::UtcNow -lt $deadline){
            $s=Invoke-ControlGet "$($config.controlUrl)/v1/status" $admin
            $running=@($s.trainers|Where-Object{-not $_.stale -and $_.role -eq 'dedicated' -and $_.process_state -ne 'stopped'})
            if($running.Count -eq 0){break}; Start-Sleep -Milliseconds 500
        }
    } else { Write-Warning 'Training control is offline; dedicated workers should fail closed after lease expiry.' }
    if($Server -and (Test-Path -LiteralPath $ServerPidPath)){
        $id=0; [void][int]::TryParse((Get-Content -LiteralPath $ServerPidPath -Raw).Trim(),[ref]$id); Stop-ProcessTree $id
        Remove-Item -LiteralPath $ServerPidPath -Force -ErrorAction SilentlyContinue; Write-Host 'BeesServer stopped.'
    }
}

function Get-LocalLearnerStats {
    $elo=$null; $step=$null; $reward=$null
    foreach($root in @((Join-Path $LogsRoot 'Training'),(Join-Path $TrainingRoot 'trainer-results'))){
        if(-not(Test-Path -LiteralPath $root)){continue}
        foreach($file in @(Get-ChildItem -LiteralPath $root -Filter '*.log' -File -Recurse -ErrorAction SilentlyContinue)){
            foreach($line in @(Get-Content -LiteralPath $file.FullName -Tail 1000 -ErrorAction SilentlyContinue)){
                if($line -match '(?i)\bELO\b[^-0-9]*(-?\d+(?:\.\d+)?)'){$elo=[double]$Matches[1]}
                if($line -match '(?i)\bStep\s*[:=]\s*(\d+)'){$step=[long]$Matches[1]}
                if($line -match '(?i)Mean Reward\s*[:=]\s*(-?\d+(?:\.\d+)?)'){$reward=[double]$Matches[1]}
            }
        }
    }
    [pscustomobject]@{ELO=$elo;Step=$step;MeanReward=$reward}
}

function Show-Status($Config,[string]$AdminToken,[bool]$Single){
    do {
        if(-not $Single){Clear-Host}
        Write-Host "Bees distributed learning status  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"; Write-Host ('='*78)
        try {
            $s=Invoke-ControlGet "$($Config.controlUrl)/v1/status" $AdminToken; $d=$s.desired
            Write-Host "Server: ONLINE   Training: $($d.training_enabled)   Revision: $($d.revision)"; Write-Host "Build:  $($d.canonical_build_id)"; Write-Host "Cluster: local_envs=$($Config.numLocalEnvs) max_remote=$($Config.maxRemoteActors) broker_port=$($Config.brokerPort)"
            $ea=@($d.environment_args); Write-Host "Env:    $(if($ea.Count){$ea -join ' '}else{'(none)'})"; Write-Host ''
            $rows=@($s.trainers|ForEach-Object{
                $m=$_.metrics
                [pscustomobject]@{Trainer=$_.trainer_id;Role=$_.role;Platform=$_.platform;State=if($_.stale){'STALE'}else{$_.process_state};Build=$_.build_id;Rev=$_.applied_revision;Age=('{0:N1}s'-f[double]$_.age_seconds);Timeout=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.timeout_pct}else{'-'};BWin=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.bee_win_pct}else{'-'};HWin=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.human_win_pct}else{'-'};Draw=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.draw_pct}else{'-'};Dur=if($m -and $m.window_episodes){'{0:N1}s'-f[double]$m.avg_duration_s}else{'-'};BeeHit=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.bee_hit_pct}else{'-'};HumanHit=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.human_hit_pct}else{'-'};Error=$_.last_error}
            })
            if($rows.Count){$rows|Format-Table Trainer,Role,Platform,State,Build,Rev,Age,Timeout,BWin,HWin,Draw,Dur,BeeHit,HumanHit,Error -AutoSize}else{Write-Host 'No managed trainers/gameplay builds have checked in.'}
            $expected=@($Config.expectedTrainers)
            if($expected.Count){$present=@($s.trainers|ForEach-Object{[string]$_.trainer_id});$missing=@($expected|Where-Object{$present -notcontains [string]$_});if($missing.Count){Write-Warning "Expected trainers not connected: $($missing -join ', ')"}}
            $l=Get-LocalLearnerStats; Write-Host ''; Write-Host ("Learner logs: Step={0}  ELO={1}  MeanReward={2}" -f $(if($null -eq $l.Step){'-'}else{$l.Step}),$(if($null -eq $l.ELO){'-'}else{'{0:N1}'-f$l.ELO}),$(if($null -eq $l.MeanReward){'-'}else{'{0:N3}'-f$l.MeanReward}))
        } catch { Write-Host "Server: OFFLINE/UNREACHABLE - $($_.Exception.Message)" }
        if($Single){return}; Write-Host ''; Write-Host "Refreshing every $RefreshSeconds s. Ctrl+C to stop."; Start-Sleep -Seconds $RefreshSeconds
    } while($true)
}

function Invoke-Status { $config=Get-ClusterConfig; $admin=Ensure-TokenFile $AdminTokenPath; Show-Status $config $admin ([bool]$Once) }

switch($Command){
    'build'{Invoke-Build}
    'start'{Invoke-Start}
    'stop'{Invoke-Stop}
    'status'{Invoke-Status}
}
,'\

function Get-RemoteSshTarget($Config){
    if($Config.remoteSshTarget -and ([string]$Config.remoteSshTarget).Trim()){ return ([string]$Config.remoteSshTarget).Trim() }
    "$env:USERNAME@$env:COMPUTERNAME"
}

function Prepare-RemoteBootstrap($Config){
    if(-not(Test-Path -LiteralPath $RemoteBootstrapTemplate)){ throw "Remote bootstrap template is missing: $RemoteBootstrapTemplate" }
    if(-not(Test-Path -LiteralPath $RemoteRequirementsPath)){ throw "Remote requirements file is missing: $RemoteRequirementsPath" }

    $maxActors=[int]$Config.maxRemoteActors
    if($maxActors -lt 1 -or $maxActors -gt 12){ throw 'maxRemoteActors must be in 1-12 for generated remote launchers.' }
    $defaultEnvs=if($Config.remoteDefaultEnvs){[int]$Config.remoteDefaultEnvs}else{32}
    if($defaultEnvs -lt 1 -or $defaultEnvs -gt 64){ throw 'remoteDefaultEnvs must be in 1-64.' }
    $sshPort=if($Config.remoteSshPort){[int]$Config.remoteSshPort}else{22}
    if($sshPort -lt 1 -or $sshPort -gt 65535){ throw 'remoteSshPort must be in 1-65535.' }
    $installRoot=if($Config.remoteInstallRoot){[string]$Config.remoteInstallRoot}else{'%LOCALAPPDATA%\BeesTraining'}
    $torchDevice=if($Config.remoteTorchDevice){[string]$Config.remoteTorchDevice}else{'cpu'}
    $learner=Get-RemoteSshTarget $Config

    Ensure-Directory $RemoteRoot
    Ensure-Directory $RuntimeRoot
    $staging=Join-Path $RuntimeRoot 'remote-runtime-staging'
    if(Test-Path -LiteralPath $staging){Remove-Item -LiteralPath $staging -Recurse -Force}
    Ensure-Directory $staging
    try {
        Get-ChildItem -Path (Join-Path $AssetsRoot 'Training\*.py') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $staging }
        Copy-Item -LiteralPath $RemoteRequirementsPath -Destination (Join-Path $staging 'bees_remote_requirements.txt')
        $runtimeZip=Join-Path $RemoteRoot 'bees-remote-runtime.zip'
        if(Test-Path -LiteralPath $runtimeZip){Remove-Item -LiteralPath $runtimeZip -Force}
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $runtimeZip -CompressionLevel Optimal
    } finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }

    $runtimeRemote=Convert-ToScpPath (Join-Path $RemoteRoot 'bees-remote-runtime.zip')
    $workerTokenRemote=Convert-ToScpPath $WorkerTokenPath
    $wanTokenRemote=Convert-ToScpPath $WanTokenPath
    $template=Get-Content -LiteralPath $RemoteBootstrapTemplate -Raw
    for($actorId=0;$actorId -lt $maxActors;$actorId++){
        $body=$template
        $replacements=@{
            '__BEES_LEARNER__'=(Escape-SingleQuoted $learner)
            '__BEES_ACTOR_ID__'=[string]$actorId
            '__BEES_ENVS__'=[string]$defaultEnvs
            '__BEES_SSH_PORT__'=[string]$sshPort
            '__BEES_INSTALL_ROOT__'=(Escape-SingleQuoted $installRoot)
            '__BEES_TORCH_DEVICE__'=(Escape-SingleQuoted $torchDevice)
            '__BEES_RUNTIME_REMOTE_PATH__'=(Escape-SingleQuoted $runtimeRemote)
            '__BEES_WORKER_TOKEN_REMOTE_PATH__'=(Escape-SingleQuoted $workerTokenRemote)
            '__BEES_WAN_TOKEN_REMOTE_PATH__'=(Escape-SingleQuoted $wanTokenRemote)
        }
        foreach($key in $replacements.Keys){$body=$body.Replace($key,[string]$replacements[$key])}
        $output=Join-Path $RemoteRoot "bees-remote-worker-$actorId.ps1"
        Set-Content -LiteralPath $output -Value $body -Encoding UTF8
    }
    Write-Host "Remote launchers prepared in $RemoteRoot (actor slots 0-$($maxActors-1), default $defaultEnvs envs each)."
    Write-Host "Copy one bees-remote-worker-N.ps1 file to each remote Windows machine and run it."
    $sshd=Get-Service -Name 'sshd' -ErrorAction SilentlyContinue
    if($null -eq $sshd -or $sshd.Status -ne 'Running'){
        Write-Warning "Remote launchers require SSH access to this learner. Windows OpenSSH Server (sshd) is not currently running; install/start it or provide another reachable SSH endpoint for remoteSshTarget."
    }
}

function Invoke-Start {
    $config=Get-ClusterConfig; $python=Resolve-Python $config; $unity=Resolve-UnityEditor $config
    $worker=Ensure-TokenFile $WorkerTokenPath; $admin=Ensure-TokenFile $AdminTokenPath; $null=Ensure-TokenFile $WanTokenPath
    Prepare-RemoteBootstrap $config
    $release=Get-LatestRelease
    Start-BeesServerIfNeeded $config $worker $admin; Publish-Release $config $admin $release; Start-CentralAgentIfNeeded $config $python $unity
    $envArgs=Get-EnvironmentArgs $config
    $desired=Invoke-ControlPost "$($config.controlUrl)/v1/admin/state" $admin @{training_enabled=$true;canonical_build_id=[string]$release.build_id;environment_args=$envArgs}
    Write-Host "Training requested: build=$($desired.canonical_build_id) revision=$($desired.revision)"
    Write-Host "Environment arguments: $(if($envArgs.Count){$envArgs -join ' '}else{'(none; defaults)'})"
    Start-Sleep -Seconds 1; Show-Status $config $admin $true
}

function Stop-ProcessTree([int]$Id){ if($Id -gt 0 -and (Get-Process -Id $Id -ErrorAction SilentlyContinue)){ & taskkill /PID $Id /T /F *> $null } }

function Invoke-Stop {
    $config=Get-ClusterConfig; $admin=Ensure-TokenFile $AdminTokenPath
    if(Test-Control ([string]$config.controlUrl) $admin){
        $desired=Invoke-ControlPost "$($config.controlUrl)/v1/admin/state" $admin @{training_enabled=$false}; Write-Host "Training stop requested at revision $($desired.revision)."
        $deadline=[DateTime]::UtcNow.AddSeconds(30)
        while([DateTime]::UtcNow -lt $deadline){
            $s=Invoke-ControlGet "$($config.controlUrl)/v1/status" $admin
            $running=@($s.trainers|Where-Object{-not $_.stale -and $_.role -eq 'dedicated' -and $_.process_state -ne 'stopped'})
            if($running.Count -eq 0){break}; Start-Sleep -Milliseconds 500
        }
    } else { Write-Warning 'Training control is offline; dedicated workers should fail closed after lease expiry.' }
    if($Server -and (Test-Path -LiteralPath $ServerPidPath)){
        $id=0; [void][int]::TryParse((Get-Content -LiteralPath $ServerPidPath -Raw).Trim(),[ref]$id); Stop-ProcessTree $id
        Remove-Item -LiteralPath $ServerPidPath -Force -ErrorAction SilentlyContinue; Write-Host 'BeesServer stopped.'
    }
}

function Get-LocalLearnerStats {
    $elo=$null; $step=$null; $reward=$null
    foreach($root in @((Join-Path $LogsRoot 'Training'),(Join-Path $TrainingRoot 'trainer-results'))){
        if(-not(Test-Path -LiteralPath $root)){continue}
        foreach($file in @(Get-ChildItem -LiteralPath $root -Filter '*.log' -File -Recurse -ErrorAction SilentlyContinue)){
            foreach($line in @(Get-Content -LiteralPath $file.FullName -Tail 1000 -ErrorAction SilentlyContinue)){
                if($line -match '(?i)\bELO\b[^-0-9]*(-?\d+(?:\.\d+)?)'){$elo=[double]$Matches[1]}
                if($line -match '(?i)\bStep\s*[:=]\s*(\d+)'){$step=[long]$Matches[1]}
                if($line -match '(?i)Mean Reward\s*[:=]\s*(-?\d+(?:\.\d+)?)'){$reward=[double]$Matches[1]}
            }
        }
    }
    [pscustomobject]@{ELO=$elo;Step=$step;MeanReward=$reward}
}

function Show-Status($Config,[string]$AdminToken,[bool]$Single){
    do {
        if(-not $Single){Clear-Host}
        Write-Host "Bees distributed learning status  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"; Write-Host ('='*78)
        try {
            $s=Invoke-ControlGet "$($Config.controlUrl)/v1/status" $AdminToken; $d=$s.desired
            Write-Host "Server: ONLINE   Training: $($d.training_enabled)   Revision: $($d.revision)"; Write-Host "Build:  $($d.canonical_build_id)"; Write-Host "Cluster: local_envs=$($Config.numLocalEnvs) max_remote=$($Config.maxRemoteActors) broker_port=$($Config.brokerPort)"
            $ea=@($d.environment_args); Write-Host "Env:    $(if($ea.Count){$ea -join ' '}else{'(none)'})"; Write-Host ''
            $rows=@($s.trainers|ForEach-Object{
                $m=$_.metrics
                [pscustomobject]@{Trainer=$_.trainer_id;Role=$_.role;Platform=$_.platform;State=if($_.stale){'STALE'}else{$_.process_state};Build=$_.build_id;Rev=$_.applied_revision;Age=('{0:N1}s'-f[double]$_.age_seconds);Timeout=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.timeout_pct}else{'-'};BWin=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.bee_win_pct}else{'-'};HWin=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.human_win_pct}else{'-'};Draw=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.draw_pct}else{'-'};Dur=if($m -and $m.window_episodes){'{0:N1}s'-f[double]$m.avg_duration_s}else{'-'};BeeHit=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.bee_hit_pct}else{'-'};HumanHit=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.human_hit_pct}else{'-'};Error=$_.last_error}
            })
            if($rows.Count){$rows|Format-Table Trainer,Role,Platform,State,Build,Rev,Age,Timeout,BWin,HWin,Draw,Dur,BeeHit,HumanHit,Error -AutoSize}else{Write-Host 'No managed trainers/gameplay builds have checked in.'}
            $expected=@($Config.expectedTrainers)
            if($expected.Count){$present=@($s.trainers|ForEach-Object{[string]$_.trainer_id});$missing=@($expected|Where-Object{$present -notcontains [string]$_});if($missing.Count){Write-Warning "Expected trainers not connected: $($missing -join ', ')"}}
            $l=Get-LocalLearnerStats; Write-Host ''; Write-Host ("Learner logs: Step={0}  ELO={1}  MeanReward={2}" -f $(if($null -eq $l.Step){'-'}else{$l.Step}),$(if($null -eq $l.ELO){'-'}else{'{0:N1}'-f$l.ELO}),$(if($null -eq $l.MeanReward){'-'}else{'{0:N3}'-f$l.MeanReward}))
        } catch { Write-Host "Server: OFFLINE/UNREACHABLE - $($_.Exception.Message)" }
        if($Single){return}; Write-Host ''; Write-Host "Refreshing every $RefreshSeconds s. Ctrl+C to stop."; Start-Sleep -Seconds $RefreshSeconds
    } while($true)
}

function Invoke-Status { $config=Get-ClusterConfig; $admin=Ensure-TokenFile $AdminTokenPath; Show-Status $config $admin ([bool]$Once) }

switch($Command){
    'build'{Invoke-Build}
    'start'{Invoke-Start}
    'stop'{Invoke-Stop}
    'status'{Invoke-Status}
}
).Replace('`','\`')
}

function Get-RemoteSshTarget($Config){
    if($Config.remoteSshTarget -and ([string]$Config.remoteSshTarget).Trim()){ return ([string]$Config.remoteSshTarget).Trim() }
    "$env:USERNAME@$env:COMPUTERNAME"
}

function Prepare-RemoteBootstrap($Config){
    if(-not(Test-Path -LiteralPath $RemoteBootstrapTemplate)){ throw "Remote bootstrap template is missing: $RemoteBootstrapTemplate" }
    if(-not(Test-Path -LiteralPath $RemoteRequirementsPath)){ throw "Remote requirements file is missing: $RemoteRequirementsPath" }

    $maxActors=[int]$Config.maxRemoteActors
    if($maxActors -lt 1 -or $maxActors -gt 12){ throw 'maxRemoteActors must be in 1-12 for generated remote launchers.' }
    $defaultEnvs=if($Config.remoteDefaultEnvs){[int]$Config.remoteDefaultEnvs}else{32}
    if($defaultEnvs -lt 1 -or $defaultEnvs -gt 64){ throw 'remoteDefaultEnvs must be in 1-64.' }
    $sshPort=if($Config.remoteSshPort){[int]$Config.remoteSshPort}else{22}
    if($sshPort -lt 1 -or $sshPort -gt 65535){ throw 'remoteSshPort must be in 1-65535.' }
    $installRoot=if($Config.remoteInstallRoot){[string]$Config.remoteInstallRoot}else{'%LOCALAPPDATA%\BeesTraining'}
    $torchDevice=if($Config.remoteTorchDevice){[string]$Config.remoteTorchDevice}else{'cpu'}
    $learner=Get-RemoteSshTarget $Config

    Ensure-Directory $RemoteRoot
    Ensure-Directory $RuntimeRoot
    $staging=Join-Path $RuntimeRoot 'remote-runtime-staging'
    if(Test-Path -LiteralPath $staging){Remove-Item -LiteralPath $staging -Recurse -Force}
    Ensure-Directory $staging
    try {
        Get-ChildItem -Path (Join-Path $AssetsRoot 'Training\*.py') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $staging }
        Copy-Item -LiteralPath $RemoteRequirementsPath -Destination (Join-Path $staging 'bees_remote_requirements.txt')
        $runtimeZip=Join-Path $RemoteRoot 'bees-remote-runtime.zip'
        if(Test-Path -LiteralPath $runtimeZip){Remove-Item -LiteralPath $runtimeZip -Force}
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $runtimeZip -CompressionLevel Optimal
    } finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }

    $runtimeRemote=Convert-ToScpPath (Join-Path $RemoteRoot 'bees-remote-runtime.zip')
    $workerTokenRemote=Convert-ToScpPath $WorkerTokenPath
    $wanTokenRemote=Convert-ToScpPath $WanTokenPath
    $template=Get-Content -LiteralPath $RemoteBootstrapTemplate -Raw
    for($actorId=0;$actorId -lt $maxActors;$actorId++){
        $body=$template
        $replacements=@{
            '__BEES_LEARNER__'=(Escape-SingleQuoted $learner)
            '__BEES_ACTOR_ID__'=[string]$actorId
            '__BEES_ENVS__'=[string]$defaultEnvs
            '__BEES_SSH_PORT__'=[string]$sshPort
            '__BEES_INSTALL_ROOT__'=(Escape-SingleQuoted $installRoot)
            '__BEES_TORCH_DEVICE__'=(Escape-SingleQuoted $torchDevice)
            '__BEES_RUNTIME_REMOTE_PATH__'=(Escape-SingleQuoted $runtimeRemote)
            '__BEES_WORKER_TOKEN_REMOTE_PATH__'=(Escape-SingleQuoted $workerTokenRemote)
            '__BEES_WAN_TOKEN_REMOTE_PATH__'=(Escape-SingleQuoted $wanTokenRemote)
        }
        foreach($key in $replacements.Keys){$body=$body.Replace($key,[string]$replacements[$key])}
        $output=Join-Path $RemoteRoot "bees-remote-worker-$actorId.ps1"
        Set-Content -LiteralPath $output -Value $body -Encoding UTF8
    }
    Write-Host "Remote launchers prepared in $RemoteRoot (actor slots 0-$($maxActors-1), default $defaultEnvs envs each)."
    Write-Host "Copy one bees-remote-worker-N.ps1 file to each remote Windows machine and run it."
    $sshd=Get-Service -Name 'sshd' -ErrorAction SilentlyContinue
    if($null -eq $sshd -or $sshd.Status -ne 'Running'){
        Write-Warning "Remote launchers require SSH access to this learner. Windows OpenSSH Server (sshd) is not currently running; install/start it or provide another reachable SSH endpoint for remoteSshTarget."
    }
}

function Invoke-Start {
    $config=Get-ClusterConfig; $python=Resolve-Python $config; $unity=Resolve-UnityEditor $config
    $worker=Ensure-TokenFile $WorkerTokenPath; $admin=Ensure-TokenFile $AdminTokenPath; $null=Ensure-TokenFile $WanTokenPath
    Prepare-RemoteBootstrap $config
    $release=Get-LatestRelease
    Start-BeesServerIfNeeded $config $worker $admin; Publish-Release $config $admin $release; Start-CentralAgentIfNeeded $config $python $unity
    $envArgs=Get-EnvironmentArgs $config
    $desired=Invoke-ControlPost "$($config.controlUrl)/v1/admin/state" $admin @{training_enabled=$true;canonical_build_id=[string]$release.build_id;environment_args=$envArgs}
    Write-Host "Training requested: build=$($desired.canonical_build_id) revision=$($desired.revision)"
    Write-Host "Environment arguments: $(if($envArgs.Count){$envArgs -join ' '}else{'(none; defaults)'})"
    Start-Sleep -Seconds 1; Show-Status $config $admin $true
}

function Stop-ProcessTree([int]$Id){ if($Id -gt 0 -and (Get-Process -Id $Id -ErrorAction SilentlyContinue)){ & taskkill /PID $Id /T /F *> $null } }

function Invoke-Stop {
    $config=Get-ClusterConfig; $admin=Ensure-TokenFile $AdminTokenPath
    if(Test-Control ([string]$config.controlUrl) $admin){
        $desired=Invoke-ControlPost "$($config.controlUrl)/v1/admin/state" $admin @{training_enabled=$false}; Write-Host "Training stop requested at revision $($desired.revision)."
        $deadline=[DateTime]::UtcNow.AddSeconds(30)
        while([DateTime]::UtcNow -lt $deadline){
            $s=Invoke-ControlGet "$($config.controlUrl)/v1/status" $admin
            $running=@($s.trainers|Where-Object{-not $_.stale -and $_.role -eq 'dedicated' -and $_.process_state -ne 'stopped'})
            if($running.Count -eq 0){break}; Start-Sleep -Milliseconds 500
        }
    } else { Write-Warning 'Training control is offline; dedicated workers should fail closed after lease expiry.' }
    if($Server -and (Test-Path -LiteralPath $ServerPidPath)){
        $id=0; [void][int]::TryParse((Get-Content -LiteralPath $ServerPidPath -Raw).Trim(),[ref]$id); Stop-ProcessTree $id
        Remove-Item -LiteralPath $ServerPidPath -Force -ErrorAction SilentlyContinue; Write-Host 'BeesServer stopped.'
    }
}

function Get-LocalLearnerStats {
    $elo=$null; $step=$null; $reward=$null
    foreach($root in @((Join-Path $LogsRoot 'Training'),(Join-Path $TrainingRoot 'trainer-results'))){
        if(-not(Test-Path -LiteralPath $root)){continue}
        foreach($file in @(Get-ChildItem -LiteralPath $root -Filter '*.log' -File -Recurse -ErrorAction SilentlyContinue)){
            foreach($line in @(Get-Content -LiteralPath $file.FullName -Tail 1000 -ErrorAction SilentlyContinue)){
                if($line -match '(?i)\bELO\b[^-0-9]*(-?\d+(?:\.\d+)?)'){$elo=[double]$Matches[1]}
                if($line -match '(?i)\bStep\s*[:=]\s*(\d+)'){$step=[long]$Matches[1]}
                if($line -match '(?i)Mean Reward\s*[:=]\s*(-?\d+(?:\.\d+)?)'){$reward=[double]$Matches[1]}
            }
        }
    }
    [pscustomobject]@{ELO=$elo;Step=$step;MeanReward=$reward}
}

function Show-Status($Config,[string]$AdminToken,[bool]$Single){
    do {
        if(-not $Single){Clear-Host}
        Write-Host "Bees distributed learning status  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"; Write-Host ('='*78)
        try {
            $s=Invoke-ControlGet "$($Config.controlUrl)/v1/status" $AdminToken; $d=$s.desired
            Write-Host "Server: ONLINE   Training: $($d.training_enabled)   Revision: $($d.revision)"; Write-Host "Build:  $($d.canonical_build_id)"; Write-Host "Cluster: local_envs=$($Config.numLocalEnvs) max_remote=$($Config.maxRemoteActors) broker_port=$($Config.brokerPort)"
            $ea=@($d.environment_args); Write-Host "Env:    $(if($ea.Count){$ea -join ' '}else{'(none)'})"; Write-Host ''
            $rows=@($s.trainers|ForEach-Object{
                $m=$_.metrics
                [pscustomobject]@{Trainer=$_.trainer_id;Role=$_.role;Platform=$_.platform;State=if($_.stale){'STALE'}else{$_.process_state};Build=$_.build_id;Rev=$_.applied_revision;Age=('{0:N1}s'-f[double]$_.age_seconds);Timeout=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.timeout_pct}else{'-'};BWin=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.bee_win_pct}else{'-'};HWin=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.human_win_pct}else{'-'};Draw=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.draw_pct}else{'-'};Dur=if($m -and $m.window_episodes){'{0:N1}s'-f[double]$m.avg_duration_s}else{'-'};BeeHit=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.bee_hit_pct}else{'-'};HumanHit=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.human_hit_pct}else{'-'};Error=$_.last_error}
            })
            if($rows.Count){$rows|Format-Table Trainer,Role,Platform,State,Build,Rev,Age,Timeout,BWin,HWin,Draw,Dur,BeeHit,HumanHit,Error -AutoSize}else{Write-Host 'No managed trainers/gameplay builds have checked in.'}
            $expected=@($Config.expectedTrainers)
            if($expected.Count){$present=@($s.trainers|ForEach-Object{[string]$_.trainer_id});$missing=@($expected|Where-Object{$present -notcontains [string]$_});if($missing.Count){Write-Warning "Expected trainers not connected: $($missing -join ', ')"}}
            $l=Get-LocalLearnerStats; Write-Host ''; Write-Host ("Learner logs: Step={0}  ELO={1}  MeanReward={2}" -f $(if($null -eq $l.Step){'-'}else{$l.Step}),$(if($null -eq $l.ELO){'-'}else{'{0:N1}'-f$l.ELO}),$(if($null -eq $l.MeanReward){'-'}else{'{0:N3}'-f$l.MeanReward}))
        } catch { Write-Host "Server: OFFLINE/UNREACHABLE - $($_.Exception.Message)" }
        if($Single){return}; Write-Host ''; Write-Host "Refreshing every $RefreshSeconds s. Ctrl+C to stop."; Start-Sleep -Seconds $RefreshSeconds
    } while($true)
}

function Invoke-Status { $config=Get-ClusterConfig; $admin=Ensure-TokenFile $AdminTokenPath; Show-Status $config $admin ([bool]$Once) }

switch($Command){
    'build'{Invoke-Build}
    'start'{Invoke-Start}
    'stop'{Invoke-Stop}
    'status'{Invoke-Status}
}
