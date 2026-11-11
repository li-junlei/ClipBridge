param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts\installer' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $repo 'src\ClipBridge.Windows\ClipBridge.Windows.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+([.-][A-Za-z0-9.-]+)?$') { throw 'Invalid project version' }
$compiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $compiler) { $compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Install Inno Setup 6 (including ChineseSimplified language) to build the Windows installer.' }
$publish = Join-Path $repo 'artifacts\windows-installer\win-x64'
New-Item -ItemType Directory -Path $publish,$OutputDirectory -Force | Out-Null
# A clean staging directory prevents old publish files from leaking into a new release.
$resolvedPublish = [IO.Path]::GetFullPath($publish)
if ($resolvedPublish -ne [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\windows-installer\win-x64'))) { throw 'Unexpected staging path' }
Get-ChildItem -LiteralPath $resolvedPublish -Force | Remove-Item -Recurse -Force
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $publish
if ($LASTEXITCODE) { throw 'Windows self-contained publish failed' }
foreach ($name in @('ClipBridge.Windows.exe','coreclr.dll','hostfxr.dll','System.Windows.Forms.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $name))) { throw "Missing installer payload: $name" }
}
$assets = Get-Content -LiteralPath (Join-Path $repo 'src\ClipBridge.Windows\obj\project.assets.json') -Raw | ConvertFrom-Json
$runtime = Get-Content -LiteralPath (Join-Path $publish 'ClipBridge.Windows.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $package = Join-Path $assets.project.restore.packagesPath ($framework.name.ToLowerInvariant() + '.runtime.win-x64\' + $framework.version)
    $notices = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -like 'LICENSE*' -or $_.Name -eq 'THIRD-PARTY-NOTICES.TXT' })
    if (-not $notices.Count) { throw "Missing runtime license: $($framework.name)" }
    $target = Join-Path $publish ('licenses\' + $framework.name)
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $notices | Copy-Item -Destination $target
}
& $compiler "/DAppVersion=$version" "/DPublishDir=$publish" "/DOutputDir=$OutputDirectory" (Join-Path $PSScriptRoot 'ClipBridge.iss')
if ($LASTEXITCODE) { throw 'Installer compilation failed' }
$result = Join-Path $OutputDirectory "ClipBridge-Setup-$version-win-x64.exe"
if (-not (Test-Path -LiteralPath $result)) { throw 'Installer output missing' }
Get-Item -LiteralPath $result | Select-Object FullName,Length
Get-FileHash -LiteralPath $result -Algorithm SHA256
