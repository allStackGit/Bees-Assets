param(
    [Parameter(Mandatory=$true,Position=0)]
    [ValidateSet('build','server','start','stop','status','bundle')]
    [string]$Command,
    [switch]$FullGame,
    [switch]$Force,
    [switch]$NewRun,
    [string[]]$EnvArg,
    [switch]$Once,
    [ValidateRange(1,60)][int]$RefreshSeconds=2,
    [switch]$Server,
    [ValidateRange(0.1,100.0)][double]$LogPercent=10.0,
    [string]$RunId
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

if($NewRun -and $Command -ne 'start'){
    throw '-NewRun is only valid with the start command.'
}
if($Command -ne 'bundle' -and $PSBoundParameters.ContainsKey('LogPercent')){
    throw '-LogPercent is only valid with the bundle command.'
}
if($Command -ne 'bundle' -and $RunId){
    throw '-RunId is only valid with the bundle command.'
}

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
$DiagnosticBundleScript=Join-Path $AssetsRoot 'Training\bees_training_bundle.py'
$ServerPidPath=Join-Path $RuntimeRoot 'bees-server.pid'
$ServerStatePath=Join-Path $RuntimeRoot 'bees-server-state.json'
$ServerDependencyStampPath=Join-Path $RuntimeRoot 'bees-server-dependencies.sha256'
$CentralAgentPidPath=Join-Path $RuntimeRoot 'central-training-agent.pid'
$CentralAgentStatePath=Join-Path $RuntimeRoot 'central-training-agent.json'
$CentralAgentInstallRoot=Join-Path $BeesRoot 'ManagedBuilds\central-learner'
$CentralAgentShutdownRequestPath=Join-Path $CentralAgentInstallRoot 'worker-shutdown.request'
$CentralModelSnapshotRequestPath=Join-Path $CentralAgentInstallRoot 'model-snapshot.request'
$CentralModelSnapshotResponsePath=Join-Path $CentralAgentInstallRoot 'model-snapshot.response.json'
$TailnetToolRoot=Join-Path $AssetsRoot 'Tools~\bees-tailnet-bridge'
$TailnetRoot=Join-Path $RuntimeRoot 'Tailnet'
$TailnetBinRoot=Join-Path $TailnetRoot 'Bin'
$TailnetBridgeManifestPath=Join-Path $TailnetBinRoot 'current.json'
$TailnetBridgeDistributionRoot=Join-Path $TailnetBinRoot 'Distribution'
$TailnetGatewayPidPath=Join-Path $TailnetRoot 'gateway.pid'
$TailnetGatewayStatePath=Join-Path $TailnetRoot 'gateway-state.json'
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
        $maxAttempts=300
        for($attempt=1;$attempt -le $maxAttempts;$attempt++){
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
            try {
                [IO.File]::Replace($sourcePath,$destinationPath,$backup,$true)
                Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
                return
            } catch [IO.IOException] {
                if($attempt -ge $maxAttempts){ throw }
                # The bootstrap service may briefly have a distribution artifact open while
                # serving a remote worker. Preserve the atomic replacement contract and retry
                # the transient Windows sharing violation instead of stopping the whole build.
                Start-Sleep -Milliseconds 100
            }
        }
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

    [pscustomobject]@{
        schema_version=2
        source_hash=$sourceHash
        gateway_windows=$versionWindows
        distribution_windows=$versionWindows
        distribution_linux=$versionLinux
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

    $gatewayState=$null
    if(Test-Path -LiteralPath $TailnetGatewayStatePath){
        try{$gatewayState=Get-Content -LiteralPath $TailnetGatewayStatePath -Raw|ConvertFrom-Json}catch{$gatewayState=$null}
    }
    if($null -ne $gatewayState){
        if(Test-ManagedProcessIdentity $gatewayState $bridge){
            $null=Stop-ManagedProcessTree $gatewayState $bridge 'embedded tailnet gateway'
        } else {
            $livePid=Get-StateReferencedLivePid $gatewayState
            if($livePid -gt 0){
                throw "Embedded tailnet gateway state references live PID $livePid but the persisted process identity does not match. Refusing to kill a possibly reused PID."
            }
        }
        Remove-Item -LiteralPath $TailnetGatewayStatePath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $TailnetGatewayPidPath -Force -ErrorAction SilentlyContinue
    } elseif(Test-Path -LiteralPath $TailnetGatewayPidPath){
        $legacyPid=0
        [void][int]::TryParse((Get-Content -LiteralPath $TailnetGatewayPidPath -Raw).Trim(),[ref]$legacyPid)
        if($legacyPid -gt 0 -and (Get-Process -Id $legacyPid -ErrorAction SilentlyContinue)){
            throw "Embedded tailnet gateway PID $legacyPid is from legacy PID-only state and cannot be proven safe to kill automatically. Stop that legacy gateway once, then rerun the command."
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
    $gatewayIdentity=Get-ProcessIdentity $p.Id
    if($null -eq $gatewayIdentity -or -not [string]::Equals(
        [string]$gatewayIdentity.executable_path,
        [IO.Path]::GetFullPath($bridge),
        [StringComparison]::OrdinalIgnoreCase
    )){
        try{$p.Kill()}catch{}
        throw 'Could not establish the embedded tailnet gateway process identity after launch.'
    }
    $p.Id | Set-Content -LiteralPath $TailnetGatewayPidPath -NoNewline -Encoding ASCII
    [pscustomobject]@{
        schema_version=1
        pid=[int]$gatewayIdentity.pid
        process_start_utc=[string]$gatewayIdentity.process_start_utc
        executable_path=[string]$gatewayIdentity.executable_path
        started_utc=[DateTime]::UtcNow.ToString('o')
    }|ConvertTo-Json|Set-Content -LiteralPath $TailnetGatewayStatePath -Encoding UTF8
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

function Get-NamedFileSetSha256([object[]]$Entries){
    $manifest=@()
    $seen=@{}
    foreach($entry in @($Entries)){
        $name=([string]$entry.name).Replace('\','/')
        $filePath=[string]$entry.path
        if(-not $name){ throw 'Content-hash entry name must be non-empty.' }
        if($seen.ContainsKey($name)){ throw "Content-hash entry is duplicated: $name" }
        if(-not(Test-Path -LiteralPath $filePath -PathType Leaf)){ throw "Content-hash source file is missing: $filePath" }
        $seen[$name]=$true
        $info=Get-Item -LiteralPath $filePath
        $manifest += [pscustomobject]@{
            name=$name
            length=[int64]$info.Length
            sha256=(Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    if($manifest.Count -eq 0){ throw 'Content-hash file set must not be empty.' }
    $ordered=@($manifest|Sort-Object name)
    Get-StringSha256 ($ordered|ConvertTo-Json -Compress -Depth 3)
}

function Get-DirectoryContentSha256([string]$Root,[string[]]$ExcludeDirectoryNames=@()){
    if(-not(Test-Path -LiteralPath $Root -PathType Container)){ throw "Content-hash root is missing: $Root" }
    $resolved=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Root).Path).TrimEnd([char[]]"\/")
    $pending=New-Object 'Collections.Generic.Stack[string]'
    $pending.Push($resolved)
    $entries=@()
    while($pending.Count -gt 0){
        $current=$pending.Pop()
        foreach($item in @(Get-ChildItem -LiteralPath $current -Force)){
            if($item.PSIsContainer){
                if($ExcludeDirectoryNames -notcontains $item.Name){ $pending.Push($item.FullName) }
                continue
            }
            $relative=$item.FullName.Substring($resolved.Length).TrimStart([char[]]"\/").Replace('\','/')
            $entries += [pscustomobject]@{name=$relative;path=$item.FullName}
        }
    }
    Get-NamedFileSetSha256 $entries
}


function Get-WorkingTreeContentSha256([string]$RelativePath){
    $git=Resolve-Git
    Push-Location $AssetsRoot
    try {
        $paths=@(& $git ls-files --cached --others --exclude-standard -- $RelativePath)
        if($LASTEXITCODE -ne 0){ throw "git ls-files failed for $RelativePath." }
    } finally {
        Pop-Location
    }
    $manifest=@()
    foreach($repoPath in @($paths|Sort-Object -Unique)){
        if(-not $repoPath){ continue }
        $normalized=([string]$repoPath).Replace('\','/')
        $fullPath=Join-Path $AssetsRoot $normalized
        if(Test-Path -LiteralPath $fullPath -PathType Leaf){
            $info=Get-Item -LiteralPath $fullPath
            $manifest += [pscustomobject]@{
                name=$normalized
                length=[int64]$info.Length
                sha256=(Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        } else {
            # A tracked file deleted from the working tree must also change the identity.
            $manifest += [pscustomobject]@{
                name=$normalized
                length=[int64]-1
                sha256='missing'
            }
        }
    }
    if($manifest.Count -eq 0){ throw "Working-tree content set is empty: $RelativePath" }
    Get-StringSha256 (@($manifest|Sort-Object name)|ConvertTo-Json -Compress -Depth 3)
}

function Get-TrainingRuntimeSourceHash {
    $sourceRoot=Join-Path $AssetsRoot 'Training'
    $entries=@(
        Get-ChildItem -LiteralPath $sourceRoot -Filter '*.py' -File |
            ForEach-Object { [pscustomobject]@{name=$_.Name;path=$_.FullName} }
    )
    $entries += [pscustomobject]@{
        name='bees_remote_requirements.txt'
        path=$RemoteRequirementsPath
    }
    Get-NamedFileSetSha256 $entries
}

function Get-BeesServerDependencyHash {
    Get-NamedFileSetSha256 @(
        [pscustomobject]@{name='package.json';path=(Join-Path $ServerRoot 'package.json')},
        [pscustomobject]@{name='package-lock.json';path=(Join-Path $ServerRoot 'package-lock.json')}
    )
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
    $git=Resolve-Git
    Invoke-Checked $Python @(
        $ArchiveRunScript,
        '--assets-root',$AssetsRoot,
        '--bees-root',$BeesRoot,
        '--run-id',$RunId,
        '--reason',$Reason,
        '--git-executable',$git
    ) $AssetsRoot
}

function New-TrainingRunPlan([string]$Python,[switch]$ForceNew){
    if(-not(Test-Path -LiteralPath $RunLifecycleScript)){
        throw "Training run lifecycle helper is missing: $RunLifecycleScript"
    }
    Ensure-Directory $RunLifecycleRoot
    Ensure-Directory $RuntimeRoot
    Remove-Item -LiteralPath $RunPlanPath -Force -ErrorAction SilentlyContinue
    $planArgs=@(
        $RunLifecycleScript,'plan',
        '--assets-root',$AssetsRoot,
        '--state',$RunStatePath,
        '--out',$RunPlanPath
    )
    if($ForceNew){ $planArgs+='--force-new' }
    $null=Invoke-Checked $Python $planArgs $AssetsRoot
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
    $previousBridgeHash=$null
    if(Test-Path -LiteralPath $TailnetBridgeManifestPath){
        try {
            $previousBridgeHash=[string]((Get-Content -LiteralPath $TailnetBridgeManifestPath -Raw | ConvertFrom-Json).source_hash)
        } catch {
            $previousBridgeHash=$null
        }
    }
    Build-TailnetBridge
    $currentBridgeHash=Get-TailnetBridgeSourceHash
    $tailnetBridgeChanged=($previousBridgeHash -ne $currentBridgeHash)
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
    Save-LatestRelease $release
    Commit-TrainingRunPlan $python

    Write-Host ""
    Write-Host "Build complete: $buildId  run=$($release.run_id)"
    $artifacts | Format-Table role,platform,folder -AutoSize

    if(Test-Path -LiteralPath $AdminTokenPath){
        $admin=(Get-Content -LiteralPath $AdminTokenPath -Raw).Trim()
        if($admin -and (Test-Control ([string]$config.controlUrl) $admin)){
            $worker=Ensure-TokenFile $WorkerTokenPath
            Start-BeesServerIfNeeded $config $worker $admin
            Assert-CentralAgentCheckpointSafe
            Write-Host 'Training control is online; staging this release without stopping the active cluster.'
            if(Test-Path -LiteralPath $TailnetAddressPath){
                Prepare-RemoteBootstrap $config
                if($tailnetBridgeChanged){
                    Write-Host 'Embedded tailnet helper changed; restarting the private gateway onto the new immutable helper version.'
                    Start-TailnetGatewayIfNeeded $config
                }
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
    $serverSourceHash=Get-WorkingTreeContentSha256 'BeesServer~'
    $node=Resolve-Node $Config
    $online=Test-Control $base $AdminToken

    if($online){
        $managedState=$null
        if(Test-Path -LiteralPath $ServerStatePath){
            try{$managedState=Get-Content -LiteralPath $ServerStatePath -Raw|ConvertFrom-Json}catch{$managedState=$null}
        }
        $managedSourceHash=Get-ObjectPropertyValue $managedState 'source_hash'
        if(
            $null -ne $managedState -and
            ([string]$managedSourceHash) -eq $serverSourceHash
        ){
            if(-not(Test-ManagedProcessIdentity $managedState $node)){
                Write-Warning 'BeesServer is healthy and current, but its persisted process identity cannot be verified. Leaving it running; a future automatic restart/stop will refuse to kill it until it is relaunched under identity-safe state.'
            }
            return
        }

        if($null -eq $managedState){
            throw 'BeesServer is online but has no managed process identity. Refusing an automatic restart because an unrelated process could now own the recorded PID.'
        }
        if(-not(Test-ManagedProcessIdentity $managedState $node)){
            $managedPid=Get-StateReferencedLivePid $managedState
            if($managedPid -gt 0){
                throw "BeesServer state references live PID $managedPid but its PID/start-time/executable identity does not match. Refusing to kill a possibly reused PID."
            }
            throw 'BeesServer is online but its persisted managed process is no longer present. Refusing to guess which process owns the live server.'
        }
        Write-Host 'BeesServer source changed; restarting the managed server without changing desired training state.'
        Assert-CentralAgentCheckpointSafe
        $null=Stop-ManagedProcessTree $managedState $node 'BeesServer'
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
    $npm=Resolve-Npm
    Ensure-Directory $RuntimeRoot
    $dependencyHash=Get-BeesServerDependencyHash
    $installedDependencyHash=''
    if(Test-Path -LiteralPath $ServerDependencyStampPath){
        try{$installedDependencyHash=(Get-Content -LiteralPath $ServerDependencyStampPath -Raw).Trim().ToLowerInvariant()}catch{$installedDependencyHash=''}
    }
    $nodeModulesPath=Join-Path $ServerRoot 'node_modules'
    if(-not(Test-Path -LiteralPath $nodeModulesPath -PathType Container) -or $installedDependencyHash -ne $dependencyHash){
        Write-Host 'Installing BeesServer dependencies for the current package lock...'
        Remove-Item -LiteralPath $ServerDependencyStampPath -Force -ErrorAction SilentlyContinue
        Invoke-Checked $npm @('ci') $ServerRoot
        $dependencyHash | Set-Content -LiteralPath $ServerDependencyStampPath -NoNewline -Encoding ASCII
    }
    Ensure-Directory (Join-Path $LogsRoot 'Server'); Ensure-Directory (Join-Path $TrainingRoot 'Control')
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
            $serverIdentity=Get-ProcessIdentity $launchedPid
            if($null -eq $serverIdentity -or -not [string]::Equals(
                [string]$serverIdentity.executable_path,
                [IO.Path]::GetFullPath($node),
                [StringComparison]::OrdinalIgnoreCase
            )){
                throw 'BeesServer became reachable but its launched process identity could not be verified. Refusing to record unsafe PID-only ownership.'
            }
            [pscustomobject]@{
                schema_version=2
                pid=[int]$serverIdentity.pid
                process_start_utc=[string]$serverIdentity.process_start_utc
                executable_path=[string]$serverIdentity.executable_path
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

function Save-LatestRelease($Release){
    $releaseTemp="$LatestReleasePath.new"
    $releaseJson=$Release | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText(
        $releaseTemp,
        $releaseJson,
        (New-Object Text.UTF8Encoding($false))
    )
    Install-AtomicFile $releaseTemp $LatestReleasePath
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


function Get-ProcessIdentity([int]$Id){
    if($Id -le 0){ return $null }
    $process=Get-Process -Id $Id -ErrorAction SilentlyContinue
    if($null -eq $process){ return $null }
    try {
        $executable=[IO.Path]::GetFullPath([string]$process.Path)
        $processStartUtc=$process.StartTime.ToUniversalTime().ToString('o')
    } catch {
        return $null
    }
    if(-not $executable -or -not $processStartUtc){ return $null }
    [pscustomobject]@{
        pid=[int]$process.Id
        process_start_utc=$processStartUtc
        executable_path=$executable
    }
}

function Get-ObjectPropertyValue($Object,[string]$Name){
    if($null -eq $Object){ return $null }
    $property=$Object.PSObject.Properties[$Name]
    if($null -eq $property){ return $null }
    return $property.Value
}

function Test-ManagedProcessIdentity($State,[string]$ExpectedExecutable=''){
    $pidValue=Get-ObjectPropertyValue $State 'pid'
    $processStartUtc=Get-ObjectPropertyValue $State 'process_start_utc'
    $executablePath=Get-ObjectPropertyValue $State 'executable_path'
    if(
        $null -eq $pidValue -or
        [string]::IsNullOrWhiteSpace([string]$processStartUtc) -or
        [string]::IsNullOrWhiteSpace([string]$executablePath)
    ){
        return $false
    }
    $id=0
    if(-not [int]::TryParse(([string]$pidValue),[ref]$id) -or $id -le 0){ return $false }
    $current=Get-ProcessIdentity $id
    if($null -eq $current){ return $false }
    if(([string]$current.process_start_utc) -ne ([string]$processStartUtc)){ return $false }
    try {
        $savedExecutable=[IO.Path]::GetFullPath([string]$executablePath)
    } catch {
        return $false
    }
    if(-not [string]::Equals(
        [string]$current.executable_path,
        $savedExecutable,
        [StringComparison]::OrdinalIgnoreCase
    )){ return $false }
    if($ExpectedExecutable){
        $expected=[IO.Path]::GetFullPath($ExpectedExecutable)
        if(-not [string]::Equals(
            [string]$current.executable_path,
            $expected,
            [StringComparison]::OrdinalIgnoreCase
        )){ return $false }
    }
    return $true
}

function Get-StateReferencedLivePid($State){
    $pidValue=Get-ObjectPropertyValue $State 'pid'
    if($null -eq $pidValue){ return 0 }
    $id=0
    if(-not [int]::TryParse(([string]$pidValue),[ref]$id) -or $id -le 0){ return 0 }
    if(Get-Process -Id $id -ErrorAction SilentlyContinue){ return $id }
    return 0
}

function Stop-ManagedProcessTree($State,[string]$ExpectedExecutable,[string]$Label){
    if(-not(Test-ManagedProcessIdentity $State $ExpectedExecutable)){
        $id=Get-StateReferencedLivePid $State
        if($id -gt 0){
            throw "Refusing to stop $Label PID $id because its persisted process identity does not match the live process. The PID may have been reused."
        }
        return $false
    }
    $id=[int]$State.pid
    & taskkill /PID $id /T /F *> $null
    return $true
}

function Get-RunningCentralAgentPid {
    if(Test-Path -LiteralPath $CentralAgentStatePath){
        $state=$null
        try{$state=Get-Content -LiteralPath $CentralAgentStatePath -Raw|ConvertFrom-Json}catch{$state=$null}
        if($null -ne $state){
            if(Test-ManagedProcessIdentity $state){
                return [int]$state.pid
            }
            $livePid=Get-StateReferencedLivePid $state
            if($livePid -gt 0){
                throw "Central learner state references live PID $livePid but its PID/start-time/executable identity does not match. Refusing to treat a possibly reused PID as the learner."
            }
        }
    }
    if(Test-Path -LiteralPath $CentralAgentPidPath){
        $legacyPid=0
        [void][int]::TryParse(
            (Get-Content -LiteralPath $CentralAgentPidPath -Raw).Trim(),
            [ref]$legacyPid
        )
        if($legacyPid -gt 0 -and (Get-Process -Id $legacyPid -ErrorAction SilentlyContinue)){
            throw "Central learner PID $legacyPid is recorded only in legacy PID-only state and cannot be proven to be the managed learner."
        }
    }
    return 0
}

function Assert-CentralAgentCheckpointSafe {
    $id=Get-RunningCentralAgentPid
    if($id -le 0){ return }
    $safe=$false
    if(Test-Path -LiteralPath $CentralAgentStatePath){
        try{
            $state=Get-Content -LiteralPath $CentralAgentStatePath -Raw|ConvertFrom-Json
            $statePid=Get-ObjectPropertyValue $state 'pid'
            $gracefulCheckpointShutdown=Get-ObjectPropertyValue $state 'graceful_checkpoint_shutdown'
            $safe=(
                $null -ne $statePid -and
                ([int]$statePid) -eq $id -and
                (Test-ManagedProcessIdentity $state) -and
                [bool]$gracefulCheckpointShutdown
            )
        }catch{ $safe=$false }
    }
    if(-not $safe){
        throw "Running central learner PID $id is not backed by checkpoint-safe verified process identity. Refusing an operation that could stop the wrong process or lose optimizer progress."
    }
}

function Stop-CentralAgentGracefully([int]$Id,[int]$TimeoutSeconds=150){
    if($Id -le 0){ return $true }
    $state=$null
    if(Test-Path -LiteralPath $CentralAgentStatePath){
        try{$state=Get-Content -LiteralPath $CentralAgentStatePath -Raw|ConvertFrom-Json}catch{$state=$null}
    }
    $statePid=Get-ObjectPropertyValue $state 'pid'
    if($null -eq $state -or $null -eq $statePid -or ([int]$statePid) -ne $Id){
        throw "Refusing graceful-stop request for central learner PID $Id because no matching managed process identity is recorded."
    }
    if(-not(Test-ManagedProcessIdentity $state)){
        $livePid=Get-StateReferencedLivePid $state
        if($livePid -gt 0){
            throw "Refusing graceful-stop request for central learner PID $Id because the PID now belongs to a different process identity."
        }
        return $true
    }

    Assert-CentralAgentCheckpointSafe

    Ensure-Directory $CentralAgentInstallRoot
    Remove-Item -LiteralPath $CentralAgentShutdownRequestPath -Force -ErrorAction SilentlyContinue
    [IO.File]::WriteAllText(
        $CentralAgentShutdownRequestPath,
        "stop`n",
        (New-Object Text.UTF8Encoding($false))
    )
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while([DateTime]::UtcNow -lt $deadline){
        if(-not(Test-ManagedProcessIdentity $state)){
            Remove-Item -LiteralPath $CentralAgentShutdownRequestPath -Force -ErrorAction SilentlyContinue
            return $true
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Central learner PID $Id is still finalizing its checkpoint after $TimeoutSeconds seconds. Refusing forced termination; the existing learner remains authoritative."
}

function Start-CentralAgentIfNeeded($Config,[string]$Python,[string]$Unity){
    Ensure-Directory $RuntimeRoot; Ensure-Directory (Join-Path $LogsRoot 'Training'); Ensure-Directory $CentralAgentInstallRoot
    $outLog=Join-Path $LogsRoot 'Training\central-agent.out.log'; $errLog=Join-Path $LogsRoot 'Training\central-agent.err.log'
    $agent=Join-Path $AssetsRoot 'Training\bees_training_worker_agent.py'; $service=Join-Path $AssetsRoot 'Training\bees_continual_elastic_wan_service.py'
    $telemetry=Join-Path $TrainingRoot 'Telemetry'; $models=Join-Path $TrainingRoot 'Models'; Ensure-Directory $telemetry; Ensure-Directory $models
    $args=@('-u',$agent,'--server-url',[string]$Config.controlUrl,'--token-file',$WorkerTokenPath,'--trainer-id','central-learner','--role','dedicated','--platform','WindowsPlayer','--install-root',$CentralAgentInstallRoot,'--shutdown-request-file',$CentralAgentShutdownRequestPath,'--',$Python,$service,"--root=$TrainingRoot","--assets-root=$AssetsRoot",'--training-env={env}',"--telemetry-quarantine=$telemetry","--model-distribution-root=$models",'--game-build-version={build_id}','--run-id={run_id}',"--unity-editor=$Unity","--unity-project-root=$BeesRoot","--generation-steps=$($Config.generationSteps)","--num-envs=$($Config.numLocalEnvs)",'--platform=WindowsPlayer',"--bees-wan-actors=$($Config.maxRemoteActors)","--bees-wan-min-actors=$($Config.minRemoteActors)","--bees-wan-broker-port=$($Config.brokerPort)","--bees-wan-auth-token-file=$WanTokenPath")
    $argString=($args|ForEach-Object{Quote-Arg ([string]$_)}) -join ' '
    $trainingSourceHash=Get-TrainingRuntimeSourceHash
    $commandHash=Get-StringSha256 ($Python + [Environment]::NewLine + $argString + [Environment]::NewLine + $trainingSourceHash)
    $existing=$null
    if(Test-Path -LiteralPath $CentralAgentStatePath){
        try{$existing=Get-Content -LiteralPath $CentralAgentStatePath -Raw|ConvertFrom-Json}catch{$existing=$null}
        if($null -ne $existing){
            if(Test-ManagedProcessIdentity $existing $Python){
                if(([string](Get-ObjectPropertyValue $existing 'command_hash')) -eq $commandHash){ return }
                Write-Host 'Central training configuration changed; checkpointing before restarting the managed central agent.'
                $null=Stop-CentralAgentGracefully ([int]$existing.pid)
            } else {
                $livePid=Get-StateReferencedLivePid $existing
                if($livePid -gt 0){
                    throw "Central learner state references live PID $livePid but its managed process identity does not match. Refusing to stop or replace a possibly reused PID."
                }
                Remove-Item -LiteralPath $CentralAgentStatePath -Force -ErrorAction SilentlyContinue
            }
        }
    }
    if(Test-Path -LiteralPath $CentralAgentPidPath) {
        $legacyPid=0
        [void][int]::TryParse((Get-Content -LiteralPath $CentralAgentPidPath -Raw).Trim(),[ref]$legacyPid)
        if($legacyPid -gt 0 -and (Get-Process -Id $legacyPid -ErrorAction SilentlyContinue)){
            throw "Central learner PID $legacyPid is from legacy PID-only state. Refusing to stop it automatically because the PID may have been reused."
        }
    }
    Remove-Item -LiteralPath $CentralAgentPidPath -Force -ErrorAction SilentlyContinue
    $p=Start-Process -FilePath $Python -ArgumentList $argString -WorkingDirectory $AssetsRoot -RedirectStandardOutput $outLog -RedirectStandardError $errLog -WindowStyle Hidden -PassThru
    $identity=Get-ProcessIdentity $p.Id
    if($null -eq $identity -or -not [string]::Equals(
        [string]$identity.executable_path,
        [IO.Path]::GetFullPath($Python),
        [StringComparison]::OrdinalIgnoreCase
    )){
        try{$p.Kill()}catch{}
        throw 'Could not establish the central learner process identity after launch.'
    }
    $p.Id | Set-Content -LiteralPath $CentralAgentPidPath -NoNewline
    [pscustomobject]@{
        schema_version=2
        pid=[int]$identity.pid
        process_start_utc=[string]$identity.process_start_utc
        executable_path=[string]$identity.executable_path
        command_hash=$commandHash
        graceful_checkpoint_shutdown=$true
        started_utc=[DateTime]::UtcNow.ToString('o')
    }|ConvertTo-Json|Set-Content -LiteralPath $CentralAgentStatePath -Encoding UTF8
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
        # Version the exact staged payload bytes, not the committed Git tree or live source
        # directory, so dirty/uncommitted changes and mid-packaging edits cannot be mislabeled.
        $runtimeVersion=Get-DirectoryContentSha256 $staging
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
    $linuxBody=$linuxBody.Replace("`r`n","`n").Replace("`r","`n")
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
    $linuxWrapper=$linuxWrapper.Replace("`r`n","`n").Replace("`r","`n")
    if(-not $linuxWrapper.StartsWith("#!/usr/bin/env bash`n")){
        throw 'Generated Linux remote launcher has an invalid shebang/newline layout.'
    }
    if($linuxWrapper.StartsWith('#!/usr/bin/env bash\n')){
        throw 'Generated Linux remote launcher contains escaped newlines instead of LF characters.'
    }
    [IO.File]::WriteAllText((Join-Path $RemoteRoot 'bees-remote-worker.sh'),$linuxWrapper,$utf8NoBom)

    Write-Host "Remote launchers prepared in $RemoteRoot."
    Write-Host 'No SSH account, SSH keys, SSH server, port forwarding, or separate Tailscale installation is required.'
    Write-Host 'Windows: run bees-remote-worker.cmd to start in the background; run bees-remote-worker.cmd stop to stop it.'
    Write-Host "Linux:   run 'bash bees-remote-worker.sh' to start in the background; run 'bash bees-remote-worker.sh stop' to stop it."
    Write-Host 'Pass -Envs N (Windows) or --envs N (Linux) only to pin a fixed environment count.'
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
        if($NewRun){
            throw "Cannot force a new training run before the first RL build exists. Run '.\Assets\bees.ps1 build' first."
        }
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

    Assert-CentralAgentCheckpointSafe

    $forcedPlan=$null
    $outgoingRun=$null
    if($NewRun){
        $status=Invoke-ControlGet "$($config.controlUrl)/v1/status" $admin
        if($status.desired.pending_release){
            throw 'Cannot force a new training run while another release rollout is pending.'
        }
        $outgoingRun=([string]$status.desired.run_id).Trim()
        if(-not $outgoingRun){ $outgoingRun=([string]$release.run_id).Trim() }
        Archive-TrainingRun $python $outgoingRun 'forced-new-precutover'

        $forcedPlan=New-TrainingRunPlan $python -ForceNew
        if($forcedPlan.previous_run_id -and
            $outgoingRun -and
            ([string]$forcedPlan.previous_run_id) -ne $outgoingRun){
            throw "Run lifecycle state disagrees with active training run. lifecycle=$($forcedPlan.previous_run_id) active=$outgoingRun"
        }
        $release=[pscustomobject]@{
            schema_version=$release.schema_version
            build_id=[string]$release.build_id
            source_commit=[string]$release.source_commit
            created_utc=[string]$release.created_utc
            run_id=[string]$forcedPlan.run_id
            previous_run_id=$outgoingRun
            compatibility_key=[string]$forcedPlan.compatibility_key
            incompatible=$true
            contract=$forcedPlan.contract
            artifacts=$release.artifacts
        }
        Write-Host "Forcing fresh training run: $($release.run_id) (same build $($release.build_id))."
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

    if($NewRun){
        $null=Wait-ReleaseRollout $config $admin ([string]$release.build_id)
        Save-LatestRelease $release
        Commit-TrainingRunPlan $python
        if($outgoingRun){
            Start-Sleep -Seconds 2
            Archive-TrainingRun $python $outgoingRun 'forced-new-final'
        }
        Write-Host "Forced new-run cutover complete. Active run: $($release.run_id)"
    }

    Write-Host "Training requested: build=$($release.build_id) run=$($release.run_id) revision=$($desired.revision)"
    if($staged.pending_release){
        Write-Host "Release rollout: $($staged.pending_release.phase) incompatible=$($staged.pending_release.incompatible)"
    }
    Write-Host "Environment arguments: $(if($envArgs.Count){$envArgs -join ' '}else{'(none; defaults)'})"
    Start-Sleep -Seconds 1
    Show-Status $config $admin $true
}

function Invoke-Stop {
    $config=Get-ClusterConfig; $admin=Ensure-TokenFile $AdminTokenPath
    Assert-CentralAgentCheckpointSafe
    if(Test-Control ([string]$config.controlUrl) $admin){
        $desired=Invoke-ControlPost "$($config.controlUrl)/v1/admin/state" $admin @{training_enabled=$false}; Write-Host "Training stop requested at revision $($desired.revision)."
        $deadline=[DateTime]::UtcNow.AddSeconds(180)
        $running=@()
        while([DateTime]::UtcNow -lt $deadline){
            $s=Invoke-ControlGet "$($config.controlUrl)/v1/status" $admin
            $running=@($s.trainers|Where-Object{-not $_.stale -and $_.role -eq 'dedicated' -and $_.process_state -ne 'stopped'})
            if($running.Count -eq 0){break}; Start-Sleep -Milliseconds 500
        }
        if($running.Count -gt 0){
            $names=($running|ForEach-Object{"$($_.trainer_id):$($_.process_state)"}) -join ', '
            throw "Dedicated trainers are still finalizing after 180 seconds ($names). Refusing to stop BeesServer while checkpoint/log preservation is incomplete."
        }
    } else {
        if($Server -and (Get-RunningCentralAgentPid) -gt 0){
            throw 'Training control is offline while the central learner is still running. Refusing to stop BeesServer because checkpoint completion cannot be coordinated.'
        }
        Write-Warning 'Training control is offline; dedicated workers should fail closed after lease expiry.'
    }
    if($Server){
        $node=Resolve-Node $config
        $serverState=$null
        if(Test-Path -LiteralPath $ServerStatePath){
            try{$serverState=Get-Content -LiteralPath $ServerStatePath -Raw|ConvertFrom-Json}catch{$serverState=$null}
        }
        if($null -ne $serverState){
            if(Test-ManagedProcessIdentity $serverState $node){
                $null=Stop-ManagedProcessTree $serverState $node 'BeesServer'
                Write-Host 'BeesServer stopped.'
            } else {
                $livePid=Get-StateReferencedLivePid $serverState
                if($livePid -gt 0){
                    throw "Refusing to stop BeesServer PID $livePid because its persisted process identity does not match the live process."
                }
            }
            Remove-Item -LiteralPath $ServerPidPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $ServerStatePath -Force -ErrorAction SilentlyContinue
        } elseif(Test-Path -LiteralPath $ServerPidPath){
            $legacyPid=0
            [void][int]::TryParse((Get-Content -LiteralPath $ServerPidPath -Raw).Trim(),[ref]$legacyPid)
            if($legacyPid -gt 0 -and (Get-Process -Id $legacyPid -ErrorAction SilentlyContinue)){
                throw "Refusing to stop legacy BeesServer PID $legacyPid because PID-only ownership cannot exclude PID reuse."
            }
            Remove-Item -LiteralPath $ServerPidPath -Force -ErrorAction SilentlyContinue
        }

        $bridges=Get-TailnetBridgePaths
        $gatewayExecutable=[string]$bridges.gateway_windows
        $gatewayState=$null
        if(Test-Path -LiteralPath $TailnetGatewayStatePath){
            try{$gatewayState=Get-Content -LiteralPath $TailnetGatewayStatePath -Raw|ConvertFrom-Json}catch{$gatewayState=$null}
        }
        if($null -ne $gatewayState){
            if(Test-ManagedProcessIdentity $gatewayState $gatewayExecutable){
                $null=Stop-ManagedProcessTree $gatewayState $gatewayExecutable 'embedded tailnet gateway'
                Write-Host 'Embedded Bees tailnet gateway stopped.'
            } else {
                $livePid=Get-StateReferencedLivePid $gatewayState
                if($livePid -gt 0){
                    throw "Refusing to stop embedded tailnet gateway PID $livePid because its persisted process identity does not match the live process."
                }
            }
            Remove-Item -LiteralPath $TailnetGatewayStatePath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $TailnetGatewayPidPath -Force -ErrorAction SilentlyContinue
        } elseif(Test-Path -LiteralPath $TailnetGatewayPidPath){
            $legacyPid=0
            [void][int]::TryParse((Get-Content -LiteralPath $TailnetGatewayPidPath -Raw).Trim(),[ref]$legacyPid)
            if($legacyPid -gt 0 -and (Get-Process -Id $legacyPid -ErrorAction SilentlyContinue)){
                throw "Refusing to stop legacy tailnet gateway PID $legacyPid because PID-only ownership cannot exclude PID reuse."
            }
            Remove-Item -LiteralPath $TailnetGatewayPidPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-LocalLearnerStats {
    $elo=$null; $step=$null; $reward=$null; $averageStepsPerSecond=$null; $liveStepsPerSecond=$null
    $files=@()
    foreach($root in @((Join-Path $LogsRoot 'Training'),(Join-Path $TrainingRoot 'trainer-results'))){
        if(Test-Path -LiteralPath $root){
            $files += @(Get-ChildItem -LiteralPath $root -Filter '*.log' -File -Recurse -ErrorAction SilentlyContinue)
        }
    }
    foreach($file in @($files | Sort-Object LastWriteTimeUtc,FullName)){
        $firstStep=$null; $firstElapsed=$null; $previousStep=$null; $previousElapsed=$null
        $fileAverageStepsPerSecond=$null; $fileLiveStepsPerSecond=$null
        foreach($line in @(Get-Content -LiteralPath $file.FullName -Tail 1000 -ErrorAction SilentlyContinue)){
            if($line -match '(?i)\bELO\b[^-0-9]*(-?\d+(?:\.\d+)?)'){$elo=[double]$Matches[1]}
            $lineStep=$null
            $lineElapsed=$null
            if($line -match '(?i)\bStep\s*[:=]\s*(\d+)'){
                $lineStep=[long]$Matches[1]
                $step=$lineStep
            }
            if($line -match '(?i)Mean Reward\s*[:=]\s*(-?\d+(?:\.\d+)?)'){$reward=[double]$Matches[1]}
            if($line -match '(?i)Time Elapsed\s*[:=]\s*(\d+(?:\.\d+)?)\s*s'){
                $lineElapsed=[double]$Matches[1]
            }
            if($null -ne $lineStep -and $null -ne $lineElapsed){
                if($null -eq $firstStep -or $null -eq $previousStep -or
                   $lineStep -lt $previousStep -or $lineElapsed -le $previousElapsed){
                    $firstStep=$lineStep
                    $firstElapsed=$lineElapsed
                    $fileAverageStepsPerSecond=$null
                    $fileLiveStepsPerSecond=$null
                }else{
                    $elapsedDelta=$lineElapsed-$previousElapsed
                    if($elapsedDelta -gt 0){
                        $fileLiveStepsPerSecond=($lineStep-$previousStep)/$elapsedDelta
                    }
                    $averageElapsed=$lineElapsed-$firstElapsed
                    if($averageElapsed -gt 0){
                        $fileAverageStepsPerSecond=($lineStep-$firstStep)/$averageElapsed
                    }
                }
                $previousStep=$lineStep
                $previousElapsed=$lineElapsed
            }
        }
        if($null -ne $fileAverageStepsPerSecond){$averageStepsPerSecond=$fileAverageStepsPerSecond}
        if($null -ne $fileLiveStepsPerSecond){$liveStepsPerSecond=$fileLiveStepsPerSecond}
    }
    [pscustomobject]@{
        ELO=$elo
        Step=$step
        MeanReward=$reward
        AverageStepsPerSecond=$averageStepsPerSecond
        LiveStepsPerSecond=$liveStepsPerSecond
    }
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
            $cap=$_.worker_capacity
            $opt=$_.env_optimizer
            $envDisplay='-'
            if($cap -and $null -ne $cap.current_envs){
                $envDisplay=[string]$cap.current_envs
                if($opt -and $null -ne $opt.desired_envs -and
                   [int]$opt.desired_envs -ne [int]$cap.current_envs){
                    $envDisplay="$($cap.current_envs)->$($opt.desired_envs)"
                }
            }
            $acceptedSps='-'
            if($opt -and $null -ne $opt.measured_sps){
                $acceptedSps=('{0:N0}'-f[double]$opt.measured_sps)
            }elseif($opt -and $null -ne $opt.baseline_sps){
                $acceptedSps=('{0:N0}'-f[double]$opt.baseline_sps)
            }
            [pscustomobject]@{
                Trainer=$_.trainer_id
                Role=$_.role
                Platform=$_.platform
                State=if($_.stale){'STALE'}else{$_.process_state}
                Envs=$envDisplay
                SPS=$acceptedSps
                Opt=if($opt -and $opt.phase){[string]$opt.phase}else{'-'}
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
            $table=($rows|Format-Table Trainer,Role,Platform,State,Envs,SPS,Opt,Build,Rev,Age,Timeout,BWin,HWin,Draw,Dur,BeeHit,HumanHit,Error -AutoSize|Out-String -Width 260).TrimEnd()
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
        $lines += ("Learner logs: Step={0}  ELO={1}  MeanReward={2}  AvgSPS={3}  LiveSPS={4}" -f $(if($null -eq $l.Step){'-'}else{$l.Step}),$(if($null -eq $l.ELO){'-'}else{'{0:N1}'-f$l.ELO}),$(if($null -eq $l.MeanReward){'-'}else{'{0:N3}'-f$l.MeanReward}),$(if($null -eq $l.AverageStepsPerSecond){'-'}else{'{0:N1}'-f$l.AverageStepsPerSecond}),$(if($null -eq $l.LiveStepsPerSecond){'-'}else{'{0:N1}'-f$l.LiveStepsPerSecond}))
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

function Write-DiagnosticJson([string]$Path,$Value){
    $json=$Value | ConvertTo-Json -Depth 24
    [IO.File]::WriteAllText(
        $Path,
        $json + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false))
    )
}

function Request-CentralDiagnosticModelSnapshot($Status,[string]$TargetRunId,[string]$OutputPath){
    $result=[ordered]@{
        schema_version=1
        status='skipped'
        run_id=$TargetRunId
        requested_utc=[DateTime]::UtcNow.ToString('o')
        reason=''
    }
    try {
        if($null -eq $Status){
            $result.reason='training control is unavailable'
            return
        }
        $activeRun=if($Status.desired -and $Status.desired.run_id){([string]$Status.desired.run_id).Trim()}else{''}
        if(-not $TargetRunId){
            $result.reason='no active run could be determined'
            return
        }
        if($activeRun -ne $TargetRunId){
            $result.reason="requested run $TargetRunId is not the active run $activeRun"
            return
        }

        $central=@($Status.trainers | Where-Object { $_.trainer_id -eq 'central-learner' } | Select-Object -First 1)
        if($central.Count -eq 0){
            $result.reason='central learner is not registered'
            return
        }
        $centralRecord=$central[0]
        if($centralRecord.stale -or ([string]$centralRecord.process_state) -ne 'running'){
            $result.reason="central learner is not actively training (state=$($centralRecord.process_state) stale=$($centralRecord.stale))"
            return
        }
        if((Get-RunningCentralAgentPid) -le 0){
            $result.reason='managed central learner process is not running'
            return
        }

        Ensure-Directory $CentralAgentInstallRoot
        $requestId=[Guid]::NewGuid().ToString('N')
        $request=[ordered]@{
            schema_version=1
            request_id=$requestId
            run_id=$TargetRunId
            requested_utc=[DateTime]::UtcNow.ToString('o')
        }
        Remove-Item -LiteralPath $CentralModelSnapshotResponsePath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $CentralModelSnapshotRequestPath -Force -ErrorAction SilentlyContinue
        $requestTemp="$CentralModelSnapshotRequestPath.new-$requestId"
        Write-DiagnosticJson $requestTemp $request
        Install-AtomicFile $requestTemp $CentralModelSnapshotRequestPath

        Write-Host 'Requesting current learner ONNX snapshot...'
        $deadline=[DateTime]::UtcNow.AddSeconds(60)
        while([DateTime]::UtcNow -lt $deadline){
            if(Test-Path -LiteralPath $CentralModelSnapshotResponsePath){
                try {
                    $response=Get-Content -LiteralPath $CentralModelSnapshotResponsePath -Raw | ConvertFrom-Json
                    if(([string]$response.request_id) -eq $requestId){
                        $result=[ordered]@{}
                        foreach($property in $response.PSObject.Properties){
                            $result[$property.Name]=$property.Value
                        }
                        return
                    }
                } catch {}
            }
            Start-Sleep -Milliseconds 200
        }
        $result.status='timeout'
        $result.reason='live learner did not complete the diagnostic model snapshot within 60 seconds'
    } catch {
        $result.status='failed'
        $result.reason="$($_.Exception.GetType().Name): $($_.Exception.Message)"
    } finally {
        Remove-Item -LiteralPath $CentralModelSnapshotRequestPath -Force -ErrorAction SilentlyContinue
        Write-DiagnosticJson $OutputPath $result
    }
}

function Invoke-Bundle {
    $config=Get-ClusterConfig
    $python=Resolve-Python $config
    if(-not(Test-Path -LiteralPath $DiagnosticBundleScript)){
        throw "Training diagnostic bundle helper is missing: $DiagnosticBundleScript"
    }

    Ensure-Directory $RuntimeRoot
    $admin=Ensure-TokenFile $AdminTokenPath
    $bundleId=[Guid]::NewGuid().ToString('N')
    $statusJson=Join-Path $RuntimeRoot "diagnostic-status-$bundleId.json"
    $statusText=Join-Path $RuntimeRoot "diagnostic-status-$bundleId.txt"
    $snapshotJson=Join-Path $RuntimeRoot "diagnostic-model-snapshot-$bundleId.json"
    $status=$null

    try {
        try {
            if(Test-Control ([string]$config.controlUrl) $admin){
                $status=Invoke-ControlGet "$($config.controlUrl)/v1/status" $admin
            }
        } catch {
            Write-Warning "Could not query live training-control state: $($_.Exception.Message)"
        }

        $targetRun=if($RunId){$RunId}else{Get-ActiveRunId $config}
        Request-CentralDiagnosticModelSnapshot $status $targetRun $snapshotJson

        # Refresh status after the snapshot so learner-step/model-lag diagnostics compare
        # against the same moment rather than the pre-export state.
        try {
            if(Test-Control ([string]$config.controlUrl) $admin){
                $status=Invoke-ControlGet "$($config.controlUrl)/v1/status" $admin
                Write-DiagnosticJson $statusJson $status
            }
        } catch {
            Write-Warning "Could not capture live training-control JSON: $($_.Exception.Message)"
        }

        try {
            $statusLines=@(Get-StatusFrameLines $config $admin)
            [IO.File]::WriteAllLines(
                $statusText,
                $statusLines,
                (New-Object Text.UTF8Encoding($false))
            )
        } catch {
            Write-Warning "Could not capture readable training status: $($_.Exception.Message)"
        }

        $percentText=$LogPercent.ToString('G',[Globalization.CultureInfo]::InvariantCulture)
        $arguments=@(
            $DiagnosticBundleScript,
            '--bees-root',$BeesRoot,
            '--assets-root',$AssetsRoot,
            '--log-percent',$percentText,
            '--output-root',(Join-Path $BeesRoot 'Diagnostics')
        )
        if($RunId){
            $arguments+=@('--run-id',$RunId)
        }
        if(Test-Path -LiteralPath $statusJson){
            $arguments+=@('--status-json',$statusJson)
        }
        if(Test-Path -LiteralPath $statusText){
            $arguments+=@('--status-text',$statusText)
        }
        if(Test-Path -LiteralPath $snapshotJson){
            $arguments+=@('--snapshot-json',$snapshotJson)
        }

        Invoke-Checked $python $arguments $AssetsRoot | Out-Host
    } finally {
        Remove-Item -LiteralPath $statusJson -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $statusText -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $snapshotJson -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Status { $config=Get-ClusterConfig; $admin=Ensure-TokenFile $AdminTokenPath; Show-Status $config $admin ([bool]$Once) }

switch($Command){
    'build'{Invoke-Build}
    'server'{Invoke-Server}
    'start'{Invoke-Start}
    'stop'{Invoke-Stop}
    'status'{Invoke-Status}
    'bundle'{Invoke-Bundle}
}
