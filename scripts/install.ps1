#Requires -Version 5
<#
.SYNOPSIS
  fedit installer for Windows.
.DESCRIPTION
  Downloads the matching release archive, verifies its checksum, extracts the
  self-contained binary together with its Fedit.PluginApi.dll sidecar and
  tree-sitter runtimes, and puts the install directory on your user PATH.
.EXAMPLE
  irm https://fedit.dev/install.ps1 | iex
.NOTES
  Environment overrides:
    FEDIT_VERSION  version to install, e.g. 1.3.0 (default: latest)
    FEDIT_DIR      install directory (default: %LOCALAPPDATA%\Programs\fedit)
#>
$ErrorActionPreference = 'Stop'

$repo = 'HelgeSverre/fedit'
$version = if ($env:FEDIT_VERSION) { $env:FEDIT_VERSION } else { 'latest' }
$onWindows = ($env:OS -eq 'Windows_NT')

$dir =
  if ($env:FEDIT_DIR) { $env:FEDIT_DIR }
  elseif ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs\fedit' }
  else { Join-Path $HOME '.fedit' }

# Only an x64 Windows build ships; it runs natively on x64 and under
# emulation on arm64 Windows.
$triple = 'x86_64-pc-windows-msvc'
$asset = "fedit-$triple.zip"
$base =
  if ($version -eq 'latest') { "https://github.com/$repo/releases/latest/download" }
  else { "https://github.com/$repo/releases/download/v$($version.TrimStart('v'))" }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("fedit-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
  $zip = Join-Path $tmp $asset
  Write-Host "fedit install: downloading $asset ($version)"
  Invoke-WebRequest -Uri "$base/$asset" -OutFile $zip -UseBasicParsing

  try {
    $sumFile = Join-Path $tmp 'sum'
    Invoke-WebRequest -Uri "$base/fedit-$triple.sha256" -OutFile $sumFile -UseBasicParsing
    $want = (Get-Content $sumFile -Raw).Trim().ToLower()
    $got = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLower()
    if ($want -and $got -ne $want) { throw "checksum mismatch (expected $want, got $got)" }
    Write-Host "fedit install: checksum verified"
  } catch {
    if ("$_" -like '*checksum mismatch*') { throw }
    Write-Host "fedit install: checksum step skipped ($_)"
  }

  Write-Host "fedit install: extracting to $dir"
  if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
  New-Item -ItemType Directory -Path $dir | Out-Null
  Expand-Archive -Path $zip -DestinationPath $dir -Force
  if (-not (Test-Path (Join-Path $dir 'fedit.exe'))) { throw 'archive did not contain fedit.exe' }

  # Add the install dir to the user PATH so fedit.exe resolves its sidecars
  # (they live alongside it). Windows-only; harmless to skip elsewhere.
  if ($onWindows) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (($userPath -split ';') -notcontains $dir) {
      [Environment]::SetEnvironmentVariable('Path', "$userPath;$dir", 'User')
      $env:Path = "$env:Path;$dir"
      Write-Host "fedit install: added $dir to your user PATH (open a new terminal to pick it up)"
    }
    try { Write-Host ("fedit install: installed " + (& (Join-Path $dir 'fedit.exe') --version)) } catch {}
  }

  Write-Host "Done. Launch with: fedit ."
} finally {
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
