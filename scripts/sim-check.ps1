# Spec 08 task 8: the closing-spike check on a running stack.
#   .\scripts\sim-check.ps1                          # 200 bots, 20 auctions, 5 minutes (the spec's check)
#   .\scripts\sim-check.ps1 -Bots 30 -Auctions 5 -Minutes 2 -Rate 3     # a lighter try for a small PC
# The stack must be up and seeded (scripts\seed.ps1). It uses http://localhost:7001 (Api) and http://localhost:7201 (Simulator).
param([int]$Bots = 200, [int]$Auctions = 20, [int]$Minutes = 5, [int]$Rate = 5)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

Get-Content .env | Where-Object { $_ -match '^\s*[^#].*=' } | ForEach-Object {
    $name, $value = $_ -split '=', 2
    Set-Item "env:$($name.Trim())" $value.Trim()
}

dotnet run --project src/Auction.Simulator -- sim-check $Bots $Auctions $Minutes $Rate
exit $LASTEXITCODE
