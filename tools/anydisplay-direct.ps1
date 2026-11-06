# Run with Windows PowerShell 5.1 (powershell.exe).
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$HostExe,
    [ValidateRange(0, 86400)]
    [int]$Seconds = 0,
    [switch]$VerboseHost
)

$ErrorActionPreference = 'Stop'
$HostExe = (Resolve-Path -LiteralPath $HostExe).ProviderPath
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this script with Windows PowerShell (powershell.exe) for WinRT support.'
}

function Test-DirectPeer([System.Net.IPAddress]$Address) {
    if ($Address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        return $false
    }
    $adapters = @(Get-NetAdapter -IncludeHidden | Where-Object {
        $_.Status -eq 'Up' -and $_.InterfaceDescription -like '*Wi-Fi Direct*'
    })
    $routes = @(Find-NetRoute -RemoteIPAddress $Address.ToString() -ErrorAction SilentlyContinue)
    return @($routes | Where-Object {
        $_.NextHop -eq '0.0.0.0' -and $_.InterfaceIndex -in $adapters.ifIndex
    }).Count -gt 0
}

$null = [Windows.Devices.WiFiDirect.WiFiDirectAdvertisementPublisher,Windows.Devices.WiFiDirect,ContentType=WindowsRuntime]
$publisher = [Windows.Devices.WiFiDirect.WiFiDirectAdvertisementPublisher]::new()
$advertisement = $publisher.Advertisement
$advertisement.IsAutonomousGroupOwnerEnabled = $true
$legacy = $advertisement.LegacySettings
$legacy.Ssid = 'DIRECT-AnyDisplay-' + [guid]::NewGuid().ToString('N').Substring(0, 4)
$legacy.IsEnabled = $true

try {
    $publisher.Start()
    $deadline = (Get-Date).AddSeconds(10)
    do {
        $status = $publisher.Status.ToString()
        if ($status -in @('Started', 'Aborted')) { break }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    if ($status -ne 'Started') { throw "Could not start Wi-Fi Direct: $status. Check that Windows Mobile Hotspot and wireless casting are off." }

    Write-Host "Tablet Wi-Fi network: $($legacy.Ssid)"
    Write-Host "Temporary password: $($legacy.Passphrase.Password.TrimEnd([char]0))"
    Write-Host 'Join that network on the tablet, stay connected if Android reports no internet, and open AnyDisplay.'
    $Peer = Read-Host 'IPv4 address shown by AnyDisplay (without the port)'
    $address = $null
    if (![System.Net.IPAddress]::TryParse($Peer, [ref]$address) -or !(Test-DirectPeer $address)) {
        throw 'The tablet address is not reachable through a Wi-Fi Direct adapter. Check that it has joined the test network.'
    }

    $hostArgs = @('connect', ('{0}:47820' -f $address))
    if ($Seconds) { $hostArgs += @('--seconds', $Seconds) }
    if ($VerboseHost) { $hostArgs += '-v' }
    & $HostExe @hostArgs
    if ($LASTEXITCODE -ne 0) { throw "AnyDisplay exited with code $LASTEXITCODE." }
} finally {
    if ($publisher.Status.ToString() -eq 'Started') { $publisher.Stop() }
}
