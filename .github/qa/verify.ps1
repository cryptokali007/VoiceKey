#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$release = 'https://github.com/cryptokali007/VoiceKey/releases/download/v1.0.0'
$zipName = 'VoiceKey-Windows-x64.zip'
$expectedZipHash = '59362ea10ee623de8e477ba402d55094cffb645531c6a3e60a71944ba6cb6ed9'
$fixtureUrl = 'https://raw.githubusercontent.com/ggml-org/whisper.cpp/v1.9.2/samples/jfk.wav'
$fixtureHash = '59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e'
$tempBase = [IO.Path]::GetTempPath()
$work = Join-Path $tempBase ('VoiceKey release QA ' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'Extracted release'
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\VoiceKey'
$installedExe = Join-Path $appDir 'VoiceKey.exe'
$appData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'VoiceKey'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'VoiceKey.lnk'

function Assert-Hash([string]$Path, [string]$Expected) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ine $Expected) { throw "SHA-256 mismatch for $Path. Actual: $actual" }
}

function Get-InstalledVoiceKey {
    Get-Process -Name 'VoiceKey' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.MainModule.FileName -ieq $installedExe } catch { $false }
    }
}

function Start-TestProcess([string]$File, [string]$Arguments, [string]$WorkDir, [switch]$Capture) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $File
    $start.Arguments = $Arguments
    $start.WorkingDirectory = $WorkDir
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $Capture.IsPresent
    $start.RedirectStandardError = $Capture.IsPresent
    return [Diagnostics.Process]::Start($start)
}

function Wait-TestProcess([Diagnostics.Process]$Process, [string]$Name, [int]$Seconds) {
    if (-not $Process.WaitForExit($Seconds * 1000)) {
        # The retained Process handle belongs to the exact executable we started.
        if (-not $Process.HasExited) {
            & (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID $Process.Id /T /F | Out-Host
        }
        throw "$Name timed out after $Seconds seconds"
    }
    $Process.WaitForExit()
}

# Never overwrite or clean up pre-existing user data, shortcuts, or installations.
foreach ($path in @($appDir, $appData, $shortcutPath, $work)) {
    if (Test-Path -LiteralPath $path) { throw "A clean Windows runner is required. Already exists: $path" }
}
$null = New-Item -ItemType Directory -Path $work

try {
    $zip = Join-Path $work $zipName
    $checksum = Join-Path $work 'SHA256SUMS.txt'
    $fixture = Join-Path $work 'President Kennedy speech.wav'
    Invoke-WebRequest "$release/$zipName" -UseBasicParsing -TimeoutSec 120 -OutFile $zip
    Invoke-WebRequest "$release/SHA256SUMS.txt" -UseBasicParsing -TimeoutSec 120 -OutFile $checksum
    $match = [regex]::Match((Get-Content -LiteralPath $checksum -Raw).Trim(),
        '^([A-Fa-f0-9]{64})\s+\*?VoiceKey-Windows-x64\.zip$')
    if (-not $match.Success -or $match.Groups[1].Value -ine $expectedZipHash) {
        throw 'The published checksum does not match the pinned VoiceKey v1.0.0 release.'
    }
    Assert-Hash $zip $expectedZipHash

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $files = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
        $required = @('Install.cmd','LICENSE','README.md','VoiceKey.exe') | Sort-Object
        if (($files -join '|') -cne ($required -join '|')) { throw "Unexpected ZIP contents: $files" }
    } finally { $archive.Dispose() }
    Expand-Archive -LiteralPath $zip -DestinationPath $payload
    Write-Host "Anonymous ZIP verified. SHA-256: $expectedZipHash"

    $installer = Start-TestProcess $env:ComSpec '/d /c Install.cmd' $payload
    try {
        Wait-TestProcess $installer 'Install.cmd' 60
        if ($installer.ExitCode -ne 0) { throw "Install.cmd exited with $($installer.ExitCode)" }
    } finally { $installer.Dispose() }
    $bundledHash = (Get-FileHash (Join-Path $payload 'VoiceKey.exe') -Algorithm SHA256).Hash
    Assert-Hash $installedExe $bundledHash
    if (-not (Test-Path -LiteralPath $shortcutPath -PathType Leaf)) { throw "Missing shortcut: $shortcutPath" }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        try {
            if ($shortcut.TargetPath -ine $installedExe) { throw "Unexpected shortcut target: $($shortcut.TargetPath)" }
        } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) }
    } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $gui = Get-InstalledVoiceKey | Where-Object {
            $_.Refresh()
            $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.MainWindowTitle -ceq 'VoiceKey'
        } | Select-Object -First 1
        if ($gui) { break }
        Start-Sleep -Milliseconds 250
    } while ($timer.Elapsed.TotalSeconds -lt 30)
    if (-not $gui) { throw 'The installer did not launch the installed VoiceKey window within 30 seconds.' }
    Write-Host "Verified shortcut, installed EXE and visible window '$($gui.MainWindowTitle)' (PID $($gui.Id))."
    $null = $gui.Handle  # Keep its process handle valid for the post-exit check.
    if (-not $gui.CloseMainWindow()) { throw 'The VoiceKey window rejected a normal close request.' }
    Wait-TestProcess $gui 'Closing the VoiceKey window' 15
    if ($gui.ExitCode -ne 0) { throw "GUI closed with code $($gui.ExitCode)" }

    Invoke-WebRequest $fixtureUrl -UseBasicParsing -TimeoutSec 120 -OutFile $fixture
    Assert-Hash $fixture $fixtureHash
    $check = Start-TestProcess $installedExe ('--self-test "' + $fixture + '"') $appDir -Capture
    try {
        $out = $check.StandardOutput.ReadToEndAsync()
        $err = $check.StandardError.ReadToEndAsync()
        Wait-TestProcess $check 'Installed executable speech test' 600
        $stdout = $out.GetAwaiter().GetResult()
        $stderr = $err.GetAwaiter().GetResult()
        if ($check.ExitCode -ne 0) { throw "Self-test exit $($check.ExitCode): $stderr $stdout" }
        if ($stdout -notmatch '\bcountry\b' -or $stdout -notmatch 'VoiceKey checks passed\.') {
            throw "Expected recognized speech and the success marker in self-test output: $stdout"
        }
        Write-Host $stdout.Trim()
    } finally { $check.Dispose() }
} finally {
    foreach ($process in @(Get-InstalledVoiceKey)) {
        if (-not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(3000)) {
                $process.Kill()
                [void]$process.WaitForExit(5000)
            }
        }
    }
    foreach ($path in @($shortcutPath, $appDir, $appData, $work)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -Recurse }
    }
}
