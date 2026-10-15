# Builds release\BFBB-Online.zip: unzip it into the Dolphin folder.
#   powershell -ExecutionPolicy Bypass -File tools\package.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "release\BFBB-Online"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
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
Get-ChildItem $out -Recurse -Include *.pdb | Remove-Item

$zip = Join-Path $root "release\BFBB-Online.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $out -DestinationPath $zip
"built $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
