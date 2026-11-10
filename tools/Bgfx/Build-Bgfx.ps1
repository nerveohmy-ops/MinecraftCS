$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$cache = Join-Path $root '.bgfx-sdk'
$sdk = Join-Path $cache 'bgfx.cmake'
$archive = Join-Path $cache 'windows.zip'
$out = Join-Path $root 'BgfxNative'
$hash = '0C93FC8CAD4E7EB058017A424BD7F73C086B21F0301CC792419BCA75BF47EF2C'
[IO.Directory]::CreateDirectory($cache) | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    $version = 'v1.167.9621-609'
    Invoke-WebRequest "https://github.com/bkaradzic/bgfx.cmake/releases/download/$version/bgfx.cmake.binary.windows.$version.zip" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $hash) {
    throw "Unexpected SHA-256 for $archive"
}
if (!(Test-Path -LiteralPath $sdk)) {
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $cache)
}
[IO.Directory]::CreateDirectory($out) | Out-Null

$vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ x64 build tools are required.' }
& (Join-Path $vs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
$clArgs = @('/nologo','/std:c++17','/O2','/MD','/EHsc','/LD', "/I$sdk/include",
    "/Fo$cache/Bridge.obj", "/Fe$out/MinecraftCS.Bgfx.dll", (Join-Path $PSScriptRoot 'Bridge.cpp'),
    '/link', "/IMPLIB:$cache/Bridge.lib", "/LIBPATH:$sdk/lib",
    'bgfx.lib','bimg.lib','bx.lib','psapi.lib','user32.lib','gdi32.lib','ole32.lib','dxgi.lib')
& cl.exe @clArgs
if ($LASTEXITCODE -ne 0) { throw 'bgfx bridge compilation failed.' }

foreach ($stage in @(@('vs','vertex'), @('fs','fragment'))) {
    $shaderArgs = @('-f', (Join-Path $PSScriptRoot "terrain.$($stage[0]).sc"), '-o',
        (Join-Path $out "terrain.$($stage[0]).bin"), '--type', $stage[1], '--platform', 'windows',
        '-p', 's_5_0', '--varyingdef', (Join-Path $PSScriptRoot 'varying.def.sc'), '-i', (Join-Path $sdk 'include/bgfx'))
    & (Join-Path $sdk 'bin/shaderc.exe') @shaderArgs
    if ($LASTEXITCODE -ne 0) { throw "bgfx $($stage[1]) shader compilation failed." }
}
Copy-Item -LiteralPath (Join-Path $sdk 'share/licences/bgfx/LICENSE') -Destination (Join-Path $out 'LICENSE.txt')
