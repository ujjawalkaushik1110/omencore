using System.CommandLine;
using System.Text.Json;
using OmenCore.Services.WindowsControl;

namespace OmenCore.Cli.Commands;

/// <summary>
/// Broad Windows inventory/control command surface. Mutating commands map to explicit native
/// Windows operations; the future GUI will use the same WindowsControlService.
/// </summary>
public static class WindowsCommand
{
    public static Command Create()
    {
        var root = new Command("os", "Windows operating-system inventory and control");

        root.AddCommand(CreateInventoryCommand());
        root.AddCommand(CreateProcessCommand());
        root.AddCommand(CreateServiceCommand());
        root.AddCommand(CreateDeviceCommand());
        root.AddCommand(CreateDriverCommand());
        root.AddCommand(CreateStartupCommand());
        root.AddCommand(CreateTaskCommand());
        root.AddCommand(CreateNetworkCommand());
        root.AddCommand(CreateFirewallCommand());
        root.AddCommand(CreateAppCommand());
        root.AddCommand(CreateUpdateCommand());
        root.AddCommand(CreateFeatureCommand());
        root.AddCommand(CreatePowerCommand());

        return root;
    }

    private static Command CreateInventoryCommand()
    {
        var command = new Command("inventory", "Collect a full Windows control-center inventory");
        var json = new Option<bool>("--json", "Output JSON.");
        command.AddOption(json);
        command.SetHandler(flag =>
        {
            var service = new WindowsControlService();
            var snapshot = service.GetInventory();
            Write(snapshot, flag);
        }, json);
        return command;
    }

    private static Command CreateProcessCommand()
    {
        var root = new Command("process", "Inspect and explicitly stop processes.");
        var list = new Command("list", "List processes.");
        var json = new Option<bool>("--json", "Output JSON.");
        list.AddOption(json);
        list.SetHandler(flag => Write(new WindowsControlService().GetProcesses(), flag), json);
        root.AddCommand(list);

        var stop = new Command("stop", "Stop one process. Use --force for a hard tree termination.");
        var pid = new Argument<int>("pid");
        var force = new Option<bool>("--force", "Explicitly hard-terminate the process tree.");
        stop.AddArgument(pid);
        stop.AddOption(force);
        stop.SetHandler((id, hard) =>
        {
            RunMutation(() => new WindowsControlService().StopProcess(id, hard), $"Process {id} stopped.");
        }, pid, force);
        root.AddCommand(stop);
        return root;
    }

    private static Command CreateServiceCommand()
    {
        var root = new Command("service", "Inspect and control Windows services.");
        var list = new Command("list", "List Windows services.");
        var json = new Option<bool>("--json", "Output JSON.");
        list.AddOption(json);
        list.SetHandler(flag => Write(new WindowsControlService().GetServices(), flag), json);
        root.AddCommand(list);

        foreach (var action in new[] { "start", "stop" })
        {
            var command = new Command(action, $"{action} one Windows service.");
            var name = new Argument<string>("name");
            command.AddArgument(name);
            command.SetHandler(serviceName =>
            {
                var svc = new WindowsControlService();
                RunMutation(
                    () =>
                    {
                        if (action == "start")
                        {
                            svc.StartService(serviceName);
                        }
                        else
                        {
                            svc.StopService(serviceName);
                        }
                    },
                    $"Service {serviceName}: {action} completed.");
            }, name);
            root.AddCommand(command);
        }

        var startup = new Command("startup-type", "Set a service startup type.");
        var startupName = new Argument<string>("name");
        var mode = new Argument<string>("mode");
        startup.AddArgument(startupName);
        startup.AddArgument(mode);
        startup.SetHandler((serviceName, startMode) =>
            RunMutation(
                () => new WindowsControlService().SetServiceStartMode(serviceName, startMode),
                $"Service {serviceName} startup type set to {startMode}."),
            startupName, mode);
        root.AddCommand(startup);
        return root;
    }

    private static Command CreateDeviceCommand()
    {
        var root = new Command("device", "Inspect Plug-and-Play devices, including non-present entries.");
        var list = new Command("list", "List PnP devices.");
        var json = new Option<bool>("--json", "Output JSON.");
        list.AddOption(json);
        list.SetHandler(flag => Write(new WindowsControlService().GetPnPDevices(), flag), json);
        root.AddCommand(list);
        return root;
    }

