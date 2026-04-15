[CmdletBinding()]
param(
    [string]$AppSettingsPath = (Join-Path $PSScriptRoot "appsettings.json"),
    [string]$TemplatePath = (Join-Path $PSScriptRoot "hev-socks5-tunnel.template.yml"),
    [string]$ConfigPath = (Join-Path $PSScriptRoot "hev-socks5-tunnel.yml"),
    [string]$TunnelBinaryPath = (Join-Path $PSScriptRoot "hev-socks5-tunnel.exe"),
    [string]$TunnelInterfaceAlias = "Wintun"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public sealed class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int jobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public IntPtr Handle { get; private set; }

    public KillOnCloseJob()
    {
        Handle = CreateJobObject(IntPtr.Zero, null);
        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create Windows job object.");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(Handle, JobObjectExtendedLimitInformation, buffer, (uint)length))
                throw new InvalidOperationException("Failed to set kill-on-close on Windows job object.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void AddProcess(IntPtr processHandle)
    {
        if (!AssignProcessToJobObject(Handle, processHandle))
            throw new InvalidOperationException("Failed to assign process to Windows job object.");
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
            CloseHandle(Handle);
    }
}
"@

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Info {
    param([string]$Message)
    Write-Host "[full-tunnel] $Message"
}

function Get-ConfigValue {
    param(
        [Parameter(Mandatory)]
        [object]$Config,
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [object]$DefaultValue
    )

    $property = $Config.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        return $DefaultValue
    }

    return $property.Value
}

function Get-ArrayValue {
    param(
        [Parameter(Mandatory)]
        [object]$Config,
        [Parameter(Mandatory)]
        [string]$Name
    )

    $property = $Config.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return @()
    }

    return @($property.Value)
}

function Get-Socks5AuthenticationBlock {
    param(
        [Parameter(Mandatory)]
        [object]$Config
    )

    $authProperty = $Config.PSObject.Properties["Socks5Authentication"]
    if ($null -eq $authProperty -or $null -eq $authProperty.Value) {
        return ""
    }

    $authConfig = $authProperty.Value
    $enabled = $false
    $enabledProperty = $authConfig.PSObject.Properties["Enabled"]
    if ($null -ne $enabledProperty -and $null -ne $enabledProperty.Value) {
        $enabled = [bool]$enabledProperty.Value
    }

    if (-not $enabled) {
        return ""
    }

    $username = [string](Get-ConfigValue -Config $authConfig -Name "Username" -DefaultValue "")
    $password = [string](Get-ConfigValue -Config $authConfig -Name "Password" -DefaultValue "")

    if ([string]::IsNullOrWhiteSpace($username)) {
        throw "Socks5Authentication.Enabled is true, but Username is empty."
    }

    if ([string]::IsNullOrEmpty($password)) {
        throw "Socks5Authentication.Enabled is true, but Password is empty."
    }

    return @"
  username: '$($username.Replace("'", "''"))'
  password: '$($password.Replace("'", "''"))'
"@
}

function Get-PreferredDefaultRoute {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("IPv4", "IPv6")]
        [string]$AddressFamily
    )

    $prefix = if ($AddressFamily -eq "IPv4") { "0.0.0.0/0" } else { "::/0" }

    $routes = Get-NetRoute -AddressFamily $AddressFamily -DestinationPrefix $prefix -PolicyStore ActiveStore -ErrorAction SilentlyContinue |
        Where-Object {
            $_.State -eq "Alive" -and
            $_.InterfaceAlias -ne $TunnelInterfaceAlias -and
            $_.InterfaceAlias -notlike "Loopback*"
        }

    $candidate = $routes |
        ForEach-Object {
            $ipInterface = Get-NetIPInterface -AddressFamily $AddressFamily -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue |
                Select-Object -First 1
            $interfaceMetric = 0
            if ($null -ne $ipInterface) {
                $interfaceMetric = [int]$ipInterface.InterfaceMetric
            }
            [pscustomobject]@{
                Route = $_
                TotalMetric = [int]$_.RouteMetric + $interfaceMetric
            }
        } |
        Sort-Object TotalMetric, @{ Expression = { $_.Route.RouteMetric } }, @{ Expression = { $_.Route.InterfaceIndex } } |
        Select-Object -First 1

    if ($null -eq $candidate) {
        return $null
    }

    return $candidate.Route
}

function Resolve-UpstreamAddresses {
    param(
        [string[]]$Hosts = @()
    )

    $resolved = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($upstreamHost in $Hosts) {
        if ([string]::IsNullOrWhiteSpace($upstreamHost)) {
            continue
        }

        $trimmedHost = $upstreamHost.Trim()
        $parsedAddress = $null
        if ([System.Net.IPAddress]::TryParse($trimmedHost, [ref]$parsedAddress)) {
            [void]$resolved.Add($parsedAddress.IPAddressToString)
            continue
        }

        try {
            foreach ($address in [System.Net.Dns]::GetHostAddresses($trimmedHost)) {
                [void]$resolved.Add($address.IPAddressToString)
            }
        }
        catch {
            throw "Failed to resolve upstream host '$trimmedHost': $($_.Exception.Message)"
        }
    }

    $addresses = New-Object 'System.Collections.Generic.List[string]'
    foreach ($address in $resolved) {
        $addresses.Add($address) | Out-Null
    }

    return ,$addresses.ToArray()
}

