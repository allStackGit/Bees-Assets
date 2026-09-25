param(
    [Parameter(Mandatory=$true,Position=0)]
    [ValidateSet('build','server','start','stop','status')]
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
$LearnerRequirementsPath=Join-Path $AssetsRoot 'Training\bees_learner_requirements.txt'
$LatestReleasePath=Join-Path $BuildsRoot 'latest-training-release.json'
$WorkerTokenPath=Join-Path $SecretsRoot 'training-worker.token'
$AdminTokenPath=Join-Path $SecretsRoot 'training-admin.token'
$WanTokenPath=Join-Path $SecretsRoot 'wan.token'
$BootstrapTokenPath=Join-Path $SecretsRoot 'training-bootstrap.token'
$RunLifecycleRoot=Join-Path $TrainingRoot 'RunLifecycle'
$RunStatePath=Join-Path $RunLifecycleRoot 'current.json'
$RunPlanPath=Join-Path $RuntimeRoot 'pending-training-run.json'
$RunLifecycleScript=Join-Path $AssetsRoot 'Training\bees_run_lifecycle.py'
$ArchiveRunScript=Join-Path $AssetsRoot 'Training\bees_archive_training_run.py'
$ServerPidPath=Join-Path $RuntimeRoot 'bees-server.pid'
$ServerStatePath=Join-Path $RuntimeRoot 'bees-server-state.json'
$CentralAgentPidPath=Join-Path $RuntimeRoot 'central-training-agent.pid'
$CentralAgentStatePath=Join-Path $RuntimeRoot 'central-training-agent.json'
$TailnetToolRoot=Join-Path $AssetsRoot 'Tools~\bees-tailnet-bridge'
$TailnetRoot=Join-Path $RuntimeRoot 'Tailnet'
$TailnetBinRoot=Join-Path $TailnetRoot 'Bin'
$TailnetBridgeManifestPath=Join-Path $TailnetBinRoot 'current.json'
$TailnetBridgeDistributionRoot=Join-Path $TailnetBinRoot 'Distribution'
$TailnetGatewayPidPath=Join-Path $TailnetRoot 'gateway.pid'
$TailnetGatewayLogPath=Join-Path $LogsRoot 'Training\tailnet-gateway.out.log'
$TailnetGatewayErrPath=Join-Path $LogsRoot 'Training\tailnet-gateway.err.log'
$TailnetAddressPath=Join-Path $TailnetRoot 'learner-ipv4.txt'
$GameplayServerPort=7146
$GoVersion='1.27.1'
$GoWindowsZipSha256='a3911b5e0e1b1053f25ed0675f4c1c6aad1e2bfcf253df2b9be4caabd2edd95d'

function Ensure-Directory([string]$Path){ $null=New-Item -ItemType Directory -Force -Path $Path }
function Install-AtomicFile([string]$Source,[string]$Destination){
    $sourcePath=[IO.Path]::GetFullPath($Source)
    $destinationPath=[IO.Path]::GetFullPath($Destination)
    Ensure-Directory (Split-Path -Parent $destinationPath)
    if(Test-Path -LiteralPath $destinationPath){
        $backup="$destinationPath.swap-backup"
        Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
        [IO.File]::Replace($sourcePath,$destinationPath,$backup,$true)
        Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    } else {
        [IO.File]::Move($sourcePath,$destinationPath)
    }
}

function Remove-Utf8BomIfPresent([string]$Path){
    if(-not(Test-Path -LiteralPath $Path)){ return }
    $bytes=[IO.File]::ReadAllBytes($Path)
    if($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF){
        return
    }
    $payload=New-Object byte[] ($bytes.Length-3)
    if($payload.Length -gt 0){
        [Array]::Copy($bytes,3,$payload,0,$payload.Length)
    }
    $temp="$Path.nobom"
    [IO.File]::WriteAllBytes($temp,$payload)
    Install-AtomicFile $temp $Path
}


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

function Resolve-Git {
    $installed=Get-Command 'git' -ErrorAction SilentlyContinue
    if($null -ne $installed){ return $installed.Source }

    $candidates=@()
    if($env:LOCALAPPDATA){
        $desktopRoot=Join-Path $env:LOCALAPPDATA 'GitHubDesktop'
        if(Test-Path -LiteralPath $desktopRoot){
            $candidates+=@(
                Get-ChildItem -LiteralPath $desktopRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
                    Sort-Object Name -Descending |
                    ForEach-Object { Join-Path $_.FullName 'resources\app\git\cmd\git.exe' }
            )
        }
    }
    if($env:ProgramFiles){
        $candidates+=Join-Path $env:ProgramFiles 'Git\cmd\git.exe'
        $candidates+=Join-Path $env:ProgramFiles 'Git\bin\git.exe'
    }

    $git=$candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if($git){ return [IO.Path]::GetFullPath([string]$git) }

    throw "Git was not found. Install Git for Windows or GitHub Desktop."
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

function Test-PythonCode([string]$Exe,[string]$Code){
    $previousErrorAction=$ErrorActionPreference
    try {
        $ErrorActionPreference='SilentlyContinue'
        & $Exe -c $Code *> $null
        return ($LASTEXITCODE -eq 0)
    } catch {
        return $false
    } finally {
        $ErrorActionPreference=$previousErrorAction
    }
}

function Ensure-LearnerPython($Config){
    if(-not(Test-Path -LiteralPath $LearnerRequirementsPath)){
        throw "Learner Python requirements are missing: $LearnerRequirementsPath"
    }
    if(-not(Test-Path -LiteralPath $RemoteRequirementsPath)){
        throw "Shared Python requirements are missing: $RemoteRequirementsPath"
    }

    $basePython=Resolve-Python $Config
    if(-not(Test-PythonCode $basePython 'import sys; raise SystemExit(0 if sys.version_info[:2] == (3,10) else 1)')){
        throw "Bees learner requires Python 3.10. Configured python resolved to '$basePython'."
    }

    $venvRoot=Join-Path $RuntimeRoot 'LearnerPython'
    $venvPython=Join-Path $venvRoot 'Scripts\python.exe'
    if(-not(Test-Path -LiteralPath $venvPython)){
        Write-Host "Creating managed learner Python environment at $venvRoot..."
        Invoke-Checked $basePython @('-m','venv',$venvRoot) $AssetsRoot | Out-Host
    }

    $requirementsHash=Get-StringSha256 (
        (Get-Content -LiteralPath $LearnerRequirementsPath -Raw) +
        [Environment]::NewLine +
        (Get-Content -LiteralPath $RemoteRequirementsPath -Raw)
    )
    $stampPath=Join-Path $venvRoot 'bees-requirements.sha256'
    $currentStamp=if(Test-Path -LiteralPath $stampPath){(Get-Content -LiteralPath $stampPath -Raw).Trim()}else{''}
    $preflight='import sys, mlagents, torch, numpy, onnxruntime; raise SystemExit(0 if sys.version_info[:2] == (3,10) else 1)'
    $importsOk=($currentStamp -eq $requirementsHash) -and (Test-PythonCode $venvPython $preflight)

    if(-not $importsOk){
        Write-Host 'Installing/updating central learner Python dependencies...'
        Invoke-Checked $venvPython @('-m','pip','install','--upgrade','pip') $AssetsRoot | Out-Host
        Invoke-Checked $venvPython @('-m','pip','install','-r',$LearnerRequirementsPath) $AssetsRoot | Out-Host
        if(-not(Test-PythonCode $venvPython $preflight)){
            throw 'Central learner Python dependency preflight failed after installation.'
        }
        $requirementsHash | Set-Content -LiteralPath $stampPath -NoNewline -Encoding ASCII
    }

    [IO.Path]::GetFullPath($venvPython)
}

function Resolve-Node($Config){ if($Config.node){ Resolve-CommandPath ([string]$Config.node) } else { Resolve-CommandPath 'node' } }
function Resolve-Npm { Resolve-CommandPath 'npm' }

function Resolve-PortableGo {
    $installed=Get-Command 'go' -ErrorAction SilentlyContinue
    if($null -ne $installed){ return $installed.Source }

    $toolchains=Join-Path $RuntimeRoot 'Toolchains'
    Ensure-Directory $toolchains
    $root=Join-Path $toolchains "go$GoVersion"
    $exe=Join-Path $root 'go\bin\go.exe'
    if(Test-Path -LiteralPath $exe){ return $exe }

    $archive=Join-Path $toolchains "go$GoVersion.windows-amd64.zip"
    if(-not(Test-Path -LiteralPath $archive)){
        $temporary="$archive.download"
        $url="https://go.dev/dl/go$GoVersion.windows-amd64.zip"
        Write-Host "Downloading portable Go $GoVersion for the embedded Bees tailnet bridge..."
        $curl=Get-Command 'curl.exe' -ErrorAction SilentlyContinue
        if($null -ne $curl){
            # Keep a partial download so an interrupted bootstrap can resume instead of starting over.
            Invoke-Checked $curl.Source @(
                '--fail','--location','--retry','3','--retry-delay','2',
                '--continue-at','-','--output',$temporary,$url
            ) $toolchains
        } else {
            # Windows PowerShell Invoke-WebRequest can be much slower for large binary downloads.
            # Use it only as a compatibility fallback when curl.exe is unavailable.
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $temporary
        }
        $actual=(Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
        if($actual -ne $GoWindowsZipSha256){
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
            throw "Portable Go download failed SHA-256 verification. expected=$GoWindowsZipSha256 actual=$actual"
        }
        Move-Item -LiteralPath $temporary -Destination $archive -Force
    } else {
        $actual=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if($actual -ne $GoWindowsZipSha256){
            throw "Cached portable Go archive failed SHA-256 verification: $archive"
        }
    }

    if(Test-Path -LiteralPath $root){ Remove-Item -LiteralPath $root -Recurse -Force }
    Ensure-Directory $root
    Expand-Archive -LiteralPath $archive -DestinationPath $root -Force
    if(-not(Test-Path -LiteralPath $exe)){ throw "Portable Go extraction did not produce $exe" }
    $exe
}

function Get-TailnetBridgeSourceHash {
    $main=Join-Path $TailnetToolRoot 'main.go'
    $module=Join-Path $TailnetToolRoot 'go.mod'
    if(-not(Test-Path -LiteralPath $main) -or -not(Test-Path -LiteralPath $module)){
        throw "Embedded tailnet bridge source is missing: $TailnetToolRoot"
    }
    Get-StringSha256 (
        (Get-Content -LiteralPath $module -Raw) +
        [Environment]::NewLine +
        (Get-Content -LiteralPath $main -Raw)
    )
}

function Build-TailnetBridge {
    $main=Join-Path $TailnetToolRoot 'main.go'
    $module=Join-Path $TailnetToolRoot 'go.mod'
    if(-not(Test-Path -LiteralPath $main) -or -not(Test-Path -LiteralPath $module)){
        throw "Embedded tailnet bridge source is missing: $TailnetToolRoot"
    }

    $sourceHash=Get-TailnetBridgeSourceHash
    $versionRoot=Join-Path (Join-Path $TailnetBinRoot 'Versions') $sourceHash
    $versionWindows=Join-Path $versionRoot 'bees-tailnet-bridge.exe'
    $versionLinux=Join-Path $versionRoot 'bees-tailnet-bridge'
    Ensure-Directory $versionRoot

    if(-not(Test-Path -LiteralPath $versionWindows) -or -not(Test-Path -LiteralPath $versionLinux)){
        $go=Resolve-PortableGo
        $source=Join-Path $TailnetRoot 'BuildSource'
        if(Test-Path -LiteralPath $source){ Remove-Item -LiteralPath $source -Recurse -Force }
        Copy-Item -LiteralPath $TailnetToolRoot -Destination $source -Recurse -Force

        $oldGoos=$env:GOOS
        $oldGoarch=$env:GOARCH
        $oldCgo=$env:CGO_ENABLED
        try {
            $env:GOARCH='amd64'
            $env:CGO_ENABLED='0'

            $env:GOOS='windows'
            Write-Host "Building embedded Bees tailnet bridge $($sourceHash.Substring(0,12)) for Windows..."
            Invoke-Checked $go @(
                'build','-mod=mod','-trimpath','-ldflags=-s -w',
                '-o',$versionWindows,'.'
            ) $source

            $env:GOOS='linux'
            Write-Host "Building embedded Bees tailnet bridge $($sourceHash.Substring(0,12)) for Linux..."
            Invoke-Checked $go @(
                'build','-mod=mod','-trimpath','-ldflags=-s -w',
                '-o',$versionLinux,'.'
            ) $source
        } finally {
            $env:GOOS=$oldGoos
            $env:GOARCH=$oldGoarch
            $env:CGO_ENABLED=$oldCgo
            Remove-Item -LiteralPath $source -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Ensure-Directory $TailnetBridgeDistributionRoot
    $distributionWindows=Join-Path $TailnetBridgeDistributionRoot 'bees-tailnet-bridge.exe'
    $distributionLinux=Join-Path $TailnetBridgeDistributionRoot 'bees-tailnet-bridge'
    $distributionWindowsTemp="$distributionWindows.new"
    $distributionLinuxTemp="$distributionLinux.new"
    Copy-Item -LiteralPath $versionWindows -Destination $distributionWindowsTemp -Force
    Copy-Item -LiteralPath $versionLinux -Destination $distributionLinuxTemp -Force
    Install-AtomicFile $distributionWindowsTemp $distributionWindows
    Install-AtomicFile $distributionLinuxTemp $distributionLinux

    [pscustomobject]@{
        schema_version=1
        source_hash=$sourceHash
        gateway_windows=$versionWindows
        distribution_windows=$distributionWindows
        distribution_linux=$distributionLinux
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $TailnetBridgeManifestPath -Encoding UTF8
}

function Get-TailnetBridgePaths {
    $sourceHash=Get-TailnetBridgeSourceHash
    $needsBuild=-not(Test-Path -LiteralPath $TailnetBridgeManifestPath)
    if(-not $needsBuild){
        try {
            $current=Get-Content -LiteralPath $TailnetBridgeManifestPath -Raw | ConvertFrom-Json
            $needsBuild=([string]$current.source_hash -ne $sourceHash)
        } catch {
            $needsBuild=$true
        }
    }
    if($needsBuild){
        Write-Host "Embedded Bees tailnet helper source changed; rebuilding helper only..."
        Build-TailnetBridge
    }
    $value=Get-Content -LiteralPath $TailnetBridgeManifestPath -Raw | ConvertFrom-Json
    foreach($path in @(
        [string]$value.gateway_windows,
        [string]$value.distribution_windows,
        [string]$value.distribution_linux
    )){
        if(-not $path -or -not(Test-Path -LiteralPath $path)){
            throw "Tailnet bridge manifest references a missing binary: $path"
        }
    }
    $value
}

function Ensure-TailnetIdentity($Config){
    $transport=if($Config.remoteTransport){([string]$Config.remoteTransport).Trim().ToLowerInvariant()}else{'tailnet'}
    if($transport -ne 'tailnet'){ return }

    $bridges=Get-TailnetBridgePaths
    $bridge=[string]$bridges.gateway_windows

    $hostname=if($Config.tailnetLearnerName){([string]$Config.tailnetLearnerName).Trim()}else{'bees-learner'}
    if($hostname -notmatch '^[A-Za-z0-9-]{1,63}$'){
        throw 'tailnetLearnerName must contain only letters, digits, and dashes.'
    }

    $state=Join-Path $TailnetRoot 'LearnerState'
    Ensure-Directory $state
    Ensure-Directory (Split-Path -Parent $TailnetAddressPath)

    Write-Host 'Checking embedded Bees tailnet identity. On first use, open the Tailscale login URL shown below.'
    Invoke-Checked $bridge @('auth','--state',$state,'--hostname',$hostname,'--ip-file',$TailnetAddressPath) $AssetsRoot

    if(-not(Test-Path -LiteralPath $TailnetAddressPath)){
        throw 'Embedded tailnet authentication did not produce a learner IPv4 address.'
    }
    $tailnetIp=(Get-Content -LiteralPath $TailnetAddressPath -Raw).Trim()
    if($tailnetIp -notmatch '^100\.(?:\d{1,3}\.){2}\d{1,3}$'){
        throw "Unexpected learner tailnet IPv4 address: $tailnetIp"
    }
}

function Start-TailnetGatewayIfNeeded($Config){
    $transport=if($Config.remoteTransport){([string]$Config.remoteTransport).Trim().ToLowerInvariant()}else{'tailnet'}
    if($transport -ne 'tailnet'){ return }

    $bridges=Get-TailnetBridgePaths
    $bridge=[string]$bridges.gateway_windows
    $state=Join-Path $TailnetRoot 'LearnerState'
    $hostname=if($Config.tailnetLearnerName){([string]$Config.tailnetLearnerName).Trim()}else{'bees-learner'}
    $controlPort=[int]$Config.controlPort
    $brokerPort=[int]$Config.brokerPort
    $bootstrapPort=if($Config.tailnetBootstrapPort){[int]$Config.tailnetBootstrapPort}else{7151}
    foreach($port in @($controlPort,$brokerPort,$bootstrapPort)){
        if($port -lt 1 -or $port -gt 65535){ throw 'Tailnet gateway ports must be in 1-65535.' }
    }
    if($controlPort -eq $brokerPort -or $controlPort -eq $bootstrapPort -or $brokerPort -eq $bootstrapPort){
        throw 'controlPort, brokerPort, and tailnetBootstrapPort must be distinct.'
    }

    $runtimeZip=Join-Path $RemoteRoot 'bees-remote-runtime.zip'
    foreach($path in @(
        $runtimeZip,
        $WorkerTokenPath,
        $WanTokenPath,
        $BootstrapTokenPath,
        $LatestReleasePath,
        [string]$bridges.distribution_windows,
        [string]$bridges.distribution_linux
    )){
        if(-not(Test-Path -LiteralPath $path)){ throw "Tailnet gateway input is missing: $path" }
    }

    if(Test-Path -LiteralPath $TailnetGatewayPidPath){
        $oldPid=0
        [void][int]::TryParse((Get-Content -LiteralPath $TailnetGatewayPidPath -Raw).Trim(),[ref]$oldPid)
        if($oldPid -gt 0 -and (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)){
            Stop-ProcessTree $oldPid
        }
        Remove-Item -LiteralPath $TailnetGatewayPidPath -Force -ErrorAction SilentlyContinue
    }

    Ensure-Directory (Split-Path -Parent $TailnetGatewayLogPath)
    $argList=@(
        'gateway',
        '--state',$state,
        '--hostname',$hostname,
        '--control-port',[string]$controlPort,
        '--broker-port',[string]$brokerPort,
        '--bootstrap-port',[string]$bootstrapPort,
        '--runtime',$runtimeZip,
        '--worker-token',$WorkerTokenPath,
        '--wan-token',$WanTokenPath,
        '--release',$LatestReleasePath,
        '--windows-bridge',[string]$bridges.distribution_windows,
        '--linux-bridge',[string]$bridges.distribution_linux,
        '--bootstrap-token',$BootstrapTokenPath
    )
    $startArgs=@{
        FilePath=$bridge
        ArgumentList=$argList
        WorkingDirectory=$AssetsRoot
        RedirectStandardOutput=$TailnetGatewayLogPath
        RedirectStandardError=$TailnetGatewayErrPath
        WindowStyle='Hidden'
        PassThru=$true
    }
    $p=Start-Process @startArgs
    Start-Sleep -Milliseconds 750
    if($p.HasExited){
        throw "Embedded tailnet gateway exited during startup. Check $TailnetGatewayErrPath"
    }
    $p.Id | Set-Content -LiteralPath $TailnetGatewayPidPath -NoNewline -Encoding ASCII
    $tailnetIp=(Get-Content -LiteralPath $TailnetAddressPath -Raw).Trim()
    Write-Host ("Embedded tailnet gateway online at {0}: control={1} broker={2} bootstrap={3} (PID {4})." -f $tailnetIp,$controlPort,$brokerPort,$bootstrapPort,$p.Id)
}


function Invoke-Checked([string]$Exe,[string[]]$ArgumentList,[string]$WorkingDirectory=$AssetsRoot){
    Push-Location $WorkingDirectory
    try {
        & $Exe @ArgumentList
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

function Assert-UnityProjectAvailableForBatchBuild {
    $lock=Join-Path $BeesRoot 'Temp\UnityLockfile'
    if(Test-Path -LiteralPath $lock){
        throw "The Bees Unity project appears to already be open in the Unity Editor. Close the Editor before running '.\Assets\bees.ps1 build'. If Unity is definitely closed, remove the stale lock file: $lock"
    }
}

function Get-UnityBuildProgressStatus([string]$LogPath){
    if(-not(Test-Path -LiteralPath $LogPath)){ return 'Starting Unity' }

    $lines=@(Get-Content -LiteralPath $LogPath -Tail 120 -ErrorAction SilentlyContinue)
    for($i=$lines.Count-1;$i -ge 0;$i--){
        $line=([string]$lines[$i]).Trim()
        if(-not $line){ continue }

        if($line -match "^Opening scene '(.+)'$"){
            return "Processing scene: $([IO.Path]::GetFileName($Matches[1]))"
        }
        if($line -match "^Importing '[^']+ - Path: (.+)'"){
            return "Importing: $($Matches[1])"
        }
        if($line -match '(?i)shader.*compil|compil.*shader'){ return 'Compiling shaders' }
        if($line -match '(?i)script.*compil|compil.*script'){ return 'Compiling scripts' }
        if($line -match '(?i)SpriteAtlasPacking'){ return 'Packing sprite atlases' }
        if($line -match '(?i)Asset Pipeline Refresh'){ return 'Refreshing assets' }
        if($line -match '(?i)building player|buildpipeline|player build'){ return 'Building player' }
        if($line -match '(?i)copying|copy file|copy files'){ return 'Copying build files' }
        if($line -match '(?i)Build Finished|result=Succeeded|Batchmode quit'){ return 'Finalizing build' }
    }

    return 'Building player'
}

function Invoke-UnityBuild([string]$Unity,[string]$Method,[string]$Output,[string]$Entrypoint,[string]$LogName){
    $logRoot=Join-Path $LogsRoot 'Build'; Ensure-Directory $logRoot
    $logPath=Join-Path $logRoot $LogName

    $stagingRoot=Join-Path $RuntimeRoot 'BuildStaging'
    Ensure-Directory $stagingRoot
    $stageName=($Method -replace '[^A-Za-z0-9_.-]','_')
    $staging=Join-Path $stagingRoot $stageName
    if(Test-Path -LiteralPath $staging){ Remove-Item -LiteralPath $staging -Recurse -Force }
    Ensure-Directory $staging

    $args=@('-batchmode','-quit','-projectPath',$BeesRoot,'-executeMethod',$Method,'-beesOutput',$staging,'-logFile',$logPath)
    Write-Host "Unity: $Method -> $Output"

    # Unity.exe is a Windows GUI executable. Launch it as a Process and explicitly wait for
    # completion so PowerShell cannot return early. While it runs, keep one in-place status line
    # visible so long builds do not look hung.
    $unityArgumentString=($args | ForEach-Object {
        $value=[string]$_
        if($value -match '[\s"]'){ '"' + $value.Replace('"','\"') + '"' } else { $value }
    }) -join ' '
    $unityProcess=Start-Process -FilePath $Unity -ArgumentList $unityArgumentString -WorkingDirectory $BeesRoot -PassThru
    $unityStarted=[DateTime]::UtcNow
    $progressActivity="Unity build: $Method"
    try {
        while(-not $unityProcess.WaitForExit(1000)){
            $elapsed=[DateTime]::UtcNow-$unityStarted
            $phase=Get-UnityBuildProgressStatus $logPath
            Write-Progress -Activity $progressActivity -Status ($phase + " - elapsed " + $elapsed.ToString('hh\:mm\:ss')) -CurrentOperation $phase
        }
        # Flush asynchronous process bookkeeping before reading ExitCode.
        $unityProcess.WaitForExit()
    } finally {
        Write-Progress -Activity $progressActivity -Completed
    }
    if($unityProcess.ExitCode -ne 0){
        $tail=''
        if(Test-Path -LiteralPath $logPath){
            $tail=(@(Get-Content -LiteralPath $logPath -Tail 60 -ErrorAction SilentlyContinue) -join [Environment]::NewLine)
        }
        $message="$Unity exited with code $($unityProcess.ExitCode)."
        if($tail){
            $message += [Environment]::NewLine + "Last Unity build log lines:" + [Environment]::NewLine + $tail
        } else {
            $message += " Check $logPath"
        }
        throw $message
    }

    $stagedEntrypoint=Join-Path $staging $Entrypoint
    $entrypointDeadline=[DateTime]::UtcNow.AddSeconds(30)
    while(
        -not(Test-Path -LiteralPath $stagedEntrypoint) -and
        [DateTime]::UtcNow -lt $entrypointDeadline
    ){
        Start-Sleep -Milliseconds 250
    }
    if(-not(Test-Path -LiteralPath $stagedEntrypoint)){
        $found=@(
            Get-ChildItem -LiteralPath $BuildsRoot -Recurse -File -Filter $Entrypoint -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty FullName
        )
        $tail=''
        if(Test-Path -LiteralPath $logPath){
            $tail=(@(Get-Content -LiteralPath $logPath -Tail 40 -ErrorAction SilentlyContinue) -join [Environment]::NewLine)
        }
        $message="Unity exited without producing the expected staged build entrypoint: $stagedEntrypoint"
        if($found.Count){ $message += [Environment]::NewLine + "Matching executable(s) found elsewhere:" + [Environment]::NewLine + ($found -join [Environment]::NewLine) }
        if($tail){ $message += [Environment]::NewLine + "Last Unity build log lines:" + [Environment]::NewLine + $tail }
        throw $message
    }

    if(Test-Path -LiteralPath $Output){ Remove-Item -LiteralPath $Output -Recurse -Force }
    Ensure-Directory (Split-Path -Parent $Output)
    Move-Item -LiteralPath $staging -Destination $Output
}

function Package-Build([string]$Python,[string]$Source,[string]$Archive,[string]$Entrypoint){
    Ensure-Directory (Split-Path -Parent $Archive)
    if(Test-Path -LiteralPath $Archive){ Remove-Item -LiteralPath $Archive -Force }
    Invoke-Checked $Python @((Join-Path $AssetsRoot 'Training\bees_package_training_build.py'),'--source',$Source,'--output',$Archive,'--entrypoint',$Entrypoint) $AssetsRoot
}

function Get-GitShortSha {
    $git=Resolve-Git; Push-Location $AssetsRoot
    try {
        $sha=(& $git rev-parse --short=12 HEAD).Trim()
        if($LASTEXITCODE -ne 0 -or -not $sha){ throw 'git rev-parse failed.' }
        $sha
    } finally { Pop-Location }
}

function Get-GitTreeSha([string]$RelativePath){
    $git=Resolve-Git; Push-Location $AssetsRoot
    try {
        $sha=(& $git rev-parse ("HEAD:" + $RelativePath)).Trim()
        if($LASTEXITCODE -ne 0 -or -not $sha){ throw "git rev-parse failed for $RelativePath." }
        $sha
    } finally { Pop-Location }
}

function Get-ActiveRunId($Config){
    if(Test-Path -LiteralPath $AdminTokenPath){
        try {
            $admin=(Get-Content -LiteralPath $AdminTokenPath -Raw).Trim()
            if($admin -and (Test-Control ([string]$Config.controlUrl) $admin)){
                $status=Invoke-ControlGet "$($Config.controlUrl)/v1/status" $admin
                if($status.desired -and $status.desired.run_id){
                    return ([string]$status.desired.run_id).Trim()
                }
            }
        } catch {}
    }
    if(Test-Path -LiteralPath $RunStatePath){
        try {
            $state=Get-Content -LiteralPath $RunStatePath -Raw | ConvertFrom-Json
            if($state.run_id){ return ([string]$state.run_id).Trim() }
        } catch {}
    }
    $null
}

function Archive-TrainingRun([string]$Python,[string]$RunId,[string]$Reason){
    if(-not $RunId){ return }
    if(-not(Test-Path -LiteralPath $ArchiveRunScript)){
        throw "Training log archive helper is missing: $ArchiveRunScript"
    }
    Write-Host "Archiving and pushing training logs for run $RunId ($Reason)..."
    Invoke-Checked $Python @(
        $ArchiveRunScript,
        '--assets-root',$AssetsRoot,
        '--bees-root',$BeesRoot,
        '--run-id',$RunId,
        '--reason',$Reason
    ) $AssetsRoot
}

function New-TrainingRunPlan([string]$Python){
    if(-not(Test-Path -LiteralPath $RunLifecycleScript)){
        throw "Training run lifecycle helper is missing: $RunLifecycleScript"
    }
    Ensure-Directory $RunLifecycleRoot
    Ensure-Directory $RuntimeRoot
    Remove-Item -LiteralPath $RunPlanPath -Force -ErrorAction SilentlyContinue
    $null=Invoke-Checked $Python @(
        $RunLifecycleScript,'plan',
        '--assets-root',$AssetsRoot,
        '--state',$RunStatePath,
        '--out',$RunPlanPath
    ) $AssetsRoot
    Get-Content -LiteralPath $RunPlanPath -Raw | ConvertFrom-Json
}

function Commit-TrainingRunPlan([string]$Python){
    Invoke-Checked $Python @(
        $RunLifecycleScript,'commit',
        '--state',$RunStatePath,
        '--plan',$RunPlanPath
    ) $AssetsRoot
}

function Stage-Release($Config,[string]$AdminToken,$Release){
    $body=@{
        build_id=[string]$Release.build_id
        run_id=[string]$Release.run_id
        compatibility_key=[string]$Release.compatibility_key
        incompatible=[bool]$Release.incompatible
    }
    Invoke-ControlPost "$($Config.controlUrl)/v1/admin/release" $AdminToken $body
}

function Wait-ReleaseRollout($Config,[string]$AdminToken,[string]$BuildId,[int]$TimeoutSeconds=600){
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while([DateTime]::UtcNow -lt $deadline){
        $status=Invoke-ControlGet "$($Config.controlUrl)/v1/status" $AdminToken
        $pending=$status.desired.pending_release
        if($null -eq $pending -and ([string]$status.desired.canonical_build_id) -eq $BuildId){
            return $status
        }
        Start-Sleep -Seconds 1
    }
    throw "Timed out waiting for release $BuildId to finish coordinated rollout."
}

function Invoke-Build {
    $config=Get-ClusterConfig
    $python=Resolve-Python $config
    $sourceSha=Get-GitShortSha

    $outgoingRun=Get-ActiveRunId $config
    if($outgoingRun){
        Archive-TrainingRun $python $outgoingRun 'pre-build'
    }

    $plan=New-TrainingRunPlan $python
    if([bool]$plan.incompatible){
        Write-Host "Training contract changed incompatibly. New run: $($plan.run_id)"
    } elseif([bool]$plan.new_run){
        Write-Host "Creating initial training run: $($plan.run_id)"
    } else {
        Write-Host "Training contract is compatible; continuing run $($plan.run_id)."
    }

    $unity=Resolve-UnityEditor $config
    Build-TailnetBridge
    Ensure-Directory $BuildsRoot
    $date=Get-Date -Format 'yyyy-MM-dd'
    $time=Get-Date -Format 'HHmmss'
    $sha=$sourceSha
    $buildId="$date-$time-$sha"
    $win=Join-Path $BuildsRoot "$date RL Windows"
    $linux=Join-Path $BuildsRoot "$date RL Linux"
    $game=Join-Path $BuildsRoot "$date Full Game Windows"
    Reset-BuildDirectory $win
    Reset-BuildDirectory $linux
    if($FullGame){ Reset-BuildDirectory $game }

    Assert-UnityProjectAvailableForBatchBuild
    Invoke-UnityBuild $unity 'BeesCommandLineBuild.BuildWindowsRl' $win 'Bees RL Training.exe' "$date-rl-windows.log"
    Invoke-UnityBuild $unity 'BeesCommandLineBuild.BuildLinuxRl' $linux 'Bees RL Training.x86_64' "$date-rl-linux.log"
    if($FullGame){
        Invoke-UnityBuild $unity 'BeesCommandLineBuild.BuildWindowsFullGame' $game 'Bees.exe' "$date-full-game-windows.log"
    }

    $packageRoot=Join-Path (Join-Path $BuildsRoot 'Packages') $buildId
    if(Test-Path -LiteralPath $packageRoot){
        if(-not $Force){ throw "Package directory exists: $packageRoot. Use -Force." }
        Remove-Item -LiteralPath $packageRoot -Recurse -Force
    }
    Ensure-Directory $packageRoot
    $winZip=Join-Path $packageRoot 'rl-windows.zip'
    $linuxZip=Join-Path $packageRoot 'rl-linux.zip'
    Package-Build $python $win $winZip 'Bees RL Training.exe'
    Package-Build $python $linux $linuxZip 'Bees RL Training.x86_64'
    $artifacts=@(
        [pscustomobject]@{
            role='dedicated';platform='WindowsPlayer';folder=$win
            archive=$winZip;entrypoint='Bees RL Training.exe'
        },
        [pscustomobject]@{
            role='dedicated';platform='LinuxPlayer';folder=$linux
            archive=$linuxZip;entrypoint='Bees RL Training.x86_64'
        }
    )
    if($FullGame){
        $gameZip=Join-Path $packageRoot 'full-game-windows.zip'
        Package-Build $python $game $gameZip 'Bees.exe'
        $artifacts+=[pscustomobject]@{
            role='full-game';platform='WindowsPlayer';folder=$game
            archive=$gameZip;entrypoint='Bees.exe'
        }
    }

    $previousRunId=$null
    if($plan.previous_run_id){ $previousRunId=[string]$plan.previous_run_id }
    $release=[pscustomobject]@{
        schema_version=2
        build_id=$buildId
        source_commit=$sha
        created_utc=[DateTime]::UtcNow.ToString('o')
        run_id=[string]$plan.run_id
        previous_run_id=$previousRunId
        compatibility_key=[string]$plan.compatibility_key
        incompatible=[bool]$plan.incompatible
        contract=$plan.contract
        artifacts=$artifacts
    }
    $releaseTemp="$LatestReleasePath.new"
    $releaseJson=$release | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText(
        $releaseTemp,
        $releaseJson,
        (New-Object Text.UTF8Encoding($false))
    )
    Install-AtomicFile $releaseTemp $LatestReleasePath
    Commit-TrainingRunPlan $python

    Write-Host ""
    Write-Host "Build complete: $buildId  run=$($release.run_id)"
    $artifacts | Format-Table role,platform,folder -AutoSize

    if(Test-Path -LiteralPath $AdminTokenPath){
        $admin=(Get-Content -LiteralPath $AdminTokenPath -Raw).Trim()
        if($admin -and (Test-Control ([string]$config.controlUrl) $admin)){
            $worker=Ensure-TokenFile $WorkerTokenPath
            Start-BeesServerIfNeeded $config $worker $admin
            Write-Host 'Training control is online; staging this release without stopping the active cluster.'
            if(Test-Path -LiteralPath $TailnetAddressPath){
                Prepare-RemoteBootstrap $config
            }
            Publish-Release $config $admin $release
            $staged=Stage-Release $config $admin $release
            Write-Host "Release staged: build=$buildId phase=$(if($staged.pending_release){$staged.pending_release.phase}else{'active'})"
            if([bool]$release.incompatible){
                $null=Wait-ReleaseRollout $config $admin $buildId
                if($release.previous_run_id){
                    Start-Sleep -Seconds 2
                    Archive-TrainingRun $python ([string]$release.previous_run_id) 'incompatible-run-final'
                }
                Write-Host "Incompatible cutover complete. Active run: $($release.run_id)"
            }
        }
    }
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
    $serverSourceHash=Get-GitTreeSha 'BeesServer~'
    $online=Test-Control $base $AdminToken

    if($online){
        $managedState=$null
        if(Test-Path -LiteralPath $ServerStatePath){
            try{$managedState=Get-Content -LiteralPath $ServerStatePath -Raw|ConvertFrom-Json}catch{$managedState=$null}
        }
        if(
            $null -ne $managedState -and
            ([string]$managedState.source_hash) -eq $serverSourceHash
        ){
            return
        }

        $managedPid=0
        if(Test-Path -LiteralPath $ServerPidPath){
            [void][int]::TryParse(
                (Get-Content -LiteralPath $ServerPidPath -Raw).Trim(),
                [ref]$managedPid
            )
        }
        if($managedPid -le 0 -or -not(Get-Process -Id $managedPid -ErrorAction SilentlyContinue)){
            throw 'BeesServer is online but is not owned by the Bees operator state. Stop the unmanaged server once, then rerun this command so future source updates can be automatic.'
        }
        Write-Host 'BeesServer source changed; restarting the managed server without changing desired training state.'
        Stop-ProcessTree $managedPid
        Remove-Item -LiteralPath $ServerPidPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $ServerStatePath -Force -ErrorAction SilentlyContinue

        $probeHost=if(([string]$Config.controlHost) -eq '0.0.0.0'){'127.0.0.1'}else{[string]$Config.controlHost}
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        while([DateTime]::UtcNow -lt $deadline){
            $open=Test-NetConnection -ComputerName $probeHost -Port ([int]$Config.controlPort) -InformationLevel Quiet -WarningAction SilentlyContinue
            if(-not $open){break}
            Start-Sleep -Milliseconds 250
        }
    }

    $probeHost=if(([string]$Config.controlHost) -eq '0.0.0.0'){'127.0.0.1'}else{[string]$Config.controlHost}
    $controlPortOpen=Test-NetConnection -ComputerName $probeHost -Port ([int]$Config.controlPort) -InformationLevel Quiet -WarningAction SilentlyContinue
    if($controlPortOpen){ throw "Training-control port $($Config.controlPort) is already in use but did not accept this admin token. Stop/reconfigure the existing server before starting another." }
    $node=Resolve-Node $Config; $npm=Resolve-Npm
    if(-not(Test-Path -LiteralPath (Join-Path $ServerRoot 'node_modules'))){ Write-Host 'Installing BeesServer dependencies...'; Invoke-Checked $npm @('ci') $ServerRoot }
    Ensure-Directory (Join-Path $LogsRoot 'Server'); Ensure-Directory (Join-Path $TrainingRoot 'Control'); Ensure-Directory $RuntimeRoot
    $serverLog=Join-Path $LogsRoot 'Server\bees-server.log'
    $env:BEES_TRAINING_CONTROL_ENABLED='1'; $env:BEES_TRAINING_CONTROL_TOKEN=$WorkerToken; $env:BEES_TRAINING_CONTROL_ADMIN_TOKEN=$AdminToken
    $env:BEES_TRAINING_CONTROL_HOST=[string]$Config.controlHost; $env:BEES_TRAINING_CONTROL_PORT=[string]$Config.controlPort
    $env:BEES_TRAINING_CONTROL_STATE=Join-Path $TrainingRoot 'Control\state.json'; $env:BEES_TRAINING_ARTIFACT_ROOT=Join-Path $TrainingRoot 'Control\Artifacts'
    $env:BEES_TRAINING_LOG_ROOT=Join-Path $TrainingRoot 'TrainerLogs'
    $env:BEES_TEST_TRAINING_CONTROL_ENABLED='1'
    $launchedPid=0
    Push-Location $ServerRoot
    try {
        $output=@(& $node (Join-Path $ServerRoot 'start-server.js') '--background' "--log=$serverLog" 'test' ([string]$GameplayServerPort) 2>&1)
        if($LASTEXITCODE -ne 0){ throw "BeesServer launcher failed: $($output -join [Environment]::NewLine)" }
        $joined=$output -join [Environment]::NewLine; Write-Host $joined
        if($joined -match 'PID\s+(\d+)'){
            $launchedPid=[int]$Matches[1]
            $launchedPid | Set-Content -LiteralPath $ServerPidPath -NoNewline
        }
    } finally { Pop-Location }
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    while([DateTime]::UtcNow -lt $deadline){
        if(Test-Control $base $AdminToken){
            [pscustomobject]@{
                schema_version=1
                pid=$launchedPid
                source_hash=$serverSourceHash
                started_utc=[DateTime]::UtcNow.ToString('o')
            } | ConvertTo-Json | Set-Content -LiteralPath $ServerStatePath -Encoding UTF8
            return
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Training control did not become reachable at $base. Check $serverLog."
}

function Get-LatestRelease {
    if(-not(Test-Path -LiteralPath $LatestReleasePath)){ throw "No release exists. Run '.\Assets\bees.ps1 build' first." }
    Get-Content -LiteralPath $LatestReleasePath -Raw | ConvertFrom-Json
}

function Publish-Release($Config,[string]$AdminToken,$Release){
    foreach($a in @($Release.artifacts)){
        if(-not (Test-Path -LiteralPath ([string]$a.archive))){ throw "Release artifact is missing: $($a.archive)" }
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
    $args=@('-u',$agent,'--server-url',[string]$Config.controlUrl,'--token-file',$WorkerTokenPath,'--trainer-id','central-learner','--role','dedicated','--platform','WindowsPlayer','--install-root',(Join-Path $BeesRoot 'ManagedBuilds\central-learner'),'--',$Python,$service,"--root=$TrainingRoot","--assets-root=$AssetsRoot",'--training-env={env}',"--telemetry-quarantine=$telemetry","--model-distribution-root=$models",'--game-build-version={build_id}','--run-id={run_id}',"--unity-editor=$Unity","--unity-project-root=$BeesRoot","--generation-steps=$($Config.generationSteps)","--num-envs=$($Config.numLocalEnvs)",'--platform=WindowsPlayer',"--bees-wan-actors=$($Config.maxRemoteActors)","--bees-wan-min-actors=$($Config.minRemoteActors)","--bees-wan-broker-port=$($Config.brokerPort)","--bees-wan-auth-token-file=$WanTokenPath")
    $argString=($args|ForEach-Object{Quote-Arg ([string]$_)}) -join ' '
    $trainingSourceHash=Get-GitTreeSha 'Training'
    $commandHash=Get-StringSha256 ($Python + [Environment]::NewLine + $argString + [Environment]::NewLine + $trainingSourceHash)
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

function Escape-SingleQuoted([string]$Value){ $Value.Replace("'","''") }
function Escape-BashDoubleQuoted([string]$Value){
    if($Value -notmatch '^[A-Za-z0-9_@.:/%~+\-]+$'){
        throw "Remote Linux launcher value contains unsupported shell characters: $Value"
    }
    $Value
}

function Prepare-RemoteBootstrap($Config){
    if(-not(Test-Path -LiteralPath $RemoteBootstrapTemplate)){ throw "Remote Windows bootstrap template is missing: $RemoteBootstrapTemplate" }
    if(-not(Test-Path -LiteralPath $RemoteLinuxBootstrapTemplate)){ throw "Remote Linux bootstrap template is missing: $RemoteLinuxBootstrapTemplate" }
    if(-not(Test-Path -LiteralPath $RemoteRequirementsPath)){ throw "Remote requirements file is missing: $RemoteRequirementsPath" }

    $maxActors=[int]$Config.maxRemoteActors
    if($maxActors -lt 1 -or $maxActors -gt 12){ throw 'maxRemoteActors must be in 1-12.' }
    $transport=if($Config.remoteTransport){([string]$Config.remoteTransport).Trim().ToLowerInvariant()}else{'tailnet'}
    if($transport -ne 'tailnet'){ throw "Generated remote launchers require remoteTransport=tailnet; got '$transport'." }

    $controlPort=[int]$Config.controlPort
    $brokerPort=[int]$Config.brokerPort
    $bootstrapPort=if($Config.tailnetBootstrapPort){[int]$Config.tailnetBootstrapPort}else{7151}
    foreach($port in @($controlPort,$brokerPort,$bootstrapPort)){
        if($port -lt 1 -or $port -gt 65535){ throw 'Configured Bees ports must be in 1-65535.' }
    }
    if($controlPort -eq $brokerPort -or $controlPort -eq $bootstrapPort -or $brokerPort -eq $bootstrapPort){
        throw 'controlPort, brokerPort, and tailnetBootstrapPort must be distinct.'
    }

    if(-not(Test-Path -LiteralPath $TailnetAddressPath)){ throw 'Learner tailnet address is missing. Authenticate the embedded tailnet first.' }
    $tailnetTarget=(Get-Content -LiteralPath $TailnetAddressPath -Raw).Trim()
    if($tailnetTarget -notmatch '^100\.(?:\d{1,3}\.){2}\d{1,3}$'){ throw "Unexpected learner tailnet IPv4 address: $tailnetTarget" }

    $installRoot=if($Config.remoteInstallRoot){[string]$Config.remoteInstallRoot}else{'%LOCALAPPDATA%\BeesTraining'}
    $linuxInstallRoot=if($Config.remoteLinuxInstallRoot){[string]$Config.remoteLinuxInstallRoot}else{'.local/share/bees-training'}
    $torchDevice=if($Config.remoteTorchDevice){[string]$Config.remoteTorchDevice}else{'cpu'}
    $bootstrapToken=Ensure-TokenFile $BootstrapTokenPath

    $bridges=Get-TailnetBridgePaths
    $windowsBridge=[string]$bridges.distribution_windows
    $linuxBridge=[string]$bridges.distribution_linux
    $windowsBridgeName='bees-tailnet-bridge-windows.exe'
    $linuxBridgeName='bees-tailnet-bridge-linux'
    $windowsBridgeSha=(Get-FileHash -LiteralPath $windowsBridge -Algorithm SHA256).Hash.ToLowerInvariant()
    $linuxBridgeSha=(Get-FileHash -LiteralPath $linuxBridge -Algorithm SHA256).Hash.ToLowerInvariant()

    Ensure-Directory $RemoteRoot
    Ensure-Directory $RuntimeRoot
    $staging=Join-Path $RuntimeRoot 'remote-runtime-staging'
    if(Test-Path -LiteralPath $staging){Remove-Item -LiteralPath $staging -Recurse -Force}
    Ensure-Directory $staging
    try {
        Get-ChildItem -Path (Join-Path $AssetsRoot 'Training\*.py') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $staging }
        Copy-Item -LiteralPath $RemoteRequirementsPath -Destination (Join-Path $staging 'bees_remote_requirements.txt')
        $runtimeVersion=Get-GitTreeSha 'Training'
        $runtimeVersion | Set-Content -LiteralPath (Join-Path $staging 'bees-runtime-version.txt') -NoNewline -Encoding ASCII
        $runtimeZip=Join-Path $RemoteRoot 'bees-remote-runtime.zip'
        # Compress-Archive requires the destination itself to end in .zip. Keep the temporary
        # archive beside the final file and atomically swap it into place after compression.
        $runtimeZipTemp=Join-Path $RemoteRoot 'bees-remote-runtime.new.zip'
        Remove-Item -LiteralPath $runtimeZipTemp -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $runtimeZipTemp -CompressionLevel Optimal
        Install-AtomicFile $runtimeZipTemp $runtimeZip
    } finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }

    $windowsTemplate=Get-Content -LiteralPath $RemoteBootstrapTemplate -Raw
    $linuxTemplate=Get-Content -LiteralPath $RemoteLinuxBootstrapTemplate -Raw
    $utf8NoBom=New-Object Text.UTF8Encoding($false)

    Get-ChildItem -LiteralPath $RemoteRoot -Filter 'bees-remote-worker-*.ps1' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem -LiteralPath $RemoteRoot -Filter 'bees-remote-worker-*.cmd' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem -LiteralPath $RemoteRoot -Filter 'bees-remote-worker-*.sh' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item -LiteralPath (Join-Path $RemoteRoot $windowsBridgeName) -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $RemoteRoot $linuxBridgeName) -Force -ErrorAction SilentlyContinue

    function Format-Base64Payload([byte[]]$Bytes,[int]$Width=120){
        $value=[Convert]::ToBase64String($Bytes)
        $builder=New-Object Text.StringBuilder
        for($offset=0;$offset -lt $value.Length;$offset+=$Width){
            $length=[Math]::Min($Width,$value.Length-$offset)
            [void]$builder.AppendLine($value.Substring($offset,$length))
        }
        $builder.ToString().TrimEnd()
    }

    $windowsBody=$windowsTemplate
    $windowsReplacements=@{
        '__BEES_TAILNET_LEARNER__'=(Escape-SingleQuoted $tailnetTarget)
        '__BEES_TAILNET_BOOTSTRAP_PORT__'=[string]$bootstrapPort
        '__BEES_CONTROL_PORT__'=[string]$controlPort
        '__BEES_BROKER_PORT__'=[string]$brokerPort
        '__BEES_TAILNET_BRIDGE_FILE__'=$windowsBridgeName
        '__BEES_TAILNET_BRIDGE_SHA256__'=$windowsBridgeSha
        '__BEES_BOOTSTRAP_TOKEN__'=(Escape-SingleQuoted $bootstrapToken)
        '__BEES_INSTALL_ROOT__'=(Escape-SingleQuoted $installRoot)
        '__BEES_TORCH_DEVICE__'=(Escape-SingleQuoted $torchDevice)
    }
    foreach($key in $windowsReplacements.Keys){ $windowsBody=$windowsBody.Replace($key,[string]$windowsReplacements[$key]) }

    # Build one self-extracting Windows launcher. The large payload comes after the batch logic,
    # so startup text appears before PowerShell reads or expands it.
    $windowsPayloadRoot=Join-Path $RuntimeRoot 'remote-windows-bootstrap-payload'
    if(Test-Path -LiteralPath $windowsPayloadRoot){Remove-Item -LiteralPath $windowsPayloadRoot -Recurse -Force}
    Ensure-Directory $windowsPayloadRoot
    $windowsPayloadZip=Join-Path $RuntimeRoot 'remote-windows-bootstrap-payload.zip'
    Remove-Item -LiteralPath $windowsPayloadZip -Force -ErrorAction SilentlyContinue
    try {
        $generatedWindowsBootstrap=Join-Path $windowsPayloadRoot 'bees-remote-worker.ps1'
        [IO.File]::WriteAllText($generatedWindowsBootstrap,$windowsBody,$utf8NoBom)

        # Validate the exact generated artifact that will be shipped to the remote. This catches
        # template/replacement quoting damage before a launcher can ever leave the learner.
        $parseErrors=$null
        [System.Management.Automation.Language.Parser]::ParseFile(
            $generatedWindowsBootstrap,
            [ref]$null,
            [ref]$parseErrors
        ) | Out-Null
        if($parseErrors.Count -gt 0){
            $details=($parseErrors | ForEach-Object {
                $extent=$_.Extent
                "line $($extent.StartLineNumber), column $($extent.StartColumnNumber): $($_.Message) near '$($extent.Text)'"
            }) -join '; '
            throw "Generated Windows remote bootstrap failed PowerShell parsing: $details"
        }

        Copy-Item -LiteralPath $windowsBridge -Destination (Join-Path $windowsPayloadRoot $windowsBridgeName) -Force
        Compress-Archive -Path (Join-Path $windowsPayloadRoot '*') -DestinationPath $windowsPayloadZip -CompressionLevel Optimal
        $windowsPayload=Format-Base64Payload ([IO.File]::ReadAllBytes($windowsPayloadZip))
    } finally {
        Remove-Item -LiteralPath $windowsPayloadRoot -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $windowsPayloadZip -Force -ErrorAction SilentlyContinue
    }

    $windowsCmd=@'
@echo off
setlocal EnableExtensions
echo [Bees remote] launching Windows training worker...
set "BEES_BOOTSTRAP_DIR=%TEMP%\BeesTrainingBootstrap"
set "BEES_SELF=%~f0"
set "BEES_PAYLOAD_ZIP=%BEES_BOOTSTRAP_DIR%\payload.zip"
if exist "%BEES_BOOTSTRAP_DIR%" rd /s /q "%BEES_BOOTSTRAP_DIR%"
mkdir "%BEES_BOOTSTRAP_DIR%" >nul 2>&1
echo [Bees remote] extracting bundled bootstrap...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$t=[IO.File]::ReadAllText($env:BEES_SELF);$m=[regex]::Match($t,'(?ms)^::BEES_PAYLOAD_BEGIN\r?\n(?<payload>.*?)\r?\n::BEES_PAYLOAD_END\s*$');if(-not $m.Success){throw 'Embedded Bees payload block not found.'};$b=$m.Groups['payload'].Value -replace '\s','';$bytes=[Convert]::FromBase64String($b);if($bytes.Length -lt 4 -or $bytes[0] -ne 0x50 -or $bytes[1] -ne 0x4B){throw 'Embedded Bees payload is not a valid ZIP archive.'};[IO.File]::WriteAllBytes($env:BEES_PAYLOAD_ZIP,$bytes);Expand-Archive -LiteralPath $env:BEES_PAYLOAD_ZIP -DestinationPath $env:BEES_BOOTSTRAP_DIR -Force"
if errorlevel 1 (
  echo [Bees remote] failed to extract the bundled bootstrap.
  exit /b 1
)
echo [Bees remote] starting PowerShell bootstrap...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%BEES_BOOTSTRAP_DIR%\bees-remote-worker.ps1" %*
set "BEES_EXIT=%ERRORLEVEL%"
if not "%BEES_EXIT%"=="0" echo [Bees remote] worker exited with code %BEES_EXIT%.
rd /s /q "%BEES_BOOTSTRAP_DIR%" >nul 2>&1
exit /b %BEES_EXIT%
::BEES_PAYLOAD_BEGIN
__WINDOWS_PAYLOAD__
::BEES_PAYLOAD_END
'@
    $windowsCmd=$windowsCmd.Replace('__WINDOWS_PAYLOAD__',$windowsPayload)
    [IO.File]::WriteAllText((Join-Path $RemoteRoot 'bees-remote-worker.cmd'),$windowsCmd,$utf8NoBom)

    $linuxBody=$linuxTemplate
    $linuxReplacements=@{
        '__BEES_TAILNET_LEARNER__'=(Escape-BashDoubleQuoted $tailnetTarget)
        '__BEES_TAILNET_BOOTSTRAP_PORT__'=[string]$bootstrapPort
        '__BEES_CONTROL_PORT__'=[string]$controlPort
        '__BEES_BROKER_PORT__'=[string]$brokerPort
        '__BEES_TAILNET_BRIDGE_FILE__'=$linuxBridgeName
        '__BEES_TAILNET_BRIDGE_SHA256__'=$linuxBridgeSha
        '__BEES_BOOTSTRAP_TOKEN__'=(Escape-BashDoubleQuoted $bootstrapToken)
        '__BEES_LINUX_INSTALL_ROOT__'=(Escape-BashDoubleQuoted $linuxInstallRoot)
        '__BEES_TORCH_DEVICE__'=(Escape-BashDoubleQuoted $torchDevice)
    }
    foreach($key in $linuxReplacements.Keys){ $linuxBody=$linuxBody.Replace($key,[string]$linuxReplacements[$key]) }
    $linuxBody=[regex]::Replace($linuxBody,"\r\n","\n")
    $linuxBridgePayload=Format-Base64Payload ([IO.File]::ReadAllBytes($linuxBridge))

    # Linux likewise gets a single self-extracting script. The binary is a here-document reached
    # only after the launcher has already printed its startup status.
    $linuxWrapper=@'
#!/usr/bin/env bash
set -euo pipefail

echo "[Bees remote] launching Linux training worker..."

have() { command -v "$1" >/dev/null 2>&1; }
sudo_cmd() {
    if [[ "$(id -u)" -eq 0 ]]; then
        "$@"
    elif have sudo; then
        sudo "$@"
    else
        echo "error: root privileges are required to install base64/coreutils, but sudo is unavailable." >&2
        return 1
    fi
}
if ! have base64; then
    echo "[Bees remote] installing base64/coreutils prerequisite..."
    if have apt-get; then sudo_cmd apt-get update && sudo_cmd apt-get install -y coreutils
    elif have dnf; then sudo_cmd dnf install -y coreutils
    elif have yum; then sudo_cmd yum install -y coreutils
    elif have zypper; then sudo_cmd zypper --non-interactive install coreutils
    elif have pacman; then sudo_cmd pacman -Sy --noconfirm coreutils
    else echo "error: base64 is required and no supported package manager was found." >&2; exit 2
    fi
fi

BOOTSTRAP_DIR="${TMPDIR:-/tmp}/bees-training-bootstrap-$$"
rm -rf "$BOOTSTRAP_DIR"
mkdir -p "$BOOTSTRAP_DIR"
trap 'rm -rf "$BOOTSTRAP_DIR"' EXIT

echo "[Bees remote] extracting bundled bootstrap..."
cat > "$BOOTSTRAP_DIR/bees-remote-worker-inner.sh" <<'__BEES_INNER_SCRIPT__'
__LINUX_INNER_SCRIPT__
__BEES_INNER_SCRIPT__

base64 -d > "$BOOTSTRAP_DIR/__LINUX_BRIDGE_NAME__" <<'__BEES_BRIDGE_PAYLOAD__'
__LINUX_BRIDGE_PAYLOAD__
__BEES_BRIDGE_PAYLOAD__

chmod 700 "$BOOTSTRAP_DIR/bees-remote-worker-inner.sh" "$BOOTSTRAP_DIR/__LINUX_BRIDGE_NAME__"
echo "[Bees remote] starting shell bootstrap..."
set +e
bash "$BOOTSTRAP_DIR/bees-remote-worker-inner.sh" "$@"
BEES_EXIT=$?
set -e
exit "$BEES_EXIT"
'@
    $linuxWrapper=$linuxWrapper.Replace('__LINUX_INNER_SCRIPT__',$linuxBody)
    $linuxWrapper=$linuxWrapper.Replace('__LINUX_BRIDGE_PAYLOAD__',$linuxBridgePayload)
    $linuxWrapper=$linuxWrapper.Replace('__LINUX_BRIDGE_NAME__',$linuxBridgeName)
    $linuxWrapper=[regex]::Replace($linuxWrapper,"\r\n","\n")
    [IO.File]::WriteAllText((Join-Path $RemoteRoot 'bees-remote-worker.sh'),$linuxWrapper,$utf8NoBom)

    Write-Host "Remote launchers prepared in $RemoteRoot."
    Write-Host 'No SSH account, SSH keys, SSH server, port forwarding, or separate Tailscale installation is required.'
    Write-Host 'Windows: copy only bees-remote-worker.cmd and run it; optionally pass -Envs N.'
    Write-Host "Linux:   copy only bees-remote-worker.sh and run 'bash bees-remote-worker.sh'; optionally pass --envs N."
}

function Invoke-Server {
    $config=Get-ClusterConfig
    $worker=Ensure-TokenFile $WorkerTokenPath
    $admin=Ensure-TokenFile $AdminTokenPath
    Start-BeesServerIfNeeded $config $worker $admin
    Write-Host 'BeesServer test mode is online on port 7146 for Unity Editor/gameplay connections. No Unity build or Steam authentication is required.'
}

function Invoke-Start {
    $config=Get-ClusterConfig
    $worker=Ensure-TokenFile $WorkerTokenPath
    $admin=Ensure-TokenFile $AdminTokenPath
    $null=Ensure-TokenFile $WanTokenPath
    $null=Ensure-TokenFile $BootstrapTokenPath

    Start-BeesServerIfNeeded $config $worker $admin

    $envArgs=@(Get-EnvironmentArgs $config)
    if(-not(Test-Path -LiteralPath $LatestReleasePath)){
        $desired=Invoke-ControlPost "$($config.controlUrl)/v1/admin/state" $admin @{
            training_enabled=$false
            environment_args=@($envArgs)
        }
        Write-Host "Unified Bees server/control is online on gameplay port $GameplayServerPort."
        Write-Host 'No training release exists yet, so no managed trainers were started. The Unity Editor can connect now.'
        Write-Host "Environment arguments: $(if($envArgs.Count){$envArgs -join ' '}else{'(none; defaults)'})"
        Start-Sleep -Seconds 1
        Show-Status $config $admin $true
        return
    }

    $release=Get-LatestRelease
    if(-not $release.run_id -or -not $release.compatibility_key){
        throw "Latest release predates automatic run lifecycle metadata. Run '.\Assets\bees.ps1 build' first."
    }
    # Windows PowerShell 5.1's historical UTF8 writer emits a BOM. Older remote
    # supervisors parse this bootstrap metadata as strict UTF-8 JSON, so repair any
    # pre-fix release in place before the gateway serves it.
    Remove-Utf8BomIfPresent $LatestReleasePath

    $pythonResult=@(Ensure-LearnerPython $config)
    if($pythonResult.Count -ne 1){
        throw "Learner Python resolver returned $($pythonResult.Count) values; expected exactly one executable path."
    }
    $python=[string]$pythonResult[0]
    if(-not(Test-Path -LiteralPath $python)){
        throw "Managed learner Python executable is missing: $python"
    }
    $unity=Resolve-UnityEditor $config
    Ensure-TailnetIdentity $config
    Prepare-RemoteBootstrap $config
    Publish-Release $config $admin $release
    Start-TailnetGatewayIfNeeded $config

    $staged=Stage-Release $config $admin $release
    $desired=Invoke-ControlPost "$($config.controlUrl)/v1/admin/state" $admin @{
        training_enabled=$true
        environment_args=@($envArgs)
    }
    Start-CentralAgentIfNeeded $config $python $unity

    Write-Host "Training requested: build=$($release.build_id) run=$($release.run_id) revision=$($desired.revision)"
    if($staged.pending_release){
        Write-Host "Release rollout: $($staged.pending_release.phase) incompatible=$($staged.pending_release.incompatible)"
    }
    Write-Host "Environment arguments: $(if($envArgs.Count){$envArgs -join ' '}else{'(none; defaults)'})"
    Start-Sleep -Seconds 1
    Show-Status $config $admin $true
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
        $id=0
        [void][int]::TryParse((Get-Content -LiteralPath $ServerPidPath -Raw).Trim(),[ref]$id)
        Stop-ProcessTree $id
        Remove-Item -LiteralPath $ServerPidPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $ServerStatePath -Force -ErrorAction SilentlyContinue
        Write-Host 'BeesServer stopped.'
    }
    if($Server -and (Test-Path -LiteralPath $TailnetGatewayPidPath)){
        $tailnetPid=0
        [void][int]::TryParse((Get-Content -LiteralPath $TailnetGatewayPidPath -Raw).Trim(),[ref]$tailnetPid)
        Stop-ProcessTree $tailnetPid
        Remove-Item -LiteralPath $TailnetGatewayPidPath -Force -ErrorAction SilentlyContinue
        Write-Host 'Embedded Bees tailnet gateway stopped.'
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

function Get-StatusFrameLines($Config,[string]$AdminToken){
    $lines=@(
        "Bees distributed learning status  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        ('='*78)
    )
    try {
        $s=Invoke-ControlGet "$($Config.controlUrl)/v1/status" $AdminToken
        $d=$s.desired
        $lines += "Server: ONLINE   Training: $($d.training_enabled)   Revision: $($d.revision)"
        $lines += "Build:  $($d.canonical_build_id)   Run: $($d.run_id)"
        $lines += "Cluster: local_envs=$($Config.numLocalEnvs) max_remote=$($Config.maxRemoteActors) broker_port=$($Config.brokerPort)"
        if($d.pending_release){
            $lines += "Pending release: build=$($d.pending_release.build_id) phase=$($d.pending_release.phase) incompatible=$($d.pending_release.incompatible)"
        }
        $ea=@($d.environment_args)
        $lines += "Env:    $(if($ea.Count){$ea -join ' '}else{'(none)'})"
        $lines += ''

        $rows=@($s.trainers|ForEach-Object{
            $m=$_.metrics
            [pscustomobject]@{
                Trainer=$_.trainer_id
                Role=$_.role
                Platform=$_.platform
                State=if($_.stale){'STALE'}else{$_.process_state}
                Build=$_.build_id
                Rev=$_.applied_revision
                Age=('{0:N1}s'-f[double]$_.age_seconds)
                Timeout=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.timeout_pct}else{'-'}
                BWin=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.bee_win_pct}else{'-'}
                HWin=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.human_win_pct}else{'-'}
                Draw=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.draw_pct}else{'-'}
                Dur=if($m -and $m.window_episodes){'{0:N1}s'-f[double]$m.avg_duration_s}else{'-'}
                BeeHit=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.bee_hit_pct}else{'-'}
                HumanHit=if($m -and $m.window_episodes){'{0:N1}%'-f[double]$m.human_hit_pct}else{'-'}
                Error=$_.last_error
            }
        })
        if($rows.Count){
            $table=($rows|Format-Table Trainer,Role,Platform,State,Build,Rev,Age,Timeout,BWin,HWin,Draw,Dur,BeeHit,HumanHit,Error -AutoSize|Out-String -Width 240).TrimEnd()
            if($table){
                $lines += @($table -split "\r?\n")
            }
        }else{
            $lines += 'No managed trainers/gameplay builds have checked in.'
        }

        $expected=@($Config.expectedTrainers)
        if($expected.Count){
            $present=@($s.trainers|ForEach-Object{[string]$_.trainer_id})
            $missing=@($expected|Where-Object{$present -notcontains [string]$_})
            if($missing.Count){
                $lines += "WARNING: Expected trainers not connected: $($missing -join ', ')"
            }
        }

        $l=Get-LocalLearnerStats
        $lines += ''
        $lines += ("Learner logs: Step={0}  ELO={1}  MeanReward={2}" -f $(if($null -eq $l.Step){'-'}else{$l.Step}),$(if($null -eq $l.ELO){'-'}else{'{0:N1}'-f$l.ELO}),$(if($null -eq $l.MeanReward){'-'}else{'{0:N3}'-f$l.MeanReward}))
    } catch {
        $lines += "Server: OFFLINE/UNREACHABLE - $($_.Exception.Message)"
    }
    $lines
}

function Initialize-LiveStatusRegion([int]$MinimumHeight){
    $height=[Math]::Max(32,$MinimumHeight)
    $height=[Math]::Min($height,[Math]::Max(1,[Console]::BufferHeight-1))
    for($i=0;$i -lt $height;$i++){
        [Console]::WriteLine()
    }
    $top=[Math]::Max(0,[Console]::CursorTop-$height)
    [pscustomobject]@{Top=$top;Height=$height}
}

function Write-LiveStatusFrame([string[]]$Lines,[int]$Top,[int]$Height){
    $width=[Math]::Max(40,[Console]::BufferWidth-1)
    $rows=[Math]::Min($Height,$Lines.Count)
    for($i=0;$i -lt $Height;$i++){
        [Console]::SetCursorPosition(0,$Top+$i)
        $line=if($i -lt $rows){[string]$Lines[$i]}else{''}
        if($line.Length -gt $width){$line=$line.Substring(0,$width)}
        [Console]::Write($line.PadRight($width))
    }
    [Console]::SetCursorPosition(0,[Math]::Min([Console]::BufferHeight-1,$Top+$Height))
}

function Show-Status($Config,[string]$AdminToken,[bool]$Single){
    if($Single){
        @(Get-StatusFrameLines $Config $AdminToken)|ForEach-Object{Write-Host $_}
        return
    }

    # A live dashboard only makes sense on an interactive console. When output is redirected,
    # emit one stable snapshot instead of creating an unbounded log every refresh interval.
    try {
        if([Console]::IsOutputRedirected){
            @(Get-StatusFrameLines $Config $AdminToken)|ForEach-Object{Write-Host $_}
            return
        }
        $null=[Console]::BufferWidth
        $null=[Console]::CursorTop
    } catch {
        @(Get-StatusFrameLines $Config $AdminToken)|ForEach-Object{Write-Host $_}
        return
    }

    $first=@(Get-StatusFrameLines $Config $AdminToken)
    $first += ''
    $first += "Refreshing every $RefreshSeconds s. Ctrl+C to stop."
    $region=Initialize-LiveStatusRegion ([Math]::Max(32,$first.Count+2))
    try {
        Write-LiveStatusFrame $first $region.Top $region.Height
        do {
            Start-Sleep -Seconds $RefreshSeconds
            $lines=@(Get-StatusFrameLines $Config $AdminToken)
            $lines += ''
            $lines += "Refreshing every $RefreshSeconds s. Ctrl+C to stop."
            Write-LiveStatusFrame $lines $region.Top $region.Height
        } while($true)
    } finally {
        [Console]::SetCursorPosition(0,[Math]::Min([Console]::BufferHeight-1,$region.Top+$region.Height))
        [Console]::WriteLine()
    }
}

function Invoke-Status { $config=Get-ClusterConfig; $admin=Ensure-TokenFile $AdminTokenPath; Show-Status $config $admin ([bool]$Once) }

switch($Command){
    'build'{Invoke-Build}
    'server'{Invoke-Server}
    'start'{Invoke-Start}
    'stop'{Invoke-Stop}
    'status'{Invoke-Status}
}
