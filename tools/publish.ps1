# Builds a single, self-contained Slate.exe (no .NET install needed) and zips it for a release.
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Runtime win-x64|win-arm64]
param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src\Slate\Slate.csproj'
$out = Join-Path $root "publish\$Runtime"

dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$version = (Select-Xml -Path $project -XPath '//Version').Node.InnerText
$zip = Join-Path $root "publish\Slate-$version-$Runtime.zip"
Compress-Archive -Path (Join-Path $out 'Slate.exe') -DestinationPath $zip -Force

$exe = Get-Item (Join-Path $out 'Slate.exe')
"Slate.exe  {0:N1} MB  ->  {1}" -f ($exe.Length / 1MB), $exe.FullName
"Release zip            ->  $zip"

# Installer (Inno Setup). Skipped with a note if ISCC isn't available.
$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
              "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
              "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if ($iscc) {
    & $iscc /Q "/DAppVersion=$version" "/DSourceExe=$($exe.FullName)" (Join-Path $root 'installer\Slate.iss')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $setup = Get-Item (Join-Path $root 'publish\SlateSetup.exe')
    "SlateSetup.exe {0:N1} MB ->  {1}" -f ($setup.Length / 1MB), $setup.FullName
} else {
    "Inno Setup not found: skipped the installer (winget install JRSoftware.InnoSetup)"
}
