using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace OmenCore.Services.WindowsControl;

/// <summary>
/// Windows OS inventory and explicit control surface shared by the CLI and the future GUI.
///
/// The service intentionally uses Windows APIs and native administrative tools rather than
/// undocumented kernel/EC writes. Read operations are broad; write operations are explicit,
/// narrow, and validated.
/// </summary>
public sealed class WindowsControlService
{
    public WindowsInventorySnapshot GetInventory()
    {
        return new WindowsInventorySnapshot(
            GetProcesses(),
            GetServices(),
            GetPnPDevices(),
            GetDrivers(),
            GetStartupEntries(),
            GetScheduledTasks(),
            GetNetworkAdapters(),
            GetTcpListeners(),
            GetUdpEndpoints(),
            GetFirewallRules(),
            GetInstalledApplications(),
            GetInstalledUpdates(),
            GetOptionalFeatures());
    }

    public IReadOnlyList<WindowsProcessInfo> GetProcesses()
    {
        var result = new List<WindowsProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                result.Add(new WindowsProcessInfo(
                    process.Id,
                    process.ProcessName,
                    TryGetProcessPath(process),
                    string.IsNullOrWhiteSpace(process.MainWindowTitle) ? null : process.MainWindowTitle,
                    TryGetStartTime(process),
                    TryGetWorkingSetMb(process),
                    process.HasExited));
            }
            catch
            {
                // A process can exit or deny inspection between enumeration and readback.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Pid)
            .ToArray();
    }

    public void StopProcess(int pid, bool force = false)
    {
        if (pid <= 4)
            throw new InvalidOperationException("Refusing to control a protected system PID.");

        using var process = Process.GetProcessById(pid);
        if (process.HasExited)
            return;

        if (force)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return;
        }

        if (!process.CloseMainWindow())
            throw new InvalidOperationException("The process has no closable main window. Use --force for an explicit hard stop.");

