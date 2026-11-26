# Installs the topwater command on Windows: https://github.com/gr-ant/Topwater
#
#   irm https://gr-ant.github.io/Topwater/install.ps1 | iex
#
# Settings (environment variables):
#   TOPWATER_VERSION       a release tag such as v0.1.0-preview.3 (default: the newest release, previews included)
#   TOPWATER_INSTALL_DIR   where topwater.exe goes (default: %LOCALAPPDATA%\Programs\Topwater, added to your PATH)
#   TOPWATER_DOWNLOAD_URL  where the release files are (default: the GitHub release for the version)
#
# Works in Windows PowerShell 5.1 and PowerShell 7. After it runs, topwater works in PowerShell, CMD and Windows Terminal.
# Uninstall: delete the folder and remove it from your user PATH.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # the progress bar makes downloads far slower in Windows PowerShell
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = 'gr-ant/Topwater'
$dir = if ($env:TOPWATER_INSTALL_DIR) { $env:TOPWATER_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\Topwater' }

$arch = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
    'X64' { 'x64' }
    'Arm64' { 'arm64' }
    default { throw "topwater install: no build for a $_ processor" }
}

$tag = $env:TOPWATER_VERSION
$base = $env:TOPWATER_DOWNLOAD_URL
if (-not $base) {
    if (-not $tag) {
        $releases = Invoke-RestMethod "https://api.github.com/repos/$repo/releases?per_page=1" -UseBasicParsing
        $tag = @($releases)[0].tag_name
        if (-not $tag) { throw "topwater install: could not find a release of $repo" }
    }
    $base = "https://github.com/$repo/releases/download/$tag"
}

$name = "topwater-win-$arch.zip"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("topwater-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Invoke-WebRequest "$base/$name" -OutFile (Join-Path $tmp $name) -UseBasicParsing
    Invoke-WebRequest "$base/SHA256SUMS" -OutFile (Join-Path $tmp 'SHA256SUMS') -UseBasicParsing

    $expected = Get-Content (Join-Path $tmp 'SHA256SUMS') |
        ForEach-Object { $f = $_ -split '\s+\*?'; if ($f[1] -eq $name) { $f[0] } } | Select-Object -First 1
    $actual = (Get-FileHash (Join-Path $tmp $name) -Algorithm SHA256).Hash
    if (-not $expected -or $expected -ne $actual) { throw "topwater install: checksum of $name does not match SHA256SUMS" }

    Expand-Archive (Join-Path $tmp $name) -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item (Join-Path $tmp 'topwater.exe') (Join-Path $dir 'topwater.exe') -Force
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not (($userPath -split ';') -contains $dir)) {
    [Environment]::SetEnvironmentVariable('Path', (@($userPath, $dir) | Where-Object { $_ }) -join ';', 'User')
    $env:Path = "$env:Path;$dir"
    Write-Host "Added $dir to your user PATH (open a new terminal for CMD and other windows to see it)."
}
Write-Host "Installed topwater $tag to $dir\topwater.exe. Run: topwater --help"
