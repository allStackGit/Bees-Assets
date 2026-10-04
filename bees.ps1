param(
    [Parameter(Mandatory=$true,Position=0)]
    [ValidateSet('build','runtime','server','start','stop','status','bundle','observe','qualify')]
    [string]$Command,
    [switch]$FullGame,
    [switch]$Force,
    [switch]$PreserveRun,
    [switch]$NewRun,
    [string]$ResumeRun,
    [switch]$Threaded,
    [ValidateRange(0,2147483647)][int]$PolicyLag,
    [ValidateRange(1,2147483647)][int]$BackpressureQueue,
    [switch]$LocalTraining,
    [switch]$PpoControl,
    [switch]$PpoSyncCleanup,
    [ValidateSet(1,2,4)][int]$PpoStreamShards,
    [switch]$PpoPrefetch,
    [switch]$PpoCriticBaselineOverlap,
    [switch]$PpoCudaGraphs,
    [string[]]$EnvArg,
    [switch]$Once,
    [ValidateRange(1,60)][int]$RefreshSeconds=2,
    [switch]$Server,
    [ValidateRange(0.1,100.0)][double]$LogPercent=10.0,
    [string]$RunId,
    [switch]$Evaluate
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

if($PreserveRun -and $Command -ne 'build'){
    throw '-PreserveRun is only valid with the build command.'
}
if($NewRun -and $Command -ne 'start' -and $Command -ne 'build'){
    throw '-NewRun is only valid with the build or start command.'
}
if($ResumeRun -and $Command -ne 'start'){
    throw '-ResumeRun is only valid with the start command.'
}
if($ResumeRun -and $NewRun){
    throw '-ResumeRun cannot be combined with -NewRun.'
}
if($PreserveRun -and $NewRun){
    throw '-PreserveRun cannot be combined with -NewRun.'
}
if($Threaded -and $Command -ne 'runtime'){
    throw '-Threaded is only valid with the runtime command.'
}
if($LocalTraining -and $Command -ne 'runtime'){
    throw '-LocalTraining is only valid with the runtime command.'
}
if($PSBoundParameters.ContainsKey('PolicyLag') -and $Command -ne 'runtime'){
    throw '-PolicyLag is only valid with the runtime command.'
}
if($PSBoundParameters.ContainsKey('PolicyLag') -and -not $Threaded){
    throw '-PolicyLag requires -Threaded.'
}
if($PSBoundParameters.ContainsKey('BackpressureQueue') -and $Command -ne 'runtime'){
    throw '-BackpressureQueue is only valid with the runtime command.'
}
$ppoExperimentRequested=(
    $PpoControl -or
    $PpoSyncCleanup -or
    $PSBoundParameters.ContainsKey('PpoStreamShards') -or
    $PpoPrefetch -or
    $PpoCriticBaselineOverlap -or
    $PpoCudaGraphs
)
if($ppoExperimentRequested -and $Command -ne 'runtime'){
    throw 'PPO learner optimization options are only valid with the runtime command.'
}
if($PpoControl -and (
    $PpoSyncCleanup -or
    $PSBoundParameters.ContainsKey('PpoStreamShards') -or
    $PpoPrefetch -or
    $PpoCriticBaselineOverlap -or
    $PpoCudaGraphs
)){
    throw '-PpoControl cannot be combined with PPO optimization options.'
}
if($Command -ne 'bundle' -and $PSBoundParameters.ContainsKey('LogPercent')){
    throw '-LogPercent is only valid with the bundle command.'
}
if($Command -ne 'bundle' -and $RunId){
    throw '-RunId is only valid with the bundle command.'
}
if($Evaluate -and $Command -ne 'bundle'){
    throw '-Evaluate is only valid with the bundle command.'
}

$assetsRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$configPath=Join-Path $assetsRoot 'Training\bees.cluster.json'
$operator=Join-Path $assetsRoot 'Training\bees_operator.js'

if(-not(Test-Path -LiteralPath $configPath -PathType Leaf)){
    throw "Tracked training configuration is missing: $configPath"
}
if(-not(Test-Path -LiteralPath $operator -PathType Leaf)){
    throw "Bees Node operator is missing: $operator"
}

$config=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$nodeSetting=if($config.node){[string]$config.node}else{'node'}
if(Test-Path -LiteralPath $nodeSetting -PathType Leaf){
    $node=[IO.Path]::GetFullPath($nodeSetting)
}else{
    $nodeCommand=Get-Command $nodeSetting -ErrorAction SilentlyContinue
    if($null -eq $nodeCommand){
        throw "Required executable '$nodeSetting' was not found on PATH."
    }
    $node=$nodeCommand.Source
}

$arguments=@($operator,$Command)
if($FullGame){$arguments+='--full-game'}
if($Force){$arguments+='--force'}
if($PreserveRun){$arguments+='--preserve-run'}
if($NewRun){$arguments+='--new-run'}
if($ResumeRun){$arguments+=@('--resume-run',[string]$ResumeRun)}
if($Threaded){$arguments+='--threaded'}
if($PSBoundParameters.ContainsKey('PolicyLag')){
    $arguments+=@('--policy-lag',[string]$PolicyLag)
}
if($PSBoundParameters.ContainsKey('BackpressureQueue')){
    $arguments+=@('--backpressure-queue',[string]$BackpressureQueue)
}
if($LocalTraining){$arguments+='--local-training'}
if($PpoControl){$arguments+='--ppo-control'}
if($PpoSyncCleanup){$arguments+='--ppo-sync-cleanup'}
if($PSBoundParameters.ContainsKey('PpoStreamShards')){
    $arguments+=@('--ppo-stream-shards',[string]$PpoStreamShards)
}
if($PpoPrefetch){$arguments+='--ppo-prefetch'}
if($PpoCriticBaselineOverlap){$arguments+='--ppo-critic-baseline-overlap'}
if($PpoCudaGraphs){$arguments+='--ppo-cuda-graphs'}
foreach($value in @($EnvArg)){
    if($null -ne $value){$arguments+=@('--env-arg',[string]$value)}
}
if($Once){$arguments+='--once'}
if($PSBoundParameters.ContainsKey('RefreshSeconds')){
    $arguments+=@('--refresh-seconds',[string]$RefreshSeconds)
}
if($Server){$arguments+='--server'}
if($PSBoundParameters.ContainsKey('LogPercent')){
    $arguments+=@('--log-percent',$LogPercent.ToString('G',[Globalization.CultureInfo]::InvariantCulture))
}
if($RunId){$arguments+=@('--run-id',$RunId)}
if($Evaluate){$arguments+='--evaluate'}

& $node @arguments
$exitCode=$LASTEXITCODE
if($exitCode -ne 0){exit $exitCode}