        if (!process.WaitForExit(5000))
            throw new System.TimeoutException("The process did not exit within 5 seconds. Use --force to terminate it explicitly.");
    }

    public IReadOnlyList<WindowsServiceInfo> GetServices()
    {
        return ReadPowerShellJson<WindowsServiceInfo>(
            @"Get-CimInstance Win32_Service |
              Select-Object Name,DisplayName,State,StartMode,StartName,PathName,@{N='CanStop';E={ [bool](Get-Service -Name $_.Name).CanStop }} |
              ConvertTo-Json -Depth 3 -Compress");
    }

    public void StartService(string serviceName)
    {
        ValidateIdentifier(serviceName, nameof(serviceName));
        using var service = new ServiceController(serviceName);
        if (service.Status == ServiceControllerStatus.Running)
            return;

        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public void StopService(string serviceName)
    {
        ValidateIdentifier(serviceName, nameof(serviceName));
        using var service = new ServiceController(serviceName);
        if (service.Status == ServiceControllerStatus.Stopped)
            return;
        if (!service.CanStop)
            throw new InvalidOperationException($"Service '{serviceName}' reports that it cannot be stopped.");

        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    public void SetServiceStartMode(string serviceName, string startMode)
    {
        ValidateIdentifier(serviceName, nameof(serviceName));
        var normalized = startMode.Trim().ToLowerInvariant() switch
        {
            "auto" or "automatic" => "Automatic",
            "manual" => "Manual",
            "disabled" => "Disabled",
            _ => throw new ArgumentException("Start mode must be Automatic, Manual, or Disabled.", nameof(startMode))
        };

        RunPowerShell(
            $"Set-Service -Name '{EscapePowerShellString(serviceName)}' -StartupType {normalized}");
    }

    public IReadOnlyList<WindowsPnPDeviceInfo> GetPnPDevices()
    {
        return ReadPowerShellJson<WindowsPnPDeviceInfo>(
            @"Get-PnpDevice -PresentOnly:$false |
              Select-Object Status,Class,FriendlyName,InstanceId,Manufacturer,Service,Problem,Present |
              ConvertTo-Json -Depth 3 -Compress");
    }

    public IReadOnlyList<WindowsDriverInfo> GetDrivers()
    {
        return ReadPowerShellJson<WindowsDriverInfo>(
            @"Get-CimInstance Win32_PnPSignedDriver |
              Select-Object DeviceName,DeviceID,DriverVersion,DriverDate,Manufacturer,InfName,FriendlyName,Signer |
              ConvertTo-Json -Depth 3 -Compress");
    }

    public void ExportDriverStore(string destinationDirectory)
    {
        var destination = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destination);
        var pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
        RunProcess(pnputil, $"/export-driver * \"{EscapeCommandArgument(destination)}\"", 300_000);
    }

    public IReadOnlyList<WindowsStartupEntry> GetStartupEntries()
    {
        var result = new List<WindowsStartupEntry>();
        foreach (var hive in new[] { (RegistryHive)0, (RegistryHive)1 })
        {
            RegistryKey? root = null;
            try
            {
                root = hive == (RegistryHive)0 ? Registry.CurrentUser : Registry.LocalMachine;
                foreach (var subKeyName in new[]
                {
                    @"Software\Microsoft\Windows\CurrentVersion\Run",
                    @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
                })
                {
                    using var key = root.OpenSubKey(subKeyName, writable: false);
                    if (key is null)
                        continue;

                    foreach (var name in key.GetValueNames())
                    {
                        var command = key.GetValue(name)?.ToString();
                        if (!string.IsNullOrWhiteSpace(command))
                        {
                            result.Add(new WindowsStartupEntry(
                                root == Registry.CurrentUser ? "HKCU" : "HKLM",
                                subKeyName,
                                name,
                                command));
                        }
                    }
                }
            }
            finally
            {
                root?.Dispose();
            }
        }

        return result
            .OrderBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<WindowsScheduledTaskInfo> GetScheduledTasks()
    {
        return ReadPowerShellJson<WindowsScheduledTaskInfo>(
            @"Get-ScheduledTask |
              Select-Object TaskName,TaskPath,@{N='State';E={ if ($null -eq $_.State) { '' } else { $_.State.ToString() } }},Author,URI,Description |
              ConvertTo-Json -Depth 3 -Compress");
    }

    public void RunScheduledTask(string taskPath, string taskName)
    {
        ValidateTaskPath(taskPath);
        ValidateIdentifier(taskName, nameof(taskName));
        RunProcess("schtasks.exe", $"/Run /TN \"{EscapeCommandArgument(taskPath + taskName)}\"", 30_000);
    }

    public void SetScheduledTaskEnabled(string taskPath, string taskName, bool enabled)
    {
        ValidateTaskPath(taskPath);
        ValidateIdentifier(taskName, nameof(taskName));
        var verb = enabled ? "ENABLE" : "DISABLE";
        RunProcess("schtasks.exe", $"/Change /TN \"{EscapeCommandArgument(taskPath + taskName)}\" /{verb}", 30_000);
    }

    public IReadOnlyList<WindowsNetworkAdapterInfo> GetNetworkAdapters()
    {
        return ReadPowerShellJson<WindowsNetworkAdapterInfo>(
            @"Get-NetAdapter |
              Select-Object Name,InterfaceDescription,ifIndex,Status,MacAddress,LinkSpeed,Virtual |
              ConvertTo-Json -Depth 4 -Compress");
    }

    public IReadOnlyList<WindowsNetworkEndpointInfo> GetTcpListeners()
    {
        return AddProcessNames(ReadPowerShellJson<WindowsNetworkEndpointInfo>(
            @"Get-NetTCPConnection -State Listen |
              Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,OwningProcess,@{N='State';E={ if ($null -eq $_.State) { '' } else { $_.State.ToString() } }},@{N='CreationTime';E={ if ($null -eq $_.CreationTime) { $null } else { ([DateTime]$_.CreationTime).ToString('o') } }} |
              ConvertTo-Json -Depth 3 -Compress"));
    }

    public IReadOnlyList<WindowsNetworkEndpointInfo> GetUdpEndpoints()
    {
        return AddProcessNames(ReadPowerShellJson<WindowsNetworkEndpointInfo>(
            @"Get-NetUDPEndpoint |
              Select-Object LocalAddress,LocalPort,@{N='RemoteAddress';E={ if ($null -eq $_.RemoteAddress) { '' } else { $_.RemoteAddress.ToString() } }},@{N='RemotePort';E={ if ($null -eq $_.RemotePort) { 0 } else { [int]$_.RemotePort } }},OwningProcess,@{N='State';E={ if ($null -eq $_.State) { '' } else { $_.State.ToString() } }},@{N='CreationTime';E={ if ($null -eq $_.CreationTime) { $null } else { ([DateTime]$_.CreationTime).ToString('o') } }} |
              ConvertTo-Json -Depth 3 -Compress"));
    }

    public IReadOnlyList<WindowsFirewallRuleInfo> GetFirewallRules()
    {
        return ReadPowerShellJson<WindowsFirewallRuleInfo>(
            @"Get-NetFirewallRule |
              Select-Object Name,DisplayName,@{N='Enabled';E={ if ($null -eq $_.Enabled) { $false } elseif ($_.Enabled -is [bool]) { $_.Enabled } else { [int]$_.Enabled -ne 0 } }},@{N='Direction';E={ if ($null -eq $_.Direction) { '' } else { $_.Direction.ToString() } }},@{N='Action';E={ if ($null -eq $_.Action) { '' } else { $_.Action.ToString() } }},@{N='Profile';E={ if ($null -eq $_.Profile) { '' } else { $_.Profile.ToString() } }},@{N='PrimaryStatus';E={ if ($null -eq $_.PrimaryStatus) { '' } else { $_.PrimaryStatus.ToString() } }},@{N='PolicyStoreSourceType';E={ if ($null -eq $_.PolicyStoreSourceType) { '' } else { $_.PolicyStoreSourceType.ToString() } }} |
              ConvertTo-Json -Depth 3 -Compress");
    }

    public void SetFirewallRuleEnabled(string ruleName, bool enabled)
    {
        ValidateIdentifier(ruleName, nameof(ruleName));
        var value = enabled ? "$true" : "$false";
        RunPowerShell(
            $"Set-NetFirewallRule -Name '{EscapePowerShellString(ruleName)}' -Enabled {value}");
    }

    public IReadOnlyList<WindowsApplicationInfo> GetInstalledApplications()
    {
        return ReadPowerShellJson<WindowsApplicationInfo>(
            @"$items = @();
              $paths = @(
                'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
                'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
                'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
              );
              foreach ($path in $paths) {
                Get-ItemProperty $path -ErrorAction SilentlyContinue |
                  Where-Object { $_.DisplayName } |
                  ForEach-Object {
                    $items += [pscustomobject]@{
                      Name=$_.DisplayName;
                      Version=$_.DisplayVersion;
                      Publisher=$_.Publisher;
                      InstallLocation=$_.InstallLocation;
                      Type='Win32';
                      PackageFullName=$null
                    }
                  }
              }
              Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue |
                ForEach-Object {
                  $items += [pscustomobject]@{
                    Name=$_.Name;
                    Version=$_.Version.ToString();
                    Publisher=$_.Publisher;
                    InstallLocation=$_.InstallLocation;
                    Type='AppX';
                    PackageFullName=$_.PackageFullName
                  }
                };
              $items | Sort-Object Type,Name | ConvertTo-Json -Depth 4 -Compress");
    }

    public IReadOnlyList<WindowsUpdateInfo> GetInstalledUpdates()
    {
        return ReadPowerShellJson<WindowsUpdateInfo>(
            @"Get-HotFix |
              Select-Object HotFixID,Description,@{N='InstalledOn';E={ if ($null -eq $_.InstalledOn) { '' } else { $_.InstalledOn.ToString() } }},InstalledBy,Caption,Source |
              ConvertTo-Json -Depth 3 -Compress");
    }

    public void TriggerWindowsUpdateScan()
    {
        RunProcess("UsoClient.exe", "StartScan", 60_000);
    }

    public IReadOnlyList<WindowsOptionalFeatureInfo> GetOptionalFeatures()
    {
        return ReadPowerShellJson<WindowsOptionalFeatureInfo>(
            @"Get-WindowsOptionalFeature -Online |
              Select-Object FeatureName,@{N='State';E={ if ($null -eq $_.State) { '' } else { $_.State.ToString() } }},DisplayName,RestartNeeded |
              ConvertTo-Json -Depth 3 -Compress");
    }

    public void SetOptionalFeatureState(string featureName, bool enabled)
    {
        ValidateIdentifier(featureName, nameof(featureName));
        var dism = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "dism.exe");
        var operation = enabled ? "/Enable-Feature" : "/Disable-Feature";
        RunProcess(
            dism,
            $"/Online {operation} /FeatureName:{EscapeCommandArgument(featureName)} /NoRestart",
            120_000);
    }

    public string GetActivePowerScheme()
    {
        var output = RunProcess("powercfg.exe", "/getactivescheme", 15_000, captureOutput: true);
        return output.Trim();
    }

    public void SetActivePowerScheme(string scheme)
    {
        var normalized = scheme.Trim().ToLowerInvariant() switch
        {
            "high" or "high-performance" or "max" or "scheme_max" => "SCHEME_MAX",
            "balanced" or "scheme_balanced" => "SCHEME_BALANCED",
            "power-saver" or "powersaver" or "scheme_min" => "SCHEME_MIN",
            _ => throw new ArgumentException("Supported aliases: high, balanced, power-saver.", nameof(scheme))
        };
        RunProcess("powercfg.exe", $"/setactive {normalized}", 20_000);
    }

    public void SetAcProcessorLimits(int minimumPercent, int maximumPercent)
    {
        if (minimumPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(minimumPercent));
        if (maximumPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximumPercent));
        if (minimumPercent > maximumPercent)
            throw new ArgumentException("Minimum processor state cannot exceed maximum processor state.");

        RunProcess("powercfg.exe",
            $"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN {minimumPercent}",
            20_000);
        RunProcess("powercfg.exe",
            $"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX {maximumPercent}",
            20_000);
        RunProcess("powercfg.exe", "/S SCHEME_CURRENT", 20_000);
    }

    public void SetAcProcessorBoostMode(int mode)
    {
        if (mode is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(mode), "Boost mode must be 0-5.");

        RunProcess("powercfg.exe",
            $"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE {mode}",
            20_000);
        RunProcess("powercfg.exe", "/S SCHEME_CURRENT", 20_000);
    }

    private static IReadOnlyList<WindowsNetworkEndpointInfo> AddProcessNames(
        IReadOnlyList<WindowsNetworkEndpointInfo> endpoints)
    {
        return endpoints
            .Select(endpoint => endpoint with { ProcessName = TryGetProcessName(endpoint.OwningProcess) })
            .ToArray();
    }

    private static string? TryGetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<T> ReadPowerShellJson<T>(string script)
    {
        var output = RunPowerShell(script);
        if (string.IsNullOrWhiteSpace(output))
            return Array.Empty<T>();

        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Deserialize<List<T>>(output, serializerOptions) ?? new List<T>();

        var item = JsonSerializer.Deserialize<T>(output, serializerOptions);
        return item is null ? Array.Empty<T>() : new[] { item };
    }

    private static string RunPowerShell(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return RunProcess(
            "powershell.exe",
            $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            120_000,
            captureOutput: true);
    }

    private static string RunProcess(
        string fileName,
        string arguments,
        int timeoutMs,
        bool captureOutput = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = captureOutput,
                RedirectStandardError = captureOutput
            }
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start '{fileName}'.");

        Task<string>? stdout = null;
        Task<string>? stderr = null;
        if (captureOutput)
        {
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
        }

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new System.TimeoutException($"'{fileName}' did not finish within {timeoutMs} ms.");
        }

        if (captureOutput)
        {
            Task.WaitAll(stdout!, stderr!);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"'{fileName}' failed with exit code {process.ExitCode}: {stderr!.Result.Trim()}");
            return stdout!.Result;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'{fileName}' failed with exit code {process.ExitCode}.");

        return string.Empty;
    }

    private static string? TryGetProcessPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private static DateTimeOffset? TryGetStartTime(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch { return null; }
    }

    private static long? TryGetWorkingSetMb(Process process)
    {
        try { return process.WorkingSet64 / (1024L * 1024L); }
        catch { return null; }
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\'') || value.Contains('"'))
            throw new ArgumentException("Identifier is empty or contains unsafe quoting characters.", parameterName);
    }

    private static void ValidateTaskPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('\\') || value.Contains('"'))
            throw new ArgumentException("Task path must start with '\\' and contain no quotes.", nameof(value));
    }

    private static string EscapePowerShellString(string value) => value.Replace("'", "''");
    private static string EscapeCommandArgument(string value) => value.Replace("\"", "\\\"");
}

public sealed record WindowsInventorySnapshot(
    IReadOnlyList<WindowsProcessInfo> Processes,
    IReadOnlyList<WindowsServiceInfo> Services,
    IReadOnlyList<WindowsPnPDeviceInfo> Devices,
    IReadOnlyList<WindowsDriverInfo> Drivers,
    IReadOnlyList<WindowsStartupEntry> Startup,
    IReadOnlyList<WindowsScheduledTaskInfo> ScheduledTasks,
    IReadOnlyList<WindowsNetworkAdapterInfo> NetworkAdapters,
    IReadOnlyList<WindowsNetworkEndpointInfo> TcpListeners,
    IReadOnlyList<WindowsNetworkEndpointInfo> UdpEndpoints,
    IReadOnlyList<WindowsFirewallRuleInfo> FirewallRules,
    IReadOnlyList<WindowsApplicationInfo> Applications,
    IReadOnlyList<WindowsUpdateInfo> InstalledUpdates,
    IReadOnlyList<WindowsOptionalFeatureInfo> OptionalFeatures);

public sealed record WindowsProcessInfo(
    int Pid,
    string Name,
    string? Path,
    string? WindowTitle,
    DateTimeOffset? StartTimeUtc,
    long? WorkingSetMb,
    bool HasExited);

public sealed record WindowsServiceInfo(
    string Name,
    string DisplayName,
    string State,
    string StartMode,
    string StartName,
    string PathName,
    bool CanStop);

public sealed record WindowsPnPDeviceInfo(
    string Status,
    string Class,
    string FriendlyName,
    string InstanceId,
    string Manufacturer,
    string Service,
    int? Problem,
    bool? Present);

public sealed record WindowsDriverInfo(
    string DeviceName,
    string DeviceID,
    string DriverVersion,
    string DriverDate,
    string Manufacturer,
    string InfName,
    string FriendlyName,
    string Signer);

public sealed record WindowsStartupEntry(
    string Scope,
    string RegistryPath,
    string Name,
    string Command);

public sealed record WindowsScheduledTaskInfo(
    string TaskName,
    string TaskPath,
    string State,
    string Author,
    string URI,
    string Description);

public sealed record WindowsNetworkAdapterInfo(
    string Name,
    string InterfaceDescription,
    int IfIndex,
    string Status,
    string MacAddress,
    string LinkSpeed,
    bool Virtual);

public sealed record WindowsNetworkEndpointInfo(
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    int OwningProcess,
    string State,
    DateTime? CreationTime,
    string? ProcessName = null);

public sealed record WindowsFirewallRuleInfo(
    string Name,
    string DisplayName,
    bool Enabled,
    string Direction,
    string Action,
    string Profile,
    string PrimaryStatus,
    string PolicyStoreSourceType);

public sealed record WindowsApplicationInfo(
    string Name,
    string Version,
    string Publisher,
    string InstallLocation,
    string Type,
    string? PackageFullName);

public sealed record WindowsUpdateInfo(
    string HotFixID,
    string Description,
    string InstalledOn,
    string InstalledBy,
    string Caption,
    string Source);

public sealed record WindowsOptionalFeatureInfo(
    string FeatureName,
    string State,
    string DisplayName,
    bool RestartNeeded);
