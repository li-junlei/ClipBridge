param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$native = Join-Path $repo 'src/clipbridge-windows-rust'
$manifest = Join-Path $native 'Cargo.toml'
$version = [regex]::Match([IO.File]::ReadAllText($manifest), '(?m)^version = "(\d+\.\d+\.\d+)"').Groups[1].Value
if (-not $version) { throw 'Rust version missing' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/installer' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$publish = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/windows-rust/win-x64'))
if ($publish -ne [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/windows-rust/win-x64'))) { throw 'Unexpected staging directory' }
New-Item -ItemType Directory -Path $publish,$OutputDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $publish -Force | Remove-Item -Recurse -Force
$oldFlags = $env:RUSTFLAGS
$oldEncoded = $env:CARGO_ENCODED_RUSTFLAGS
try {
    $flags = @('-C','target-feature=+crt-static',"--remap-path-prefix=$repo=.","--remap-path-prefix=$env:USERPROFILE\.cargo=/cargo", "--remap-path-prefix=$env:USERPROFILE\.rustup=/rust")
    if ($oldEncoded) { $flags = @($oldEncoded.Split([char]31)) + $flags }
    elseif ($oldFlags) { $flags = @($oldFlags.Split(' ',[StringSplitOptions]::RemoveEmptyEntries)) + $flags }
    $env:CARGO_ENCODED_RUSTFLAGS = $flags -join [char]31
    & cargo build --manifest-path $manifest --locked --release --bin clipbridge -j 4
    if ($LASTEXITCODE) { throw 'Rust GPUI build failed' }
} finally { $env:RUSTFLAGS = $oldFlags; $env:CARGO_ENCODED_RUSTFLAGS = $oldEncoded }
Copy-Item -LiteralPath (Join-Path $native 'target/release/clipbridge.exe') -Destination (Join-Path $publish 'ClipBridge.Windows.exe')
& python (Join-Path $PSScriptRoot 'redact-native-paths.py') (Join-Path $publish 'ClipBridge.Windows.exe') (Join-Path $env:USERPROFILE '.cargo') $repo
if ($LASTEXITCODE) { throw 'Native diagnostic sanitization failed' }
& python (Join-Path $PSScriptRoot 'rust-licenses.py') $manifest (Join-Path $publish 'licenses')
if ($LASTEXITCODE) { throw 'Rust license collection failed' }
$compiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $compiler) { $compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
& $compiler "/DAppVersion=$version" "/DPublishDir=$publish" "/DOutputDir=$OutputDirectory" (Join-Path $PSScriptRoot 'ClipBridge.iss')
if ($LASTEXITCODE) { throw 'Installer compilation failed' }
Get-Item -LiteralPath (Join-Path $OutputDirectory "ClipBridge-Setup-$version-win-x64.exe") | Select-Object FullName,Length
