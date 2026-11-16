[CmdletBinding()]
param(
    [string]$AppPath,
    [string]$SessionDirectory,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$BrainmindRoot = Split-Path -Parent $PSScriptRoot

function Read-BrainmindJson([string]$Path) {
    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Get-BrainmindHash([string]$Path) {
    $Stream = [IO.File]::OpenRead($Path)
    $Algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($Algorithm.ComputeHash($Stream)).Replace('-', '').ToLowerInvariant() }
    finally { $Algorithm.Dispose(); $Stream.Dispose() }
}

function Find-BrainmindApp([string]$ExplicitPath) {
    $Candidates = New-Object 'System.Collections.Generic.List[string]'
    if ($ExplicitPath) {
        $Expanded = [Environment]::ExpandEnvironmentVariables($ExplicitPath)
        if (Test-Path -LiteralPath $Expanded -PathType Container) { $Expanded = Join-Path $Expanded 'TheBrain.exe' }
        $Candidates.Add($Expanded)
    }
    else {
        foreach ($Association in @('brain', 'TheBrain.Document', 'TheBrain.Theme')) {
            $AssociationKey = [Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey(($Association + '\shell\open\command'))
            if ($null -ne $AssociationKey) {
                try {
                    $Command = [string]$AssociationKey.GetValue('')
                    if ($Command -match '^"([^"]+\.exe)"|^(.+?\.exe)(?:\s|$)') {
                        $Executable = $Matches[1]
                        if (-not $Executable) { $Executable = $Matches[2] }
                        $Candidates.Add([Environment]::ExpandEnvironmentVariables($Executable))
                    }
                }
                finally { $AssociationKey.Dispose() }
            }
        }
        foreach ($Hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
            foreach ($View in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
                $BaseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, $View)
                try {
                    $AppKey = $BaseKey.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\App Paths\TheBrain.exe')
                    if ($null -ne $AppKey) {
                        try { $Value = [string]$AppKey.GetValue(''); if ($Value) { $Candidates.Add($Value.Trim('"')) } }
                        finally { $AppKey.Dispose() }
                    }
                    $Uninstall = $BaseKey.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall')
                    if ($null -ne $Uninstall) {
                        try {
                            foreach ($KeyName in $Uninstall.GetSubKeyNames()) {
                                $Key = $Uninstall.OpenSubKey($KeyName)
                                if ($null -eq $Key) { continue }
                                try {
                                    if ([string]$Key.GetValue('DisplayName') -notlike '*TheBrain*') { continue }
                                    $Location = [string]$Key.GetValue('InstallLocation')
                                    if ($Location) { $Candidates.Add((Join-Path ([Environment]::ExpandEnvironmentVariables($Location.Trim('"'))) 'TheBrain.exe')) }
                                    $Icon = [string]$Key.GetValue('DisplayIcon')
                                    if ($Icon) {
                                        if ($Icon -match '^"([^"]+)"') { $Icon = $Matches[1] }
                                        else { $Icon = $Icon -replace ',\s*-?\d+\s*$', '' }
                                        $Candidates.Add([Environment]::ExpandEnvironmentVariables($Icon.Trim('"')))
                                    }
                                }
                                finally { $Key.Dispose() }
                            }
                        }
                        finally { $Uninstall.Dispose() }
                    }
                }
                finally { $BaseKey.Dispose() }
            }
        }
        foreach ($Variable in @('ProgramFiles', 'ProgramFiles(x86)', 'LOCALAPPDATA')) {
            $Directory = [Environment]::GetEnvironmentVariable($Variable)
            if ($Directory) {
                foreach ($Suffix in @('TheBrain\TheBrain.exe', 'TheBrain\TheBrain\TheBrain.exe', 'Programs\TheBrain\TheBrain.exe')) {
                    $Candidates.Add((Join-Path $Directory $Suffix))
                }
            }
        }
    }
    $Supported = Read-BrainmindJson (Join-Path $BrainmindRoot 'config\supported-build.json')
    $SupportedApps = New-Object 'System.Collections.Generic.List[string]'
    foreach ($Candidate in ($Candidates | Select-Object -Unique)) {
        if ([IO.Path]::GetFileName($Candidate) -ine 'TheBrain.exe') { $Candidate = Join-Path (Split-Path -Parent $Candidate) 'TheBrain.exe' }
        if (-not (Test-Path -LiteralPath $Candidate -PathType Leaf)) { continue }
        $Resolved = (Resolve-Path -LiteralPath $Candidate).ProviderPath
        $InstallDirectory = Split-Path -Parent $Resolved
        $Valid = $true
        foreach ($Entry in $Supported.files) {
            $File = Join-Path $InstallDirectory $Entry.Name
            if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { $Valid = $false; break }
            if ((Get-BrainmindHash $File) -ne $Entry.SHA256) { $Valid = $false; break }
        }
        if ($Valid -and -not $SupportedApps.Contains($Resolved)) { $SupportedApps.Add($Resolved) }
    }
    if ($SupportedApps.Count -ne 1) {
        throw 'Exactly one supported TheBrain 14.0.102.0 installation is required. Pass -AppPath with its TheBrain.exe path. The pinned file hashes must match config/supported-build.json.'
    }
    return $SupportedApps[0]
}

try {
    if (@(Get-Process -Name TheBrain -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'TheBrain is already running. Close it normally, then launch Brainmind so the startup hook can be applied. No running process was changed.'
    }
    $RuntimePath = Join-Path $BrainmindRoot 'build\0.5.0\Brainmind.Runtime.dll'
    $ManifestPath = Join-Path $BrainmindRoot 'build\0.5.0\manifest.json'
    if (-not (Test-Path -LiteralPath $RuntimePath -PathType Leaf) -or -not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw 'Brainmind runtime is not built. Run python tools/brainmind.py build --app <TheBrain.exe> from the source package, or use the built release package.'
    }
    $Manifest = Read-BrainmindJson $ManifestPath
    if ($Manifest.target_version -ne '14.0.102.0' -or $Manifest.runtime_version -ne '0.5.0' -or $Manifest.runtime_file -ne 'Brainmind.Runtime.dll') { throw 'Unsupported Brainmind build manifest.' }
    if ((Get-BrainmindHash $RuntimePath) -ne $Manifest.runtime_sha256) { throw 'Brainmind runtime hash does not match build/manifest.json. Rebuild the source package.' }
    $SupportedPath = Join-Path $BrainmindRoot 'config\supported-build.json'
    if ((Get-BrainmindHash $SupportedPath) -ne $Manifest.supported_build_sha256) { throw 'Supported-build configuration has changed. Rebuild the source package.' }
    $ResolvedApp = Find-BrainmindApp $AppPath
    $RuntimePath = (Resolve-Path -LiteralPath $RuntimePath).ProviderPath
    if (-not $SessionDirectory) { $SessionDirectory = [Environment]::GetEnvironmentVariable('BRAINMIND_SESSION_DIR', 'Process') }
    if (-not $SessionDirectory) { $SessionDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Brainmind\sessions' }
    $SessionDirectory = [IO.Path]::GetFullPath($SessionDirectory)
    [IO.Directory]::CreateDirectory($SessionDirectory) | Out-Null
    $StartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $StartInfo.FileName = $ResolvedApp
    $StartInfo.WorkingDirectory = Split-Path -Parent $ResolvedApp
    $StartInfo.UseShellExecute = $false
    $StartInfo.EnvironmentVariables['DOTNET_STARTUP_HOOKS'] = $RuntimePath
    $StartInfo.EnvironmentVariables['BRAINMIND_SESSION_DIR'] = $SessionDirectory
    if ($ConfigPath) { $StartInfo.EnvironmentVariables['BRAINMIND_CONFIG_PATH'] = [IO.Path]::GetFullPath($ConfigPath) }
    # Original application entry point and user profile; never edit profile,
    # installation files, version, persisted environment, or existing brains.
    $ApplicationProcess = [System.Diagnostics.Process]::Start($StartInfo)
    @{ success = $true; process_id = $ApplicationProcess.Id; target_version = '14.0.102.0'; session_directory = $SessionDirectory; app = $ResolvedApp } | ConvertTo-Json -Compress
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
