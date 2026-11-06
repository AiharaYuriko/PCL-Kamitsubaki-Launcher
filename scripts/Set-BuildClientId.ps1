param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot '../Plain Craft Launcher 2/PCL.Online.json')
)

$ErrorActionPreference = 'Stop'
$clientId = $env:PCL_MS_CLIENT_ID
$parsedId = [Guid]::Empty
if ([string]::IsNullOrWhiteSpace($clientId) -or
    -not [Guid]::TryParse($clientId, [ref]$parsedId) -or
    $parsedId -eq [Guid]::Empty) {
    throw 'Configure the CLIENT_ID repository secret with a valid Microsoft application ID before building.'
}

# Read the value through the process environment, never interpolate it into shell code.
# This resource is embedded in the EXE by the VB project; no source commit is made.
$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$config.MicrosoftClientId = $parsedId.ToString()
$json = $config | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText(
    (Resolve-Path -LiteralPath $ConfigPath).Path,
    $json + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false)
)
Write-Host 'Microsoft Client ID injected into the embedded build configuration.'
