param(
    [string]$GameDir = "F:\SteamLibrary\steamapps\common\SpiritVale",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectDir "AuctionPriceScanner.csproj"
$targetDir = Join-Path $GameDir "BepInEx\plugins\AuctionPriceScanner"
$assembly = Join-Path $projectDir "bin\$Configuration\net6.0\NevarisAuctionPriceScanner.dll"

dotnet build $project -c $Configuration -p:GameDir=$GameDir
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Copy-Item -LiteralPath $assembly -Destination $targetDir -Force
Write-Host "Installed Auction Price Scanner to $targetDir"
