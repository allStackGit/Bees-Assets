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
$DiagnosticBenchmarkScript=Join-Path $AssetsRoot 'Training\bees_training_diagnostic_benchmark.py'
$ReleaseRuntimeScript=Join-Path $AssetsRoot 'Training\bees_release_runtime.py'
$BootstrapBundleScript=Join-Path $AssetsRoot 'Training\bees_bootstrap_bundle.py'
$ReleaseRuntimeInstallRoot=Join-Path $RuntimeRoot 'TrainingReleases'
$BootstrapBundlePath=Join-Path $RemoteRoot 'bees-bootstrap-bundle.zip'
$ServerPidPath=Join-Path $RuntimeRoot 'bees-server.pid'
$ServerStatePath=Join-Path $RuntimeRoot 'bees-server-state.json'
$ServerReleaseRoot=Join-Path $RuntimeRoot 'ServerReleases'
$CentralAgentPidPath=Join-Path $RuntimeRoot 'central-training-agent.pid'
$CentralAgentStatePath=Join-Path $RuntimeRoot 'central-training-agent.json'
$CentralAgentInstallRoot=Join-Path $BeesRoot 'ManagedBuilds\central-learner'
$CentralAgentShutdownRequestPath=Join-Path $CentralAgentInstallRoot 'worker-shutdown.request'
$CentralRuntimePointerPath=Join-Path $CentralAgentInstallRoot 'release-runtime.json'
$CentralRuntimeReadyBuildPath=Join-Path $CentralAgentInstallRoot 'runtime-ready-build.txt'
$CentralRuntimeStatePath=Join-Path $CentralAgentInstallRoot 'active-runtime.json'
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