function Wait-TunnelInterface {
    param(
        [Parameter(Mandatory)]
        [string]$Alias,
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,
        [int]$TimeoutSeconds = 20
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($Process.HasExited) {
            throw "hev-socks5-tunnel exited before interface '$Alias' appeared. Exit code: $($Process.ExitCode)."
        }

        $ipInterface = Get-NetIPInterface -InterfaceAlias $Alias -ErrorAction SilentlyContinue |
            Sort-Object AddressFamily |
            Select-Object -First 1
        if ($null -ne $ipInterface) {
            return $ipInterface.InterfaceIndex
        }

        Start-Sleep -Milliseconds 500
        $Process.Refresh()
    }

    throw "Timed out waiting for tunnel interface '$Alias'."
}

function Add-TrackedRoute {
    param(
        [System.Collections.Generic.List[object]]$RouteStore,
        [Parameter(Mandatory)]
        [string]$DestinationPrefix,
        [Parameter(Mandatory)]
        [uint32]$InterfaceIndex,
        [Parameter(Mandatory)]
        [string]$NextHop,
        [int]$RouteMetric = 3
    )

    if ($null -eq $RouteStore) {
        throw "RouteStore is required."
    }

    $route = Get-NetRoute `
        -PolicyStore ActiveStore `
        -DestinationPrefix $DestinationPrefix `
        -InterfaceIndex $InterfaceIndex `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.NextHop -eq $NextHop } |
        Select-Object -First 1

    if ($null -eq $route) {
        $route = New-NetRoute `
            -PolicyStore ActiveStore `
            -DestinationPrefix $DestinationPrefix `
            -InterfaceIndex $InterfaceIndex `
            -NextHop $NextHop `
            -RouteMetric $RouteMetric `
            -ErrorAction Stop
    }

    $RouteStore.Add([pscustomobject]@{
        DestinationPrefix = $route.DestinationPrefix
        InterfaceIndex = [uint32]$route.InterfaceIndex
        NextHop = $route.NextHop
        AddressFamily = $route.AddressFamily
    }) | Out-Null
}

function Test-RouteAlreadyAbsent {
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    $message = [string]$ErrorRecord.Exception.Message
    if ($message -like "*No matching MSFT_NetRoute objects found*") {
        return $true
    }

    if ($message -like "*не удалось обнаружить соответствующие объекты*") {
        return $true
    }

    if ($ErrorRecord.FullyQualifiedErrorId -like "*ObjectNotFound*") {
        return $true
    }

    return $false
}

if (-not (Test-Administrator)) {
    throw "Administrator privileges are required to configure Windows routes for Wintun."
}

if (-not (Test-Path -LiteralPath $AppSettingsPath)) {
    throw "appsettings.json not found: $AppSettingsPath"
}

if (-not (Test-Path -LiteralPath $TemplatePath)) {
    throw "hev-socks5-tunnel template not found: $TemplatePath"
}

if (-not (Test-Path -LiteralPath $TunnelBinaryPath)) {
    throw "hev-socks5-tunnel executable not found: $TunnelBinaryPath"
}

$config = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json
$listenPort = [int](Get-ConfigValue -Config $config -Name "ListenPort" -DefaultValue 1080)
$listenAddress = [string](Get-ConfigValue -Config $config -Name "ListenAddress" -DefaultValue "127.0.0.1")
$remoteHost = [string](Get-ConfigValue -Config $config -Name "RemoteHost" -DefaultValue "")
$remoteServers = Get-ArrayValue -Config $config -Name "RemoteServers"

$upstreamHosts = New-Object 'System.Collections.Generic.List[string]'
foreach ($server in $remoteServers) {
    $hostProperty = $server.PSObject.Properties["Host"]
    if ($null -ne $hostProperty -and -not [string]::IsNullOrWhiteSpace([string]$hostProperty.Value)) {
        $upstreamHosts.Add([string]$hostProperty.Value)
    }
}

if ($upstreamHosts.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($remoteHost)) {
    $upstreamHosts.Add($remoteHost)
}

$template = Get-Content -LiteralPath $TemplatePath -Raw
$generatedConfig = $template.Replace("__SOCKS_ADDRESS__", $listenAddress)
$generatedConfig = $generatedConfig.Replace(
    "__SOCKS_PORT__",
    $listenPort.ToString([System.Globalization.CultureInfo]::InvariantCulture))
$generatedConfig = $generatedConfig.Replace("__SOCKS5_AUTH_BLOCK__", (Get-Socks5AuthenticationBlock -Config $config))

