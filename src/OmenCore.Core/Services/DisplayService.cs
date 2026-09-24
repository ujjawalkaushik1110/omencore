using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace OmenCore.Services
{
    /// <summary>
    /// Service for controlling display settings including refresh rate and power state.
    /// Implements features from OmenMon like quick refresh rate switching.
    /// </summary>
    public class DisplayService
    {
        private readonly LoggingService _logging;
        
        // Default preset values (can be configured)
        public int HighRefreshRate { get; set; } = 165;
        public int LowRefreshRate { get; set; } = 60;

        public DisplayService(LoggingService logging)
        {
            _logging = logging;
        }

        #region P/Invoke Declarations
        
        [DllImport("user32.dll")]
        private static extern int EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll")]
        private static extern int ChangeDisplaySettings(ref DEVMODE devMode, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern int ChangeDisplaySettingsEx(
            string? lpszDeviceName,
            ref DEVMODE lpDevMode,
            IntPtr hwnd,
            int dwflags,
            IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);
        
        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int CDS_UPDATEREGISTRY = 0x01;
        private const int CDS_TEST = 0x02;
        private const int DISP_CHANGE_SUCCESSFUL = 0;
        private const int DISP_CHANGE_RESTART = 1;
        private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
        private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
        private const int SC_MONITORPOWER = 0xF170;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int MONITOR_OFF = 2;
        private const int MONITOR_ON = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        // CCD (Connecting and Configuring Displays) API - the only Windows API that says which
        // output is the laptop's own panel. EnumDisplayDevices cannot: it knows "primary", and a
        // docked laptop's primary is usually the external monitor.
        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements,
            IntPtr modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        private const int ERROR_SUCCESS = 0;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const int DisplayConfigModeInfoSize = 64;

        // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values that mean "built into this machine".
        private const uint OutputTechLvds = 6;
        private const uint OutputTechDisplayPortEmbedded = 11;
        private const uint OutputTechUdiEmbedded = 13;
        private const uint OutputTechInternal = 0x80000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        #endregion

        // Marshalled sizes, pinned by tests against the Windows SDK (72 and 84 bytes). A layout
        // mistake here does not throw - it returns a plausible wrong panel.
        internal static int PathInfoMarshalSize => Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>();
        internal static int SourceDeviceNameMarshalSize => Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();

        /// <summary>
        /// Whether a CCD output technology is the machine's own panel rather than an external port.
        /// </summary>
        internal static bool IsInternalOutputTechnology(uint outputTechnology) =>
            outputTechnology == OutputTechInternal ||
            outputTechnology == OutputTechDisplayPortEmbedded ||
            outputTechnology == OutputTechUdiEmbedded ||
            outputTechnology == OutputTechLvds;

        /// <summary>
        /// GDI device name (e.g. <c>\\.\DISPLAY1</c>) of the laptop's built-in panel, or null when
        /// no active internal panel is found (lid closed, desktop, or the query failed).
        /// </summary>
        public string? GetInternalPanelDeviceName()
        {
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var numPaths, out var numModes) != ERROR_SUCCESS)
                        return null;

                    var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
                    var modes = Marshal.AllocHGlobal((int)Math.Max(1, numModes) * DisplayConfigModeInfoSize);
                    try
                    {
                        int result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
                        if (result == ERROR_INSUFFICIENT_BUFFER) continue; // topology changed between the two calls
                        if (result != ERROR_SUCCESS) return null;

                        for (int i = 0; i < numPaths; i++)
                        {
                            if (!IsInternalOutputTechnology(paths[i].targetInfo.outputTechnology)) continue;

                            var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                            {
                                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                                {
                                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                                    adapterId = paths[i].sourceInfo.adapterId,
                                    id = paths[i].sourceInfo.id
                                }
                            };

                            if (DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS &&
                                !string.IsNullOrWhiteSpace(request.viewGdiDeviceName))
                            {
                                return request.viewGdiDeviceName;
                            }
                        }

                        return null;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(modes);
                    }
                }
            }
            catch (Exception ex)
            {
                _logging.Warn($"Internal panel detection failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// The display a laptop refresh-rate shortcut should act on: the built-in panel when one is
        /// active, otherwise the primary display (null), which is what these shortcuts always used.
        /// Docked with an external monitor set as primary, "primary" used to mean the external one.
        /// </summary>
        public string? ResolveLaptopPanelTarget() => GetInternalPanelDeviceName();

        /// <summary>
        /// Get the current refresh rate of the primary display.
        /// </summary>
        public int GetCurrentRefreshRate()
        {
            return GetCurrentRefreshRate(null);
        }

        /// <summary>
        /// Get the current refresh rate for a specific display device.
        /// Pass null to query the primary display.
        /// </summary>
        public int GetCurrentRefreshRate(string? deviceName)
        {
            try
            {
                var dm = new DEVMODE
                {
                    dmSize = (short)Marshal.SizeOf(typeof(DEVMODE))
                };

                if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) != 0)
                {
                    return dm.dmDisplayFrequency;
                }
            }
            catch (Exception ex)
            {
                _logging.Warn($"Failed to get refresh rate for '{deviceName ?? "primary"}': {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// Get all available refresh rates for the current resolution.
        /// </summary>
        public List<int> GetAvailableRefreshRates()
        {
            return GetAvailableRefreshRates(null);
        }

        /// <summary>
        /// Get all available refresh rates for a display at its current resolution.
        /// Pass null to query the primary display.
        /// </summary>
        public List<int> GetAvailableRefreshRates(string? deviceName)
        {
            var refreshRates = new HashSet<int>();
            
            try
            {
                var dm = new DEVMODE
                {
                    dmSize = (short)Marshal.SizeOf(typeof(DEVMODE))
                };

                // Get current resolution
                if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) == 0)
                    return refreshRates.ToList();
                    
                int currentWidth = dm.dmPelsWidth;
                int currentHeight = dm.dmPelsHeight;
                int currentBpp = dm.dmBitsPerPel;

                // Enumerate all modes and find those matching current resolution
                int modeNum = 0;
                while (EnumDisplaySettings(deviceName, modeNum++, ref dm) != 0)
                {
                    if (dm.dmPelsWidth == currentWidth && 
                        dm.dmPelsHeight == currentHeight && 
                        dm.dmBitsPerPel == currentBpp)
                    {
                        refreshRates.Add(dm.dmDisplayFrequency);
                    }
                }
            }
            catch (Exception ex)
            {
                _logging.Warn($"Failed to enumerate refresh rates for '{deviceName ?? "primary"}': {ex.Message}");
            }
            
            return refreshRates.OrderBy(r => r).ToList();
        }

        /// <summary>
        /// Set the display refresh rate.
        /// </summary>
        /// <param name="refreshRate">Target refresh rate in Hz</param>
        /// <returns>True if successful</returns>
        public bool SetRefreshRate(int refreshRate)
        {
            return SetRefreshRate(refreshRate, null);
        }

        /// <summary>
        /// Set refresh rate for a display device. Pass null for the primary display.
        /// </summary>
        public bool SetRefreshRate(int refreshRate, string? deviceName)
        {
            try
            {
                var dm = new DEVMODE
                {
                    dmSize = (short)Marshal.SizeOf(typeof(DEVMODE))
                };

                if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) == 0)
                {
                    _logging.Warn($"Failed to get current display settings for '{deviceName ?? "primary"}'");
                    return false;
                }

                // Find a mode with the requested refresh rate
                int targetWidth = dm.dmPelsWidth;
                int targetHeight = dm.dmPelsHeight;
                int targetBpp = dm.dmBitsPerPel;
                
                int modeNum = 0;
                bool found = false;
                
                while (EnumDisplaySettings(deviceName, modeNum++, ref dm) != 0)
                {
                    if (dm.dmPelsWidth == targetWidth && 
                        dm.dmPelsHeight == targetHeight && 
                        dm.dmBitsPerPel == targetBpp &&
                        dm.dmDisplayFrequency == refreshRate)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    _logging.Warn($"Refresh rate {refreshRate}Hz not available at current resolution for '{deviceName ?? "primary"}'");
                    return false;
                }

                // Test the change first
                int testResult = string.IsNullOrWhiteSpace(deviceName)
                    ? ChangeDisplaySettings(ref dm, CDS_TEST)
                    : ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
                if (testResult != DISP_CHANGE_SUCCESSFUL)
                {
                    _logging.Warn($"Display settings test failed: {testResult}");
                    return false;
                }

                // Apply the change
                int result = string.IsNullOrWhiteSpace(deviceName)
                    ? ChangeDisplaySettings(ref dm, CDS_UPDATEREGISTRY)
                    : ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
                if (result == DISP_CHANGE_SUCCESSFUL || result == DISP_CHANGE_RESTART)
                {
                    _logging.Info($"✓ Refresh rate changed to {refreshRate}Hz for '{deviceName ?? "primary"}'");
                    return true;
                }
                else
                {
                    _logging.Warn($"Failed to change refresh rate: {result}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logging.Error($"Failed to set refresh rate for '{deviceName ?? "primary"}': {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Toggle between high and low refresh rates.
        /// </summary>
        /// <returns>The new refresh rate, or 0 if failed</returns>
        public int ToggleRefreshRate()
        {
            return ToggleRefreshRate(ResolveLaptopPanelTarget());
        }

        /// <summary>
        /// Toggle refresh rate for a specific display device.
        /// </summary>
        public int ToggleRefreshRate(string? deviceName)
        {
            int current = GetCurrentRefreshRate(deviceName);
            var available = GetAvailableRefreshRates(deviceName);
            if (!available.Any())
            {
                return 0;
            }
            
            // Determine target rate
            int target;
            if (current >= HighRefreshRate || !available.Contains(LowRefreshRate))
            {
                // Currently high (or low not available), switch to low if possible
                target = available.Contains(LowRefreshRate) ? LowRefreshRate : available.Min();
            }
            else
            {
                // Currently low or mid, switch to high if possible
                target = available.Contains(HighRefreshRate) ? HighRefreshRate : available.Max();
            }
            
            if (SetRefreshRate(target, deviceName))
            {
                return target;
            }
            return 0;
        }

        /// <summary>
        /// Enumerate active desktop displays for per-display refresh operations.
        /// </summary>
        public List<DisplayTarget> GetDisplayTargets()
        {
            var results = new List<DisplayTarget>();

            try
            {
                uint index = 0;
                while (true)
                {
                    var displayDevice = new DISPLAY_DEVICE
                    {
                        cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE))
                    };

                    if (EnumDisplayDevices(null, index, ref displayDevice, 0) == 0)
                    {
                        break;
                    }

                    if ((displayDevice.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                    {
                        results.Add(new DisplayTarget
                        {
                            DeviceName = displayDevice.DeviceName,
                            FriendlyName = string.IsNullOrWhiteSpace(displayDevice.DeviceString) ? displayDevice.DeviceName : displayDevice.DeviceString,
                            IsPrimary = (displayDevice.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0
                        });
                    }

                    index++;
                }
            }
            catch (Exception ex)
            {
                _logging.Warn($"Failed to enumerate display targets: {ex.Message}");
            }

            return results
                .OrderByDescending(display => display.IsPrimary)
                .ThenBy(display => display.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Switch to high refresh rate.
        /// </summary>
        public bool SetHighRefreshRate()
        {
            var panel = ResolveLaptopPanelTarget();
            var available = GetAvailableRefreshRates(panel);
            if (available.Count == 0) return false;
            int target = available.Contains(HighRefreshRate) ? HighRefreshRate : available.Max();
            return SetRefreshRate(target, panel);
        }

        /// <summary>
        /// Switch to low/power-saving refresh rate.
        /// </summary>
        public bool SetLowRefreshRate()
        {
            var panel = ResolveLaptopPanelTarget();
            var available = GetAvailableRefreshRates(panel);
            if (available.Count == 0) return false;
            int target = available.Contains(LowRefreshRate) ? LowRefreshRate : available.Min();
            return SetRefreshRate(target, panel);
        }

        /// <summary>
        /// Turn off the display while keeping the system running.
        /// Useful for background tasks while saving power.
        /// </summary>
        public bool TurnOffDisplay()
        {
            try
            {
                // Get the foreground window to send the message to
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    // Use desktop window as fallback
                    hwnd = GetDesktopWindow();
                }
                
                SendMessage(hwnd, WM_SYSCOMMAND, SC_MONITORPOWER, MONITOR_OFF);
                _logging.Info("✓ Display turned off");
                return true;
            }
            catch (Exception ex)
            {
                _logging.Error($"Failed to turn off display: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Turn on the display.
        /// </summary>
        public bool TurnOnDisplay()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    hwnd = GetDesktopWindow();
                }
                
                SendMessage(hwnd, WM_SYSCOMMAND, SC_MONITORPOWER, MONITOR_ON);
                _logging.Info("✓ Display turned on");
                return true;
            }
            catch (Exception ex)
            {
                _logging.Error($"Failed to turn on display: {ex.Message}", ex);
                return false;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        /// <summary>
        /// Get display information.
        /// </summary>
        public DisplayInfo GetDisplayInfo()
        {
            try
            {
                var dm = new DEVMODE
                {
                    dmSize = (short)Marshal.SizeOf(typeof(DEVMODE))
                };

                if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) != 0)
                {
                    return new DisplayInfo
                    {
                        Width = dm.dmPelsWidth,
                        Height = dm.dmPelsHeight,
                        RefreshRate = dm.dmDisplayFrequency,
                        BitsPerPixel = dm.dmBitsPerPel
                    };
                }
            }
            catch (Exception ex)
            {
                _logging.Warn($"Failed to get display info: {ex.Message}");
            }
            
            return new DisplayInfo();
        }
    }

    public class DisplayInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int RefreshRate { get; set; }
        public int BitsPerPixel { get; set; }

        public override string ToString() => $"{Width}x{Height} @ {RefreshRate}Hz";
    }

    public class DisplayTarget
    {
        public string DeviceName { get; set; } = string.Empty;
        public string FriendlyName { get; set; } = string.Empty;
        public bool IsPrimary { get; set; }
    }
}
