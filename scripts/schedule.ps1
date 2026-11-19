# Adds extra Scheduled auctions across a time window. Keeps all existing data (users, bots, bids).
# Times are local to the -Offset you give (MST is -07:00). Slots already started are skipped.
# Safe to run again for the same window: it adds nothing twice.
#   powershell -File scripts\schedule.ps1 -From "2026-11-20 12:00" -To "2026-11-20 18:00"
#   powershell -File scripts\schedule.ps1 -From "2026-11-20 12:00" -To "2026-11-20 18:00" -Every 5
param(
    [Parameter(Mandatory)] [string] $From,
    [Parameter(Mandatory)] [string] $To,
    [int] $Every = 10,
    [string] $Offset = "-07:00"
)

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $f = ([datetime]$From).ToString("yyyy-MM-ddTHH:mm") + $Offset
    $t = ([datetime]$To).ToString("yyyy-MM-ddTHH:mm") + $Offset
    docker compose --env-file .env -f deploy/compose.lite.yml run --rm --build migrator --window $f $t $Every
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
