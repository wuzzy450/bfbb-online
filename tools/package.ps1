# Builds release\BFBB-Online.zip: unzip it into the Dolphin folder.
#   powershell -ExecutionPolicy Bypass -File tools\package.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "release\BFBB-Online"
# Replace only the program files: a host's own data in the release folder (party saves, settings,
# memory card, the game folder linking to the Deluxe files) is kept. The zip gets only the program.
$keep = @("server-data", "game", "memcard", "BFBB-Online.json")
if (Test-Path $out) {
    Get-ChildItem $out -Force | Where-Object { $keep -notcontains $_.Name } | Remove-Item -Recurse -Force
}
New-Item -ItemType Directory -Force "$out\server" | Out-Null

Push-Location "$root\patch"; python build.py; if ($LASTEXITCODE) { throw "patch build failed" }; Pop-Location

$pub = @("-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true",
         "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none")
dotnet publish "$root\client\BfbbOnline.csproj" @pub -o "$out"
if ($LASTEXITCODE) { throw "launcher publish failed" }
dotnet publish "$root\server\Dedicated\BfbbOnlineServer.csproj" @pub -o "$out\server"
if ($LASTEXITCODE) { throw "server publish failed" }

Copy-Item "$root\server\start_server.bat" "$out\server\"
Copy-Item "$root\README.md" "$out\README.md"
New-Item -ItemType Directory -Force "$out\docs" | Out-Null
Copy-Item "$root\docs\*.md" "$out\docs\"
Get-ChildItem $out -Force | Where-Object { $keep -notcontains $_.Name } | ForEach-Object {
    Get-ChildItem $_.FullName -Recurse -Include *.pdb -ErrorAction SilentlyContinue | Remove-Item }

$zip = Join-Path $root "release\BFBB-Online.zip"
if (Test-Path $zip) { Remove-Item $zip }
# zip a copy of the program files only (none of the host's own data)
$stage = Join-Path ([System.IO.Path]::GetTempPath()) "bbol-package\BFBB-Online"
if (Test-Path (Split-Path $stage)) { Remove-Item -Recurse -Force (Split-Path $stage) }
New-Item -ItemType Directory -Force $stage | Out-Null
Get-ChildItem $out -Force | Where-Object { $keep -notcontains $_.Name } | Copy-Item -Destination $stage -Recurse
Compress-Archive -Path $stage -DestinationPath $zip
Remove-Item -Recurse -Force (Split-Path $stage)
"built $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
