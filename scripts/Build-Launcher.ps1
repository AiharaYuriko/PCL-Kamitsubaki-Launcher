param(
    [ValidateSet('Release', 'Debug', 'Snapshot')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$configPath = Join-Path $repoRoot 'Plain Craft Launcher 2/PCL.Online.json'
$originalConfig = [IO.File]::ReadAllBytes($configPath)
$previousSdkPath = $env:MSBuildSDKsPath
$previousWorkloadResolver = $env:MSBuildEnableWorkloadResolver
Push-Location $repoRoot
try {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with MSBuild and WPF build support.' }
    $installations = & $vswhere -all -products '*' -format json | ConvertFrom-Json
    $msbuild = $installations | ForEach-Object {
        Join-Path $_.installationPath 'MSBuild/Current/Bin/amd64/MSBuild.exe'
    } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $msbuild) { throw 'Full-framework MSBuild.exe was not found. dotnet build cannot compile this legacy WPF project.' }

    $sdks = & dotnet --list-sdks
    if ($LASTEXITCODE -ne 0) { throw 'Unable to list .NET SDKs.' }
    $sdk = $sdks | ForEach-Object {
        if ($_ -match '^(\d+\.\d+\.\d+)\s+\[(.+)\]$') {
            [PSCustomObject]@{ Version = [Version]$Matches[1]; Root = $Matches[2] }
        }
    } | Where-Object { $_.Version.Major -ge 9 } | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $sdk) { throw 'Install .NET SDK 9 or newer for the C# 13 projects.' }
    $env:MSBuildSDKsPath = Join-Path (Join-Path $sdk.Root $sdk.Version.ToString()) 'Sdks'
    # No mobile or other workload is used by this .NET Framework application.
    $env:MSBuildEnableWorkloadResolver = 'false'

    if (-not (Test-Path 'MeloongCore/Shared/MeloongCore.csproj')) {
        & git submodule update --init --recursive
        if ($LASTEXITCODE -ne 0) { throw 'Submodule initialization failed.' }
    }
    & dotnet restore 'PCLCS/PCLCS.csproj' --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }

    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $referencePackage = Join-Path $nugetRoot 'microsoft.netframework.referenceassemblies.net48'
    $referenceRoot = Get-ChildItem -LiteralPath $referencePackage -Directory | Sort-Object { [Version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'build' } |
        Where-Object { Test-Path (Join-Path $_ '.NETFramework/v4.8/mscorlib.dll') } | Select-Object -First 1
    if (-not $referenceRoot) { throw '.NET Framework 4.8 reference assemblies were not restored.' }

    & "$PSScriptRoot/Set-BuildClientId.ps1" -ConfigPath $configPath
    & $msbuild 'Plain Craft Launcher 2/Plain Craft Launcher 2.vbproj' /t:Rebuild "/p:Configuration=$Configuration" "/p:TargetFrameworkRootPath=$referenceRoot\" /m:1 /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }

    $output = Join-Path $repoRoot "artifacts/$Configuration"
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $exe = Join-Path $output 'PCL-Kamitsubaki.exe'
    Copy-Item -LiteralPath 'Plain Craft Launcher 2/bin/Plain Craft Launcher 2.exe' -Destination $exe -Force
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  PCL-Kamitsubaki.exe`n")
    Write-Host "Built: $exe"
    Write-Host "SHA256: $hash"
} finally {
    # Never leave the injected ID in tracked source, including after failed builds.
    [IO.File]::WriteAllBytes($configPath, $originalConfig)
    $env:MSBuildSDKsPath = $previousSdkPath
    $env:MSBuildEnableWorkloadResolver = $previousWorkloadResolver
    Pop-Location
}