function Ensure-LearnerPython($Config,[string]$RequirementsRoot=''){
    $requirementsRootPath=if($RequirementsRoot){[IO.Path]::GetFullPath($RequirementsRoot)}else{Join-Path $AssetsRoot 'Training'}
    $learnerRequirements=Join-Path $requirementsRootPath 'bees_learner_requirements.txt'
    $remoteRequirements=Join-Path $requirementsRootPath 'bees_remote_requirements.txt'
    if(-not(Test-Path -LiteralPath $learnerRequirements)){
        throw "Learner Python requirements are missing: $learnerRequirements"
    }
    if(-not(Test-Path -LiteralPath $remoteRequirements)){
        throw "Shared Python requirements are missing: $remoteRequirements"
    }

    $basePython=Resolve-Python $Config
    if(-not(Test-PythonCode $basePython 'import sys; raise SystemExit(0 if sys.version_info[:2] == (3,10) else 1)')){
        throw "Bees learner requires Python 3.10. Configured python resolved to '$basePython'."
    }

    $requirementsHash=Get-StringSha256 (
        (Get-Content -LiteralPath $learnerRequirements -Raw) +
        [Environment]::NewLine +
        (Get-Content -LiteralPath $remoteRequirements -Raw)
    )
    $venvBase=Join-Path $RuntimeRoot 'LearnerPython'
    $venvRoot=Join-Path $venvBase $requirementsHash
    $venvPython=Join-Path $venvRoot 'Scripts\python.exe'
    if(-not(Test-Path -LiteralPath $venvPython)){
        Write-Host "Creating release-isolated learner Python environment at $venvRoot..."
        Ensure-Directory $venvBase
        Invoke-Checked $basePython @('-m','venv',$venvRoot) $AssetsRoot | Out-Host
    }

    $stampPath=Join-Path $venvRoot 'bees-requirements.sha256'
    $currentStamp=if(Test-Path -LiteralPath $stampPath){(Get-Content -LiteralPath $stampPath -Raw).Trim()}else{''}
    $preflight='import sys, mlagents, torch, numpy, onnxruntime; raise SystemExit(0 if sys.version_info[:2] == (3,10) else 1)'
    $importsOk=($currentStamp -eq $requirementsHash) -and (Test-PythonCode $venvPython $preflight)

    if(-not $importsOk){
        Write-Host "Installing central learner dependencies for runtime $requirementsHash..."
        Invoke-Checked $venvPython @('-m','pip','install','--upgrade','pip') $requirementsRootPath | Out-Host
        Invoke-Checked $venvPython @('-m','pip','install','-r',$learnerRequirements) $requirementsRootPath | Out-Host
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

    $gatewayState=$null
    if(Test-Path -LiteralPath $TailnetGatewayStatePath){
        try{$gatewayState=Get-Content -LiteralPath $TailnetGatewayStatePath -Raw|ConvertFrom-Json}catch{$gatewayState=$null}
    }
    if($null -ne $gatewayState){
        if(Test-ManagedProcessIdentity $gatewayState){
            if(-not(Test-Path -LiteralPath $TailnetAddressPath)){
                throw 'Embedded tailnet gateway is running but its persisted learner IPv4 address is missing. Refusing to start a second tsnet server against the same state directory.'
            }
            $tailnetIp=(Get-Content -LiteralPath $TailnetAddressPath -Raw).Trim()
            if($tailnetIp -notmatch '^100\.(?:\d{1,3}\.){2}\d{1,3}$'){
                throw "Embedded tailnet gateway is running but its persisted learner IPv4 address is invalid: $tailnetIp"
            }
            Write-Host "Embedded Bees tailnet identity already active at $tailnetIp; reusing the live gateway state."
            return
        }
        $livePid=Get-StateReferencedLivePid $gatewayState
        if($livePid -gt 0){
            throw "Tailnet gateway state references live PID $livePid but its persisted PID/start-time/executable identity does not match. Refusing concurrent authentication against a possibly unrelated process/state owner."
        }
    }

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

    foreach($path in @(
        $BootstrapBundlePath,
        $BootstrapTokenPath
    )){
        if(-not(Test-Path -LiteralPath $path)){ throw "Tailnet gateway input is missing: $path" }
    }

    $argList=@(
        'gateway',
        '--state',$state,
        '--hostname',$hostname,
        '--control-port',[string]$controlPort,
        '--broker-port',[string]$brokerPort,
        '--bootstrap-port',[string]$bootstrapPort,
        '--bootstrap-bundle',$BootstrapBundlePath,
        '--bootstrap-token',$BootstrapTokenPath
    )
    # The complete worker bootstrap is one atomically replaced outer ZIP. Publishing a new
    # release therefore does not require recycling the private endpoint and a worker can never
    # observe a cross-generation mix of release metadata/runtime/tokens/helper binaries.
    # Restart only when process-level gateway configuration actually changes.
    $bootstrapTokenSha=(Get-FileHash -LiteralPath $BootstrapTokenPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $gatewayConfigHash=Get-StringSha256 (
        ([IO.Path]::GetFullPath($bridge)) + [Environment]::NewLine +
        ($argList -join [Environment]::NewLine) + [Environment]::NewLine +
        "bootstrap-token-sha256=$bootstrapTokenSha"
    )

    $gatewayState=$null
    if(Test-Path -LiteralPath $TailnetGatewayStatePath){
        try{$gatewayState=Get-Content -LiteralPath $TailnetGatewayStatePath -Raw|ConvertFrom-Json}catch{$gatewayState=$null}
    }
    if($null -ne $gatewayState){
        if(Test-ManagedProcessIdentity $gatewayState){
            $recordedConfigHash=[string](Get-ObjectPropertyValue $gatewayState 'config_hash')
            $desiredExecutableMatches=Test-ManagedProcessIdentity $gatewayState $bridge
            if($desiredExecutableMatches -and $recordedConfigHash -eq $gatewayConfigHash){
                $tailnetIp=(Get-Content -LiteralPath $TailnetAddressPath -Raw).Trim()
                Write-Host ("Embedded tailnet gateway already healthy at {0}: control={1} broker={2} bootstrap={3} (PID {4})." -f $tailnetIp,$controlPort,$brokerPort,$bootstrapPort,[int]$gatewayState.pid)
                return
            }
            Write-Host 'Embedded tailnet gateway executable/configuration changed; replacing the verified owned gateway.'
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
        schema_version=2
        pid=[int]$gatewayIdentity.pid
        process_start_utc=[string]$gatewayIdentity.process_start_utc
        executable_path=[string]$gatewayIdentity.executable_path
        config_hash=$gatewayConfigHash
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

function Get-UnityProcessesForProject([string]$ProjectPath){
    $normalized=[IO.Path]::GetFullPath($ProjectPath).TrimEnd([char[]]"\\/")
    $matches=@()
    try {
        foreach($process in @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" -ErrorAction SilentlyContinue)){
            $commandLine=[string]$process.CommandLine
            if(-not $commandLine){ continue }
            if($commandLine.IndexOf($normalized,[StringComparison]::OrdinalIgnoreCase) -ge 0){
                $matches += [pscustomobject]@{
                    pid=[int]$process.ProcessId
                    command_line=$commandLine
                }
            }
        }
    } catch {}
    return @($matches)
}

function Assert-UnityProjectAvailableForBatchBuild {
    $lock=Join-Path $BeesRoot 'Temp\UnityLockfile'
    if(-not(Test-Path -LiteralPath $lock)){ return }

    $projectProcesses=@(Get-UnityProcessesForProject $BeesRoot)

    # Unity may have been closing while this check ran. Do not report a vanished lock as stale.
    if(-not(Test-Path -LiteralPath $lock)){ return }

    if($projectProcesses.Count -gt 0){
        $pids=(@($projectProcesses|ForEach-Object{[string]$_.pid}) -join ', ')
        throw "The Bees Unity project is open in a live Unity Editor process (PID(s): $pids). Close that Editor before running '.\Assets\bees.ps1 build'."
    }

    $anyUnity=@(Get-Process -Name 'Unity' -ErrorAction SilentlyContinue)
    if($anyUnity.Count -eq 0){
        try {
            Remove-Item -LiteralPath $lock -Force -ErrorAction Stop
            Write-Warning "Removed stale Unity lock file because no Unity Editor process is running: $lock"
            return
        } catch {
            throw "A stale Unity lock file exists but could not be removed: $lock. $($_.Exception.Message)"
        }
    }

    $runningPids=(@($anyUnity|ForEach-Object{[string]$_.Id}) -join ', ')
    throw "UnityLockfile exists for the Bees project, and Unity process(es) are running (PID(s): $runningPids), but their command lines could not be proven to own $BeesRoot. Refusing to remove the lock automatically. Close Unity and retry; if the lock still exists after all Unity processes exit, the next build will remove it as stale."
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

function Invoke-PythonJson([string]$Python,[string[]]$ArgumentList,[string]$WorkingDirectory=$AssetsRoot){
    Push-Location $WorkingDirectory
    try {
        $output=@(& $Python @ArgumentList)
        $exitCode=$LASTEXITCODE
        if($exitCode -ne 0){
            throw "$Python exited with code $exitCode while running $($ArgumentList -join ' ')."
        }
        $json=($output -join [Environment]::NewLine).Trim()
        if(-not $json){ throw "$Python produced no JSON output for $($ArgumentList[0])." }
        try { return ($json | ConvertFrom-Json) }
        catch { throw "Invalid JSON from $($ArgumentList[0]): $json" }
    } finally {
        Pop-Location
    }
}

function New-ReleaseTrainingRuntime(
    [string]$Python,
    [string]$BuildId,
    [string]$SourceCommit,
    [string]$Archive
){
    if(-not(Test-Path -LiteralPath $ReleaseRuntimeScript)){
        throw "Training runtime packager is missing: $ReleaseRuntimeScript"
    }
    Invoke-PythonJson $Python @(
        $ReleaseRuntimeScript,'package',
        '--assets-root',$AssetsRoot,
        '--output',$Archive,
        '--build-id',$BuildId,
        '--source-commit',$SourceCommit
    ) $AssetsRoot
}

function Resolve-ReleaseTrainingRuntime(
    [string]$Python,
    $Release,
    [switch]$AllowLegacyPin
){
    $runtime=Get-ObjectPropertyValue $Release 'training_runtime'
    if($null -eq $runtime){
        if(-not $AllowLegacyPin){
            throw "Release $($Release.build_id) has no immutable training runtime. Rebuild the release."
        }
        $legacyBuild=([string]$Release.build_id).Trim()
        if(-not $legacyBuild){ throw 'Legacy release has no build_id.' }
        $packageRoot=Join-Path (Join-Path $BuildsRoot 'Packages') $legacyBuild
        Ensure-Directory $packageRoot
        $archive=Join-Path $packageRoot 'training-runtime.zip'
        $sourceCommit=Get-GitShortSha
        Write-Warning "Release $legacyBuild predates immutable training runtimes. Pinning the current Training runtime once for recovery; the next build will pin its runtime at build time."
        $runtime=New-ReleaseTrainingRuntime $Python $legacyBuild $sourceCommit $archive
        $runtime | Add-Member -NotePropertyName legacy_pinned_after_build -NotePropertyValue $true -Force
        $Release | Add-Member -NotePropertyName training_runtime -NotePropertyValue $runtime -Force
        if($null -ne (Get-ObjectPropertyValue $Release 'schema_version')){
            $Release.schema_version=3
        }
        Save-LatestRelease $Release
    }

    $archivePath=([string](Get-ObjectPropertyValue $runtime 'archive')).Trim()
    $archiveSha=([string](Get-ObjectPropertyValue $runtime 'archive_sha256')).Trim().ToLowerInvariant()
    $runtimeVersion=([string](Get-ObjectPropertyValue $runtime 'runtime_version')).Trim().ToLowerInvariant()
    $buildId=([string]$Release.build_id).Trim()
    if(-not $archivePath -or -not $archiveSha -or -not $runtimeVersion){
        throw "Release $buildId has incomplete immutable training runtime metadata."
    }

    Invoke-PythonJson $Python @(
        $ReleaseRuntimeScript,'verify',
        '--archive',$archivePath,
        '--expected-sha256',$archiveSha,
        '--expected-version',$runtimeVersion,
        '--expected-build-id',$buildId
    ) $AssetsRoot
}

function Install-ReleaseTrainingRuntime([string]$Python,$Release,[switch]$AllowLegacyPin){
    $runtime=Resolve-ReleaseTrainingRuntime $Python $Release -AllowLegacyPin:$AllowLegacyPin
    Invoke-PythonJson $Python @(
        $ReleaseRuntimeScript,'install',
        '--archive',[string]$runtime.archive,
        '--destination-root',$ReleaseRuntimeInstallRoot,
        '--expected-sha256',[string]$runtime.archive_sha256,
        '--expected-version',[string]$runtime.runtime_version,
        '--expected-build-id',[string]$Release.build_id
    ) $AssetsRoot
}

function New-CentralLearnerLaunchCommand(
    $Config,
    [string]$LearnerPython,
    [string]$Unity,
    [string]$RuntimeRootPath
){
    $service=Join-Path $RuntimeRootPath 'bees_continual_elastic_wan_service.py'
    $trainerConfig=Join-Path $RuntimeRootPath 'rl_1v1_config.yaml'
    $continualConfig=Join-Path $RuntimeRootPath 'continual_learning_config.json'
    foreach($required in @($LearnerPython,$service,$trainerConfig,$continualConfig)){
        if(-not(Test-Path -LiteralPath $required)){
            throw "Central release runtime is missing: $required"
        }
    }
    $telemetry=Join-Path $TrainingRoot 'Telemetry'
    $models=Join-Path $TrainingRoot 'Models'
    Ensure-Directory $telemetry
    Ensure-Directory $models
    @(
        $LearnerPython,
        $service,
        "--root=$TrainingRoot",
        "--assets-root=$AssetsRoot",
        "--runtime-training-root=$RuntimeRootPath",
        '--training-env={env}',
        "--telemetry-quarantine=$telemetry",
        "--model-distribution-root=$models",
        '--game-build-version={build_id}',
        '--run-id={run_id}',
        "--trainer-config=$trainerConfig",
        "--continual-config=$continualConfig",
        "--unity-editor=$Unity",
        "--unity-project-root=$BeesRoot",
        "--generation-steps=$($Config.generationSteps)",
        "--num-envs=$($Config.numLocalEnvs)",
        '--platform=WindowsPlayer',
        "--bees-wan-actors=$($Config.maxRemoteActors)",
        "--bees-wan-min-actors=$($Config.minRemoteActors)",
        "--bees-wan-broker-port=$($Config.brokerPort)",
        "--bees-wan-auth-token-file=$WanTokenPath"
    )
}

function Prepare-CentralReleaseRuntime(
    $Config,
    [string]$BootstrapPython,
    [string]$Unity,
    $Release
){
    Ensure-Directory $CentralAgentInstallRoot
    $installed=Install-ReleaseTrainingRuntime $BootstrapPython $Release -AllowLegacyPin
    $runtimeRootPath=[string]$installed.installed_root
    $runtimeVersion=([string]$installed.runtime_version).Trim().ToLowerInvariant()
    $learnerResult=@(Ensure-LearnerPython $Config $runtimeRootPath)
    if($learnerResult.Count -ne 1){
        throw "Learner Python resolver returned $($learnerResult.Count) values while staging central runtime; expected one."
    }
    $learnerPython=[IO.Path]::GetFullPath([string]$learnerResult[0])
    $launchCommand=@(New-CentralLearnerLaunchCommand $Config $learnerPython $Unity $runtimeRootPath)
    $pointer=[ordered]@{
        schema_version=1
        build_id=[string]$Release.build_id
        runtime_version=$runtimeVersion
        runtime_root=[IO.Path]::GetFullPath($runtimeRootPath)
        python_executable=$learnerPython
        launch_command=@($launchCommand)
        prepared_utc=[DateTime]::UtcNow.ToString('o')
    }

    $pointerTemp="$CentralRuntimePointerPath.new"
    [IO.File]::WriteAllText(
        $pointerTemp,
        ($pointer|ConvertTo-Json -Depth 8) + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false))
    )
    Install-AtomicFile $pointerTemp $CentralRuntimePointerPath

    $readyTemp="$CentralRuntimeReadyBuildPath.new"
    [IO.File]::WriteAllText(
        $readyTemp,
        ([string]$Release.build_id) + [Environment]::NewLine,
        (New-Object Text.ASCIIEncoding)
    )
    Install-AtomicFile $readyTemp $CentralRuntimeReadyBuildPath

    [pscustomobject]@{
        build_id=[string]$Release.build_id
        runtime_version=$runtimeVersion
        runtime_root=[IO.Path]::GetFullPath($runtimeRootPath)
        learner_python=$learnerPython
        launch_command=@($launchCommand)
    }
}

function Get-CentralFallbackLaunchCommand(
    $Config,
    [string]$Unity,
    $PreparedRuntime
){
    $canonicalBuild=''
    if(Test-Path -LiteralPath $AdminTokenPath){
        try{
            $admin=(Get-Content -LiteralPath $AdminTokenPath -Raw).Trim()
            if($admin){
                $status=Invoke-ControlGet "$($Config.controlUrl)/v1/status" $admin
                $desired=Get-ObjectPropertyValue $status 'desired'
                $canonicalBuild=([string](Get-ObjectPropertyValue $desired 'canonical_build_id')).Trim()
            }
        }catch{$canonicalBuild=''}
    }

    if(-not $canonicalBuild -or $canonicalBuild -eq [string]$PreparedRuntime.build_id){
        return [pscustomobject]@{
            build_id=[string]$PreparedRuntime.build_id
            launch_command=@($PreparedRuntime.launch_command)
        }
    }

    if(Test-Path -LiteralPath $CentralRuntimeStatePath){
        try{
            $runtimeState=Get-Content -LiteralPath $CentralRuntimeStatePath -Raw|ConvertFrom-Json
            $stateBuild=([string](Get-ObjectPropertyValue $runtimeState 'build_id')).Trim()
            $statePython=([string](Get-ObjectPropertyValue $runtimeState 'python_executable')).Trim()
            $stateRoot=([string](Get-ObjectPropertyValue $runtimeState 'runtime_root')).Trim()
            if($stateBuild -eq $canonicalBuild -and $statePython -and $stateRoot){
                return [pscustomobject]@{
                    build_id=$stateBuild
                    launch_command=@(New-CentralLearnerLaunchCommand $Config $statePython $Unity $stateRoot)
                }
            }
        }catch{}
    }

    $existing=$null
    if(Test-Path -LiteralPath $CentralAgentStatePath){
        try{$existing=Get-Content -LiteralPath $CentralAgentStatePath -Raw|ConvertFrom-Json}catch{$existing=$null}
    }
    if($null -ne $existing){
        $existingPython=([string](Get-ObjectPropertyValue $existing 'learner_python')).Trim()
        $existingRoot=([string](Get-ObjectPropertyValue $existing 'release_runtime_root')).Trim()
        $cutoverCapable=[bool](Get-ObjectPropertyValue $existing 'runtime_cutover_capable')
        $fallbackBuild=([string](Get-ObjectPropertyValue $existing 'fallback_build_id')).Trim()
        if($cutoverCapable){
            if($fallbackBuild -eq $canonicalBuild -and $existingPython -and $existingRoot){
                return [pscustomobject]@{
                    build_id=$fallbackBuild
                    launch_command=@(New-CentralLearnerLaunchCommand $Config $existingPython $Unity $existingRoot)
                }
            }
        }else{
            # Legacy immutable-runtime supervisors did not record fallback_build_id. Their runtime
            # fields were the only child command they could run, so current.json can prove which
            # build those fields accompanied during the one-time stable-supervisor migration.
            $currentBuildPath=Join-Path $CentralAgentInstallRoot 'current.json'
            if(Test-Path -LiteralPath $currentBuildPath){
                try{
                    $currentBuild=Get-Content -LiteralPath $currentBuildPath -Raw|ConvertFrom-Json
                    $currentBuildId=([string](Get-ObjectPropertyValue $currentBuild 'build_id')).Trim()
                    if($currentBuildId -eq $canonicalBuild -and $existingPython -and $existingRoot){
                        return [pscustomobject]@{
                            build_id=$currentBuildId
                            launch_command=@(New-CentralLearnerLaunchCommand $Config $existingPython $Unity $existingRoot)
                        }
                    }
                }catch{}
            }
        }
    }

    throw "Cannot safely restart the central supervisor while canonical build $canonicalBuild differs from prepared build $($PreparedRuntime.build_id): no verified launch command bound to the canonical runtime is available."
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

function Get-BeesServerRuntimeFileNames {
    # Keep this list limited to files that participate in the managed server process. The focused
    # contract test walks the transitive local require graph so a newly required runtime module
    # cannot be forgotten here.
    @(
        'start-server.js',
        'server.js',
        'siServerDev.js',
        'serverContracts.js',
        'database.js',
        'gamePersistence.js',
        'outcomeReservations.js',
        'campaignCheckpoint.js',
        'security.js',
        'cachePersistence.js',
        'rlDemonstrationUploads.js',
        'rlTelemetryUploadSecurity.js',
        'rlTelemetryUploads.js',
        'rlModelDistributionSecurity.js',
        'rlModelDistribution.js',
        'trainingControl.js',
        'trainingEnvOptimizer.js',
        'package.json',
        'package-lock.json'
    )
}

function Get-BeesServerRuntimeSourceHash {
    $entries=@(
        foreach($name in @(Get-BeesServerRuntimeFileNames)){
            [pscustomobject]@{
                name=$name
                path=(Join-Path $ServerRoot $name)
            }
        }
    )
    Get-NamedFileSetSha256 $entries
}

function Prune-BeesServerRuntimes([string[]]$KeepRoots=@(),[int]$KeepNewest=3){
    if(-not(Test-Path -LiteralPath $ServerReleaseRoot -PathType Container)){ return }
    $keep=@{}
    foreach($root in @($KeepRoots)){
        if(-not $root){ continue }
        try{$keep[[IO.Path]::GetFullPath([string]$root).TrimEnd('\')]=1}catch{}
    }

    $directories=@(
        Get-ChildItem -LiteralPath $ServerReleaseRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object{$_.Name -notlike '*.candidate-*'} |
            Sort-Object LastWriteTimeUtc -Descending
    )
    foreach($directory in @($directories|Select-Object -First $KeepNewest)){
        try{$keep[[IO.Path]::GetFullPath($directory.FullName).TrimEnd('\')]=1}catch{}
    }

    foreach($directory in $directories){
        $full=[IO.Path]::GetFullPath($directory.FullName).TrimEnd('\')
        if($keep.ContainsKey($full)){ continue }
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Test-BeesServerStagedRuntime([string]$RuntimeRoot,[string]$ExpectedSourceHash){
    if(-not(Test-Path -LiteralPath $RuntimeRoot -PathType Container)){ return $false }
    $readyPath=Join-Path $RuntimeRoot 'bees-server-runtime.json'
    $nodeModules=Join-Path $RuntimeRoot 'node_modules'
    if(-not(Test-Path -LiteralPath $readyPath -PathType Leaf) -or
       -not(Test-Path -LiteralPath $nodeModules -PathType Container)){
        return $false
    }
    try {
        $ready=Get-Content -LiteralPath $readyPath -Raw|ConvertFrom-Json
        if(([string](Get-ObjectPropertyValue $ready 'source_hash')).Trim().ToLowerInvariant() -ne $ExpectedSourceHash){
            return $false
        }
        $entries=@(
            foreach($name in @(Get-BeesServerRuntimeFileNames)){
                [pscustomobject]@{
                    name=$name
                    path=(Join-Path $RuntimeRoot $name)
                }
            }
        )
        return ((Get-NamedFileSetSha256 $entries) -eq $ExpectedSourceHash)
    } catch {
        return $false
    }
}

function Prepare-BeesServerRuntime([string]$Node){
    $sourceHash=Get-BeesServerRuntimeSourceHash
    Ensure-Directory $ServerReleaseRoot
    $runtimeRoot=Join-Path $ServerReleaseRoot $sourceHash
    if(Test-BeesServerStagedRuntime $runtimeRoot $sourceHash){
        return [pscustomobject]@{
            source_hash=$sourceHash
            dependency_hash=Get-BeesServerDependencyHash $runtimeRoot
            runtime_root=[IO.Path]::GetFullPath($runtimeRoot)
        }
    }

    $candidate=Join-Path $ServerReleaseRoot ("$sourceHash.candidate-" + [Guid]::NewGuid().ToString('N'))
    Ensure-Directory $candidate
    try {
        foreach($name in @(Get-BeesServerRuntimeFileNames)){
            $source=Join-Path $ServerRoot $name
            if(-not(Test-Path -LiteralPath $source -PathType Leaf)){
                throw "BeesServer runtime source is missing: $source"
            }
            Copy-Item -LiteralPath $source -Destination (Join-Path $candidate $name)
        }

        $dependencyHash=Get-BeesServerDependencyHash $candidate
        $npm=Resolve-Npm
        Write-Host "Pre-staging BeesServer runtime $($sourceHash.Substring(0,12)) while the current server remains online..."
        Invoke-Checked $npm @('ci') $candidate

        foreach($name in @(Get-BeesServerRuntimeFileNames|Where-Object{$_.EndsWith('.js',[StringComparison]::OrdinalIgnoreCase)})){
            Invoke-Checked $Node @('--check',(Join-Path $candidate $name)) $candidate
        }
        Invoke-Checked $Node @(
            '-e',
            "const runtime=require('./server'); runtime.loadLegacyRuntime();"
        ) $candidate

        $actualEntries=@(
            foreach($name in @(Get-BeesServerRuntimeFileNames)){
                [pscustomobject]@{
                    name=$name
                    path=(Join-Path $candidate $name)
                }
            }
        )
        $actualHash=Get-NamedFileSetSha256 $actualEntries
        if($actualHash -ne $sourceHash){
            throw "BeesServer source changed while staging. expected=$sourceHash staged=$actualHash"
        }

        $ready=[ordered]@{
            schema_version=1
            source_hash=$sourceHash
            dependency_hash=$dependencyHash
            prepared_utc=[DateTime]::UtcNow.ToString('o')
        }
        [IO.File]::WriteAllText(
            (Join-Path $candidate 'bees-server-runtime.json'),
            ($ready|ConvertTo-Json -Depth 4) + [Environment]::NewLine,
            (New-Object Text.UTF8Encoding($false))
        )

        if(Test-Path -LiteralPath $runtimeRoot){
            if(Test-BeesServerStagedRuntime $runtimeRoot $sourceHash){
                Remove-Item -LiteralPath $candidate -Recurse -Force
            } else {
                $activeState=$null
                if(Test-Path -LiteralPath $ServerStatePath){
                    try{$activeState=Get-Content -LiteralPath $ServerStatePath -Raw|ConvertFrom-Json}catch{$activeState=$null}
                }
                $activeRuntime=if($null -ne $activeState){([string](Get-ObjectPropertyValue $activeState 'runtime_root')).Trim()}else{''}
                $sameActiveRuntime=$false
                if($activeRuntime){
                    try{
                        $sameActiveRuntime=[string]::Equals(
                            [IO.Path]::GetFullPath($activeRuntime),
                            [IO.Path]::GetFullPath($runtimeRoot),
                            [StringComparison]::OrdinalIgnoreCase
                        )
                    }catch{$sameActiveRuntime=$false}
                }
                if($sameActiveRuntime -and (Test-ManagedProcessIdentity $activeState)){
                    throw "Active BeesServer runtime failed staged verification; refusing to mutate its live runtime directory: $runtimeRoot"
                }
                Remove-Item -LiteralPath $runtimeRoot -Recurse -Force
                Move-Item -LiteralPath $candidate -Destination $runtimeRoot
            }
        } else {
            Move-Item -LiteralPath $candidate -Destination $runtimeRoot
        }
    } finally {
        if(Test-Path -LiteralPath $candidate){
            Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if(-not(Test-BeesServerStagedRuntime $runtimeRoot $sourceHash)){
        throw "BeesServer staged runtime failed post-install verification: $runtimeRoot"
    }
    [pscustomobject]@{
        source_hash=$sourceHash
        dependency_hash=Get-BeesServerDependencyHash $runtimeRoot
        runtime_root=[IO.Path]::GetFullPath($runtimeRoot)
    }
}

function Get-BeesServerDependencyHash([string]$Root=$ServerRoot){
    Get-NamedFileSetSha256 @(
        [pscustomobject]@{name='package.json';path=(Join-Path $Root 'package.json')},
        [pscustomobject]@{name='package-lock.json';path=(Join-Path $Root 'package-lock.json')}
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

function New-TrainingRunPlan(
    [string]$Python,
    [switch]$ForceNew,
    [string]$BuildId='',
    [string[]]$EnvironmentArgs=@()
){
    if(-not(Test-Path -LiteralPath $RunLifecycleScript)){
        throw "Training run lifecycle helper is missing: $RunLifecycleScript"
    }
    if($BuildId -and -not $ForceNew){
        throw '-BuildId is only valid for a forced-new training run plan.'
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
    if($ForceNew){
        $planArgs+='--force-new'
        if($BuildId){ $planArgs+=@('--build-id',$BuildId) }
        $environmentArgsJson=ConvertTo-Json -InputObject @($EnvironmentArgs) -Compress
        $planArgs+=@('--environment-args-json',$environmentArgsJson)
    }
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

function Get-PendingForcedNewRunPlan {
    if(-not(Test-Path -LiteralPath $RunPlanPath)){ return $null }
    $plan=$null
    try { $plan=Get-Content -LiteralPath $RunPlanPath -Raw | ConvertFrom-Json }
    catch { throw "Pending training run plan is unreadable: $RunPlanPath" }
    if($null -eq $plan -or -not [bool](Get-ObjectPropertyValue $plan 'forced_new_run')){
        return $null
    }
    $plan
}

function Convert-ReleaseToForcedRunPlan($Release,$Plan,[string]$OutgoingRun){
    $copy=$Release | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    $copy | Add-Member -NotePropertyName run_id -NotePropertyValue ([string]$Plan.run_id) -Force
    $copy | Add-Member -NotePropertyName previous_run_id -NotePropertyValue $OutgoingRun -Force
    $copy | Add-Member -NotePropertyName compatibility_key -NotePropertyValue ([string]$Plan.compatibility_key) -Force
    $copy | Add-Member -NotePropertyName incompatible -NotePropertyValue $true -Force
    $copy | Add-Member -NotePropertyName contract -NotePropertyValue $Plan.contract -Force
    $copy
}

function Complete-ForcedNewRunPlan($Plan,$Release){
    if(-not(Test-Path -LiteralPath $RunPlanPath)){ return }
    $current=$null
    try { $current=Get-Content -LiteralPath $RunPlanPath -Raw | ConvertFrom-Json }
    catch { throw "Pending training run plan is unreadable during completion: $RunPlanPath" }
    $currentBuild=([string](Get-ObjectPropertyValue $current 'build_id')).Trim()
    $buildMatches=(-not $currentBuild -or $currentBuild -eq ([string]$Release.build_id).Trim())
    if($null -eq $current -or
        -not [bool](Get-ObjectPropertyValue $current 'forced_new_run') -or
        -not $buildMatches -or
        ([string](Get-ObjectPropertyValue $current 'run_id')).Trim() -ne ([string]$Release.run_id).Trim() -or
        ([string](Get-ObjectPropertyValue $current 'compatibility_key')).Trim().ToLowerInvariant() -ne ([string]$Release.compatibility_key).Trim().ToLowerInvariant()){
        throw "Refusing to clear a forced-new run plan that no longer matches the completed release."
    }
    Remove-Item -LiteralPath $RunPlanPath -Force
}

function Get-TrainingCompatibilityFingerprint([string]$Python){
    Invoke-PythonJson $Python @(
        $RunLifecycleScript,'fingerprint',
        '--assets-root',$AssetsRoot
    ) $AssetsRoot
}

function Ensure-RunLifecycleMatchesRelease([string]$Python,$Release){
    $releaseRun=([string]$Release.run_id).Trim()
    $releaseKey=([string]$Release.compatibility_key).Trim().ToLowerInvariant()
    if(-not $releaseRun -or -not $releaseKey){
        throw 'Release is missing run lifecycle identity.'
    }

    $state=$null
    if(Test-Path -LiteralPath $RunStatePath){
        try { $state=Get-Content -LiteralPath $RunStatePath -Raw | ConvertFrom-Json }
        catch { throw "Training run lifecycle state is unreadable: $RunStatePath" }
    }
    if($null -ne $state -and
        ([string]$state.run_id).Trim() -eq $releaseRun -and
        ([string]$state.compatibility_key).Trim().ToLowerInvariant() -eq $releaseKey){
        return
    }

    if(Test-Path -LiteralPath $RunPlanPath){
        $plan=$null
        try { $plan=Get-Content -LiteralPath $RunPlanPath -Raw | ConvertFrom-Json }
        catch { throw "Pending training run plan is unreadable: $RunPlanPath" }
        if($null -ne $plan -and
            ([string]$plan.run_id).Trim() -eq $releaseRun -and
            ([string]$plan.compatibility_key).Trim().ToLowerInvariant() -eq $releaseKey){
            Commit-TrainingRunPlan $Python
            Write-Host "Recovered pending training run lifecycle commit for $releaseRun."
            return
        }
    }

    $stateRun=if($null -ne $state){([string]$state.run_id).Trim()}else{'(missing)'}
    throw "Run lifecycle state disagrees with latest release. lifecycle=$stateRun release=$releaseRun"
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

function Wait-ReleaseRollout(
    $Config,
    [string]$AdminToken,
    [string]$BuildId,
    [string]$RunId,
    [string]$CompatibilityKey,
    [int]$TimeoutSeconds=600
){
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastProgress=''
    $lastProgressAt=[DateTime]::MinValue
    while([DateTime]::UtcNow -lt $deadline){
        $status=Invoke-ControlGet "$($Config.controlUrl)/v1/status" $AdminToken
        $pending=$status.desired.pending_release
        if($null -eq $pending -and
            ([string]$status.desired.canonical_build_id) -eq $BuildId -and
            ([string]$status.desired.run_id) -eq $RunId -and
            ([string]$status.desired.compatibility_key) -eq $CompatibilityKey){
            Write-Host "Release rollout complete: build=$BuildId run=$RunId."
            return $status
        }

        if($null -eq $pending){
            throw "Release rollout ended without activating the expected identity. expected build=$BuildId run=$RunId; active build=$($status.desired.canonical_build_id) run=$($status.desired.run_id)."
        }

        $pendingBuild=([string](Get-ObjectPropertyValue $pending 'build_id')).Trim()
        $pendingRun=([string](Get-ObjectPropertyValue $pending 'run_id')).Trim()
        $pendingKey=([string](Get-ObjectPropertyValue $pending 'compatibility_key')).Trim().ToLowerInvariant()
        if($pendingBuild -ne $BuildId -or
           $pendingRun -ne $RunId -or
           $pendingKey -ne $CompatibilityKey){
            throw "A different release became pending while waiting. expected build=$BuildId run=$RunId; pending build=$pendingBuild run=$pendingRun."
        }

        $phase=[string](Get-ObjectPropertyValue $pending 'phase')
        $phaseRevision=Get-ObjectPropertyValue $pending 'phase_revision'
        $required=@(Get-ObjectPropertyValue $pending 'required_trainers')
        $trainerRecords=@($status.trainers)
        $waiting=@()
        foreach($requiredTrainer in $required){
            $trainerId=[string](Get-ObjectPropertyValue $requiredTrainer 'trainer_id')
            $record=@($trainerRecords|Where-Object{
                [string](Get-ObjectPropertyValue $_ 'trainer_id') -eq $trainerId
            }|Select-Object -First 1)
            if($record.Count -eq 0){
                $waiting += ("{0}:missing" -f $trainerId)
                continue
            }
            $r=$record[0]
            $stale=[bool](Get-ObjectPropertyValue $r 'stale')
            $state=[string](Get-ObjectPropertyValue $r 'process_state')
            $build=[string](Get-ObjectPropertyValue $r 'build_id')
            $prepared=[string](Get-ObjectPropertyValue $r 'prepared_build_id')
            $rev=Get-ObjectPropertyValue $r 'applied_revision'
            $error=[string](Get-ObjectPropertyValue $r 'last_error')
            $satisfied=$false
            if($phase -eq 'preparing'){
                $satisfied=(-not $stale -and ($build -eq $BuildId -or $prepared -eq $BuildId))
            }elseif($phase -eq 'rolling'){
                $satisfied=(-not $stale -and $state -eq 'running' -and
                    $build -eq $BuildId -and -not $error -and
                    ($null -eq $phaseRevision -or [int]$rev -ge [int]$phaseRevision))
            }elseif($phase -eq 'stopping'){
                $satisfied=(-not $stale -and $state -eq 'stopped' -and
                    ($null -eq $phaseRevision -or [int]$rev -ge [int]$phaseRevision))
            }
            if(-not $satisfied){
                $detail=("{0}:{1}" -f $trainerId,$state)
                if($stale){$detail+='(STALE)'}
                $detail+=" build=$(if($build){$build}else{'-'})"
                if($prepared){$detail+=" prepared=$prepared"}
                if($null -ne $rev){$detail+=" rev=$rev"}
                if($error){$detail+=" error=$error"}
                $waiting += $detail
            }
        }
        $centralFailure=$trainerRecords|Where-Object{
            [string](Get-ObjectPropertyValue $_ 'trainer_id') -eq 'central-learner' -and
            [string](Get-ObjectPropertyValue $_ 'process_state') -eq 'stopped' -and
            [string](Get-ObjectPropertyValue $_ 'last_error')
        }|Select-Object -First 1
        if($null -ne $centralFailure){
            $centralError=[string](Get-ObjectPropertyValue $centralFailure 'last_error')
            if($centralError -match '^managed process exited with code '){
                throw "Central learner failed while rolling release ${BuildId}: $centralError. See $LogsRoot\Training\central-agent.err.log and central-agent.out.log."
            }
        }

        $progress="Waiting for release rollout: phase=$phase remaining=$(if($waiting.Count){$waiting -join '; '}else{'control state advancing'})"
        $now=[DateTime]::UtcNow
        if($progress -ne $lastProgress -or ($now - $lastProgressAt).TotalSeconds -ge 10){
            Write-Host $progress
            $lastProgress=$progress
            $lastProgressAt=$now
        }
        Start-Sleep -Seconds 1
    }
    throw "Timed out waiting for release $BuildId run=$RunId to finish coordinated rollout."
}

function Reconcile-LatestReleaseBeforeBuild(
    $Config,
    [string]$Python,
    [string]$Unity,
    [string]$AdminToken,
    $Release
){
    Ensure-RunLifecycleMatchesRelease $Python $Release
    $centralRuntime=Prepare-CentralReleaseRuntime $Config $Python $Unity $Release
    Start-CentralAgentIfNeeded $Config $Python $Unity $Release $centralRuntime

    $status=Invoke-ControlGet "$($Config.controlUrl)/v1/status" $AdminToken
    $pending=$status.desired.pending_release
    $releaseBuild=([string]$Release.build_id).Trim()
    $releaseRun=([string]$Release.run_id).Trim()
    $releaseKey=([string]$Release.compatibility_key).Trim().ToLowerInvariant()

    if($pending){
        $pendingBuild=([string]$pending.build_id).Trim()
        $pendingRun=([string]$pending.run_id).Trim()
        $pendingKey=([string]$pending.compatibility_key).Trim().ToLowerInvariant()
        if($pendingBuild -ne $releaseBuild -or
           $pendingRun -ne $releaseRun -or
           $pendingKey -ne $releaseKey){
            throw "Training control has a pending release that differs from latest release metadata. pending=$pendingBuild/$pendingRun latest=$releaseBuild/$releaseRun"
        }
        Write-Host "Previous release is still rolling out (phase=$($pending.phase)); finishing build $releaseBuild before compiling another release."
        $null=Wait-ReleaseRollout $Config $AdminToken $releaseBuild $releaseRun $releaseKey
        return
    }

    $canonicalBuild=([string]$status.desired.canonical_build_id).Trim()
    $canonicalRun=([string]$status.desired.run_id).Trim()
    $canonicalKey=([string]$status.desired.compatibility_key).Trim().ToLowerInvariant()
    if($canonicalBuild -eq $releaseBuild -and
       $canonicalRun -eq $releaseRun -and
       $canonicalKey -eq $releaseKey){
        return
    }

    # A prior build can be interrupted after persisting its immutable release but before publishing
    # or staging it. Recover that release instead of silently replacing its durable intent with a
    # newer build.
    Write-Host "Latest release $releaseBuild was persisted but is not canonical; reconciling it before compiling another release."
    Ensure-TailnetIdentity $Config
    Prepare-RemoteBootstrap $Config $Python $Release
    Publish-Release $Config $AdminToken $Release
    Start-TailnetGatewayIfNeeded $Config
    $staged=Stage-Release $Config $AdminToken $Release
    if($staged.pending_release){
        $null=Wait-ReleaseRollout $Config $AdminToken $releaseBuild $releaseRun $releaseKey
    } else {
        $after=Invoke-ControlGet "$($Config.controlUrl)/v1/status" $AdminToken
        if(([string]$after.desired.canonical_build_id).Trim() -ne $releaseBuild -or
           ([string]$after.desired.run_id).Trim() -ne $releaseRun -or
           ([string]$after.desired.compatibility_key).Trim().ToLowerInvariant() -ne $releaseKey){
            throw "Previous release reconciliation returned without making $releaseBuild/$releaseRun canonical."
        }
    }
}

function Invoke-Build {
    $config=Get-ClusterConfig
    $python=Resolve-Python $config
    $unfinishedForcedPlan=Get-PendingForcedNewRunPlan
    if($null -ne $unfinishedForcedPlan){
        $unfinishedBuild=([string](Get-ObjectPropertyValue $unfinishedForcedPlan 'build_id')).Trim()
        if(-not $unfinishedBuild){ $unfinishedBuild='legacy-unbound' }
        throw "A forced new-run operation is still unfinished for build $unfinishedBuild run=$($unfinishedForcedPlan.run_id). Run '.\Assets\bees.ps1 start' to resume/finalize it before creating another build."
    }
    $sourceSha=Get-GitShortSha
    $unity=Resolve-UnityEditor $config
    Assert-UnityProjectAvailableForBatchBuild

    # Reconcile any previously persisted release before creating a newer one. This covers both an
    # in-flight rollout and the crash window after latest-release was saved but before it was staged.
    if((Test-Path -LiteralPath $LatestReleasePath) -and (Test-Path -LiteralPath $AdminTokenPath)){
        $preBuildAdmin=(Get-Content -LiteralPath $AdminTokenPath -Raw).Trim()
        $preBuildControlOnline=if($preBuildAdmin){Test-Control ([string]$config.controlUrl) $preBuildAdmin}else{$false}
        $preBuildManagedServerExists=Test-Path -LiteralPath $ServerStatePath
        if($preBuildAdmin -and ($preBuildControlOnline -or $preBuildManagedServerExists)){
            $preBuildWorker=Ensure-TokenFile $WorkerTokenPath
            Start-BeesServerIfNeeded $config $preBuildWorker $preBuildAdmin
            if(-not(Test-Control ([string]$config.controlUrl) $preBuildAdmin)){
                throw 'Managed BeesServer reconciliation completed without a reachable training-control endpoint before build.'
            }
            $currentRelease=Get-LatestRelease
            if($currentRelease.run_id -and $currentRelease.compatibility_key){
                Reconcile-LatestReleaseBeforeBuild $config $python $unity $preBuildAdmin $currentRelease
            }
        }
    }

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

    # Pin the release's Python/config runtime before Unity compilation begins. A developer can
    # keep editing the working tree while a long build runs without silently changing the
    # runtime bytes that will later be paired with these Unity artifacts.
    $packageRoot=Join-Path (Join-Path $BuildsRoot 'Packages') $buildId
    if(Test-Path -LiteralPath $packageRoot){
        if(-not $Force){ throw "Package directory exists: $packageRoot. Use -Force." }
        Remove-Item -LiteralPath $packageRoot -Recurse -Force
    }
    Ensure-Directory $packageRoot
    $trainingRuntimeArchive=Join-Path $packageRoot 'training-runtime.zip'
    $trainingRuntime=New-ReleaseTrainingRuntime $python $buildId $sha $trainingRuntimeArchive

    Reset-BuildDirectory $win
    Reset-BuildDirectory $linux
    if($FullGame){ Reset-BuildDirectory $game }

    Invoke-UnityBuild $unity 'BeesCommandLineBuild.BuildWindowsRl' $win 'Bees RL Training.exe' "$date-rl-windows.log"
    Invoke-UnityBuild $unity 'BeesCommandLineBuild.BuildLinuxRl' $linux 'Bees RL Training.x86_64' "$date-rl-linux.log"
    if($FullGame){
        Invoke-UnityBuild $unity 'BeesCommandLineBuild.BuildWindowsFullGame' $game 'Bees.exe' "$date-full-game-windows.log"
    }

    $postBuildFingerprint=Get-TrainingCompatibilityFingerprint $python
    $postBuildKey=([string]$postBuildFingerprint.compatibility_key).Trim().ToLowerInvariant()
    $plannedKey=([string]$plan.compatibility_key).Trim().ToLowerInvariant()
    if(-not $postBuildKey -or $postBuildKey -ne $plannedKey){
        throw "Training compatibility contract changed while Unity was building. Refusing to publish a mixed release. planned=$plannedKey current=$postBuildKey. Re-run the build from the current source."
    }

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
        schema_version=3
        build_id=$buildId
        source_commit=$sha
        created_utc=[DateTime]::UtcNow.ToString('o')
        run_id=[string]$plan.run_id
        previous_run_id=$previousRunId
        compatibility_key=[string]$plan.compatibility_key
        incompatible=[bool]$plan.incompatible
        contract=$plan.contract
        artifacts=$artifacts
        training_runtime=$trainingRuntime
    }
    Save-LatestRelease $release
    Commit-TrainingRunPlan $python

    Write-Host ""
    Write-Host "Build complete: $buildId  run=$($release.run_id)"
    $artifacts | Format-Table role,platform,folder -AutoSize

    if(Test-Path -LiteralPath $AdminTokenPath){
        $admin=(Get-Content -LiteralPath $AdminTokenPath -Raw).Trim()
        $controlOnline=if($admin){Test-Control ([string]$config.controlUrl) $admin}else{$false}
        $managedServerExists=Test-Path -LiteralPath $ServerStatePath
        if($admin -and ($controlOnline -or $managedServerExists)){
            $worker=Ensure-TokenFile $WorkerTokenPath
            # A previously managed server that crashed during the build is recoverable state, not
            # a reason to silently skip release staging. An intentionally stopped server has no
            # managed state file and remains stopped.
            Start-BeesServerIfNeeded $config $worker $admin
            if(-not(Test-Control ([string]$config.controlUrl) $admin)){
                throw 'Managed BeesServer reconciliation completed without a reachable training-control endpoint.'
            }
            Assert-CentralAgentCheckpointSafe
            $centralRuntime=Prepare-CentralReleaseRuntime $config $python $unity $release
            Start-CentralAgentIfNeeded $config $python $unity $release $centralRuntime
            Write-Host 'Training control is online; staging this release without stopping the active cluster.'
            if(Test-Path -LiteralPath $TailnetAddressPath){
                Prepare-RemoteBootstrap $config $python $release
                if($tailnetBridgeChanged){
                    Write-Host 'Embedded tailnet helper changed; reconciling the private gateway onto the new immutable helper version.'
                }
                # Reconcile on every live-cluster build. This is idempotent when healthy and
                # self-heals a crashed/missing gateway even when the helper version did not change.
                Start-TailnetGatewayIfNeeded $config
            }
            Publish-Release $config $admin $release
            $staged=Stage-Release $config $admin $release
            Write-Host "Release staged: build=$buildId phase=$(if($staged.pending_release){$staged.pending_release.phase}else{'active'})"
            if([bool]$release.incompatible){
                $null=Wait-ReleaseRollout $config $admin $buildId ([string]$release.run_id) ([string]$release.compatibility_key)
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

function Get-BeesServerLaunchConfigHash($Config,[string]$WorkerToken,[string]$AdminToken){
    $dbPasswordHash=if($env:BEES_DB_PASSWORD){Get-StringSha256 ([string]$env:BEES_DB_PASSWORD)}else{''}
    $payload=[ordered]@{
        control_url=[string]$Config.controlUrl
        control_host=[string]$Config.controlHost
        control_port=[int]$Config.controlPort
        gameplay_port=[int]$GameplayServerPort
        worker_token_sha256=Get-StringSha256 $WorkerToken
        admin_token_sha256=Get-StringSha256 $AdminToken
        control_state=Join-Path $TrainingRoot 'Control\state.json'
        artifact_root=Join-Path $TrainingRoot 'Control\Artifacts'
        log_root=Join-Path $TrainingRoot 'TrainerLogs'
        db_host=[string]$env:BEES_DB_HOST
        db_user=[string]$env:BEES_DB_USER
        db_password_sha256=$dbPasswordHash
        db_name=[string]$env:BEES_DB_NAME
        require_test_db=[string]$env:BEES_REQUIRE_TEST_DB
        disable_background_jobs=[string]$env:BEES_DISABLE_BACKGROUND_JOBS
    }
    Get-StringSha256 ($payload|ConvertTo-Json -Compress -Depth 4)
}

function Set-BeesServerLaunchEnvironment($Config,[string]$WorkerToken,[string]$AdminToken){
    $env:BEES_TRAINING_CONTROL_ENABLED='1'
    $env:BEES_TRAINING_CONTROL_TOKEN=$WorkerToken
    $env:BEES_TRAINING_CONTROL_ADMIN_TOKEN=$AdminToken
    $env:BEES_TRAINING_CONTROL_HOST=[string]$Config.controlHost
    $env:BEES_TRAINING_CONTROL_PORT=[string]$Config.controlPort
    $env:BEES_TRAINING_CONTROL_STATE=Join-Path $TrainingRoot 'Control\state.json'
    $env:BEES_TRAINING_ARTIFACT_ROOT=Join-Path $TrainingRoot 'Control\Artifacts'
    $env:BEES_TRAINING_LOG_ROOT=Join-Path $TrainingRoot 'TrainerLogs'
    $env:BEES_TEST_TRAINING_CONTROL_ENABLED='1'
}

function Write-BeesServerManagedState(
    $Identity,
    [string]$SourceHash,
    [string]$DependencyHash,
    [string]$RuntimeRoot,
    [string]$ConfigHash,
    [string]$Status,
    [string]$RollbackReason=''
){
    $state=[ordered]@{
        schema_version=5
        pid=[int]$Identity.pid
        process_start_utc=[string]$Identity.process_start_utc
        executable_path=[string]$Identity.executable_path
        source_hash=$SourceHash
        dependency_hash=$DependencyHash
        runtime_root=[IO.Path]::GetFullPath($RuntimeRoot)
        config_hash=$ConfigHash
        status=$Status
        started_utc=[DateTime]::UtcNow.ToString('o')
    }
    if($RollbackReason){$state.rollback_reason=$RollbackReason}
    $temp="$ServerStatePath.new"
    [IO.File]::WriteAllText(
        $temp,
        ($state|ConvertTo-Json -Depth 6) + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false))
    )
    Install-AtomicFile $temp $ServerStatePath

    $pidTemp="$ServerPidPath.new"
    [IO.File]::WriteAllText(
        $pidTemp,
        ([string]$Identity.pid),
        (New-Object Text.ASCIIEncoding)
    )
    Install-AtomicFile $pidTemp $ServerPidPath
}

function Start-BeesServerRuntimeProcess(
    $Config,
    [string]$Node,
    [string]$RuntimeRoot,
    [string]$WorkerToken,
    [string]$AdminToken,
    [string]$ServerLog,
    $RuntimeIdentity,
    [string]$RollbackReason='',
    [int]$TimeoutSeconds=30
){
    $base=[string]$Config.controlUrl
    $launcher=Join-Path $RuntimeRoot 'start-server.js'
    if(-not(Test-Path -LiteralPath $launcher -PathType Leaf)){
        throw "Prepared BeesServer runtime is missing its launcher: $launcher"
    }

    Set-BeesServerLaunchEnvironment $Config $WorkerToken $AdminToken
    $launchedPid=0
    Push-Location $RuntimeRoot
    try {
        $output=@(& $Node $launcher '--background' "--log=$ServerLog" 'test' ([string]$GameplayServerPort) 2>&1)
        if($LASTEXITCODE -ne 0){
            throw "BeesServer launcher failed: $($output -join [Environment]::NewLine)"
        }
        $joined=$output -join [Environment]::NewLine
        Write-Host $joined
        if($joined -match 'PID\s+(\d+)'){
            $launchedPid=[int]$Matches[1]
        }
    } finally {
        Pop-Location
    }
    if($launchedPid -le 0){
        throw 'BeesServer launcher succeeded without reporting the managed child PID.'
    }

    $identity=Get-ProcessIdentity $launchedPid
    if($null -eq $identity -or -not [string]::Equals(
        [string]$identity.executable_path,
        [IO.Path]::GetFullPath($Node),
        [StringComparison]::OrdinalIgnoreCase
    )){
        if($null -ne $identity){
            try{$null=Stop-ManagedProcessTree $identity $Node 'failed BeesServer candidate'}catch{}
        }
        throw 'Could not establish the BeesServer candidate process identity after launch.'
    }

    Write-BeesServerManagedState $identity ([string]$RuntimeIdentity.source_hash) ([string]$RuntimeIdentity.dependency_hash) ([string]$RuntimeIdentity.runtime_root) ([string]$RuntimeIdentity.config_hash) 'starting' $RollbackReason

    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while([DateTime]::UtcNow -lt $deadline){
        if(Test-Control $base $AdminToken){
            Write-BeesServerManagedState $identity ([string]$RuntimeIdentity.source_hash) ([string]$RuntimeIdentity.dependency_hash) ([string]$RuntimeIdentity.runtime_root) ([string]$RuntimeIdentity.config_hash) 'active' $RollbackReason
            return $identity
        }
        if(-not(Test-ManagedProcessIdentity $identity $Node)){
            throw "BeesServer candidate PID $launchedPid exited before the control endpoint became healthy. Check $ServerLog."
        }
        Start-Sleep -Milliseconds 500
    }

    if(Test-ManagedProcessIdentity $identity $Node){
        try{$null=Stop-ManagedProcessTree $identity $Node 'unhealthy BeesServer candidate'}catch{}
    }
    throw "BeesServer candidate did not become reachable at $base within $TimeoutSeconds seconds. Check $ServerLog."
}

function Start-BeesServerIfNeeded($Config,[string]$WorkerToken,[string]$AdminToken){
    $base=[string]$Config.controlUrl
    $serverConfigHash=Get-BeesServerLaunchConfigHash $Config $WorkerToken $AdminToken
    $node=Resolve-Node $Config
    # Stage and validate the replacement before inspecting/stopping the live process. A broken
    # source/dependency update therefore leaves the already healthy control plane untouched.
    $preparedServer=Prepare-BeesServerRuntime $node
    $serverSourceHash=[string]$preparedServer.source_hash
    $serverDependencyHash=[string]$preparedServer.dependency_hash
    $serverRuntimeRoot=[IO.Path]::GetFullPath([string]$preparedServer.runtime_root)
    $probeHost=if(([string]$Config.controlHost) -eq '0.0.0.0'){'127.0.0.1'}else{[string]$Config.controlHost}

    # Process ownership is authoritative even when the desired control URL/token has changed or
    # the old control endpoint is unhealthy. Reconcile the recorded process first so configuration
    # changes cannot orphan a server that the operator can still prove it owns.
    $managedState=$null
    $managedOwned=$false
    if(Test-Path -LiteralPath $ServerStatePath){
        try{$managedState=Get-Content -LiteralPath $ServerStatePath -Raw|ConvertFrom-Json}catch{$managedState=$null}
    }
    if($null -ne $managedState){
        if(Test-ManagedProcessIdentity $managedState){
            $managedOwned=$true
        } else {
            $managedPid=Get-StateReferencedLivePid $managedState
            if($managedPid -gt 0){
                throw "BeesServer state references live PID $managedPid but its PID/start-time/executable ownership does not match. Refusing to kill a possibly reused PID."
            }
            Remove-Item -LiteralPath $ServerPidPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $ServerStatePath -Force -ErrorAction SilentlyContinue
            $managedState=$null
        }
    } elseif(Test-Path -LiteralPath $ServerPidPath){
        $legacyPid=0
        [void][int]::TryParse((Get-Content -LiteralPath $ServerPidPath -Raw).Trim(),[ref]$legacyPid)
        if($legacyPid -gt 0 -and (Get-Process -Id $legacyPid -ErrorAction SilentlyContinue)){
            throw "BeesServer PID $legacyPid is from legacy PID-only state and cannot be proven safe to kill automatically. Stop that legacy server once, then rerun the command."
        }
        Remove-Item -LiteralPath $ServerPidPath -Force -ErrorAction SilentlyContinue
    }

    $online=Test-Control $base $AdminToken
    if($online){
        if($null -eq $managedState -or -not $managedOwned){
            throw 'BeesServer is online but has no matching managed process identity. Refusing an automatic restart because an unrelated process could own the live endpoint.'
        }
        $managedSourceHash=[string](Get-ObjectPropertyValue $managedState 'source_hash')
        $managedConfigHash=[string](Get-ObjectPropertyValue $managedState 'config_hash')
        $managedRuntimeRoot=([string](Get-ObjectPropertyValue $managedState 'runtime_root')).Trim()
        $runtimeMatches=$false
        if($managedRuntimeRoot){
            try {
                $runtimeMatches=[string]::Equals(
                    [IO.Path]::GetFullPath($managedRuntimeRoot),
                    $serverRuntimeRoot,
                    [StringComparison]::OrdinalIgnoreCase
                )
            } catch { $runtimeMatches=$false }
        }
        $serverExecutableMatches=Test-ManagedProcessIdentity $managedState $node
        if(
            $serverExecutableMatches -and
            $managedSourceHash -eq $serverSourceHash -and
            $managedConfigHash -eq $serverConfigHash -and
            $runtimeMatches
        ){
            if(([string](Get-ObjectPropertyValue $managedState 'status')) -ne 'active'){
                Write-BeesServerManagedState $managedState $serverSourceHash $serverDependencyHash $serverRuntimeRoot $serverConfigHash 'active'
            }
            return
        }
        Write-Host 'BeesServer executable/runtime/launch configuration changed; restarting the verified managed server without changing desired training state.'
    } elseif($managedOwned){
        Write-Host 'Managed BeesServer is not accepting the desired control endpoint/token; restarting the verified owned process to converge launch configuration.'
    }

    if($managedOwned){
        Assert-CentralAgentCheckpointSafe
        $null=Stop-ManagedProcessTree $managedState $node 'BeesServer'

        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        while([DateTime]::UtcNow -lt $deadline){
            $open=Test-NetConnection -ComputerName $probeHost -Port ([int]$Config.controlPort) -InformationLevel Quiet -WarningAction SilentlyContinue
            if(-not $open){break}
            Start-Sleep -Milliseconds 250
        }
    }

    $controlPortOpen=Test-NetConnection -ComputerName $probeHost -Port ([int]$Config.controlPort) -InformationLevel Quiet -WarningAction SilentlyContinue
    if($controlPortOpen){ throw "Training-control port $($Config.controlPort) is already in use but did not accept this admin token. The process is not the verified managed BeesServer, so it will not be killed automatically." }

    Ensure-Directory (Join-Path $LogsRoot 'Server')
    Ensure-Directory (Join-Path $TrainingRoot 'Control')
    $serverLog=Join-Path $LogsRoot 'Server\bees-server.log'
    $previousState=$managedState
    $previousConfigHash=if($null -ne $previousState){[string](Get-ObjectPropertyValue $previousState 'config_hash')}else{''}
    $previousSourceHash=if($null -ne $previousState){[string](Get-ObjectPropertyValue $previousState 'source_hash')}else{''}
    $previousRuntimeRoot=if($null -ne $previousState){([string](Get-ObjectPropertyValue $previousState 'runtime_root')).Trim()}else{''}
    $previousDependencyHash=if($null -ne $previousState){[string](Get-ObjectPropertyValue $previousState 'dependency_hash')}else{''}

    $serverRuntimeIdentity=[pscustomobject]@{
        source_hash=$serverSourceHash
        dependency_hash=$serverDependencyHash
        runtime_root=$serverRuntimeRoot
        config_hash=$serverConfigHash
    }
    try {
        $serverIdentity=Start-BeesServerRuntimeProcess $Config $node $serverRuntimeRoot $WorkerToken $AdminToken $serverLog $serverRuntimeIdentity
    } catch {
        $replacementError=$_.Exception.Message
        $rollbackError=''
        $rolledBack=$false
        if(
            $previousRuntimeRoot -and
            $previousConfigHash -eq $serverConfigHash -and
            $previousSourceHash -and
            (Test-BeesServerStagedRuntime $previousRuntimeRoot $previousSourceHash)
        ){
            Write-Warning "Replacement BeesServer failed after cutover; restoring previously verified runtime $previousSourceHash."
            try {
                $rollbackRuntimeIdentity=[pscustomobject]@{
                    source_hash=$previousSourceHash
                    dependency_hash=$previousDependencyHash
                    runtime_root=[IO.Path]::GetFullPath($previousRuntimeRoot)
                    config_hash=$previousConfigHash
                }
                $rollbackIdentity=Start-BeesServerRuntimeProcess $Config $node $previousRuntimeRoot $WorkerToken $AdminToken $serverLog $rollbackRuntimeIdentity $replacementError
                Prune-BeesServerRuntimes @($previousRuntimeRoot)
                $rolledBack=$true
            } catch {
                $rollbackError=$_.Exception.Message
            }
        }
        if($rolledBack){
            throw "Replacement BeesServer failed, but the previous verified runtime was restored successfully. replacement_error=$replacementError"
        }
        if($rollbackError){
            throw "Replacement BeesServer failed and rollback also failed. replacement_error=$replacementError rollback_error=$rollbackError"
        }
        throw
    }

    Prune-BeesServerRuntimes @($serverRuntimeRoot)
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

function Quote-Arg([string]$Value){
    if($Value -notmatch '[\s"]'){ return $Value }

    # Start-Process reparses its ArgumentList before creating the child process. Quoting an
    # entire --name=value token can lose that quoting layer and split a spaced value such as
    # --unity-editor=C:\Program Files\Unity\... into multiple argv entries. Preserve the option
    # name outside the quotes and quote only the value.
    $equals=$Value.IndexOf('=')
    if($equals -gt 2 -and $Value.StartsWith('--')){
        $name=$Value.Substring(0,$equals + 1)
        $argumentValue=$Value.Substring($equals + 1).Replace('"','\"')
        return $name + '"' + $argumentValue + '"'
    }

    '"' + ($Value.Replace('"','\"')) + '"'
}

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
    if(-not(Test-ManagedProcessIdentity $State)){
        $id=Get-StateReferencedLivePid $State
        if($id -gt 0){
            throw "Refusing to stop $Label PID $id because its persisted PID/start-time/executable ownership does not match the live process. The PID may have been reused."
        }
        return $false
    }
    if($ExpectedExecutable -and -not(Test-ManagedProcessIdentity $State $ExpectedExecutable)){
        $ownedExecutable=[string](Get-ObjectPropertyValue $State 'executable_path')
        Write-Host "$Label desired executable changed; safely replacing verified owned process $ownedExecutable."
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

function Start-CentralAgentIfNeeded(
    $Config,
    [string]$BootstrapPython,
    [string]$Unity,
    $Release,
    $PreparedRuntime=$null
){
    Ensure-Directory $RuntimeRoot
    Ensure-Directory (Join-Path $LogsRoot 'Training')
    Ensure-Directory $CentralAgentInstallRoot
    $outLog=Join-Path $LogsRoot 'Training\central-agent.out.log'
    $errLog=Join-Path $LogsRoot 'Training\central-agent.err.log'

    if($null -eq $PreparedRuntime){
        $PreparedRuntime=Prepare-CentralReleaseRuntime $Config $BootstrapPython $Unity $Release
    }
    $agent=Join-Path $AssetsRoot 'Training\bees_training_worker_agent.py'
    if(-not(Test-Path -LiteralPath $agent)){
        throw "Stable central training supervisor is missing: $agent"
    }
    if(-not(Test-PythonCode $BootstrapPython 'import sys; raise SystemExit(0 if sys.version_info[:2] == (3,10) else 1)')){
        throw "Central training supervisor requires Python 3.10: $BootstrapPython"
    }

    $fallback=Get-CentralFallbackLaunchCommand $Config $Unity $PreparedRuntime
    $fallbackCommand=@($fallback.launch_command)
    $supervisorArgs=@(
        '-u',$agent,
        '--server-url',[string]$Config.controlUrl,
        '--token-file',$WorkerTokenPath,
        '--trainer-id','central-learner',
        '--role','dedicated',
        '--platform','WindowsPlayer',
        '--install-root',$CentralAgentInstallRoot,
        '--runtime-ready-file',$CentralRuntimeReadyBuildPath,
        '--runtime-cutover-pointer',$CentralRuntimePointerPath,
        '--runtime-state-file',$CentralRuntimeStatePath,
        '--shutdown-request-file',$CentralAgentShutdownRequestPath
    )
    $args=@($supervisorArgs + @('--') + $fallbackCommand)
    $argString=($args|ForEach-Object{Quote-Arg ([string]$_)}) -join ' '

    $agentSourceHash=(Get-FileHash -LiteralPath $agent -Algorithm SHA256).Hash.ToLowerInvariant()
    $workerTokenHash=(Get-FileHash -LiteralPath $WorkerTokenPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $commandHash=Get-StringSha256 (
        [IO.Path]::GetFullPath($BootstrapPython) + [Environment]::NewLine +
        $agentSourceHash + [Environment]::NewLine +
        $workerTokenHash + [Environment]::NewLine +
        (($supervisorArgs|ForEach-Object{[string]$_}) -join [Environment]::NewLine)
    )

    $existing=$null
    if(Test-Path -LiteralPath $CentralAgentStatePath){
        try{$existing=Get-Content -LiteralPath $CentralAgentStatePath -Raw|ConvertFrom-Json}catch{$existing=$null}
        if($null -ne $existing){
            if(Test-ManagedProcessIdentity $existing){
                if(
                    (Test-ManagedProcessIdentity $existing $BootstrapPython) -and
                    ([string](Get-ObjectPropertyValue $existing 'command_hash')) -eq $commandHash -and
                    [bool](Get-ObjectPropertyValue $existing 'runtime_cutover_capable')
                ){
                    return
                }
                Write-Host 'Central supervisor/control configuration changed; checkpointing the learner before replacing the verified supervisor.'
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
    $p=Start-Process -FilePath $BootstrapPython -ArgumentList $argString -WorkingDirectory $AssetsRoot -RedirectStandardOutput $outLog -RedirectStandardError $errLog -WindowStyle Hidden -PassThru
    $identity=Get-ProcessIdentity $p.Id
    if($null -eq $identity -or -not [string]::Equals(
        [string]$identity.executable_path,
        [IO.Path]::GetFullPath($BootstrapPython),
        [StringComparison]::OrdinalIgnoreCase
    )){
        try{$p.Kill()}catch{}
        throw 'Could not establish the stable central supervisor process identity after launch.'
    }

    $p.Id | Set-Content -LiteralPath $CentralAgentPidPath -NoNewline
    [pscustomobject]@{
        schema_version=3
        pid=[int]$identity.pid
        process_start_utc=[string]$identity.process_start_utc
        executable_path=[string]$identity.executable_path
        command_hash=$commandHash
        supervisor_python=[IO.Path]::GetFullPath($BootstrapPython)
        learner_python=[string]$PreparedRuntime.learner_python
        release_runtime_root=[string]$PreparedRuntime.runtime_root
        release_runtime_version=[string]$PreparedRuntime.runtime_version
        runtime_cutover_pointer=$CentralRuntimePointerPath
        runtime_ready_file=$CentralRuntimeReadyBuildPath
        runtime_state_file=$CentralRuntimeStatePath
        runtime_cutover_capable=$true
        fallback_build_id=[string]$fallback.build_id
        graceful_checkpoint_shutdown=$true
        started_utc=[DateTime]::UtcNow.ToString('o')
    }|ConvertTo-Json|Set-Content -LiteralPath $CentralAgentStatePath -Encoding UTF8
    Write-Host "Stable central training supervisor started with PID $($p.Id)."
}

function Get-EnvironmentArgs($Config){ if($null -ne $EnvArg -and $EnvArg.Count -gt 0){return @($EnvArg)}; if($null -eq $Config.environmentArgs){return @()}; @($Config.environmentArgs|ForEach-Object{[string]$_}) }

function Escape-SingleQuoted([string]$Value){ $Value.Replace("'","''") }
function Escape-BashDoubleQuoted([string]$Value){
    if($Value -notmatch '^[A-Za-z0-9_@.:/%~+\-]+$'){
        throw "Remote Linux launcher value contains unsupported shell characters: $Value"
    }
    $Value
}

function Prepare-RemoteBootstrap($Config,[string]$Python,$Release){
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
    $releaseRuntime=Resolve-ReleaseTrainingRuntime $Python $Release -AllowLegacyPin
    $runtimeVersion=[string]$releaseRuntime.runtime_version
    $releaseRuntimeArchive=[string]$releaseRuntime.archive
    # Keep the standalone runtime mirror during migration so an already-running pre-bundle
    # gateway can still serve a coherent payload until Start-TailnetGatewayIfNeeded replaces it.
    $runtimeZip=Join-Path $RemoteRoot 'bees-remote-runtime.zip'
    $runtimeZipTemp=Join-Path $RemoteRoot 'bees-remote-runtime.new.zip'
    Remove-Item -LiteralPath $runtimeZipTemp -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath $releaseRuntimeArchive -Destination $runtimeZipTemp
    Install-AtomicFile $runtimeZipTemp $runtimeZip

    if(-not(Test-Path -LiteralPath $BootstrapBundleScript)){
        throw "Remote bootstrap bundle publisher is missing: $BootstrapBundleScript"
    }
    $bootstrapBundleCandidate=Join-Path $RuntimeRoot 'bees-bootstrap-bundle.candidate.zip'
    $windowsLauncherCandidate=Join-Path $RuntimeRoot 'bees-remote-worker.candidate.cmd'
    $linuxLauncherCandidate=Join-Path $RuntimeRoot 'bees-remote-worker.candidate.sh'
    foreach($candidate in @($bootstrapBundleCandidate,$windowsLauncherCandidate,$linuxLauncherCandidate)){
        Remove-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
    }
    $bootstrapBundle=Invoke-PythonJson $Python @(
        $BootstrapBundleScript,
        '--output',$bootstrapBundleCandidate,
        '--runtime',$releaseRuntimeArchive,
        '--worker-token',$WorkerTokenPath,
        '--wan-token',$WanTokenPath,
        '--release',$LatestReleasePath,
        '--windows-bridge',$windowsBridge,
        '--linux-bridge',$linuxBridge
    ) $AssetsRoot
    if(([string]$bootstrapBundle.build_id) -ne ([string]$Release.build_id)){
        throw "Published bootstrap bundle build identity disagrees with release. bundle=$($bootstrapBundle.build_id) release=$($Release.build_id)"
    }

    $windowsTemplate=Get-Content -LiteralPath $RemoteBootstrapTemplate -Raw
    $linuxTemplate=Get-Content -LiteralPath $RemoteLinuxBootstrapTemplate -Raw
    $utf8NoBom=New-Object Text.UTF8Encoding($false)

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
    [IO.File]::WriteAllText($windowsLauncherCandidate,$windowsCmd,$utf8NoBom)

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
    [IO.File]::WriteAllText($linuxLauncherCandidate,$linuxWrapper,$utf8NoBom)

    # Publish only after every candidate has been generated and validated. The bundle goes last:
    # existing remotes keep seeing the previous complete generation until both copy-and-run
    # launchers for the next generation are safely in place.
    Install-AtomicFile $windowsLauncherCandidate (Join-Path $RemoteRoot 'bees-remote-worker.cmd')
    Install-AtomicFile $linuxLauncherCandidate (Join-Path $RemoteRoot 'bees-remote-worker.sh')
    Install-AtomicFile $bootstrapBundleCandidate $BootstrapBundlePath

    # Remove only deprecated generated names after the replacement set is durable.
    Get-ChildItem -LiteralPath $RemoteRoot -Filter 'bees-remote-worker-*.ps1' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem -LiteralPath $RemoteRoot -Filter 'bees-remote-worker-*.cmd' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem -LiteralPath $RemoteRoot -Filter 'bees-remote-worker-*.sh' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item -LiteralPath (Join-Path $RemoteRoot $windowsBridgeName) -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $RemoteRoot $linuxBridgeName) -Force -ErrorAction SilentlyContinue

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

    $bootstrapPython=Resolve-Python $config
    if(-not(Test-PythonCode $bootstrapPython 'import sys; raise SystemExit(0 if sys.version_info[:2] == (3,10) else 1)')){
        throw "Bees release tooling requires Python 3.10. Configured python resolved to '$bootstrapPython'."
    }
    $installedReleaseRuntime=Install-ReleaseTrainingRuntime $bootstrapPython $release -AllowLegacyPin
    $runtimeRoot=[string]$installedReleaseRuntime.installed_root

    $pythonResult=@(Ensure-LearnerPython $config $runtimeRoot)
    if($pythonResult.Count -ne 1){
        throw "Learner Python resolver returned $($pythonResult.Count) values; expected exactly one executable path."
    }
    $python=[string]$pythonResult[0]
    if(-not(Test-Path -LiteralPath $python)){
        throw "Managed learner Python executable is missing: $python"
    }

    Ensure-RunLifecycleMatchesRelease $python $release
    Assert-CentralAgentCheckpointSafe

    $forcedPlan=Get-PendingForcedNewRunPlan
    $resumeForcedNewRun=($null -ne $forcedPlan)
    $outgoingRun=$null

    if($resumeForcedNewRun){
        $planBuild=([string](Get-ObjectPropertyValue $forcedPlan 'build_id')).Trim()
        $planRun=([string]$forcedPlan.run_id).Trim()
        $planKey=([string]$forcedPlan.compatibility_key).Trim().ToLowerInvariant()
        $planPreviousRun=([string]$forcedPlan.previous_run_id).Trim()
        $planPreviousKey=([string]$forcedPlan.previous_compatibility_key).Trim().ToLowerInvariant()
        $releaseBuild=([string]$release.build_id).Trim()
        $releaseRun=([string]$release.run_id).Trim()
        $releaseKey=([string]$release.compatibility_key).Trim().ToLowerInvariant()

        if(-not $planBuild){
            $planBuild=$releaseBuild
            Write-Warning "Resuming a legacy forced-new run plan without a persisted build binding; binding this recovery attempt to latest release $releaseBuild."
        } elseif($planBuild -ne $releaseBuild){
            throw "Pending forced-new operation targets build $planBuild but latest release is $releaseBuild. Refusing to guess which release should own the run."
        }
        $outgoingRun=$planPreviousRun
        $persistedEnvironmentArgs=Get-ObjectPropertyValue $forcedPlan 'environment_args'
        if($null -eq $persistedEnvironmentArgs){
            if($NewRun){
                Write-Warning 'Legacy forced-new plan has no persisted environment arguments; using the arguments supplied on this retry.'
            } else {
                $resumeStatus=Invoke-ControlGet "$($config.controlUrl)/v1/status" $admin
                $envArgs=@($resumeStatus.desired.environment_args | ForEach-Object {[string]$_})
                Write-Warning 'Legacy forced-new plan has no persisted environment arguments; using the server-owned desired arguments for one-time recovery.'
            }
        } else {
            $envArgs=@(@($persistedEnvironmentArgs) | ForEach-Object {[string]$_})
        }

        if($releaseRun -eq $planRun -and $releaseKey -eq $planKey){
            Write-Host "Resuming interrupted forced new-run operation: target=$planRun build=$planBuild."
        } elseif($releaseRun -eq $planPreviousRun -and $releaseKey -eq $planPreviousKey){
            $release=Convert-ReleaseToForcedRunPlan $release $forcedPlan $outgoingRun
            Save-LatestRelease $release
            Commit-TrainingRunPlan $python
            Write-Host "Recovered forced new-run intent before release staging: target=$planRun build=$planBuild."
        } else {
            throw "Pending forced-new operation does not match either the latest release or its recorded predecessor. plan=$planRun previous=$planPreviousRun release=$releaseRun"
        }
    } elseif($NewRun){
        $status=Invoke-ControlGet "$($config.controlUrl)/v1/status" $admin
        $pending=$status.desired.pending_release
        if($pending){
            $pendingBuild=([string]$pending.build_id).Trim()
            $pendingRun=([string]$pending.run_id).Trim()
            $pendingKey=([string]$pending.compatibility_key).Trim().ToLowerInvariant()
            $pendingIncompatible=[bool]$pending.incompatible
            $latestBuild=([string]$release.build_id).Trim()
            $latestRun=([string]$release.run_id).Trim()
            $latestKey=([string]$release.compatibility_key).Trim().ToLowerInvariant()

            if(-not $pendingIncompatible -and
               $pendingBuild -eq $latestBuild -and
               $pendingRun -eq $latestRun -and
               $pendingKey -eq $latestKey){
                Write-Host "Latest compatible release is still rolling out (phase=$($pending.phase)); waiting for build $pendingBuild to become canonical before forcing the new run."
                $status=Wait-ReleaseRollout $config $admin $pendingBuild $pendingRun $pendingKey
            } else {
                throw "Cannot force a new training run while a different or incompatible release rollout is pending (build=$pendingBuild run=$pendingRun phase=$($pending.phase) incompatible=$pendingIncompatible)."
            }
        }
        $outgoingRun=([string]$status.desired.run_id).Trim()
        if(-not $outgoingRun){ $outgoingRun=([string]$release.run_id).Trim() }
        Archive-TrainingRun $python $outgoingRun 'forced-new-precutover'

        $forcedPlan=New-TrainingRunPlan $python -ForceNew -BuildId ([string]$release.build_id) -EnvironmentArgs @($envArgs)
        if($forcedPlan.previous_run_id -and
            $outgoingRun -and
            ([string]$forcedPlan.previous_run_id) -ne $outgoingRun){
            throw "Run lifecycle state disagrees with active training run. lifecycle=$($forcedPlan.previous_run_id) active=$outgoingRun"
        }
        $release=Convert-ReleaseToForcedRunPlan $release $forcedPlan $outgoingRun
        # The forced plan is durable operation intent. Keep it until the replacement run is
        # promoted and the outgoing run's terminal archive succeeds, so any later start can resume.
        Save-LatestRelease $release
        Commit-TrainingRunPlan $python
        Write-Host "Forcing fresh training run: $($release.run_id) (same build $($release.build_id))."
    }

    $performForcedNewRun=($NewRun -or $resumeForcedNewRun)

    $unity=Resolve-UnityEditor $config
    $centralRuntime=Prepare-CentralReleaseRuntime $config $bootstrapPython $unity $release
    Ensure-TailnetIdentity $config
    Prepare-RemoteBootstrap $config $python $release
    Publish-Release $config $admin $release
    Start-TailnetGatewayIfNeeded $config
    Start-CentralAgentIfNeeded $config $bootstrapPython $unity $release $centralRuntime

    $staged=Stage-Release $config $admin $release
    $desired=Invoke-ControlPost "$($config.controlUrl)/v1/admin/state" $admin @{
        training_enabled=$true
        environment_args=@($envArgs)
    }

    if($performForcedNewRun){
        $null=Wait-ReleaseRollout $config $admin ([string]$release.build_id) ([string]$release.run_id) ([string]$release.compatibility_key)
        if($outgoingRun){
            Start-Sleep -Seconds 2
            Archive-TrainingRun $python $outgoingRun 'forced-new-final'
        }
        Complete-ForcedNewRunPlan $forcedPlan $release
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
            if(Test-ManagedProcessIdentity $serverState){
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
            if(Test-ManagedProcessIdentity $gatewayState){
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
    } catch {
        $lines += "Server: OFFLINE/UNREACHABLE - $($_.Exception.Message)"
        return $lines
    }

    try {
        $d=Get-ObjectPropertyValue $s 'desired'
        if($null -eq $d){ throw 'status payload has no desired state object' }
        $trainingEnabled=Get-ObjectPropertyValue $d 'training_enabled'
        $revision=Get-ObjectPropertyValue $d 'revision'
        $canonicalBuildId=Get-ObjectPropertyValue $d 'canonical_build_id'
        $runId=Get-ObjectPropertyValue $d 'run_id'
        $pending=Get-ObjectPropertyValue $d 'pending_release'
        $trainerValue=Get-ObjectPropertyValue $s 'trainers'
        $trainerRecords=if($null -eq $trainerValue){@()}else{@($trainerValue)}

        $lines += "Server: ONLINE   Training: $trainingEnabled   Revision: $revision"
        $lines += "Build:  $canonicalBuildId   Run: $runId"
        $lines += "Cluster: local_envs=$($Config.numLocalEnvs) max_remote=$($Config.maxRemoteActors) broker_port=$($Config.brokerPort)"
        if($null -ne $pending){
            $pendingBuildId=Get-ObjectPropertyValue $pending 'build_id'
            $pendingPhase=Get-ObjectPropertyValue $pending 'phase'
            $pendingIncompatible=Get-ObjectPropertyValue $pending 'incompatible'
            $lines += "Pending release: build=$pendingBuildId phase=$pendingPhase incompatible=$pendingIncompatible"
            $required=@(Get-ObjectPropertyValue $pending 'required_trainers')
            $failureGraceSeconds=Get-ObjectPropertyValue $d 'compatible_failure_grace_seconds'
            $blockers=@()
            foreach($requiredTrainer in $required){
                $requiredId=[string](Get-ObjectPropertyValue $requiredTrainer 'trainer_id')
                $requiredPlatform=[string](Get-ObjectPropertyValue $requiredTrainer 'platform')
                $failureSinceMs=Get-ObjectPropertyValue $requiredTrainer 'failure_since_ms'
                $failureGraceDisplay=''
                if($null -ne $failureSinceMs){
                    $unixNowMs=([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()*1000)
                    $failureAgeSeconds=[Math]::Max(0.0,($unixNowMs-[double]$failureSinceMs)/1000.0)
                    if($null -ne $failureGraceSeconds){
                        $failureGraceDisplay=(" failure-grace={0:N1}/{1:N0}s" -f $failureAgeSeconds,[double]$failureGraceSeconds)
                    }else{
                        $failureGraceDisplay=(" failure-grace={0:N1}s" -f $failureAgeSeconds)
                    }
                }
                $record=@($trainerRecords|Where-Object{
                    [string](Get-ObjectPropertyValue $_ 'trainer_id') -eq $requiredId
                }|Select-Object -First 1)
                if($record.Count -eq 0){
                    $blockers += ("{0}[{1}]: missing/no heartbeat" -f $requiredId,$requiredPlatform)
                    continue
                }
                $r=$record[0]
                $stale=[bool](Get-ObjectPropertyValue $r 'stale')
                $age=Get-ObjectPropertyValue $r 'age_seconds'
                $build=[string](Get-ObjectPropertyValue $r 'build_id')
                $prepared=[string](Get-ObjectPropertyValue $r 'prepared_build_id')
                $state=[string](Get-ObjectPropertyValue $r 'process_state')
                $rev=Get-ObjectPropertyValue $r 'applied_revision'
                $error=[string](Get-ObjectPropertyValue $r 'last_error')
                $ready=($build -eq [string]$pendingBuildId -or $prepared -eq [string]$pendingBuildId)
                $phaseRevision=Get-ObjectPropertyValue $pending 'phase_revision'
                if($pendingPhase -eq 'preparing' -and ($stale -or -not $ready)){
                    $reason=if($stale){'STALE'}else{'not prepared'}
                    $blockers += ("{0}[{1}]: {2} state={3} age={4:N1}s build={5} prepared={6} rev={7}{8}" -f
                        $requiredId,$requiredPlatform,$reason,$state,[double]$age,
                        $(if($build){$build}else{'-'}),
                        $(if($prepared){$prepared}else{'-'}),
                        $(if($null -ne $rev){$rev}else{'-'}),
                        $(if($error){" error=$error"}else{''}) + $failureGraceDisplay)
                }elseif($pendingPhase -eq 'rolling' -and
                        ($stale -or $build -ne [string]$pendingBuildId -or $state -ne 'running' -or
                         $error -or ($null -ne $phaseRevision -and [int]$rev -lt [int]$phaseRevision))){
                    $blockers += ("{0}[{1}]: rollout state={2} age={3:N1}s build={4} prepared={5} rev={6}{7}" -f
                        $requiredId,$requiredPlatform,$state,[double]$age,
                        $(if($build){$build}else{'-'}),
                        $(if($prepared){$prepared}else{'-'}),
                        $(if($null -ne $rev){$rev}else{'-'}),
                        $(if($error){" error=$error"}else{''}) + $failureGraceDisplay)
                }elseif($pendingPhase -eq 'stopping' -and
                        ($stale -or $state -ne 'stopped' -or
                         ($null -ne $phaseRevision -and [int]$rev -lt [int]$phaseRevision))){
                    $blockers += ("{0}[{1}]: stop state={2} age={3:N1}s build={4} rev={5}{6}" -f
                        $requiredId,$requiredPlatform,$state,[double]$age,
                        $(if($build){$build}else{'-'}),
                        $(if($null -ne $rev){$rev}else{'-'}),
                        $(if($error){" error=$error"}else{''}))
                }
            }
            if($blockers.Count){
                $lines += "Rollout blockers:"
                $lines += @($blockers|ForEach-Object{"  $_"})
            }else{
                $lines += "Rollout blockers: none visible; waiting for the control state machine to advance."
            }
        }
        $environmentArgs=Get-ObjectPropertyValue $d 'environment_args'
        $ea=if($null -eq $environmentArgs){@()}else{@($environmentArgs)}
        $lines += "Env:    $(if($ea.Count){$ea -join ' '}else{'(none)'})"
        $lines += ''

        $rows=@($trainerRecords|ForEach-Object{
            $record=$_
            $m=Get-ObjectPropertyValue $record 'metrics'
            $cap=Get-ObjectPropertyValue $record 'worker_capacity'
            $opt=Get-ObjectPropertyValue $record 'env_optimizer'
            $throughput=Get-ObjectPropertyValue $m 'throughput'
            $windowEpisodes=Get-ObjectPropertyValue $m 'window_episodes'
            $timeoutPct=Get-ObjectPropertyValue $m 'timeout_pct'
            $beeWinPct=Get-ObjectPropertyValue $m 'bee_win_pct'
            $humanWinPct=Get-ObjectPropertyValue $m 'human_win_pct'
            $drawPct=Get-ObjectPropertyValue $m 'draw_pct'
            $avgDuration=Get-ObjectPropertyValue $m 'avg_duration_s'
            $beeHitsPerShot=Get-ObjectPropertyValue $m 'bee_hits_per_shot'
            $humanHitsPerShot=Get-ObjectPropertyValue $m 'human_hits_per_shot'
            $beeAimSamples=Get-ObjectPropertyValue $m 'bee_aim_samples'
            $humanAimSamples=Get-ObjectPropertyValue $m 'human_aim_samples'
            $beeAimError=Get-ObjectPropertyValue $m 'bee_aim_error_deg'
            $humanAimError=Get-ObjectPropertyValue $m 'human_aim_error_deg'
            $beeAimWithin5=Get-ObjectPropertyValue $m 'bee_aim_within_5_pct'
            $humanAimWithin5=Get-ObjectPropertyValue $m 'human_aim_within_5_pct'
            $sentBytes=Get-ObjectPropertyValue $throughput 'network_sent_bytes_total'
            $receivedBytes=Get-ObjectPropertyValue $throughput 'network_received_bytes_total'
            $networkMibPerS=Get-ObjectPropertyValue $throughput 'network_mib_per_s'
            $currentEnvs=Get-ObjectPropertyValue $cap 'current_envs'
            $desiredEnvs=Get-ObjectPropertyValue $opt 'desired_envs'
            $measuredSps=Get-ObjectPropertyValue $opt 'measured_sps'
            $baselineSps=Get-ObjectPropertyValue $opt 'baseline_sps'
            $optimizerPhase=Get-ObjectPropertyValue $opt 'phase'
            $trainerId=Get-ObjectPropertyValue $record 'trainer_id'
            $role=Get-ObjectPropertyValue $record 'role'
            $platform=Get-ObjectPropertyValue $record 'platform'
            $processState=Get-ObjectPropertyValue $record 'process_state'
            $stale=Get-ObjectPropertyValue $record 'stale'
            $buildId=Get-ObjectPropertyValue $record 'build_id'
            $appliedRevision=Get-ObjectPropertyValue $record 'applied_revision'
            $ageSeconds=Get-ObjectPropertyValue $record 'age_seconds'
            $lastError=Get-ObjectPropertyValue $record 'last_error'
            $envDisplay='-'
            if($null -ne $currentEnvs){
                $envDisplay=[string]$currentEnvs
                if($null -ne $desiredEnvs -and [int]$desiredEnvs -ne [int]$currentEnvs){
                    $envDisplay="$currentEnvs->$desiredEnvs"
                }
            }
            $optimizerExperienceSps='-'
            if($null -ne $measuredSps){
                $optimizerExperienceSps=('{0:N0}'-f[double]$measuredSps)
            }elseif($null -ne $baselineSps){
                $optimizerExperienceSps=('{0:N0}'-f[double]$baselineSps)
            }
            [pscustomobject]@{
                Trainer=if($trainerId){$trainerId}else{'-'}
                Role=if($role){$role}else{'-'}
                Platform=if($platform){$platform}else{'-'}
                State=if($stale){'STALE'}elseif($processState){$processState}else{'-'}
                Envs=$envDisplay
                'OptExp/s'=$optimizerExperienceSps
                SentGiB=if($null -ne $sentBytes){'{0:N2}'-f([double]$sentBytes/1GB)}else{'-'}
                RecvGiB=if($null -ne $receivedBytes){'{0:N2}'-f([double]$receivedBytes/1GB)}else{'-'}
                'MiB/s'=if($null -ne $networkMibPerS){'{0:N2}'-f[double]$networkMibPerS}else{'-'}
                Opt=if($optimizerPhase){[string]$optimizerPhase}else{'-'}
                Build=if($buildId){$buildId}else{'-'}
                Rev=if($null -ne $appliedRevision){$appliedRevision}else{'-'}
                Age=if($null -ne $ageSeconds){'{0:N1}s'-f[double]$ageSeconds}else{'-'}
                Timeout=if($windowEpisodes -and $null -ne $timeoutPct){'{0:N1}%'-f[double]$timeoutPct}else{'-'}
                BWin=if($windowEpisodes -and $null -ne $beeWinPct){'{0:N1}%'-f[double]$beeWinPct}else{'-'}
                HWin=if($windowEpisodes -and $null -ne $humanWinPct){'{0:N1}%'-f[double]$humanWinPct}else{'-'}
                Draw=if($windowEpisodes -and $null -ne $drawPct){'{0:N1}%'-f[double]$drawPct}else{'-'}
                Dur=if($windowEpisodes -and $null -ne $avgDuration){'{0:N1}s'-f[double]$avgDuration}else{'-'}
                'BHit/Sh'=if($windowEpisodes -and $null -ne $beeHitsPerShot){'{0:N2}x'-f[double]$beeHitsPerShot}else{'-'}
                'HHit/Sh'=if($windowEpisodes -and $null -ne $humanHitsPerShot){'{0:N2}x'-f[double]$humanHitsPerShot}else{'-'}
                BAim=if($beeAimSamples -and $null -ne $beeAimError){'{0:N1}deg'-f[double]$beeAimError}else{'-'}
                HAim=if($humanAimSamples -and $null -ne $humanAimError){'{0:N1}deg'-f[double]$humanAimError}else{'-'}
                'B<5'=if($beeAimSamples -and $null -ne $beeAimWithin5){'{0:N1}%'-f[double]$beeAimWithin5}else{'-'}
                'H<5'=if($humanAimSamples -and $null -ne $humanAimWithin5){'{0:N1}%'-f[double]$humanAimWithin5}else{'-'}
                Error=if($lastError){$lastError}else{''}
            }
        })
        if($rows.Count){
            $table=($rows|Format-Table Trainer,Role,Platform,State,Envs,'OptExp/s',SentGiB,RecvGiB,'MiB/s',Opt,Build,Rev,Age,Timeout,BWin,HWin,Draw,Dur,'BHit/Sh','HHit/Sh',BAim,HAim,'B<5','H<5',Error -AutoSize|Out-String -Width 340).TrimEnd()
            if($table){
                $lines += @($table -split "\r?\n")
            }
        }else{
            $lines += 'No managed trainers/gameplay builds have checked in.'
        }

        $expected=@($Config.expectedTrainers)
        if($expected.Count){
            $present=@($trainerRecords|ForEach-Object{[string](Get-ObjectPropertyValue $_ 'trainer_id')})
            $missing=@($expected|Where-Object{$present -notcontains [string]$_})
            if($missing.Count){
                $lines += "WARNING: Expected trainers not connected: $($missing -join ', ')"
            }
        }

        $l=Get-LocalLearnerStats
        $lines += ''
        $lines += ("Learner logs: Step={0}  ELO={1}  MeanReward={2}  LearnerAvgStep/s={3}  LearnerLiveStep/s={4}" -f $(if($null -eq $l.Step){'-'}else{$l.Step}),$(if($null -eq $l.ELO){'-'}else{'{0:N1}'-f$l.ELO}),$(if($null -eq $l.MeanReward){'-'}else{'{0:N3}'-f$l.MeanReward}),$(if($null -eq $l.AverageStepsPerSecond){'-'}else{'{0:N1}'-f$l.AverageStepsPerSecond}),$(if($null -eq $l.LiveStepsPerSecond){'-'}else{'{0:N1}'-f$l.LiveStepsPerSecond}))
        $lines += 'Rates: OptExp/s is the last per-worker optimizer consumption sample; learner Step/s is the global ML-Agents training-step rate.'
    } catch {
        $lines += ''
        $lines += "Dashboard: RENDER ERROR - $($_.Exception.GetType().Name): $($_.Exception.Message)"
        $lines += 'Control endpoint: RESPONDED. The server is reachable; only this status snapshot failed to render completely.'
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

function Invoke-CentralDiagnosticBenchmark(
    [string]$TargetRunId,
    [string]$SnapshotJson,
    [string]$OutputJson
){
    $result=[ordered]@{
        schema_version=1
        status='skipped'
        benchmark='deterministic-wasp-vs-gunship-v1'
        run_id=$TargetRunId
        requested_utc=[DateTime]::UtcNow.ToString('o')
        reason=''
    }
    $stdout=$null
    $stderr=$null

    try {
        if(-not(Test-Path -LiteralPath $DiagnosticBenchmarkScript)){
            $result.reason="diagnostic benchmark helper is missing: $DiagnosticBenchmarkScript"
            return
        }
        if(-not(Test-Path -LiteralPath $SnapshotJson)){
            $result.reason='live model snapshot metadata is unavailable'
            return
        }

        $snapshot=Get-Content -LiteralPath $SnapshotJson -Raw | ConvertFrom-Json
        if(([string]$snapshot.status) -ne 'succeeded'){
            $result.reason="live model snapshot status is $([string]$snapshot.status)"
            return
        }
        if(([string]$snapshot.run_id) -ne $TargetRunId){
            $result.reason="live model snapshot belongs to run $([string]$snapshot.run_id)"
            return
        }

        $modelPath=[string]$snapshot.model_path
        if(-not $modelPath -or -not(Test-Path -LiteralPath $modelPath)){
            $result.reason="live model snapshot file is unavailable: $modelPath"
            return
        }

        $currentBuildPath=Join-Path $CentralAgentInstallRoot 'current.json'
        if(-not(Test-Path -LiteralPath $currentBuildPath)){
            $result.reason='central learner has no installed current build manifest'
            return
        }
        $currentBuild=Get-Content -LiteralPath $currentBuildPath -Raw | ConvertFrom-Json
        $environmentPath=[string]$currentBuild.entrypoint
        if(-not $environmentPath -or -not(Test-Path -LiteralPath $environmentPath)){
            $result.reason="central learner training executable is unavailable: $environmentPath"
            return
        }

        $learnerPython=''
        $currentBuildId=([string](Get-ObjectPropertyValue $currentBuild 'build_id')).Trim()
        if(Test-Path -LiteralPath $CentralRuntimeStatePath){
            try {
                $runtimeState=Get-Content -LiteralPath $CentralRuntimeStatePath -Raw | ConvertFrom-Json
                $runtimeBuild=([string](Get-ObjectPropertyValue $runtimeState 'build_id')).Trim()
                if($currentBuildId -and $runtimeBuild -ne $currentBuildId){
                    $result.reason="central learner runtime/build identity is inconsistent: build=$currentBuildId runtime=$runtimeBuild"
                    return
                }
                $learnerPython=[string](Get-ObjectPropertyValue $runtimeState 'python_executable')
            } catch {
                $result.reason="central learner active runtime state is unreadable: $CentralRuntimeStatePath"
                return
            }
        }
        if(-not $learnerPython -and (Test-Path -LiteralPath $CentralAgentStatePath)){
            # Legacy fallback for a central supervisor that predates active-runtime reporting.
            try {
                $centralState=Get-Content -LiteralPath $CentralAgentStatePath -Raw | ConvertFrom-Json
                $learnerPython=[string](Get-ObjectPropertyValue $centralState 'learner_python')
            } catch { $learnerPython='' }
        }
        if(-not $learnerPython){
            # Final legacy fallback for pre-release-isolation central state.
            $learnerPython=Join-Path $RuntimeRoot 'LearnerPython\Scripts\python.exe'
        }
        if(-not(Test-Path -LiteralPath $learnerPython)){
            $result.reason="managed learner Python is unavailable: $learnerPython"
            return
        }

        $benchmarkId=[Guid]::NewGuid().ToString('N')
        $stdout=Join-Path $RuntimeRoot "diagnostic-benchmark-$benchmarkId.out.log"
        $stderr=Join-Path $RuntimeRoot "diagnostic-benchmark-$benchmarkId.err.log"
        $args=@(
            $DiagnosticBenchmarkScript,
            '--env',$environmentPath,
            '--model',$modelPath,
            '--output',$OutputJson
        )
        $argumentString=($args|ForEach-Object{Quote-Arg ([string]$_)}) -join ' '

        Write-Host 'Running deterministic diagnostic benchmark (20 fixed 1v1 matches)...'
        $process=Start-Process -FilePath $learnerPython -ArgumentList $argumentString -WorkingDirectory $AssetsRoot -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru

        $finished=$process.WaitForExit(180000)
        if(-not $finished){
            $treeKilled=$false
            try {
                & taskkill.exe /PID $process.Id /T /F *> $null
                $treeKilled=($LASTEXITCODE -eq 0)
            } catch {
                $treeKilled=$false
            }
            if(-not $treeKilled){
                try{$process.Kill()}catch{}
            }
            $result.status='timeout'
            $result.reason='deterministic benchmark exceeded 180 seconds'
            Write-DiagnosticJson $OutputJson $result
            Write-Warning $result.reason
            return
        }
        $process.WaitForExit()

        if($process.ExitCode -ne 0){
            if(Test-Path -LiteralPath $OutputJson){
                try{
                    $failure=Get-Content -LiteralPath $OutputJson -Raw|ConvertFrom-Json
                    Write-Warning "Deterministic benchmark failed: $([string]$failure.error)"
                    return
                }catch{}
            }
            $tail=''
            if(Test-Path -LiteralPath $stderr){
                $tail=(@(Get-Content -LiteralPath $stderr -Tail 20 -ErrorAction SilentlyContinue)-join ' ')
            }
            $result.status='failed'
            $suffix=if($tail){': '+$tail}else{''}
            $result.reason="benchmark process exited with code $($process.ExitCode)$suffix"
            Write-DiagnosticJson $OutputJson $result
            Write-Warning $result.reason
            return
        }

        if(-not(Test-Path -LiteralPath $OutputJson)){
            $result.status='failed'
            $result.reason='benchmark process succeeded without writing its result JSON'
            Write-DiagnosticJson $OutputJson $result
            Write-Warning $result.reason
        }
    } catch {
        $result.status='failed'
        $result.reason="$($_.Exception.GetType().Name): $($_.Exception.Message)"
        Write-DiagnosticJson $OutputJson $result
        Write-Warning "Deterministic benchmark failed: $($result.reason)"
    } finally {
        if($stdout){Remove-Item -LiteralPath $stdout -Force -ErrorAction SilentlyContinue}
        if($stderr){Remove-Item -LiteralPath $stderr -Force -ErrorAction SilentlyContinue}
        if(-not(Test-Path -LiteralPath $OutputJson)){
            Write-DiagnosticJson $OutputJson $result
        }
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
    $benchmarkJson=Join-Path $RuntimeRoot "diagnostic-deterministic-benchmark-$bundleId.json"
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
        Invoke-CentralDiagnosticBenchmark $targetRun $snapshotJson $benchmarkJson

        # Refresh status after the snapshot/benchmark so learner-step/model-lag diagnostics compare
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
        if(Test-Path -LiteralPath $benchmarkJson){
            $arguments+=@('--benchmark-json',$benchmarkJson)
        }

        Invoke-Checked $python $arguments $AssetsRoot | Out-Host
    } finally {
        Remove-Item -LiteralPath $statusJson -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $statusText -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $snapshotJson -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $benchmarkJson -Force -ErrorAction SilentlyContinue
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
