$path = 'R:\Bees\Assets\results\bees-rl-1v1-full-001\run_logs'
$N = 50

$rx = 'RL 1v1 episode=(\d+).*?timeout=(True|False) duration=([\d.]+)s bee_tsv=(\d+)->(\d+) human_tsv=(\d+)->(\d+).*?bee_fire_requests=(\d+) bee_shots=(\d+) bee_hits=(\d+) bee_damage=(\d+).*?human_fire_requests=(\d+) human_shots=(\d+) human_hits=(\d+) human_damage=(\d+)'

function Parse-Episode($line, $envName) {
    if ($line -match $rx) {
        $to = $Matches[2] -eq 'True'
        $bf = [int]$Matches[5]
        $hf = [int]$Matches[7]

        return [pscustomobject]@{
            Env  = $envName
            Ep   = [int]$Matches[1]
            TO   = $to

            BW   = (!$to -and $bf -gt 0 -and $hf -eq 0)
            HW   = (!$to -and $hf -gt 0 -and $bf -eq 0)

            Dur  = [double]$Matches[3]

            BTSV = 100 * $bf / [math]::Max(1, [int]$Matches[4])
            HTSV = 100 * $hf / [math]::Max(1, [int]$Matches[6])

            BR   = [int]$Matches[8]
            BS   = [int]$Matches[9]
            BH   = [int]$Matches[10]
            BD   = [int]$Matches[11]

            HR   = [int]$Matches[12]
            HS   = [int]$Matches[13]
            HH   = [int]$Matches[14]
            HD   = [int]$Matches[15]
        }
    }

    return $null
}

function Show-Window {
    $w = @($script:q)
    $n = $w.Count

    if ($n -eq 0) { return }

    $bw = @($w | Where-Object { $_.BW }).Count
    $hw = @($w | Where-Object { $_.HW }).Count
    $to = @($w | Where-Object { $_.TO }).Count
    $d  = $n - $bw - $hw - $to

    $bs = ($w | Measure-Object BS -Sum).Sum
    $bh = ($w | Measure-Object BH -Sum).Sum
    $br = ($w | Measure-Object BR -Sum).Sum

    $hs = ($w | Measure-Object HS -Sum).Sum
    $hh = ($w | Measure-Object HH -Sum).Sum
    $hr = ($w | Measure-Object HR -Sum).Sum

    $beeHit   = if ($bs -gt 0) { 100 * $bh / $bs } else { 0 }
    $humanHit = if ($hs -gt 0) { 100 * $hh / $hs } else { 0 }

    $beeFire   = if ($br -gt 0) { 100 * $bs / $br } else { 0 }
    $humanFire = if ($hr -gt 0) { 100 * $hs / $hr } else { 0 }

    "N={0,2}  AvgDur={1,5:N1}s   ||   BEE: Win {2,2:N0}%  Loss {3,2:N0}%  Draw {4,2:N0}%  Timeout {5,2:N0}%   |   Shots {6,4:N1}  Hit {7,5:N1}%  Damage {8,5:N1}  Fire {9,5:N1}%  TSV {10,5:N1}%   ||   HUMAN: Win {11,2:N0}%  Loss {12,2:N0}%  Draw {13,2:N0}%  Timeout {14,2:N0}%   |   Shots {15,4:N1}  Hit {16,5:N1}%  Damage {17,5:N1}  Fire {18,5:N1}%  TSV {19,5:N1}%" -f `
        $n,
        (($w | Measure-Object Dur -Average).Average),

        (100 * $bw / $n),
        (100 * $hw / $n),
        (100 * $d  / $n),
        (100 * $to / $n),

        ($bs / $n),
        $beeHit,
        (($w | Measure-Object BD -Average).Average),
        $beeFire,
        (($w | Measure-Object BTSV -Average).Average),

        (100 * $hw / $n),
        (100 * $bw / $n),
        (100 * $d  / $n),
        (100 * $to / $n),

        ($hs / $n),
        $humanHit,
        (($w | Measure-Object HD -Average).Average),
        $humanFire,
        (($w | Measure-Object HTSV -Average).Average)
}

$files = @(Get-ChildItem "$path\Player-*.log")

if ($files.Count -eq 0) {
    throw "No Player-*.log files found in $path"
}

# Seed the rolling window from existing logs.
$history = foreach ($f in $files) {
    foreach ($line in Get-Content $f.FullName) {
        if ($line -like '*RL 1v1 episode=*') {
            Parse-Episode $line $f.BaseName
        }
    }
}

$script:q = @(
    $history |
        Sort-Object Ep, Env |
        Select-Object -Last $N
)

# Remember the current byte position of every log.
$state = @{}

foreach ($f in $files) {
    $state[$f.FullName] = @{
        Name    = $f.BaseName
        Position = $f.Length
        Pending  = ''
    }
}

Write-Host ""
Write-Host "Watching $($files.Count) environments. Rolling window = $($script:q.Count)/$N episodes."
Write-Host "Current rolling window:"
Show-Window
Write-Host ""
Write-Host "Waiting for new completed episodes. Ctrl+C to stop."
Write-Host ""

while ($true) {

    foreach ($f in $files) {

        $s = $state[$f.FullName]

        $info = Get-Item $f.FullName
        $length = $info.Length

        # Handle truncation/recreation of a log.
        if ($length -lt $s.Position) {
            $s.Position = 0
            $s.Pending = ''
        }

        if ($length -gt $s.Position) {

            $count = $length - $s.Position
            $bytes = New-Object byte[] $count

            $stream = [System.IO.File]::Open(
                $f.FullName,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite
            )

            try {
                $null = $stream.Seek(
                    $s.Position,
                    [System.IO.SeekOrigin]::Begin
                )

                $read = $stream.Read($bytes, 0, $bytes.Length)
            }
            finally {
                $stream.Dispose()
            }

            if ($read -gt 0) {

                $text = $s.Pending +
                    [System.Text.Encoding]::UTF8.GetString(
                        $bytes, 0, $read
                    )

                $s.Position += $read

                # Keep an unfinished final line for the next poll.
                $parts = $text -split "`r?`n"

                if ($text -match "(`r?`n)$") {
                    $completeLines = $parts
                    $s.Pending = ''
                }
                else {
                    $completeLines = $parts[0..($parts.Count - 2)]
                    $s.Pending = $parts[-1]
                }

                foreach ($line in $completeLines) {

                    if ($line -like '*RL 1v1 episode=*') {

                        $result = Parse-Episode $line $s.Name

                        if ($null -ne $result) {

                            $script:q += $result

                            if ($script:q.Count -gt $N) {
                                $script:q = @(
                                    $script:q |
                                        Select-Object -Last $N
                                )
                            }

                            Show-Window
                        }
                    }
                }
            }
        }
    }

    Start-Sleep -Milliseconds 500
}