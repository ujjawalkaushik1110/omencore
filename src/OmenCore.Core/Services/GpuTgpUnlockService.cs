using System;
using System.Collections.Generic;
using OmenCore.Hardware;

namespace OmenCore.Services
{
    /// <summary>
    /// Lifts the GPU half of board 8D87's power clamp by holding two EC bits pinned while a WMI
    /// command runs, then letting go.
    ///
    /// THE MECHANISM, per <c>docs/8D87-OMEN-MAX-16-SUPPORT-PLAN.md</c> §1: three independent
    /// clamps sit between this board's RTX 5080 and 175 W. Stage 3 (the AMD SMU limits) is
    /// <see cref="ApuPowerClampService"/> and needs no EC write - the SMU does not fight back.
    /// Stages 1 and 2 are EC bits the firmware actively reasserts, and this class is both of them:
    ///
    ///   Stage 1  GPU 105 W -&gt; 175 W   OGHP  (EC 0x59 bit 1)  "OMEN Gaming Hub Present" - gates
    ///            whether NVPCF._DSM's configurable-TGP adder runs at all.
    ///   Stage 2  GPU 175 W -&gt;  35 W   PROH  (EC 0x90)        the EC's adapter verdict, reasserted
    ///            on its own ~100 ms cycle.
    ///
    /// A single <c>Default 0x22</c> WMI call (<see cref="HpWmiBios.SetGpuPower"/>) internally runs
    /// both _DSM (gated on OGHP) and, as a side effect of writing DSTA, _Q73 (which re-reads PROH) -
    /// so one call fired while BOTH bits are pinned engages both stages together. That call has to
    /// land while the pin is still live: the EC's own reassertion is what these bits are being held
    /// against, and letting go before firing it just hands the byte straight back.
    ///
    /// GENUINE, DOCUMENTED RISK, NOT A HYPOTHETICAL ONE. §5.2.3 of the design doc: a forced unlock
    /// on an undersized adapter put the GPU into a degraded state - <c>nvidia-smi</c> taking 23-27 s
    /// per call, unable to render - that persisted after the manipulation stopped and cleared only
    /// on reboot, on a machine with a history of power-related BSODs. This is why every entry point
    /// into this class requires an adapter that meets a high bar (see <see cref="SupplyMeetsSafetyBar"/>),
    /// why it is opt-in only, and why it never survives more than a session (see <see cref="Reassert"/>).
    ///
    /// NOTHING IN THIS CLASS IS CONFIRMED ON REAL HARDWARE. The design doc's own measurements are
    /// the evidence behind the byte offsets and timings; nobody has run this exact code on an 8D87.
    /// Treat every public method here as implemented-pending-confirmation, same bar as every other
    /// unconfirmed fix this project ships - it does not get a lower one for being higher-stakes.
    /// </summary>
    public sealed class GpuTgpUnlockService
    {
        /// <summary>
        /// Boards this class may run on at all. Exact match only - see design doc §5.2.4, "the same
        /// class of error as PowerLimitController's 0xC0-0xC5 and OmenMon-Reborn's 0x9F". Only 8D87
        /// has been measured; siblings named in the doc (8D88/8DD5/8DD6, believed to share the same
        /// DSDT) are deliberately NOT listed here until one of them has its own evidence - sharing a
        /// firmware base is a reason to expect the same offsets work, not proof they do.
        /// </summary>
        private static readonly HashSet<string> SupportedBoards = new(StringComparer.OrdinalIgnoreCase)
        {
            "8D87"
        };

        public static bool BoardIsSupported(string? productId) =>
            !string.IsNullOrEmpty(productId) && SupportedBoards.Contains(productId);

        /// <summary>
        /// Hard off-switch: <see cref="Engage"/> refuses, and the UI does not offer the unlock, while
        /// this is false. It stays false until all three of these are done on a real 8D87:
        ///
        ///   1. WRITES through the ACPI EC ports are shown to reach the MMIO window. Design doc §5.1
        ///      (T3.1) proved the ports alias it for READS only, on one adapter state, and §2 says in
        ///      so many words "nothing may write EC RAM that way yet". This class writes it.
        ///   2. The pin is held CONTINUOUSLY while GC22 runs. The EC clears OGHP on ~98% of 2 ms
        ///      cycles and the doc's own result is "won by repetition". This class pins for 2 ms,
        ///      stops, then fires, then restores the original byte - so OGHP is almost certainly
        ///      gone by the time the firmware reads it. It needs a pin thread that runs across the
        ///      WMI call, not before it.
        ///   3. Success is measured as the ENFORCED power limit (nvidia-smi enforced.power.limit
        ///      &gt;= 170 W), as the doc does. <see cref="Engage"/> reads NVAPI power DRAW, which on an
        ///      idle GPU stays low whether or not the limit moved, so it would report failure on
        ///      almost every attempt made outside a game.
        ///
        /// Found in review before 4.4.0 shipped. Nothing reached a release with this true.
        /// </summary>
        internal static readonly bool EcWritePathValidated = false;

