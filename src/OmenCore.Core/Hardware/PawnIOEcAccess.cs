using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace OmenCore.Hardware
{
    /// <summary>
    /// PawnIO-based EC access provider for Secure Boot compatible systems.
    /// Uses the signed PawnIO driver with LpcACPIEC module for ACPI EC port access.
    /// </summary>
    public sealed class PawnIOEcAccess : IEcAccess
    {
        private IntPtr _handle = IntPtr.Zero;
        private IntPtr _pawnIOLib = IntPtr.Zero;
        private bool _moduleLoaded;
        private bool _disposed;

        // The compiled LpcACPIEC PawnIO module, read from disk by LoadEcModule and cached here.
        //
        // NOT embedded in the assembly, despite how a cached static byte[] reads. PublishSingleFile
        // bundles managed assemblies, not `None` content items, and IncludeAllContentForSelfExtract
        // is deliberately off — so drivers\LpcACPIEC.bin has to ship as a loose file beside the exe.
        // A deployment that copies only OmenCore.exe loses EC access, undervolt and the APU clamp in
        // one go, and the only symptom is the "No EC" badge. The installer gets this right by taking
        // the publish directory with recursesubdirs; hand-assembled drops are where it goes wrong.
        private static byte[]? _lpcAcpiEcModule;

        // ACPI EC standard ports
        private const ushort EC_DATA_PORT = 0x62;
        private const ushort EC_CMD_PORT = 0x66;

        // EC commands
        private const byte EC_CMD_READ = 0x80;
        private const byte EC_CMD_WRITE = 0x81;

        // EC status bits
        private const byte EC_STATUS_OBF = 0x01;  // Output Buffer Full
        private const byte EC_STATUS_IBF = 0x02;  // Input Buffer Full

        // Timeout for EC operations
        private const int EC_TIMEOUT_MS = 100;
        private const int EC_POLL_DELAY_US = 10;

        // Function delegates
        private delegate int PawnioOpen(out IntPtr handle);
        private delegate int PawnioLoad(IntPtr handle, byte[] blob, IntPtr size);
        private delegate int PawnioExecute(IntPtr handle, string name, ulong[] input, IntPtr inSize, ulong[] output, IntPtr outSize, out IntPtr returnSize);
        private delegate int PawnioClose(IntPtr handle);

        private PawnioOpen? _pawnioOpen;
        private PawnioLoad? _pawnioLoad;
        private PawnioExecute? _pawnioExecute;
        private PawnioClose? _pawnioClose;

        /// <summary>
        /// Allowlist of EC addresses that are safe to write (fan control only).
        /// IMPORTANT: Keyboard RGB EC addresses (0xB0-0xBE) are NOT included because
        /// they vary by model and can cause system crashes on some hardware (e.g., OMEN 17-ck2xxx).
        /// Keyboard lighting should use WMI BIOS only.
        /// </summary>
        private static readonly HashSet<ushort> AllowedWriteAddresses = new()
        {
            // Fan control registers (HP Omen typical addresses)
            0x2C, // Fan 1 set speed % (XSS1) - OmenMon-style, newer models
            0x2D, // Fan 2 set speed % (XSS2) - OmenMon-style, newer models
            0x2E, // Fan 1 speed % (legacy)
            0x2F, // Fan 2 speed % (legacy)
            0x34, // Fan 1 speed in 100 RPM units (0-55)
            0x35, // Fan 2 speed in 100 RPM units (0-55)
            0x44, // Fan 1 duty cycle
            0x45, // Fan 2 duty cycle
            0x46, // Fan control mode
            0x4A, // Fan 1 speed low byte
            0x4B, // Fan 1 speed high byte
            0x4C, // Fan 2 speed low byte
            0x4D, // Fan 2 speed high byte
            0x62, // OMCC - BIOS manual/auto control (0x06=Manual, 0x00=Auto)
            0x63, // Timer register
            0x95, // Performance mode register for throttling mitigation (write 0x31 for performance)
            0xB0, // Fan speed target CPU
            0xB1, // Fan speed target GPU
            0xCE, // Performance mode register
            0xCF, // Power limit control
            0xEC, // Fan boost (0x00=OFF, 0x0C=ON)
            0xF4, // Fan state (0x00=Enable, 0x02=Disable)
            
            // OMCC (Omen Control Center) register
            0x96, // OMCC control register
            
            // NOTE: Keyboard backlight EC addresses (0xB2-0xBE) are NOT safe to write!
            // These registers vary by model and caused hard crashes on OMEN 17-ck2xxx.
            // Use WMI BIOS SetColorTable() for keyboard lighting instead.
        };
        
        /// <summary>
        /// Experimental keyboard EC addresses - enabled only when user explicitly enables EC keyboard.
        /// WARNING: These may cause system crashes on some models!
        /// </summary>
        private static readonly HashSet<ushort> ExperimentalKeyboardAddresses = new()
        {
            0xB2, 0xB3, // Zone 1 G, B
            0xB4, 0xB5, 0xB6, // Zone 2 R, G, B  
            0xB7, 0xB8, 0xB9, // Zone 3 R, G, B
            0xBA, 0xBB, 0xBC, // Zone 4 R, G, B
            0xBD, // Keyboard brightness
            0xBE, // Keyboard effect
        };
        
        /// <summary>
        /// Enable experimental keyboard EC writes. Set this to true when ExperimentalEcKeyboardEnabled is on.
        /// </summary>
        public static bool EnableExperimentalKeyboardWrites { get; set; } = false;
        
        /// <summary>
        /// Enable exclusive EC access diagnostic mode.
        /// When enabled, acquires and holds the EC mutex exclusively for diagnostic purposes.
        /// </summary>
        public static bool EnableExclusiveEcAccessDiagnostics { get; set; } = false;
        
        /// <summary>
        /// Tracks if we've already logged the EC contention warning this session.
        /// Prevents log spam when another app (like OmenMon) holds the EC mutex.
        /// </summary>
        public static bool EcContentionWarningLogged { get; set; } = false;

        // EC mutex to prevent concurrent access
        private static readonly Mutex EcMutex = new(false, @"Global\Access_EC");

        public bool IsAvailable => _handle != IntPtr.Zero && _moduleLoaded;

        public bool Initialize(string devicePath)
        {
            try
            {
                // Try bundled PawnIOLib.dll first (self-contained mode)
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string bundledLibPath = Path.Combine(appDir, "drivers", "PawnIOLib.dll");
                string? libPath = null;
                
                if (File.Exists(bundledLibPath))
                {
                    libPath = bundledLibPath;
                    System.Diagnostics.Debug.WriteLine($"[PawnIO] Using bundled PawnIOLib.dll");
                }
                else
                {
                    // Fall back to PawnIO installation
                    if (DriverInitializationHelper.IsPawnIOInstalled())
                    {
                        string? pawnIOPath = FindPawnIOInstallationPath();
                        if (pawnIOPath != null)
                        {
                            string installedLibPath = Path.Combine(pawnIOPath, "PawnIOLib.dll");
                            if (File.Exists(installedLibPath))
                            {
                                libPath = installedLibPath;
                                System.Diagnostics.Debug.WriteLine($"[PawnIO] Using installed PawnIOLib.dll from {pawnIOPath}");
                            }
                        }
                    }
                }
                
                if (libPath == null)
                {
                    System.Diagnostics.Debug.WriteLine("[PawnIO] PawnIOLib.dll not found (bundled or installed)");
                    return false;
                }

                _pawnIOLib = NativeMethods.LoadLibrary(libPath);
                if (_pawnIOLib == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"[PawnIO] Failed to load PawnIOLib.dll: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                // Resolve functions
                if (!ResolveFunctions())
                {
                    System.Diagnostics.Debug.WriteLine("[PawnIO] Failed to resolve PawnIOLib functions");
                    return false;
                }

                // Open PawnIO handle
                int hr = _pawnioOpen!(out _handle);
                if (hr < 0 || _handle == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"[PawnIO] Failed to open PawnIO: HRESULT 0x{hr:X8}");
                    return false;
                }

                // Load LpcACPIEC module
                if (!LoadEcModule())
                {
                    System.Diagnostics.Debug.WriteLine("[PawnIO] Failed to load LpcACPIEC module");
                    
                    // Check if this is a post-installation issue (PawnIO installed but module failed)
                    if (DriverInitializationHelper.IsPawnIOInstalled())
                    {
                        var warning = DriverInitializationHelper.GetPostInstallationRebootWarning("PawnIO", "LpcACPIEC");
                        System.Diagnostics.Debug.WriteLine($"[PawnIO] Post-install reboot warning: {warning}");
                    }
                    
                    _pawnioClose!(_handle);
                    _handle = IntPtr.Zero;
                    return false;
                }

                _moduleLoaded = true;
                System.Diagnostics.Debug.WriteLine("[PawnIO] Successfully initialized PawnIO EC access");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PawnIO] Initialization error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find PawnIO installation path. Uses centralized detection internally.
        /// </summary>
        private string? FindPawnIOInstallationPath()
        {
            try
            {
                // Check registry first (matches DriverInitializationHelper logic)
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (key != null)
                {
                    string? installLocation = key.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
                    {
                        return installLocation;
                    }
                }
            }
            catch (Exception ex)
            {
                // A registry read this can fail on is an unusual machine, and the fallback below
                // silently covers it - which is exactly why it is worth a line. Someone debugging
                // "No EC" wants to know the lookup threw rather than found nothing.
                System.Diagnostics.Debug.WriteLine($"[PawnIO] Install-path registry lookup failed: {ex.Message}");
            }

            // Fallback to default location
            string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
            if (Directory.Exists(defaultPath))
            {
                return defaultPath;
            }

            return null;
        }

        private bool ResolveFunctions()
        {
            IntPtr openPtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_open");
            IntPtr loadPtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_load");
            IntPtr executePtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_execute");
            IntPtr closePtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_close");

            if (openPtr == IntPtr.Zero || loadPtr == IntPtr.Zero || 
                executePtr == IntPtr.Zero || closePtr == IntPtr.Zero)
            {
                return false;
            }

            _pawnioOpen = Marshal.GetDelegateForFunctionPointer<PawnioOpen>(openPtr);
            _pawnioLoad = Marshal.GetDelegateForFunctionPointer<PawnioLoad>(loadPtr);
            _pawnioExecute = Marshal.GetDelegateForFunctionPointer<PawnioExecute>(executePtr);
            _pawnioClose = Marshal.GetDelegateForFunctionPointer<PawnioClose>(closePtr);

            return true;
        }

        private bool LoadEcModule()
        {
            try
            {
                // Try to load from application directory first (check both .bin and .amx extensions)
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] moduleNames = { "LpcACPIEC.bin", "LpcACPIEC.amx" };
                
                foreach (var moduleName in moduleNames)
                {
                    string modulePath = Path.Combine(appDir, "drivers", moduleName);
                    if (File.Exists(modulePath))
                    {
                        _lpcAcpiEcModule = File.ReadAllBytes(modulePath);
                        System.Diagnostics.Debug.WriteLine($"[PawnIO] Loaded module from: {modulePath}");
                        break;
                    }
                }

                // If not found in app directory, try PawnIO installation's modules directory
                if (_lpcAcpiEcModule == null || _lpcAcpiEcModule.Length == 0)
                {
                    string? pawnIOPath = FindPawnIOInstallationPath();
                    if (pawnIOPath != null)
                    {
                        foreach (var moduleName in moduleNames)
                        {
                            string installedModule = Path.Combine(pawnIOPath, "modules", moduleName);
                            if (File.Exists(installedModule))
                            {
                                _lpcAcpiEcModule = File.ReadAllBytes(installedModule);
                                System.Diagnostics.Debug.WriteLine($"[PawnIO] Loaded module from: {installedModule}");
                                break;
                            }
                        }
                    }
                }

                if (_lpcAcpiEcModule == null || _lpcAcpiEcModule.Length == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[PawnIO] LpcACPIEC module not found");
                    System.Diagnostics.Debug.WriteLine("[PawnIO] Download from: https://github.com/namazso/PawnIO.Modules/releases");
                    System.Diagnostics.Debug.WriteLine("[PawnIO] Place LpcACPIEC.bin in: C:\\Program Files\\PawnIO\\modules\\");
                    return false;
                }

                int hr = _pawnioLoad!(_handle, _lpcAcpiEcModule, (IntPtr)_lpcAcpiEcModule.Length);
                if (hr < 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[PawnIO] Failed to load module: HRESULT 0x{hr:X8}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PawnIO] Module load error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Maximum retry attempts for EC operations when another app (e.g., OmenMon) holds the mutex
        /// </summary>
        private const int EC_CONFLICT_MAX_RETRIES = 3;
        
        /// <summary>
        /// Delay between retry attempts in milliseconds
        /// </summary>
        private const int EC_CONFLICT_RETRY_DELAY_MS = 100;
        
        /// <summary>
        /// Event raised when EC conflict is detected (another app like OmenMon holds the mutex)
        /// </summary>
        public static event EventHandler<string>? EcConflictDetected;

        public byte ReadByte(ushort address)
        {
            return ReadByteWithRetry(address, EC_CONFLICT_MAX_RETRIES);
        }
        
        /// <summary>
        /// Read byte with retry logic for EC conflicts with other applications
        /// </summary>
        private byte ReadByteWithRetry(ushort address, int maxRetries)
        {
            EnsureAvailable();

            Exception? lastException = null;
            
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                bool gotMutex = false;
                try
                {
                    // Increase timeout on retries to give other apps time to release
                    int timeoutMs = attempt == 0 ? 200 : 500;
                    gotMutex = EcMutex.WaitOne(timeoutMs);
                    
                    if (!gotMutex)
                    {
                        if (attempt < maxRetries)
                        {
                            // Log conflict and retry
                            System.Diagnostics.Debug.WriteLine($"[PawnIO] EC mutex contention detected (attempt {attempt + 1}/{maxRetries + 1}), retrying...");
                            
                            // Raise event on first conflict detection
                            if (attempt == 0)
                            {
                                EcConflictDetected?.Invoke(null, "EC mutex acquisition failed - another application (possibly OmenMon) holds EC access");
                            }
                            
                            Thread.Sleep(EC_CONFLICT_RETRY_DELAY_MS);
                            continue;
                        }
                        
                        if (EnableExclusiveEcAccessDiagnostics)
                        {
                            throw new TimeoutException("EC mutex acquisition failed after retries - another application holds EC access (exclusive diagnostics mode)");
                        }
                        else
                        {
                            throw new TimeoutException("Failed to acquire EC mutex after multiple attempts - check if OmenMon or another EC app is running");
                        }
                    }

                    // Wait for IBF to clear
                    if (!WaitForInputBufferEmpty())
                    {
                        throw new TimeoutException("EC input buffer not empty");
                    }

                    // Send read command
                    WritePort(EC_CMD_PORT, EC_CMD_READ);

                    // Wait for IBF to clear
                    if (!WaitForInputBufferEmpty())
                    {
                        throw new TimeoutException("EC input buffer not empty after command");
                    }

                    // Send address
                    WritePort(EC_DATA_PORT, (byte)address);

                    // Wait for OBF to be set
                    if (!WaitForOutputBufferFull())
                    {
                        throw new TimeoutException("EC output buffer not full");
                    }

                    // Read data - success!
                    return ReadPort(EC_DATA_PORT);
                }
                catch (TimeoutException ex) when (attempt < maxRetries && !gotMutex)
                {
                    lastException = ex;
                    Thread.Sleep(EC_CONFLICT_RETRY_DELAY_MS);
                    continue;
                }
                finally
                {
                    if (gotMutex)
                    {
                        EcMutex.ReleaseMutex();
                    }
                }
            }
            
            // Should not reach here, but if it does, throw the last exception
            throw lastException ?? new TimeoutException("EC read failed after retries");
        }

        public void WriteByte(ushort address, byte value)
        {
            WriteByteWithRetry(address, value, EC_CONFLICT_MAX_RETRIES);
        }
        
        /// <summary>
        /// Write byte with retry logic for EC conflicts with other applications
        /// </summary>
        private void WriteByteWithRetry(ushort address, byte value, int maxRetries)
        {
            EnsureAvailable();

            // CRITICAL SAFETY CHECK: Only allow writes to pre-approved addresses
            // Also allow experimental keyboard addresses if explicitly enabled
            bool isAllowed = AllowedWriteAddresses.Contains(address) ||
                             (EnableExperimentalKeyboardWrites && ExperimentalKeyboardAddresses.Contains(address));
            
            if (!isAllowed)
            {
                var allowedList = string.Join(", ", AllowedWriteAddresses.Select(a => $"0x{a:X2}"));
                throw new UnauthorizedAccessException(
                    $"EC write to address 0x{address:X2} is blocked for safety. " +
                    $"Only approved addresses can be written. Allowed: {allowedList}");
            }

            Exception? lastException = null;
            
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                bool gotMutex = false;
                try
                {
                    // Increase timeout on retries to give other apps time to release
                    int timeoutMs = attempt == 0 ? 200 : 500;
                    gotMutex = EcMutex.WaitOne(timeoutMs);
                    
                    if (!gotMutex)
                    {
                        if (attempt < maxRetries)
                        {
                            // Log conflict and retry
                            System.Diagnostics.Debug.WriteLine($"[PawnIO] EC mutex contention detected on write (attempt {attempt + 1}/{maxRetries + 1}), retrying...");
                            
                            // Raise event on first conflict detection
                            if (attempt == 0)
                            {
                                EcConflictDetected?.Invoke(null, "EC mutex acquisition failed - another application (possibly OmenMon) holds EC access");
                            }
                            
                            Thread.Sleep(EC_CONFLICT_RETRY_DELAY_MS);
                            continue;
                        }
                        
                        if (EnableExclusiveEcAccessDiagnostics)
                        {
                            throw new TimeoutException("EC mutex acquisition failed after retries - another application holds EC access (exclusive diagnostics mode)");
                        }
                        else
                        {
                            throw new TimeoutException("Failed to acquire EC mutex after multiple attempts - check if OmenMon or another EC app is running");
                        }
                    }

                    // Wait for IBF to clear
                    if (!WaitForInputBufferEmpty())
                    {
                        throw new TimeoutException("EC input buffer not empty");
                    }

                    // Send write command
                    WritePort(EC_CMD_PORT, EC_CMD_WRITE);

                    // Wait for IBF to clear
                    if (!WaitForInputBufferEmpty())
                    {
                        throw new TimeoutException("EC input buffer not empty after command");
                    }

                    // Send address
                    WritePort(EC_DATA_PORT, (byte)address);

                    // Wait for IBF to clear
                    if (!WaitForInputBufferEmpty())
                    {
                        throw new TimeoutException("EC input buffer not empty after address");
                    }

                    // Send data
                    WritePort(EC_DATA_PORT, value);

                    // Small delay to let EC process
                    Thread.Sleep(1);
                    
                    // Success!
                    return;
                }
                catch (TimeoutException ex) when (attempt < maxRetries && !gotMutex)
                {
                    lastException = ex;
                    Thread.Sleep(EC_CONFLICT_RETRY_DELAY_MS);
                    continue;
                }
                finally
                {
                    if (gotMutex)
                    {
                        EcMutex.ReleaseMutex();
                    }
                }
            }
            
            // Should not reach here, but if it does, throw the last exception
            throw lastException ?? new TimeoutException("EC write failed after retries");
        }

        /// <summary>
        /// Addresses <see cref="HoldByteAndFire"/> may pin. Deliberately separate from
        /// <see cref="AllowedWriteAddresses"/>: that allowlist gates single, retried, mutex-scoped-
        /// per-call writes, which is a different and much lower-risk write pattern than holding the
        /// EC mutex across a spin loop plus a synchronous WMI call. Nothing should be added here
        /// without the same board-scoped, outcome-verified evidence <c>docs/8D87-OMEN-MAX-16-SUPPORT-PLAN.md</c>
        /// §5.2 requires for the two entries below - 0x59 (OGHP/DBST) and 0x90 (PROH) on board 8D87.
        /// </summary>
        private static readonly HashSet<ushort> AllowedHoldAddresses = new() { 0x59, 0x90 };

        /// <summary>
        /// Pin one EC byte to <paramref name="setBits"/> (preserving whatever <paramref name="preserveMask"/>
        /// marks as not-ours) for up to <paramref name="holdBudget"/>, then run <paramref name="duringHold"/>
        /// once while STILL holding the EC mutex and the pin, then restore the byte's original value
        /// and release.
        ///
        /// THE ONE THING THIS METHOD EXISTS TO FIX. <see cref="WriteByte"/> acquires <see cref="EcMutex"/>
        /// and releases it on every single call, and ends every write with <c>Thread.Sleep(1)</c> - which
        /// yields the rest of a scheduler quantum, commonly 1-15 ms. A caller that needs to hold one EC
        /// bit against the firmware's own ~100 ms reassertion cycle for a ~2 ms window, per
        /// <c>docs/8D87-OMEN-MAX-16-SUPPORT-PLAN.md</c> §5.1, cannot do that through per-call writes: the
        /// sleep alone can exceed the entire window. This acquires the mutex ONCE for the whole
        /// operation and spins on the raw port write with no sleep between iterations.
        ///
        /// <paramref name="duringHold"/> runs INSIDE the mutex hold, immediately after the pin loop, on
        /// purpose - per the design doc, the WMI trigger that reads this EC state through ACPI must fire
        /// while the byte is still freshly pinned, not after some other writer (fan control, tuning) has
        /// had a chance to run first. A synchronous WMI call and a raw EC port write are different
        /// transports with no shared lock, so holding <see cref="EcMutex"/> across it cannot deadlock -
        /// it only makes every OTHER EC consumer wait for the WMI round trip, same as they already wait
        /// for any other single EC operation.
        ///
        /// ORIGINAL VALUE IS ALWAYS RESTORED before the mutex is released, success or failure. This
        /// method does not decide what OGHP or PROH should end up holding long-term - a caller that wants
        /// the effect to persist is expected to have already gotten it (the WMI trigger's resulting
        /// firmware Notify is not retracted when the pin is released; see the design doc §1) rather than
        /// leaning on this method to leave a bit changed.
        /// </summary>
        /// <param name="address">Must be in <see cref="AllowedHoldAddresses"/>.</param>
        /// <param name="preserveMask">Bits of the original byte to keep untouched.</param>
        /// <param name="setBits">Bits to force to 1 outside <paramref name="preserveMask"/>.</param>
        /// <param name="holdBudget">Upper bound on the pin loop's own duration. A hard ceiling, not a
        /// target - the loop exits as soon as it is elapsed, it does not wait to fill it.</param>
        /// <param name="duringHold">Run once, synchronously, while the pin and the mutex are both still
        /// held. Its return value is this method's return value.</param>
        public T HoldByteAndFire<T>(ushort address, byte preserveMask, byte setBits, TimeSpan holdBudget, Func<T> duringHold)
        {
            if (duringHold == null) throw new ArgumentNullException(nameof(duringHold));
            if (!AllowedHoldAddresses.Contains(address))
            {
                throw new UnauthorizedAccessException(
                    $"EC address 0x{address:X2} is not on the hold-and-fire allowlist. This is a " +
                    "board-scoped, high-risk write pattern; see PawnIOEcAccess.AllowedHoldAddresses.");
            }

            EnsureAvailable();

            bool gotMutex = EcMutex.WaitOne(EC_TIMEOUT_MS * (EC_CONFLICT_MAX_RETRIES + 1));
            if (!gotMutex)
            {
                throw new TimeoutException(
                    "Failed to acquire EC mutex for a hold-and-fire operation - another application " +
                    "holds EC access.");
            }

            try
            {
                byte original = ReadByteRaw(address);
                byte pinned = (byte)((original & preserveMask) | setBits);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < holdBudget)
                {
                    WriteByteRaw(address, pinned);
                }

                try
                {
                    return duringHold();
                }
                finally
                {
                    // Restore even if duringHold threw - never leave the firmware's own EC RAM
                    // holding a value this process invented, only what firing the trigger needed.
                    WriteByteRaw(address, original);
                }
            }
            finally
            {
                EcMutex.ReleaseMutex();
            }
        }

        /// <summary>
        /// The command/address/data sequence <see cref="ReadByteWithRetry"/> uses, without the mutex
        /// acquire/release or the retry loop - the caller already holds the mutex for the whole
        /// operation. Never call this without holding <see cref="EcMutex"/> first.
        /// </summary>
        private byte ReadByteRaw(ushort address)
        {
            if (!WaitForInputBufferEmpty()) throw new TimeoutException("EC input buffer not empty");
            WritePort(EC_CMD_PORT, EC_CMD_READ);
            if (!WaitForInputBufferEmpty()) throw new TimeoutException("EC input buffer not empty after command");
            WritePort(EC_DATA_PORT, (byte)address);
            if (!WaitForOutputBufferFull()) throw new TimeoutException("EC output buffer not full");
            return ReadPort(EC_DATA_PORT);
        }

        /// <summary>
        /// The command/address/data sequence <see cref="WriteByteWithRetry"/> uses, without the mutex
        /// acquire/release, the retry loop, or the trailing <c>Thread.Sleep(1)</c> - that sleep is
        /// exactly what makes a tight pin loop impossible through the normal write path. Never call
        /// this without holding <see cref="EcMutex"/> first.
        /// </summary>
        private void WriteByteRaw(ushort address, byte value)
        {
            if (!WaitForInputBufferEmpty()) throw new TimeoutException("EC input buffer not empty");
            WritePort(EC_CMD_PORT, EC_CMD_WRITE);
            if (!WaitForInputBufferEmpty()) throw new TimeoutException("EC input buffer not empty after command");
            WritePort(EC_DATA_PORT, (byte)address);
            if (!WaitForInputBufferEmpty()) throw new TimeoutException("EC input buffer not empty after address");
            WritePort(EC_DATA_PORT, value);
        }

        private bool WaitForInputBufferEmpty()
        {
            int startTime = Environment.TickCount;
            while ((Environment.TickCount - startTime) < EC_TIMEOUT_MS)
            {
                byte status = ReadPort(EC_CMD_PORT);
                if ((status & EC_STATUS_IBF) == 0)
                {
                    return true;
                }
                Thread.SpinWait(EC_POLL_DELAY_US);
            }
            return false;
        }

        private bool WaitForOutputBufferFull()
        {
            int startTime = Environment.TickCount;
            while ((Environment.TickCount - startTime) < EC_TIMEOUT_MS)
            {
                byte status = ReadPort(EC_CMD_PORT);
                if ((status & EC_STATUS_OBF) != 0)
                {
                    return true;
                }
                Thread.SpinWait(EC_POLL_DELAY_US);
            }
            return false;
        }

        private byte ReadPort(ushort port)
        {
            ulong[] input = { port };
            ulong[] output = new ulong[1];

            int hr = _pawnioExecute!(_handle, "ioctl_pio_read", input, (IntPtr)1, output, (IntPtr)1, out IntPtr returnSize);
            if (hr < 0)
            {
                throw new InvalidOperationException($"PawnIO read failed: HRESULT 0x{hr:X8}");
            }

            return (byte)(output[0] & 0xFF);
        }

        private void WritePort(ushort port, byte value)
        {
            ulong[] input = { port, value };
            ulong[] output = Array.Empty<ulong>();

            int hr = _pawnioExecute!(_handle, "ioctl_pio_write", input, (IntPtr)2, output, IntPtr.Zero, out IntPtr returnSize);
            if (hr < 0)
            {
                throw new InvalidOperationException($"PawnIO write failed: HRESULT 0x{hr:X8}");
            }
        }

        private void EnsureAvailable()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PawnIOEcAccess));
            }
            if (!IsAvailable)
            {
                throw new InvalidOperationException("PawnIO EC access is not available");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_handle != IntPtr.Zero && _pawnioClose != null)
            {
                _pawnioClose(_handle);
                _handle = IntPtr.Zero;
            }

            if (_pawnIOLib != IntPtr.Zero)
            {
                NativeMethods.FreeLibrary(_pawnIOLib);
                _pawnIOLib = IntPtr.Zero;
            }

            _moduleLoaded = false;
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr LoadLibrary(string lpFileName);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool FreeLibrary(IntPtr hModule);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
            public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
        }
    }
}