    private static Command CreateDriverCommand()
    {
        var root = new Command("driver", "Inspect and back up the Windows driver store.");
        var list = new Command("list", "List signed PnP drivers.");
        var json = new Option<bool>("--json", "Output JSON.");
        list.AddOption(json);
        list.SetHandler(flag => Write(new WindowsControlService().GetDrivers(), flag), json);
        root.AddCommand(list);

        var backup = new Command("backup", "Export the installed driver store.");
        var path = new Argument<string>("destination");
        backup.AddArgument(path);
        backup.SetHandler(destination =>
            RunMutation(
                () => new WindowsControlService().ExportDriverStore(destination),
                $"Driver store exported to {Path.GetFullPath(destination)}."),
            path);
        root.AddCommand(backup);
        return root;
    }

    private static Command CreateStartupCommand()
    {
        var root = new Command("startup", "Inspect Windows Run/RunOnce startup entries.");
        var list = new Command("list", "List current-user and local-machine registry startup entries.");
        var json = new Option<bool>("--json", "Output JSON.");
        list.AddOption(json);
        list.SetHandler(flag => Write(new WindowsControlService().GetStartupEntries(), flag), json);
        root.AddCommand(list);
        return root;
    }

    private static Command CreateTaskCommand()
    {
        var root = new Command("task", "Inspect and control Scheduled Tasks.");
        var list = new Command("list", "List scheduled tasks.");
        var json = new Option<bool>("--json", "Output JSON.");
        list.AddOption(json);
        list.SetHandler(flag => Write(new WindowsControlService().GetScheduledTasks(), flag), json);
        root.AddCommand(list);

        var run = new Command("run", "Run one scheduled task.");
        var path = new Argument<string>("task-path");
        var name = new Argument<string>("task-name");
        run.AddArgument(path);
        run.AddArgument(name);
        run.SetHandler((taskPath, taskName) =>
            RunMutation(
                () => new WindowsControlService().RunScheduledTask(taskPath, taskName),
                $"Scheduled task {taskPath}{taskName} started."),
            path, name);
        root.AddCommand(run);

        var enabled = new Command("enabled", "Enable or disable one scheduled task.");
        var enabledPath = new Argument<string>("task-path");
        var enabledName = new Argument<string>("task-name");
        var state = new Argument<bool>("enabled");
        enabled.AddArgument(enabledPath);
        enabled.AddArgument(enabledName);
        enabled.AddArgument(state);
        enabled.SetHandler((taskPath, taskName, value) =>
            RunMutation(
                () => new WindowsControlService().SetScheduledTaskEnabled(taskPath, taskName, value),
                $"Scheduled task {taskPath}{taskName} enabled={value}."),
            enabledPath, enabledName, state);
        root.AddCommand(enabled);
        return root;
    }

    private static Command CreateNetworkCommand()
    {
        var root = new Command("network", "Inspect network adapters and listening endpoints.");

        var adapters = new Command("adapters", "List network adapters.");
        var adapterJson = new Option<bool>("--json", "Output JSON.");
        adapters.AddOption(adapterJson);
        adapters.SetHandler(flag => Write(new WindowsControlService().GetNetworkAdapters(), flag), adapterJson);
        root.AddCommand(adapters);

        var tcp = new Command("tcp", "List TCP listeners with owning PID/process where readable.");
        var tcpJson = new Option<bool>("--json", "Output JSON.");
        tcp.AddOption(tcpJson);
        tcp.SetHandler(flag => Write(new WindowsControlService().GetTcpListeners(), flag), tcpJson);
        root.AddCommand(tcp);

        var udp = new Command("udp", "List UDP endpoints with owning PID/process where readable.");
        var udpJson = new Option<bool>("--json", "Output JSON.");
        udp.AddOption(udpJson);
        udp.SetHandler(flag => Write(new WindowsControlService().GetUdpEndpoints(), flag), udpJson);
        root.AddCommand(udp);
        return root;
    }

    private static Command CreateFirewallCommand()
    {
        var root = new Command("firewall", "Inspect and explicitly enable/disable firewall rules.");
        var list = new Command("list", "List firewall rules.");
        var json = new Option<bool>("--json", "Output JSON.");
        list.AddOption(json);
        list.SetHandler(flag => Write(new WindowsControlService().GetFirewallRules(), flag), json);
        root.AddCommand(list);

        var set = new Command("set", "Enable or disable one firewall rule by exact name.");
        var name = new Argument<string>("name");
        var enabled = new Argument<bool>("enabled");
        set.AddArgument(name);
        set.AddArgument(enabled);
        set.SetHandler((ruleName, value) =>
            RunMutation(
                () => new WindowsControlService().SetFirewallRuleEnabled(ruleName, value),
                $"Firewall rule {ruleName}: enabled={value}."),
            name, enabled);
        root.AddCommand(set);
        return root;
    }