        // EC 0x59: OGHP is bit 1. Bit 4 is DBST, which the design doc says this must not disturb -
        // it is a read-modify-write, not a value write. Every other bit is preserved too: nothing
        // here has decoded what they are, and "unknown" is not "safe to clear".
        internal const byte OghpAddress = 0x59;
        internal const byte OghpSetBit = 0x02;
        internal const byte OghpPreserveMask = unchecked((byte)~OghpSetBit); // 0xFD

        // EC 0x90: the design doc describes PROH as "Holding PROH = 1" with no bit-position detail
        // the way OGHP got - so this takes that literally as a whole-byte write, not a masked one.
        internal const byte ProhAddress = 0x90;
        internal const byte ProhValue = 0x01;
        internal const byte ProhPreserveMask = 0x00;

        // Design doc T3.4: "2 ms hold, 5000 ms settle before re-read. 5 ms hold fails." Read against
        // §5.1 ("EC clears it on 98% of 2 ms cycles... won by repetition"), the 2 ms is the RE-PIN
        // interval of a loop that keeps running across the GC22 call, not the total length of a pin
        // that ends before it - which is how this class currently uses it. See EcWritePathValidated.
        internal static readonly TimeSpan HoldBudget = TimeSpan.FromMilliseconds(2);
        internal static readonly TimeSpan SettleBeforeVerify = TimeSpan.FromMilliseconds(5000);

        // The bar a connected adapter must clear before this runs at all. 8D87 ships with a 330 W
        // adapter (design doc §3.5, Default 0x28 bytes 0-1); the doc measured 330 W, 280 W and 200 W
        // supplies (100% / 85% / 61%), and the degraded-GPU failure (§5.2.3) was a forced unlock on
        // an under-rated supply. At 90% only a full-rated adapter passes - which also means the
        // PROH half does nothing useful when it does (PROH only clamps on non-330 W supplies, §1).
        // The under-rated case the feature exists for is deliberately excluded until a proportional
        // cap (design doc T3.5) exists.
        internal const double MinimumSupplyFraction = 0.90;

        /// <summary>Delivered watts this counts as "the unlock took". Comfortably below the
        /// measured 175 W ceiling and comfortably above the 105 W Stage-1-only baseline, so
        /// ordinary NVAPI percentage-estimate noise cannot cross it by accident either way.</summary>
        internal const double VerifiedDeliveredWattsFloor = 150.0;

        private readonly LoggingService _logging;
        private readonly HpWmiBios _wmiBios;
        private readonly NvapiService _nvapi;
        private readonly object _sync = new();
        private bool _lastEngageSucceeded;

        public GpuTgpUnlockService(LoggingService logging, HpWmiBios wmiBios, NvapiService nvapi)
        {
            _logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _wmiBios = wmiBios ?? throw new ArgumentNullException(nameof(wmiBios));
            _nvapi = nvapi ?? throw new ArgumentNullException(nameof(nvapi));
        }

        /// <summary>True once <see cref="Engage"/> has verified the unlock took. Reset to false by
        /// any subsequent event this class knows can take it back again.</summary>
        public bool IsEngaged { get { lock (_sync) { return _lastEngageSucceeded; } } }

        public enum Outcome
        {
            /// <summary>Ran, and delivered watts confirmed the unlock took.</summary>
            Verified,
            /// <summary>Ran, but delivered watts never crossed <see cref="VerifiedDeliveredWattsFloor"/>.
            /// Per design doc §5.2.1, a status code is not evidence here - this is the honest result
            /// whenever the mailbox/EC accepted everything and nothing measurable changed.</summary>
            RanButUnverified,
            /// <summary>Refused before touching the EC - wrong board, adapter below the safety bar,
            /// or no EC access.</summary>
            Refused
        }

        public sealed record Result(Outcome Outcome, string Message);

