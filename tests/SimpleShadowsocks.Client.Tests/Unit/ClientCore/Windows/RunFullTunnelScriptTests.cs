using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SimpleShadowsocks.Client.Tests.Unit.ClientCore.Windows;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class RunFullTunnelScriptTests
{
    public static TheoryData<string[], int> ResolveUpstreamAddressesCases => new()
    {
        { Array.Empty<string>(), 0 },
        { ["188.137.240.192"], 1 },
        { ["1.1.1.1", "8.8.8.8"], 2 }
    };

    [Theory]
    [MemberData(nameof(ResolveUpstreamAddressesCases))]
    public async Task ResolveUpstreamAddresses_ReturnsStableArrayShape(string[] hosts, int expectedCount)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await InvokeResolveUpstreamAddressesAsync(hosts);

        Assert.Equal("System.String[]", result.Type);
        Assert.Equal(expectedCount, result.Count);
        Assert.Equal(expectedCount, result.Addresses.Length);
        Assert.Equal(
            hosts.Order(StringComparer.Ordinal),
            result.Addresses.Order(StringComparer.Ordinal));
    }

    private static async Task<ResolveUpstreamAddressesResult> InvokeResolveUpstreamAddressesAsync(string[] hosts)
    {
        var helperScriptPath = GetRunFullTunnelScriptPath();
        var helperScript = await File.ReadAllTextAsync(helperScriptPath);
        const string marker = "if (-not (Test-Administrator)) {";
        var markerIndex = helperScript.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex > 0, $"Could not find script entrypoint marker '{marker}' in '{helperScriptPath}'.");

        var definitions = helperScript[..markerIndex];
        var serializedHosts = string.Join(", ", hosts.Select(static host => $"'{host.Replace("'", "''")}'"));
        var probeScript = $$"""
        {{definitions}}

        $hosts = New-Object 'System.Collections.Generic.List[string]'
        foreach ($entry in @({{serializedHosts}})) {
            $hosts.Add($entry) | Out-Null
        }

        $result = Resolve-UpstreamAddresses -Hosts $hosts
        [pscustomobject]@{
            Type = $result.GetType().FullName
            Count = [int]$result.Count
            Addresses = [string[]]@($result)
        } | ConvertTo-Json -Compress
        """;

        var tempScriptPath = Path.Combine(Path.GetTempPath(), $"ss-run-full-tunnel-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(tempScriptPath, probeScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            var execution = await InvokePowerShellScriptAsync(tempScriptPath);
            Assert.True(
                execution.ExitCode == 0,
                $"PowerShell probe failed with exit code {execution.ExitCode}.{Environment.NewLine}STDOUT:{Environment.NewLine}{execution.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{execution.StandardError}");

            var json = execution.StandardOutput.Trim();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return new ResolveUpstreamAddressesResult(
                root.GetProperty("Type").GetString() ?? string.Empty,
                root.GetProperty("Count").GetInt32(),
                root.GetProperty("Addresses").ValueKind == JsonValueKind.Array
                    ? root.GetProperty("Addresses").EnumerateArray().Select(static value => value.GetString() ?? string.Empty).ToArray()
                    : []);
        }
        finally
        {
            if (File.Exists(tempScriptPath))
            {
                File.Delete(tempScriptPath);
            }
        }
    }

    private static async Task<PowerShellExecutionResult> InvokePowerShellScriptAsync(string scriptPath)
    {
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            try
            {
                return await InvokePowerShellScriptAsync(candidate, scriptPath);
            }
            catch (Win32Exception) when (candidate == "pwsh")
            {
            }
        }

        throw new InvalidOperationException("Neither 'pwsh' nor 'powershell' is available to run Windows helper-script tests.");
    }

    private static async Task<PowerShellExecutionResult> InvokePowerShellScriptAsync(string executablePath, string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start PowerShell executable '{executablePath}'.");

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new PowerShellExecutionResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private static string GetRunFullTunnelScriptPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SimpleShadowsocks.sln")))
            {
                return Path.Combine(directory.FullName, "src", "SimpleShadowsocks.Client", "Platforms", "Windows", "run-full-tunnel.ps1");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    private sealed record ResolveUpstreamAddressesResult(string Type, int Count, string[] Addresses);

    private sealed record PowerShellExecutionResult(int ExitCode, string StandardOutput, string StandardError);
}
