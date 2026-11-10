[CmdletBinding()]
param([Parameter(Mandatory)][string]$IrisJar)

$ErrorActionPreference = 'Stop'
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $IrisJar).Path)
try {
    $manifest = $archive.GetEntry('fabric.mod.json')
    if ($null -eq $manifest) { throw 'The archive has no Fabric manifest.' }
    $reader = [IO.StreamReader]::new($manifest.Open())
    try { $mod = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }
    if ($mod.id -ne 'iris') { throw 'Choose the Iris Fabric JAR.' }

    $entries = @(
        'antlr4-runtime-4.13.1.jar'
        'glsl-transformer-3.0.0-pre3.jar'
        'jcpp-1.4.14.jar'
    ) | ForEach-Object {
        $entry = $archive.GetEntry("META-INF/jars/$_")
        if ($null -eq $entry) { throw "The Iris archive does not contain $_." }
        $entry
    }
    $destination = Join-Path $PSScriptRoot 'lib'
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    foreach ($entry in $entries) {
        $file = Join-Path $destination $entry.Name
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $file, $true)
        Get-FileHash -LiteralPath $file -Algorithm SHA256
    }
}
finally { $archive.Dispose() }
