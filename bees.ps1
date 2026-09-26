param(
    [Parameter(Mandatory=$true,Position=0)]
    [ValidateSet('build','server','start','stop','status','bundle','qualify')]
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
if($NewRun){$arguments+='--new-run'}
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

& $node @arguments
$exitCode=$LASTEXITCODE
if($exitCode -ne 0){exit $exitCode}
