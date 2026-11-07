# Preview a disabled current-user logon task; installation never starts it.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('coordinator','agent')]
    [string]$Role,
    [string]$TrainingDirectory=$PSScriptRoot,
    [string]$NodePath,
    [string]$EnvironmentFile,
    [switch]$Install,
    [switch]$Run
)

$ErrorActionPreference='Stop'

function Resolve-LocalFile([string]$Value) {
    if (-not [IO.Path]::IsPathRooted($Value)) { throw 'Use absolute local file paths.' }
    $resolved=(Resolve-Path -LiteralPath $Value).ProviderPath
    $item=Get-Item -LiteralPath $resolved
    if ($item.PSIsContainer -or $resolved -match '["\r\n]' -or $resolved.StartsWith('\\')) {
        throw 'Task files must be regular local files with unambiguous paths.'
    }
    return $resolved
}

function Quote-TaskArgument([string]$Value) {
    if ($Value -match '["\r\n]' -or $Value.EndsWith('\')) { throw 'Unsafe task argument.' }
    return '"'+$Value+'"'
}

if ($Run -and $Install) { throw 'Choose either installation or execution.' }
if (-not $NodePath) { $NodePath=(Get-Command node.exe -CommandType Application | Select-Object -First 1).Source }
$NodePath=Resolve-LocalFile $NodePath
$TrainingDirectory=(Resolve-Path -LiteralPath $TrainingDirectory).ProviderPath
if (-not (Get-Item -LiteralPath $TrainingDirectory).PSIsContainer) { throw 'Training directory is not a directory.' }
$entry=if ($Role -eq 'coordinator') { 'bees_experiments.js' } else { 'experiment_hub_agent.js' }
$entry=Resolve-LocalFile (Join-Path $TrainingDirectory $entry)
if ($Role -eq 'agent' -and -not $EnvironmentFile) { throw 'Agent tasks require an administrator-managed environment file.' }
if ($EnvironmentFile) { $EnvironmentFile=Resolve-LocalFile $EnvironmentFile }
if ($EnvironmentFile) {
    $nodeMajor=& $NodePath -p 'parseInt(process.versions.node)'
    if ($LASTEXITCODE -ne 0 -or [int]$nodeMajor -lt 22) { throw 'Environment files require Node 22 or newer.' }
}

if ($Run) {
    Set-Location -LiteralPath $TrainingDirectory
    $arguments=@()
    if ($EnvironmentFile) {
        # Explicit task configuration takes precedence over inherited Hub
        # settings from this user's interactive session.
        Get-ChildItem Env:EXPERIMENT_HUB_* | Remove-Item
        $arguments+='--env-file='+$EnvironmentFile
    }
    $arguments+=$entry
    & $NodePath @arguments
    exit $LASTEXITCODE
}

$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$sid=$identity.User.Value
$taskName='Bees-ExperimentHub-'+$Role
$service=New-Object -ComObject Schedule.Service
$service.Connect()
$definition=$service.NewTask(0)
$definition.RegistrationInfo.Description='Experiment Hub '+$Role+' for '+$identity.Name
$definition.Principal.UserId=$sid
$definition.Principal.LogonType=3 # InteractiveToken. No stored password.
$definition.Principal.RunLevel=0
$definition.Settings.Enabled=$false
$definition.Settings.Hidden=$true
$definition.Settings.MultipleInstances=2 # IgnoreNew.
$definition.Settings.ExecutionTimeLimit='PT0S'
$definition.Settings.DisallowStartIfOnBatteries=$false
$definition.Settings.StopIfGoingOnBatteries=$false
$definition.Settings.StartWhenAvailable=$true
# Native task-start retry settings do not establish application crash recovery.
$definition.Settings.RestartInterval='PT1M'
$definition.Settings.RestartCount=3
$trigger=$definition.Triggers.Create(9) # At this user's logon.
$trigger.UserId=$sid
$trigger.Delay='PT30S'
$action=$definition.Actions.Create(0)
$action.Path=Resolve-LocalFile (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe')
$taskArguments=@('-NoProfile','-NonInteractive','-WindowStyle','Hidden','-File',
    (Quote-TaskArgument $PSCommandPath),'-Run','-Role',$Role,
    '-TrainingDirectory',(Quote-TaskArgument $TrainingDirectory),'-NodePath',(Quote-TaskArgument $NodePath))
if ($EnvironmentFile) { $taskArguments+=@('-EnvironmentFile',(Quote-TaskArgument $EnvironmentFile)) }
$action.Arguments=$taskArguments -join ' '
$action.WorkingDirectory=$TrainingDirectory

if (-not $Install) {
    $definition.XmlText
    return
}

# TASK_CREATE fails if a task with this name already exists. Installation
# preserves the disabled setting and never starts the coordinator or worker.
$folder=$service.GetFolder('\')
$null=$folder.RegisterTaskDefinition($taskName,$definition,2,$sid,$null,3,$null)
Write-Output "Installed disabled task $taskName. Review its settings and environment file permissions before enabling it in Task Scheduler."
