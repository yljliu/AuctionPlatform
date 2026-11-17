# Applies migrations and adds demo data (users, categories, 6 auctions).
# Safe to run more than once. Run from anywhere:
#   powershell -File scripts\seed.ps1

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    docker compose --env-file .env -f deploy/compose.lite.yml run --rm --build migrator --seed
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
