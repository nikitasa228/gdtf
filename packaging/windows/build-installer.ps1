<#
.SYNOPSIS
  Builds the Windows installer of Candela on Windows: Candela-Setup-<version>-win-x64.exe in artifacts\windows\.

.DESCRIPTION
  The same steps as build-installer.sh: publish the editor (candela-app) and the command-line tool (candela) for
  win-x64, self-contained, into one folder (no trimming; ReadyToRun); complete the installed layout and check the
  native dependencies (packaging\windows\winpack.py); download Microsoft's Visual C++ 2015-2022 x64 redistributable
  into a new temporary folder and check its Authenticode signature (valid, signed by Microsoft Corporation); run
  makensis.

  Needs: the .NET 8 SDK, Python 3 (winget install Python.Python.3.12) and NSIS 3 (winget install NSIS.NSIS, or
  choco install nsis). The version comes from Directory.Build.props.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\windows\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string] $Out,
    [switch] $NoReadyToRun,
    [string] $Redist,
    [switch] $NoRedist
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $here '..\..')).Path
if (-not $Out) { $Out = Join-Path $root 'artifacts\windows' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path

function Find-Tool([string[]] $names, [string[]] $extraPaths, [string] $hint) {
    foreach ($name in $names) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    foreach ($path in $extraPaths) {
        if ($path -and (Test-Path $path)) { return $path }
    }
    throw "Not found: $($names -join ' / '). $hint"
}

$dotnet = Find-Tool @('dotnet') @() 'Install the .NET 8 SDK.'
$python = Find-Tool @('py', 'python', 'python3') @() 'Install Python 3: winget install Python.Python.3.12'
$pythonArgs = @()
if ((Split-Path -Leaf $python) -like 'py*.exe' -and (Split-Path -Leaf $python) -notlike 'python*') { $pythonArgs = @('-3') }
$makensis = Find-Tool @('makensis') @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") `
    'Install NSIS 3: winget install NSIS.NSIS (or choco install nsis).'

function Invoke-Checked([string] $exe, [string[]] $arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

function Get-MsBuildProperty([string] $project, [string] $name) {
    $value = & $dotnet msbuild $project -nologo "-getProperty:$name" | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw "Reading $name from $project failed." }
    return $value.Trim()
}

$app = Join-Path $root 'src\Candela.App\Candela.App.csproj'
$cli = Join-Path $root 'src\Candela.Cli\Candela.Cli.csproj'
$version = Get-MsBuildProperty $app 'Version'
$fileVersion = Get-MsBuildProperty $app 'FileVersion'
$setup = Join-Path $Out "Candela-Setup-$version-win-x64.exe"
Write-Host "== Candela $version ($fileVersion) for win-x64 -> $setup"

$publish = Join-Path $Out 'publish'
$generated = Join-Path $Out 'generated'
foreach ($dir in @($publish, $generated)) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Path $dir | Out-Null
}

$readyToRun = if ($NoReadyToRun) { 'false' } else { 'true' }
Write-Host "== Publishing the editor and the command-line tool (self-contained, ReadyToRun: $readyToRun)"
foreach ($project in @($app, $cli)) {
    Invoke-Checked $dotnet @('publish', $project, '-nologo', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        "-p:PublishReadyToRun=$readyToRun", '-p:PublishTrimmed=false', '-o', $publish)
}

$winpack = Join-Path $here 'winpack.py'
Write-Host '== Completing the installed layout'
Invoke-Checked $python ($pythonArgs + @('-I', $winpack, 'stage', $root, $publish, $generated))
Write-Host '== Checking the native dependencies'
Invoke-Checked $python ($pythonArgs + @('-I', $winpack, 'check', $publish))

$nsisRedist = @()
if (-not $NoRedist) {
    if (-not $Redist) {
        # A new, empty folder for the download.
        $download = Join-Path ([System.IO.Path]::GetTempPath()) ("candela-vcredist-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $download | Out-Null
        $Redist = Join-Path $download 'vc_redist.x64.exe'
        Write-Host "== Downloading the Visual C++ redistributable into $download"
        Invoke-WebRequest -UseBasicParsing -Uri 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $Redist
    }
    Write-Host "== Verifying $Redist"
    $size = (Get-Item $Redist).Length
    if ($size -lt 10MB -or $size -gt 80MB) { throw "$Redist has an unexpected size ($size bytes)." }
    $signature = Get-AuthenticodeSignature -FilePath $Redist
    if ($signature.Status -ne 'Valid') { throw "The Authenticode signature of $Redist is $($signature.Status)." }
    if ($signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation') {
        throw "$Redist is signed by '$($signature.SignerCertificate.Subject)', not Microsoft Corporation."
    }
    $redistVersion = (Get-Item $Redist).VersionInfo.FileVersion
    $redistBuild = $redistVersion.Split('.')[2]
    Write-Host "   signed by Microsoft Corporation, version $redistVersion, $size bytes"
    $nsisRedist = @("-DREDIST=$Redist", "-DREDIST_BUILD=$redistBuild")
}

Write-Host '== Building the installer'
Invoke-Checked $makensis (@('/NOCD', '/INPUTCHARSET', 'UTF8', '/V2', "/DVERSION=$version", "/DFILE_VERSION=$fileVersion",
    "/DSTAGE=$publish", "/DGENERATED=$generated") + $nsisRedist + @("/DOUTFILE=$setup", (Join-Path $here 'installer.nsi')))

$hash = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $setup)" | Set-Content -Encoding ascii "$setup.sha256"
Write-Host '== Done'
Get-Item $setup | Format-Table Name, Length
Write-Host "SHA-256: $hash"