[System.IO.File]::WriteAllText(
    $ConfigPath,
    $generatedConfig,
    (New-Object System.Text.UTF8Encoding($false)))
Write-Info "Generated hev-socks5-tunnel config at '$ConfigPath'."

$defaultIpv4Route = Get-PreferredDefaultRoute -AddressFamily IPv4
$defaultIpv6Route = Get-PreferredDefaultRoute -AddressFamily IPv6

$upstreamAddresses = Resolve-UpstreamAddresses -Hosts $upstreamHosts
if ($upstreamAddresses.Count -gt 0) {
    Write-Info "Upstream tunnel endpoints: $($upstreamAddresses -join ', ')"
}

$addedRoutes = New-Object 'System.Collections.Generic.List[object]'
$tunnelProcess = $null
$jobObject = $null

try {
    Write-Info "Starting hev-socks5-tunnel."
    $tunnelProcess = Start-Process `
        -FilePath $TunnelBinaryPath `
        -ArgumentList @($ConfigPath) `
        -WorkingDirectory $PSScriptRoot `
        -PassThru

    $jobObject = New-Object KillOnCloseJob
    $jobObject.AddProcess($tunnelProcess.Handle)

    $tunnelIndex = Wait-TunnelInterface -Alias $TunnelInterfaceAlias -Process $tunnelProcess
    Write-Info "Wintun interface '$TunnelInterfaceAlias' is up with index $tunnelIndex."

    foreach ($address in $upstreamAddresses) {
        $ip = [System.Net.IPAddress]::Parse($address)
        if ($ip.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork) {
            if ($null -eq $defaultIpv4Route) {
                throw "No IPv4 default route found for bypassing upstream endpoint $address."
            }

            Add-TrackedRoute -RouteStore $addedRoutes `
                -DestinationPrefix "$address/32" `
                -InterfaceIndex ([uint32]$defaultIpv4Route.InterfaceIndex) `
                -NextHop $defaultIpv4Route.NextHop `
                -RouteMetric 1
            continue
        }

        if ($null -eq $defaultIpv6Route) {
            throw "No IPv6 default route found for bypassing upstream endpoint $address."
        }

        Add-TrackedRoute -RouteStore $addedRoutes `
            -DestinationPrefix "$address/128" `
            -InterfaceIndex ([uint32]$defaultIpv6Route.InterfaceIndex) `
            -NextHop $defaultIpv6Route.NextHop `
            -RouteMetric 1
    }

    Add-TrackedRoute -RouteStore $addedRoutes -DestinationPrefix "0.0.0.0/1" -InterfaceIndex ([uint32]$tunnelIndex) -NextHop "0.0.0.0"
    Add-TrackedRoute -RouteStore $addedRoutes -DestinationPrefix "128.0.0.0/1" -InterfaceIndex ([uint32]$tunnelIndex) -NextHop "0.0.0.0"
    Add-TrackedRoute -RouteStore $addedRoutes -DestinationPrefix "::/1" -InterfaceIndex ([uint32]$tunnelIndex) -NextHop "::"
    Add-TrackedRoute -RouteStore $addedRoutes -DestinationPrefix "8000::/1" -InterfaceIndex ([uint32]$tunnelIndex) -NextHop "::"

    Write-Info "Full-tunnel routes installed. Press Ctrl+C to stop and restore routes."
    Wait-Process -Id $tunnelProcess.Id

    if ($tunnelProcess.ExitCode -eq -1073741510) {
        Write-Info "hev-socks5-tunnel stopped by console signal."
    }
    elseif ($tunnelProcess.ExitCode -ne 0) {
        throw "hev-socks5-tunnel exited with code $($tunnelProcess.ExitCode)."
    }
}
finally {
    Write-Info "Cleaning up routes and child processes."

    for ($index = $addedRoutes.Count - 1; $index -ge 0; $index--) {
        $route = $addedRoutes[$index]
        try {
            Remove-NetRoute `
                -PolicyStore ActiveStore `
                -AddressFamily $route.AddressFamily `
                -DestinationPrefix $route.DestinationPrefix `
                -InterfaceIndex $route.InterfaceIndex `
                -Confirm:$false `
                -ErrorAction Stop
        }
        catch {
            if (-not (Test-RouteAlreadyAbsent -ErrorRecord $_)) {
                Write-Warning "Failed to remove route $($route.DestinationPrefix): $($_.Exception.Message)"
            }
        }
    }

    if ($null -ne $tunnelProcess) {
        try {
            $tunnelProcess.Refresh()
            if (-not $tunnelProcess.HasExited) {
                Stop-Process -Id $tunnelProcess.Id -Force -ErrorAction Stop
                $tunnelProcess.WaitForExit()
            }
        }
        catch {
            Write-Warning "Failed to stop hev-socks5-tunnel: $($_.Exception.Message)"
        }
        finally {
            $tunnelProcess.Dispose()
        }
    }

    if ($null -ne $jobObject) {
        $jobObject.Dispose()
    }
}
