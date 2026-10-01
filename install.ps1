# Installs (or updates) Slate from the latest GitHub release.
#   irm https://raw.githubusercontent.com/kazuna1/slate/main/install.ps1 | iex
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is far faster without the progress bar
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $url = 'https://github.com/kazuna1/slate/releases/latest/download/SlateSetup.exe'
    $setup = Join-Path $env:TEMP 'SlateSetup.exe'

    Write-Host 'Downloading Slate...' -ForegroundColor Magenta
    Invoke-WebRequest $url -OutFile $setup -UseBasicParsing

    Write-Host 'Installing...' -ForegroundColor Magenta
    $p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=autostart' -Wait -PassThru
    Remove-Item $setup -ErrorAction SilentlyContinue
    if ($p.ExitCode -ne 0) { throw "Slate setup failed (exit code $($p.ExitCode))." }

    Start-Process (Join-Path $env:LOCALAPPDATA 'Programs\Slate\Slate.exe')
    Write-Host 'Slate is installed and running. Press Win + Space.' -ForegroundColor Green
}
