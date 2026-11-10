[CmdletBinding()]
param([string]$Archive)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$cache = Join-Path $root '.filament-sdk'
$sdk = Join-Path $cache 'sdk'
$out = Join-Path $root 'FilamentNative'
[IO.Directory]::CreateDirectory($cache) | Out-Null
[IO.Directory]::CreateDirectory($out) | Out-Null
$defaultArchive = !$Archive
if ($defaultArchive) { $Archive = Join-Path $cache 'sdk.tgz' }
if ($defaultArchive -and (!(Test-Path -LiteralPath $Archive) -or (Get-Item -LiteralPath $Archive).Length -lt 867739530)) {
    $url = 'https://github.com/google/filament/releases/download/v1.80.0/filament-v1.80.0-windows.tgz'
    curl.exe -fL --retry 2 --connect-timeout 15 -C - -o $Archive $url
    if ($LASTEXITCODE -ne 0) { throw 'Filament SDK download failed. Use -Archive with the official Windows tarball.' }
}
if (!(Test-Path -LiteralPath $Archive)) { throw 'Filament SDK archive not found.' }
$hash = 'D464617FD70E756D75A86893388D4438F0023826FF989F4DE03CE1BD9BC5798F'
if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $hash) {
    throw "Unexpected SHA-256 for $Archive"
}
if (!(Test-Path -LiteralPath (Join-Path $sdk '.extracted'))) {
    [IO.Directory]::CreateDirectory($sdk) | Out-Null
    tar.exe -xzf $Archive -C $sdk include lib/x86_64/mt bin/matc.exe LICENSE
    if ($LASTEXITCODE -ne 0) { throw 'Filament SDK extraction failed.' }
    New-Item -ItemType File -Path (Join-Path $sdk '.extracted') -Force | Out-Null
}

$vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ x64 build tools are required.' }
& (Join-Path $vs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
$lib = Join-Path $sdk 'lib/x86_64/mt'
$libs = Get-ChildItem -LiteralPath $lib -File -Filter '*.lib' | Select-Object -ExpandProperty FullName
$compiler = Get-Command clang-cl.exe -ErrorAction SilentlyContinue
if (!$compiler) { $compiler = Get-Command cl.exe -ErrorAction Stop }
$work = Join-Path $cache ("build-" + [guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($work) | Out-Null
$clArgs = @('/nologo','/std:c++20','/O2','/MT','/EHsc','/LD', "/I$sdk/include",
    "/Fo$work/Bridge.obj", "/Fe$work/MinecraftCS.Filament.dll", (Join-Path $PSScriptRoot 'Bridge.cpp'),
    '/link', "/IMPLIB:$work/Bridge.lib") + $libs +
    @('user32.lib','gdi32.lib','opengl32.lib','shlwapi.lib','shell32.lib','advapi32.lib','ole32.lib','ws2_32.lib','winmm.lib','psapi.lib')
& $compiler.Source @clArgs
if ($LASTEXITCODE -ne 0) { throw 'Filament bridge compilation failed.' }

& (Join-Path $sdk 'bin/matc.exe') -p desktop -a vulkan -o (Join-Path $work 'terrain.filamat') (Join-Path $PSScriptRoot 'terrain.mat')
if ($LASTEXITCODE -ne 0) { throw 'Filament material compilation failed.' }
Copy-Item -LiteralPath (Join-Path $work 'MinecraftCS.Filament.dll'),(Join-Path $work 'terrain.filamat') -Destination $out -Force
Copy-Item -LiteralPath (Join-Path $sdk 'LICENSE') -Destination (Join-Path $out 'LICENSE.txt')
