param([switch]$Android)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build src/ClipBridge.Relay -c Release
    if ($LASTEXITCODE) { throw 'Relay build failed' }
    dotnet publish src/ClipBridge.Tool -c Release -o artifacts/pairing
    if ($LASTEXITCODE) { throw 'Pairing tool build failed' }
    dotnet publish src/ClipBridge.Windows -c Release -o artifacts/windows
    if ($LASTEXITCODE) { throw 'Windows build failed' }
    & (Join-Path $PSScriptRoot 'installer/build.ps1')
    if (-not $?) { throw 'Windows installer build failed' }
    dotnet run --project tests/ClipBridge.Tests
    if ($LASTEXITCODE) { throw 'Protocol checks failed' }
    python tests/group_flow.py
    if ($LASTEXITCODE) { throw 'Group pairing checks failed' }
    python tests/integration.py
    if ($LASTEXITCODE) { throw 'Integration checks failed' }
    python tests/attachment_flow.py
    if ($LASTEXITCODE) { throw 'Attachment checks failed' }
    python tests/relay_attachment_flow.py
    if ($LASTEXITCODE) { throw 'Relay attachment checks failed' }
    python tests/webdav_flow.py
    if ($LASTEXITCODE) { throw 'WebDAV checks failed' }
    if ($Android) {
        Push-Location android
        try {
            .\gradlew.bat --no-daemon assembleDebug lintDebug
            if ($LASTEXITCODE) { throw 'Android build/check failed' }
        } finally { Pop-Location }
    }
} finally { Pop-Location }
