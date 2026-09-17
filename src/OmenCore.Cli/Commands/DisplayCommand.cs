using System.CommandLine;
using System.Text.Json;
using OmenCore.Services;

namespace OmenCore.Cli.Commands;

public static class DisplayCommand
{
    public static Command Create()
    {
        var root = new Command("display", "Inspect and control display refresh rates.");

        root.AddCommand(CreateListCommand());
        root.AddCommand(CreateStatusCommand());
        root.AddCommand(CreateModesCommand());
        root.AddCommand(CreateRefreshCommand());
        root.AddCommand(CreateToggleCommand());

        return root;
    }

    private static Command CreateListCommand()
    {
        var command = new Command("list", "List active displays and their available refresh rates.");
        var json = new Option<bool>("--json", "Output JSON.");
        command.AddOption(json);

        command.SetHandler(useJson =>
        {
            var ctx = CliContext.Create();
            var service = new DisplayService(ctx.Logging);

            var rows = service.GetDisplayTargets()
                .Select(display =>
                {
                    var refresh = service.GetCurrentRefreshRate(display.DeviceName);
                    var rates = service.GetAvailableRefreshRates(display.DeviceName);

                    return new DisplaySnapshot(
                        display.DeviceName,
                        display.FriendlyName,
                        display.IsPrimary,
                        refresh,
                        rates);
                })
                .ToArray();

            Write(rows, useJson);
        }, json);

        return command;
    }

    private static Command CreateStatusCommand()
    {
        var command = new Command("status", "Show current refresh status for one display.");
        var device = new Option<string?>("--device", "Display device name, for example \\.\\DISPLAY1.");
        var json = new Option<bool>("--json", "Output JSON.");

        command.AddOption(device);
        command.AddOption(json);

        command.SetHandler((deviceName, useJson) =>
        {
            var ctx = CliContext.Create();
            var service = new DisplayService(ctx.Logging);

            var target = ResolveTarget(service, deviceName);
            var refresh = service.GetCurrentRefreshRate(target.DeviceName);
            var rates = service.GetAvailableRefreshRates(target.DeviceName);

            var snapshot = new DisplaySnapshot(
                target.DeviceName,
                target.FriendlyName,
                target.IsPrimary,
                refresh,
                rates);

            Write(snapshot, useJson);
        }, device, json);

        return command;
    }

    private static Command CreateModesCommand()
    {
        var command = new Command("modes", "Show refresh rates available at the current resolution.");
        var device = new Option<string?>("--device", "Display device name, for example \\.\\DISPLAY1.");
        command.AddOption(device);

        command.SetHandler(deviceName =>
        {
            var ctx = CliContext.Create();
            var service = new DisplayService(ctx.Logging);

            var target = ResolveTarget(service, deviceName);
            var current = service.GetCurrentRefreshRate(target.DeviceName);
            var rates = service.GetAvailableRefreshRates(target.DeviceName);

            Console.WriteLine($"Display : {target.FriendlyName}");
            Console.WriteLine($"Device  : {target.DeviceName}");
            Console.WriteLine($"Primary : {target.IsPrimary}");
            Console.WriteLine($"Current : {current} Hz");
            Console.WriteLine("Modes   : " + (rates.Count == 0
                ? "none detected"
                : string.Join(", ", rates.Select(rate => $"{rate} Hz"))));
        }, device);

        return command;
    }