        /// <summary>
        /// Whether this adapter clears the bar in <see cref="MinimumSupplyFraction"/>. Exposed
        /// separately from <see cref="Engage"/> so a caller (a settings toggle) can explain why the
        /// option is greyed out without having to attempt anything.
        /// </summary>
        public static bool SupplyMeetsSafetyBar(HpWmiBios.AdapterInfo adapter, int shippingRequirementWatts, out string reason)
        {
            if (adapter.Status == HpWmiBios.SmartAdapterStatus.ConnectedTypeC)
            {
                reason = "This is a USB-C Power Delivery supply. The GPU power unlock is not offered " +
                         "there - it was only ever measured on barrel adapters.";
                return false;
            }

            if (!adapter.PowerRatingKnown || adapter.PowerRatingWatts <= 0 || shippingRequirementWatts <= 0)
            {
                reason = "The connected adapter's rating was not reported, so there is no way to judge " +
                         "whether it can carry the unlock safely.";
                return false;
            }

            if (adapter.PowerRatingWatts < shippingRequirementWatts * MinimumSupplyFraction)
            {
                var floorWatts = (int)Math.Ceiling(shippingRequirementWatts * MinimumSupplyFraction);
                reason = $"The connected {adapter.PowerRatingWatts} W adapter is below the {floorWatts} W " +
                         $"this needs on a {shippingRequirementWatts} W machine. Forcing this unlock on an " +
                         "undersized supply has been observed to leave the GPU in a degraded state that " +
                         "only cleared on reboot - this stays off rather than risk that.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Run the unlock once. Caller must already have gotten explicit opt-in from the user - this
        /// method does not ask, it only re-checks the board and adapter gates and then acts.
        /// </summary>
        public Result Engage(string? productId, PawnIOEcAccess? ecAccess, HpWmiBios.AdapterInfo? adapter)
        {
            lock (_sync)
            {
                _lastEngageSucceeded = false;

                if (!EcWritePathValidated)
                {
                    return new Result(Outcome.Refused,
                        "The GPU power unlock is disabled in this build: its EC write path has not been " +
                        "validated on real 8D87 hardware yet.");
                }

                if (!BoardIsSupported(productId))
                {
                    return new Result(Outcome.Refused,
                        $"The GPU power unlock is only implemented for board 8D87; this machine resolved as " +
                        $"'{productId ?? "unknown"}'.");
                }

                if (ecAccess == null || !ecAccess.IsAvailable)
                {
                    return new Result(Outcome.Refused, "No EC access is available - the unlock needs PawnIO.");
                }

                if (adapter is not HpWmiBios.AdapterInfo info)
                {
                    return new Result(Outcome.Refused, "The connected adapter's power verdict could not be read.");
                }

                var shipping = _wmiBios.SystemDesign?.ShippingAdapterPowerRatingWatts ?? 0;
                if (!SupplyMeetsSafetyBar(info, shipping, out var reason))
                {
                    return new Result(Outcome.Refused, reason);
                }

                try
                {
                    // Nested, not sequential: the outer hold's mutex ownership (a named Windows
                    // Mutex, reentrant on the owning thread) is still held when the inner one
                    // acquires it again, so both bytes are pinned simultaneously across the one WMI
                    // call that needs to see them both unlocked at once.
                    var sent = ecAccess.HoldByteAndFire(OghpAddress, OghpPreserveMask, OghpSetBit, HoldBudget, () =>
                        ecAccess.HoldByteAndFire(ProhAddress, ProhPreserveMask, ProhValue, HoldBudget, () =>
                            _wmiBios.SetGpuPower(HpWmiBios.GpuPowerLevel.Maximum)));

                    _logging.Info($"[GpuTgpUnlock] Stage 1+2 pin-and-fire sent (GC22 reported {sent}); " +
                                  $"waiting {SettleBeforeVerify.TotalSeconds:F0}s before verifying by delivered watts.");
                }
                catch (Exception ex)
                {
                    _logging.Warn($"[GpuTgpUnlock] Engage failed before verification: {ex.Message}");
                    return new Result(Outcome.Refused, $"The EC write failed: {ex.Message}");
                }
            }

            // Deliberately outside the lock and outside the try above: design doc §5.2.1, verify by
            // outcome, never by the request. A 5-second sleep on a caller-owned background thread is
            // the doc's own measured settle time, not a guess.
            System.Threading.Thread.Sleep(SettleBeforeVerify);
            var delivered = _nvapi.GetGpuPowerWatts();
            var verified = delivered >= VerifiedDeliveredWattsFloor;

            lock (_sync) { _lastEngageSucceeded = verified; }

            return verified
                ? new Result(Outcome.Verified,
                    $"Confirmed by delivered power: the GPU is drawing {delivered:F0} W.")
                : new Result(Outcome.RanButUnverified,
                    $"The EC write and the WMI trigger both completed, but delivered power reads only " +
                    $"{delivered:F0} W - below the {VerifiedDeliveredWattsFloor:F0} W this counts the unlock " +
                    "as having taken. Per how this mechanism was measured, that most likely means the pin " +
                    "was gone before the firmware's read landed inside it, not that the write failed outright.");
        }

        /// <summary>
        /// Whether an event this class knows can revert the unlock has happened, so a caller should
        /// re-run <see cref="Engage"/> if it still wants the unlock held. Design doc §5.2.2: "no
        /// stage survives a reboot... a supervised re-applier with hooks on adapter change, resume,
        /// and a slow watchdog. Not a button." This class does not own those hooks - the app already
        /// has them (see <c>PowerAutomationService.PowerStateChanged</c>, used the same way by
        /// <see cref="ApuPowerClampService"/>'s caller) - it only marks itself no-longer-engaged so
        /// the caller knows to act on them.
        /// </summary>
        public void Reassert() { lock (_sync) { _lastEngageSucceeded = false; } }
    }
}
