# Preview by default; install only a new disabled LocalService service.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('coordinator','agent')][string]$Role,
    [Parameter(Mandatory=$true)][string]$ServiceExecutable,
    [Parameter(Mandatory=$true)][string]$ConfigurationFile,
    [Parameter(Mandatory=$true)][string]$StateDirectory,
    [string]$TrainingDirectory=$PSScriptRoot,
    [string]$NodePath,
    [string]$EnvironmentFile,
    [string]$Name,
    [switch]$Install
)
$ErrorActionPreference='Stop'
function LocalPath([string]$Value,[bool]$Directory=$false) {
    if(-not [IO.Path]::IsPathRooted($Value) -or $Value.StartsWith('\\') -or $Value -match '["\r\n]') {
        throw 'Use absolute local paths without quotes or newlines.'
    }
    $item=Get-Item -LiteralPath $Value
    if($item.PSIsContainer -ne $Directory){throw 'Configured path has the wrong file type.'}
    return $item.FullName
}
if(-not $Name){$Name='Bees-ExperimentHub-'+$Role}
if($Name -notmatch '^Bees-ExperimentHub-[A-Za-z0-9-]{1,64}$'){throw 'Invalid service name.'}
$ServiceExecutable=LocalPath $ServiceExecutable
$TrainingDirectory=LocalPath $TrainingDirectory $true
$StateDirectory=LocalPath $StateDirectory $true
if(-not $NodePath){$NodePath=(Get-Command node.exe -CommandType Application | Select-Object -First 1).Source}
$NodePath=LocalPath $NodePath
$entry=if($Role -eq 'agent'){'experiment_hub_agent.js'}else{'bees_experiments.js'}
$null=LocalPath (Join-Path $TrainingDirectory $entry)
$null=LocalPath (Join-Path $TrainingDirectory experiment_hub_service_console.js)
if($EnvironmentFile){
    $EnvironmentFile=LocalPath $EnvironmentFile
    $major=& $NodePath -p 'parseInt(process.versions.node)'
    if($LASTEXITCODE -ne 0 -or [int]$major -lt 22){throw 'Environment files require Node 22 or newer.'}
}elseif($Role -eq 'agent'){throw 'Agent service requires a private environment file.'}
if(-not [IO.Path]::IsPathRooted($ConfigurationFile) -or $ConfigurationFile.StartsWith('\\') -or
    $ConfigurationFile -match '["\r\n]' -or (Test-Path -LiteralPath $ConfigurationFile)){
    throw 'Configuration must be an absent absolute local file.'
}
$ConfigurationFile=[IO.Path]::GetFullPath($ConfigurationFile)
$null=LocalPath ([IO.Path]::GetDirectoryName($ConfigurationFile)) $true
$config=[ordered]@{Name=$Name;Role=$Role;NodePath=$NodePath;TrainingDirectory=$TrainingDirectory;
    EnvironmentFile=$(if($EnvironmentFile){$EnvironmentFile}else{$null});StateDirectory=$StateDirectory}
$binary='"'+$ServiceExecutable+'" --service "'+$ConfigurationFile+'"'
if(-not $Install){
    [ordered]@{ServiceName=$Name;BinaryPath=$binary;Account='NT AUTHORITY\LocalService';
        Startup='Disabled';Restart='Three retries, 60 seconds apart, then stop';
        Configuration=$config} | ConvertTo-Json -Depth 3
    return
}
if(Get-Service -Name $Name -ErrorAction SilentlyContinue){throw 'Service already exists.'}
$stream=[IO.File]::Open($ConfigurationFile,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{
    $bytes=[Text.Encoding]::UTF8.GetBytes(($config | ConvertTo-Json)+[Environment]::NewLine)
    $stream.Write($bytes,0,$bytes.Length)
}finally{$stream.Dispose()}
$created=$false
try{
    $credential=New-Object Management.Automation.PSCredential('NT AUTHORITY\LocalService',(New-Object Security.SecureString))
    $null=New-Service -Name $Name -BinaryPathName $binary -StartupType Disabled -Credential $credential -Description ('Experiment Hub '+$Role+'; OS-owned process recovery, existing training ownership preserved.')
    $created=$true
    & "$env:SystemRoot\System32\sc.exe" failure $Name reset= 86400 actions= 'restart/60000/restart/60000/restart/60000//0'
    if($LASTEXITCODE -ne 0){throw 'Unable to configure bounded native recovery.'}
}catch{
    if($created){& "$env:SystemRoot\System32\sc.exe" delete $Name | Out-Null}
    Remove-Item -LiteralPath $ConfigurationFile
    throw
}
Write-Output "Installed disabled service $Name. Review its account, file permissions and recovery settings before enabling it. No Hub process was started."