    private static Command CreateAppCommand()
    {
        var root = new Command("app", "Inventory installed Win32 and AppX applications.");
        var list = new Command("list", "List installed applications.");
        var json = new Option<bool>("--json", "Output JSON.");
        list.AddOption(json);
        list.SetHandler(flag => Write(new WindowsControlService().GetInstalledApplications(), flag), json);
        root.AddCommand(list);
        return root;
    }

    private static Command CreateUpdateCommand()
    {
        var root = new Command("update", "Inspect installed Windows updates and trigger a native scan.");
        var list = new Command("list", "List installed hotfix/update entries.");
        var json = new Option<bool>("--json", "Output JSON.");
        list.AddOption(json);
        list.SetHandler(flag => Write(new WindowsControlService().GetInstalledUpdates(), flag), json);
        root.AddCommand(list);

        var scan = new Command("scan", "Trigger Windows Update's native scan operation.");
        scan.SetHandler(() =>
            RunMutation(
                () => new WindowsControlService().TriggerWindowsUpdateScan(),
                "Windows Update scan triggered."));
        root.AddCommand(scan);
        return root;
    }

    private static Command CreateFeatureCommand()
    {
        var root = new Command("feature", "Inspect and explicitly enable/disable Windows optional features.");
        var list = new Command("list", "List Windows optional features.");
        var json = new Option<bool>("--json", "Output JSON.");
        list.AddOption(json);
        list.SetHandler(flag => Write(new WindowsControlService().GetOptionalFeatures(), flag), json);
        root.AddCommand(list);

        var set = new Command("set", "Enable or disable one Windows optional feature.");
        var name = new Argument<string>("name");
        var enabled = new Argument<bool>("enabled");
        set.AddArgument(name);
        set.AddArgument(enabled);
        set.SetHandler((featureName, value) =>
            RunMutation(
                () => new WindowsControlService().SetOptionalFeatureState(featureName, value),
                $"Optional feature {featureName}: enabled={value}."),
            name, enabled);
        root.AddCommand(set);
        return root;
    }

    private static Command CreatePowerCommand()
    {
        var root = new Command("power", "Control Windows power policy exposed through powercfg.");

        var status = new Command("status", "Show the active Windows power scheme.");
        status.SetHandler(() => Console.WriteLine(new WindowsControlService().GetActivePowerScheme()));
        root.AddCommand(status);

        var plan = new Command("scheme", "Activate high-performance, balanced, or power-saver.");
        var scheme = new Argument<string>("scheme");
        plan.AddArgument(scheme);
        plan.SetHandler(value =>
            RunMutation(
                () => new WindowsControlService().SetActivePowerScheme(value),
                $"Power scheme activated: {value}."),
            scheme);
        root.AddCommand(plan);

        var cpu = new Command("cpu", "Set AC minimum/maximum processor state.");
        var min = new Argument<int>("minimum");
        var max = new Argument<int>("maximum");
        cpu.AddArgument(min);
        cpu.AddArgument(max);
        cpu.SetHandler((minimum, maximum) =>
            RunMutation(
                () => new WindowsControlService().SetAcProcessorLimits(minimum, maximum),
                $"AC processor state set to {minimum}-{maximum}%."),
            min, max);
        root.AddCommand(cpu);

        var boost = new Command("boost", "Set AC processor performance boost mode (0-5).");
        var mode = new Argument<int>("mode");
        boost.AddArgument(mode);
        boost.SetHandler(value =>
            RunMutation(
                () => new WindowsControlService().SetAcProcessorBoostMode(value),
                $"AC processor boost mode set to {value}."),
            mode);
        root.AddCommand(boost);
        return root;
    }

    private static void Write<T>(T value, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        switch (value)
        {
            case WindowsProcessInfo[] processes:
                PrintRows(processes, p => $"{p.Pid,6}  {p.Name,-32} {p.WorkingSetMb,8} MB  {p.Path}");
                break;
            case IReadOnlyList<WindowsProcessInfo> processList:
                PrintRows(processList, p => $"{p.Pid,6}  {p.Name,-32} {p.WorkingSetMb,8} MB  {p.Path}");
                break;
            default:
                Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
                break;
        }
    }

    private static void PrintRows<T>(IEnumerable<T> rows, Func<T, string> formatter)
    {
        foreach (var row in rows)
            Console.WriteLine(formatter(row));
    }

    private static void RunMutation(Action action, string success)
    {
        try
        {
            action();
            Console.WriteLine($"OK: {success}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
        }
    }
}