    private static Command CreateRefreshCommand()
    {
        var command = new Command("refresh", "Set an explicitly available refresh rate.");
        var rate = new Argument<int>("rate", "Target refresh rate in Hz.");
        var device = new Option<string?>("--device", "Display device name, for example \\.\\DISPLAY1.");

        command.AddArgument(rate);
        command.AddOption(device);

        command.SetHandler((targetRate, deviceName) =>
        {
            var ctx = CliContext.Create();
            var service = new DisplayService(ctx.Logging);
            var target = ResolveTarget(service, deviceName);
            var available = service.GetAvailableRefreshRates(target.DeviceName);

            if (!available.Contains(targetRate))
            {
                Console.Error.WriteLine(
                    $"ERROR: {targetRate} Hz is not available for {target.DeviceName}. " +
                    $"Available: {string.Join(", ", available.Select(r => r + " Hz"))}");
                return;
            }

            var before = service.GetCurrentRefreshRate(target.DeviceName);

            if (!service.SetRefreshRate(targetRate, target.DeviceName))
            {
                Console.Error.WriteLine($"ERROR: Refresh-rate change to {targetRate} Hz failed.");
                return;
            }

            var after = service.GetCurrentRefreshRate(target.DeviceName);

            if (after != targetRate)
            {
                Console.Error.WriteLine(
                    $"ERROR: Display reported {after} Hz after requesting {targetRate} Hz. " +
                    "Change was not verified.");
                return;
            }

            Console.WriteLine(
                $"OK: {target.DeviceName} refresh changed {before} Hz -> {after} Hz.");
        }, rate, device);

        return command;
    }

    private static Command CreateToggleCommand()
    {
        var command = new Command(
            "toggle",
            "Toggle between the lowest and highest refresh rates actually exposed by the display.");

        var device = new Option<string?>("--device", "Display device name, for example \\.\\DISPLAY1.");
        command.AddOption(device);

        command.SetHandler(deviceName =>
        {
            var ctx = CliContext.Create();
            var service = new DisplayService(ctx.Logging);
            var target = ResolveTarget(service, deviceName);

            var current = service.GetCurrentRefreshRate(target.DeviceName);
            var available = service.GetAvailableRefreshRates(target.DeviceName);

            if (available.Count < 2)
            {
                Console.Error.WriteLine(
                    $"ERROR: Fewer than two refresh rates are available for {target.DeviceName}.");
                return;
            }

            var low = available.Min();
            var high = available.Max();
            var targetRate = current <= low ? high : low;

            if (!service.SetRefreshRate(targetRate, target.DeviceName))
            {
                Console.Error.WriteLine(
                    $"ERROR: Failed to switch {target.DeviceName} from {current} Hz to {targetRate} Hz.");
                return;
            }

            var after = service.GetCurrentRefreshRate(target.DeviceName);

            if (after != targetRate)
            {
                Console.Error.WriteLine(
                    $"ERROR: Toggle requested {targetRate} Hz but readback returned {after} Hz.");
                return;
            }

            Console.WriteLine(
                $"OK: {target.DeviceName} refresh toggled {current} Hz -> {after} Hz.");
        }, device);

        return command;
    }

    private static DisplayTarget ResolveTarget(
        DisplayService service,
        string? requestedDevice)
    {
        var targets = service.GetDisplayTargets();

        if (targets.Count == 0)
            throw new InvalidOperationException("No active desktop displays were detected.");

        if (string.IsNullOrWhiteSpace(requestedDevice))
            return targets.FirstOrDefault(t => t.IsPrimary) ?? targets[0];

        var match = targets.FirstOrDefault(
            t => string.Equals(
                t.DeviceName,
                requestedDevice,
                StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new InvalidOperationException(
                $"Display '{requestedDevice}' was not found. " +
                $"Available: {string.Join(", ", targets.Select(t => t.DeviceName))}");

        return match;
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

        if (value is IEnumerable<DisplaySnapshot> snapshots)
        {
            foreach (var item in snapshots)
            {
                Console.WriteLine(
                    $"{item.DeviceName,-16} " +
                    $"{(item.IsPrimary ? "PRIMARY" : "       ")}  " +
                    $"{item.CurrentRefreshRate,3} Hz  " +
                    $"{item.FriendlyName}");
                Console.WriteLine(
                    $"{"",-16} Available: " +
                    $"{string.Join(", ", item.AvailableRefreshRates.Select(r => r + " Hz"))}");
            }

            return;
        }

        Console.WriteLine(JsonSerializer.Serialize(
            value,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record DisplaySnapshot(
        string DeviceName,
        string FriendlyName,
        bool IsPrimary,
        int CurrentRefreshRate,
        List<int> AvailableRefreshRates);
}