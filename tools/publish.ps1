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
