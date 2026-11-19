# Spec 05 task 7: leader failover check on the full stack.
# Run from the repo root while the full stack is up:
#   docker compose --env-file .env -f deploy/compose.yml up -d --build
#   .\scripts\failover-check.ps1          # clean stop of the leader
#   .\scripts\failover-check.ps1 kill     # crash the leader instead
param([ValidateSet("stop", "kill")][string]$Mode = "stop")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# Read SA_PASSWORD (and anything else) from .env into this window.
Get-Content .env | Where-Object { $_ -match '^\s*[^#].*=' } | ForEach-Object {
    $name, $value = $_ -split '=', 2
    Set-Item "env:$($name.Trim())" $value.Trim()
}

$env:ConnectionStrings__Sql = "Server=localhost,1433;Database=Auction;User Id=sa;Password=$env:SA_PASSWORD;TrustServerCertificate=True"
$env:ConnectionStrings__Redis = "localhost:6379"

dotnet run --project src/Auction.Simulator -- failover-check $Mode
exit $LASTEXITCODE
