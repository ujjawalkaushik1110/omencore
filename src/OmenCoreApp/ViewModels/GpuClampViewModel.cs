using OmenCore.Hardware;
using OmenCore.Models;
using OmenCore.Services;
using OmenCore.Services.Diagnostics;
using OmenCore.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace OmenCore.ViewModels
{
    /// <summary>
    /// GPU power-limit / adapter power-override clamp — extracted out of MainViewModel since it's
    /// a large, self-contained feature: reading the GPU's actual enforced power limit, a "power
    /// adapter" verdict/explanation (whether the connected charger meets the GPU's needs), an
    /// adapter override that restarts the GPU driver for a fresh verdict, a related AMD CPU power
    /// clamp (STAPM/APU wattage), and an "automatic clamp lift" that watches for the clamp being
    /// released and reapplies settings.
    /// </summary>
    public class GpuClampViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly LoggingService _logging;
        private readonly ConfigurationService _configService;
        private readonly AppConfig _config;
        private readonly HpWmiBios _wmiBios;
        private readonly UndervoltService _undervoltService;
        private readonly NotificationService _notificationService;
        private readonly PowerAutomationService? _powerAutomationService;
        private readonly NvapiService? _nvapiService;
        private readonly string? _productId;

        public event PropertyChangedEventHandler? PropertyChanged;

        public GpuClampViewModel(
            LoggingService logging,
            ConfigurationService configService,
            AppConfig config,
            HpWmiBios wmiBios,
            UndervoltService undervoltService,
            NotificationService? notificationService = null,
            PowerAutomationService? powerAutomationService = null,
            NvapiService? nvapiService = null,
            string? productId = null)
        {
            _logging = logging;
            _configService = configService;
            _config = config;
            _wmiBios = wmiBios;
            _undervoltService = undervoltService;
            _notificationService = notificationService ?? new NotificationService(_logging);
            _powerAutomationService = powerAutomationService;
            _nvapiService = nvapiService;
            _productId = productId;

            _adapterOverrideService = new AdapterPowerOverrideService(_logging);

            if (_powerAutomationService != null)
            {
                _powerAutomationService.PowerStateChanged += OnPowerStateChangedForClampLift;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════
        // Power adapter (Legacy 0x0F) - read-only, refreshed when the Diagnostics tab opens
        // ═══════════════════════════════════════════════════════════════════════════════════

        private HpWmiBios.AdapterInfo? _adapterInfo;

        /// <summary>
        /// True when the board answered the adapter query at least once. Boards that don't implement
        /// it keep the whole panel hidden rather than showing an empty or misleading one.
        /// </summary>
        public bool HasPowerAdapterInfo => _adapterInfo is HpWmiBios.AdapterInfo info && info.HasVerdict;

        /// <summary>
        /// One-line adapter summary, e.g. "330 W - meets this machine's requirement".
        /// </summary>
        public string PowerAdapterSummary
        {
            get
            {
                if (_adapterInfo is not HpWmiBios.AdapterInfo info)
                    return "Not reported by this board";

                // On battery there is no adapter to describe, and the wattage byte reads 0 - pairing
                // that with "running on battery" would render as the nonsensical "0 W - running on
                // battery".
                if (info.Status == HpWmiBios.SmartAdapterStatus.BatteryPower)
                    return "None — running on battery";

                var watts = info.PowerRatingKnown
                    ? $"{info.PowerRatingWatts} W"
                    : "Wattage not reported";

                var verdict = info.Status switch
                {
                    HpWmiBios.SmartAdapterStatus.MeetsRequirement => "meets this machine's requirement",
                    HpWmiBios.SmartAdapterStatus.BelowRequirement => "below this machine's requirement",
                    HpWmiBios.SmartAdapterStatus.BatteryPower => "running on battery",
                    HpWmiBios.SmartAdapterStatus.NotFunctioning => "not functioning correctly",
                    HpWmiBios.SmartAdapterStatus.ConnectedTypeC => "USB-C Power Delivery",
                    HpWmiBios.SmartAdapterStatus.NotSupported => "adapter detection not supported",
                    _ => info.Status.ToString()
                };

                return $"{watts} — {verdict}";
            }
        }

        /// <summary>
        /// The wattage this SKU shipped with, when the firmware reports it. Shown next to the
        /// connected wattage so the user can see both halves of the comparison the firmware makes.
        /// </summary>
        public string PowerAdapterRequirement
        {
            get
            {
                var shipping = _wmiBios?.SystemDesign?.ShippingAdapterPowerRatingWatts ?? 0;
                return shipping > 0 ? $"{shipping} W" : "Not reported";
            }
        }

        /// <summary>
        /// A plain explanation when the supply is under-rated, or null when there is nothing to say.
        ///
        /// This exists because the symptom is otherwise unattributable: on boards that clamp GPU power
        /// on an under-rated supply, the user sees a GPU pinned far below its rating and nothing
        /// anywhere in the app connects that to the adapter they plugged in.
        /// </summary>
        public string? PowerAdapterExplanation
        {
            get
            {
                if (_adapterInfo is not HpWmiBios.AdapterInfo info || !info.HasVerdict)
                    return null;

                if (info.Status == HpWmiBios.SmartAdapterStatus.BatteryPower)
                {
                    return "Running on battery. Expect reduced CPU and GPU power limits until an adapter is connected.";
                }

                if (!info.IsLowWattage)
                    return null;

                var shipping = _wmiBios?.SystemDesign?.ShippingAdapterPowerRatingWatts ?? 0;
                var connected = info.PowerRatingKnown ? $"{info.PowerRatingWatts} W" : "an unrecognised";
                var required = shipping > 0 ? $" This machine shipped with a {shipping} W adapter." : string.Empty;

                // Attribute the verdict to whoever actually reached it. On a barrel adapter the
                // firmware says BelowRequirement outright and quoting it is accurate. On USB-C it
                // says ConnectedTypeC - a description of the supply, not a complaint about it - and
                // the under-rated judgement comes from HP's rule about the chassis rather than from
                // the firmware. Saying "the firmware reports it as below requirement" there states
                // something the firmware did not say, in the one panel a user opens to find out what
                // the firmware said.
                string verdict;
                if (info.Status == HpWmiBios.SmartAdapterStatus.ConnectedTypeC)
                {
                    var supply = info.PowerRatingKnown
                        ? $"This machine is running from a {info.PowerRatingWatts} W USB-C Power Delivery supply. "
                        : "This machine is running from a USB-C Power Delivery supply of unreported wattage. ";

                    // Two different reasons reach "under-rated" on Type-C, and they are not
                    // interchangeable. Either the chassis advertises a USB-C design rating and this
                    // supply came in under it, or it advertises none at all and expects a barrel
                    // adapter - in which case no USB-C source would satisfy it, whatever it offers.
                    var reason = info.UsbcDesignRatingWatts > 0
                        ? $"That is below the {info.UsbcDesignRatingWatts} W this machine's USB-C input is designed for."
                        : "This chassis expects a barrel adapter and advertises no USB-C design rating, so HP's " +
                          "own rule treats any USB-C source as below its requirement.";

                    verdict = supply + "The firmware describes it as USB-C rather than calling it " +
                              $"under-rated. {reason}{required}";
                }
                else
                {
                    verdict = $"The firmware reports {connected} adapter as below this machine's requirement.{required}";
                }

                // Deliberately does not say the platform is "protecting" the supply, which is what
                // this used to say and is not what was measured. On board 8D87 the hardware power
                // brake never asserted in any of 131 load samples, battery rate held at exactly 0 W
                // with the clamp on, and the clamp is not even proportional to the shortfall. It is
                // an advertised number, and telling a user their machine is defending itself sends
                // them to buy a power supply they may well not need.
                return verdict + " " +
                       "Some HP firmware reduces CPU and GPU power limits in this state, which can look like a fault " +
                       "in OmenCore or in the GPU driver but is a policy the firmware applies to a supply it does not " +
                       "rate highly enough - a limit it advertises, rather than a measurement of what the supply can " +
                       "deliver. If performance is lower than expected, this is the first thing to rule out.";
            }
        }

        /// <summary>True when <see cref="PowerAdapterExplanation"/> has something to show.</summary>
        public bool HasPowerAdapterExplanation => PowerAdapterExplanation != null;

        // ── The override ─────────────────────────────────────────────────────────────────────────
        //
        // Offered only where there is something to undo: an under-rated supply, on a machine with a
        // dGPU to restart. On a supply that meets the requirement there is no clamp to discard and
        // restarting the device would cost a black screen for nothing.

        private readonly AdapterPowerOverrideService _adapterOverrideService;

        private string _adapterOverrideStatus = string.Empty;
        private bool _adapterOverrideBusy;

        // Built on first use rather than in the constructor. A field initializer cannot reference
        // instance members (CS0236), and initialising it in the constructor body would leave a window
        // where RefreshPowerAdapterStatus() - which the Diagnostics view is free to call - touches a
        // null. Lazy removes the ordering question rather than answering it.
        private AsyncRelayCommand? _applyAdapterOverrideCommand;

        /// <summary>
        /// Whether to offer the override at all. Kept separate from whether it can run, so a machine
        /// that qualifies but is missing something gets a disabled button with a reason rather than
        /// no button and no explanation.
        /// </summary>
        public bool CanOfferAdapterOverride =>
            _adapterInfo is HpWmiBios.AdapterInfo info
            && info.HasVerdict
            && info.IsLowWattage
            && _adapterOverrideService.FindDiscreteGpu() != null;

        // ── What the GPU is limited to right now ─────────────────────────────────────────────────
        //
        // The override's entire claim is that the card is being held below its own limit, and the
        // price of testing that claim is a black screen and every GPU context on the machine. So the
        // number goes in front of the button, not behind it: with it, "is there anything to gain
        // here" is answered before anyone presses anything.
        //
        // Two figures, because one alone says nothing. `enforced.power.limit` is what the clamp
        // moves; `power.default_limit` is the card's own limit, which the clamp leaves alone. On
        // board 8D87 that reads 35 W against 80 W while clamped and 80 W against 80 W once the driver
        // has restarted without the verdict - so the gap between them, not the absolute number, is
        // the evidence.
        //
        // Read on opening the panel, never on a timer, and never while the dGPU is parked: nvidia-smi
        // answers by waking the card and holding it awake for the driver's inactivity timeout. A
        // parked GPU gets a button and the cost stated instead.

        private AdapterPowerOverrideService.PowerLimits? _gpuPowerLimits;
        private DateTime? _gpuPowerLimitReadAt;
        private bool _gpuPowerLimitBusy;
        private bool _gpuPowerLimitUnanswered;

        private AsyncRelayCommand? _checkGpuPowerLimitCommand;

        /// <summary>True once a reading has been taken and the enforced limit is known.</summary>
        public bool HasGpuPowerLimitReading => _gpuPowerLimits?.EnforcedWatts is not null;

        /// <summary>
        /// The reading itself, with the time it was taken. The time is not decoration: the clamp
        /// comes back whenever the firmware tells the driver about the adapter again, so a reading is
        /// a statement about a moment and a panel that implied otherwise would be lying by omission.
        /// </summary>
        public string GpuPowerLimitSummary
        {
            get
            {
                if (_gpuPowerLimits?.EnforcedWatts is not double enforced) return string.Empty;

                var reference = _gpuPowerLimits.DefaultWatts is double standard
                    ? $" of this card's own {standard:0.#} W limit"
                    : string.Empty;

                var when = _gpuPowerLimitReadAt is DateTime taken
                    ? $" — read at {taken:HH:mm}"
                    : string.Empty;

                return $"{enforced:0.#} W{reference}{when}";
            }
        }

        /// <summary>
        /// Whether that reading looks like the adapter clamp, in the terms the evidence supports.
        ///
        /// Stated as what it is consistent with rather than as a diagnosis. A GPU held below its own
        /// default limit is exactly what the clamp looks like from here, and it is also what a
        /// third-party tuning tool's power limit looks like from here; this panel cannot tell them
        /// apart and does not pretend to.
        /// </summary>
        public string GpuPowerLimitAttribution
        {
            get
            {
                if (_gpuPowerLimits is not AdapterPowerOverrideService.PowerLimits limits ||
                    limits.EnforcedWatts is not double enforced)
                {
                    return string.Empty;
                }

                if (limits.DefaultWatts is not double standard)
                {
                    return "Whether that is being held down cannot be told from here - nvidia-smi did " +
                           "not report this card's default limit, so there is nothing to compare it to.";
                }

                if (!limits.EnforcedIsBelowDefault)
                {
                    return "That is the card's own limit, so nothing is holding it down at the moment " +
                           "and the restart below has no clamp to discard.";
                }

                return $"Something is holding this card {standard - enforced:0.#} W below its own limit. " +
                       "With an under-rated supply attached, that is what the firmware's clamp looks " +
                       "like, and it is what the restart below discards. Another tool that sets a GPU " +
                       "power limit would look the same from here, and restarting the device would not " +
                       "clear that one.";
            }
        }

        public bool HasGpuPowerLimitAttribution => GpuPowerLimitAttribution.Length > 0;

        /// <summary>
        /// What to say when there is no reading: why not, and what taking one would cost. Empty once
        /// a reading exists.
        /// </summary>
        public string GpuPowerLimitPrompt
        {
            get
            {
                if (_gpuPowerLimitBusy) return "Reading the current limit...";
                if (HasGpuPowerLimitReading) return string.Empty;

                if (!_adapterOverrideService.CanReadPowerLimit)
                {
                    return "The current limit cannot be read here: nvidia-smi is not installed, and it " +
                           "is the only thing that reports it. It ships with the NVIDIA driver.";
                }

                if (_gpuPowerLimitUnanswered)
                {
                    return "The GPU did not report its power limit. It may have been busy waking up - " +
                           "try again in a moment.";
                }

                return "The GPU is parked and asleep. Reading its power limit wakes it, and the driver " +
                       "keeps it awake for a couple of minutes afterwards.";
            }
        }

        public bool HasGpuPowerLimitPrompt => GpuPowerLimitPrompt.Length > 0;

        /// <summary>Label that says which of the two things the button does.</summary>
        public string CheckGpuPowerLimitLabel =>
            HasGpuPowerLimitReading ? "Read the limit again" : "Read the current limit";

        public bool CanCheckGpuPowerLimit =>
            !_gpuPowerLimitBusy && _adapterOverrideService.CanReadPowerLimit && !_adapterOverrideBusy;

        /// <summary>
        /// Take a reading on request. Also the way back from a parked GPU, where nothing is read
        /// automatically - the user pressing this is the consent for the wake.
        /// </summary>
        public ICommand CheckGpuPowerLimitCommand =>
            _checkGpuPowerLimitCommand ??=
                new AsyncRelayCommand(_ => ReadGpuPowerLimitAsync(onlyIfAwake: false),
                                      _ => CanCheckGpuPowerLimit);

        private async Task ReadGpuPowerLimitAsync(bool onlyIfAwake)
        {
            if (_gpuPowerLimitBusy || !_adapterOverrideService.CanReadPowerLimit)
            {
                OnGpuPowerLimitChanged();
                return;
            }

            // Only the automatic read defers to a parked GPU. A user who pressed the button has been
            // told what it costs and has answered.
            if (onlyIfAwake && _adapterOverrideService.DiscreteGpuIsParked())
            {
                OnGpuPowerLimitChanged();
                return;
            }

            SetGpuPowerLimitBusy(true);

            try
            {
                // Off the UI thread: nvidia-smi takes milliseconds on a woken card and tens of
                // seconds on one that is coming back, and the panel stays live either way.
                var limits = await Task.Run(() => _adapterOverrideService.ReadPowerLimitsWatts())
                                       .ConfigureAwait(true);

                if (limits.EnforcedWatts is null)
                {
                    _gpuPowerLimitUnanswered = true;
                }
                else
                {
                    _gpuPowerLimits = limits;
                    _gpuPowerLimitReadAt = DateTime.Now;
                    _gpuPowerLimitUnanswered = false;
                }
            }
            catch (Exception ex)
            {
                _logging.Debug($"GPU power limit read failed: {ex.Message}");
                _gpuPowerLimitUnanswered = true;
            }
            finally
            {
                SetGpuPowerLimitBusy(false);
            }
        }

        private void SetGpuPowerLimitBusy(bool busy)
        {
            _gpuPowerLimitBusy = busy;
            OnGpuPowerLimitChanged();
        }

        private void OnGpuPowerLimitChanged()
        {
            OnPropertyChanged(nameof(HasGpuPowerLimitReading));
            OnPropertyChanged(nameof(GpuPowerLimitSummary));
            OnPropertyChanged(nameof(GpuPowerLimitAttribution));
            OnPropertyChanged(nameof(HasGpuPowerLimitAttribution));
            OnPropertyChanged(nameof(GpuPowerLimitPrompt));
            OnPropertyChanged(nameof(HasGpuPowerLimitPrompt));
            OnPropertyChanged(nameof(CheckGpuPowerLimitLabel));
            OnPropertyChanged(nameof(CanCheckGpuPowerLimit));
            _checkGpuPowerLimitCommand?.RaiseCanExecuteChanged();
        }

        // ── The CPU half of the same clamp ───────────────────────────────────────────────────────
        //
        // The under-rated supply clamps the APU as well as the GPU, and the two are separate
        // mechanisms: the GPU's arrives as a driver notification a device restart discards, the
        // CPU's arrives as three of the four AMD SMU power limits pinned at 25 W. Lifting one and
        // not the other leaves the machine half fixed, which is how this looked for a while - the
        // GPU going 35 W to 80 W while the CPU sat at 25 W with nothing in the app connecting the
        // two. So the button does both, and says which parts of it applied.
        //
        // Narrower gating than the GPU restart, because it needs three things the restart does not:
        // an AMD part whose SMU message IDs are known rather than guessed, a firmware that states
        // its own CPU-with-GPU budget to use as the target, and a supply with room for the result.

        private ApuPowerClampService? _apuClampService;
        private string _apuClampStatus = string.Empty;

        /// <summary>
        /// Built on first use and only on a part that has one. Constructing it means touching the
        /// SMU provider, which a machine with no AMD APU has no reason to do.
        /// </summary>
        private ApuPowerClampService? ApuClamp
        {
            get
            {
                if (_apuClampService != null) return _apuClampService;

                if (_undervoltService?.Provider is not AmdUndervoltProvider amd) return null;

                _apuClampService = new ApuPowerClampService(_logging, amd);
                return _apuClampService;
            }
        }

        /// <summary>
        /// What the four limits will be raised to, and where that number came from. The STAPM value
        /// set in AMD CPU Power Limits if there is one, otherwise the firmware's own CPU-with-GPU
        /// budget - see <see cref="ApuPowerClampService.TargetFor"/>.
        /// </summary>
        public ApuPowerClampService.Target ApuClampTarget =>
            ApuPowerClampService.TargetFor(_config.AmdPowerLimits, _wmiBios?.SystemDesign);

        public uint ApuClampTargetWatts => ApuClampTarget.Watts;

        /// <summary>
        /// Names the number and its origin in one phrase, so the panel can say "the 51 W you set"
        /// rather than presenting a figure that appears from nowhere and disagrees with a slider
        /// two tabs away.
        /// </summary>
        public string ApuClampTargetDescription
        {
            get
            {
                var target = ApuClampTarget;
                return target.Source switch
                {
                    ApuPowerClampService.TargetSource.UserSetting =>
                        $"the {target.Watts} W you set in AMD CPU Power Limits",
                    ApuPowerClampService.TargetSource.Firmware =>
                        $"the {target.Watts} W this machine's firmware states for a loaded CPU and GPU",
                    _ => string.Empty
                };
            }
        }

        /// <summary>
        /// Whether the CPU half will be attempted alongside the GPU restart.
        /// </summary>
        public bool CanOfferApuClampLift =>
            CanOfferAdapterOverride
            && ApuClamp != null
            && ApuClampTargetWatts > 0
            && ApuPowerClampService.CpuIsSupported(RyzenControl.Family)
            && _adapterInfo is HpWmiBios.AdapterInfo info
            && ApuPowerClampService.SupplyCanCarryIt(
                   info, _wmiBios?.SystemDesign?.ShippingAdapterPowerRatingWatts ?? 0, out _);

        /// <summary>
        /// What the button will do, in one sentence, before it is pressed.
        ///
        /// Says explicitly when only the GPU half applies. A user on a supply this refuses to raise
        /// the CPU limit on would otherwise press a button whose heading mentions the CPU and get a
        /// result that does not, with nothing saying why.
        /// </summary>
        public string AdapterOverrideScope
        {
            get
            {
                if (!CanOfferAdapterOverride) return string.Empty;

                if (CanOfferApuClampLift)
                {
                    return "This restarts the GPU to drop its clamp, and raises the four CPU power " +
                           $"limits to {ApuClampTargetDescription}.";
                }

                var why = ApuClampReason();
                return "This restarts the GPU to drop its clamp. The CPU side is not included" +
                       (why.Length > 0 ? $": {why}" : ".");
            }
        }

        public bool HasAdapterOverrideScope => AdapterOverrideScope.Length > 0;

        /// <summary>Why the CPU half is not on offer, or empty when it is.</summary>
        private string ApuClampReason()
        {
            if (ApuClamp == null || !ApuPowerClampService.CpuIsSupported(RyzenControl.Family))
            {
                return "this CPU is not one whose SMU power-limit messages are known";
            }

            if (ApuClampTargetWatts == 0)
            {
                return "there is no CPU power limit to aim at - set one in AMD CPU Power Limits, or " +
                       "this machine's firmware would have to report its own";
            }

            if (_adapterInfo is HpWmiBios.AdapterInfo info &&
                !ApuPowerClampService.SupplyCanCarryIt(
                    info, _wmiBios?.SystemDesign?.ShippingAdapterPowerRatingWatts ?? 0, out var reason))
            {
                // The service phrases these as whole sentences for a panel of their own; folded into
                // the middle of one here, so the leading capital has to go.
                return char.ToLowerInvariant(reason[0]) + reason[1..];
            }

            return string.Empty;
        }

        /// <summary>True while OmenCore will put the four SMU limits back after an event.</summary>
        public bool IsApuClampHeld => _apuClampService?.IsHeld == true;

        /// <summary>What the CPU half did, or empty before it has been tried.</summary>
        public string ApuClampStatus
        {
            get => _apuClampStatus;
            private set
            {
                if (_apuClampStatus == value) return;
                _apuClampStatus = value;
                OnPropertyChanged(nameof(ApuClampStatus));
                OnPropertyChanged(nameof(HasApuClampStatus));
            }
        }

        public bool HasApuClampStatus => _apuClampStatus.Length > 0;

        private RelayCommand? _releaseApuClampCommand;

        /// <summary>
        /// Stop putting the CPU limits back. Not an undo - it cannot restore the clamp, and does
        /// not try. It stops OmenCore writing at all, and the platform reclaims the registers on the
        /// next event that touches them.
        /// </summary>
        public ICommand ReleaseApuClampCommand =>
            _releaseApuClampCommand ??= new RelayCommand(_ =>
            {
                _apuClampService?.Release();
                ApuClampStatus = "OmenCore will no longer put the CPU power limits back. The " +
                                 "platform reclaims them on the next resume or change of supply; a " +
                                 "reboot restores stock.";
                OnPropertyChanged(nameof(IsApuClampHeld));
            }, _ => IsApuClampHeld);

        // ── The 8D87 GPU TGP unlock ──────────────────────────────────────────────────────────────
        //
        // A different mechanism from the adapter override above, and a different risk class.
        // The override restarts the GPU driver so it re-reads a limit the firmware already applied;
        // this pins two EC bits the firmware is actively reasserting and fires a WMI command while
        // they are held, per docs/8D87-OMEN-MAX-16-SUPPORT-PLAN.md §5. NOT CONFIRMED ON REAL
        // HARDWARE - see GpuTgpUnlockService's own remarks. Offered only on the one board it was
        // measured on, and only when the connected adapter clears a stricter bar than the CPU clamp
        // above uses, because a forced unlock on an undersized supply has been observed to leave the
        // GPU degraded until reboot.

        private GpuTgpUnlockService? _gpuTgpUnlockService;

        private GpuTgpUnlockService? GpuTgpUnlock
        {
            get
            {
                if (_gpuTgpUnlockService != null) return _gpuTgpUnlockService;
                if (!GpuTgpUnlockService.EcWritePathValidated) return null;
                if (_nvapiService == null) return null;
                if (!GpuTgpUnlockService.BoardIsSupported(_productId)) return null;

                _gpuTgpUnlockService = new GpuTgpUnlockService(_logging, _wmiBios, _nvapiService);
                return _gpuTgpUnlockService;
            }
        }

        /// <summary>Whether this board and the connected adapter both clear the bar for this
        /// specific unlock - independent of <see cref="CanOfferAdapterOverride"/>, which uses a
        /// looser threshold for a lower-risk mechanism.</summary>
        public bool CanOfferGpuTgpUnlock =>
            GpuTgpUnlock != null
            && _adapterInfo is HpWmiBios.AdapterInfo info
            && GpuTgpUnlockService.SupplyMeetsSafetyBar(
                   info, _wmiBios?.SystemDesign?.ShippingAdapterPowerRatingWatts ?? 0, out _);

        /// <summary>Why the option is unavailable, for the adapter half specifically - board gating
        /// has no user-facing reason, since it is simply not offered on any other board.</summary>
        public string GpuTgpUnlockBlockedReason
        {
            get
            {
                if (GpuTgpUnlock == null) return string.Empty;
                if (_adapterInfo is not HpWmiBios.AdapterInfo info) return string.Empty;
                GpuTgpUnlockService.SupplyMeetsSafetyBar(
                    info, _wmiBios?.SystemDesign?.ShippingAdapterPowerRatingWatts ?? 0, out var reason);
                return reason;
            }
        }

        public bool HasGpuTgpUnlockBlockedReason => GpuTgpUnlockBlockedReason.Length > 0;

        private string _gpuTgpUnlockStatus = string.Empty;

        public string GpuTgpUnlockStatus
        {
            get => _gpuTgpUnlockStatus;
            private set
            {
                if (_gpuTgpUnlockStatus == value) return;
                _gpuTgpUnlockStatus = value;
                OnPropertyChanged(nameof(GpuTgpUnlockStatus));
                OnPropertyChanged(nameof(HasGpuTgpUnlockStatus));
            }
        }

        public bool HasGpuTgpUnlockStatus => _gpuTgpUnlockStatus.Length > 0;

        private bool _gpuTgpUnlockBusy;

        public bool GpuTgpUnlockBusy
        {
            get => _gpuTgpUnlockBusy;
            private set
            {
                if (_gpuTgpUnlockBusy == value) return;
                _gpuTgpUnlockBusy = value;
                OnPropertyChanged(nameof(GpuTgpUnlockBusy));
                OnPropertyChanged(nameof(CanEngageGpuTgpUnlock));
                _engageGpuTgpUnlockCommand?.RaiseCanExecuteChanged();
            }
        }

        public bool CanEngageGpuTgpUnlock => CanOfferGpuTgpUnlock && !GpuTgpUnlockBusy;

        private AsyncRelayCommand? _engageGpuTgpUnlockCommand;

        /// <summary>
        /// Runs the unlock once. The confirmation dialog lives in the view's code-behind, same as
        /// <see cref="ApplyAdapterOverrideCommand"/> - this command performs the action, it does not
        /// ask for consent to it.
        /// </summary>
        public ICommand EngageGpuTgpUnlockCommand =>
            _engageGpuTgpUnlockCommand ??=
                new AsyncRelayCommand(_ => EngageGpuTgpUnlockAsync(), _ => CanEngageGpuTgpUnlock);

        private async Task EngageGpuTgpUnlockAsync()
        {
            var service = GpuTgpUnlock;
            if (service == null) return;

            GpuTgpUnlockBusy = true;
            GpuTgpUnlockStatus = "Pinning the firmware's power gates and asking the driver to re-read them...";

            try
            {
                var ec = Hardware.EcAccessFactory.GetEcAccess() as Hardware.PawnIOEcAccess;
                var adapter = _adapterInfo;

                // Off the UI thread: Engage() sleeps for the design doc's measured 5 s settle time
                // before it verifies anything, and the EC hold loop itself is a busy-wait.
                var result = await Task.Run(() => service.Engage(_productId, ec, adapter)).ConfigureAwait(true);
                GpuTgpUnlockStatus = result.Message;
            }
            catch (Exception ex)
            {
                _logging.Warn($"GPU TGP unlock failed: {ex.Message}");
                GpuTgpUnlockStatus = $"Failed: {ex.Message}";
            }
            finally
            {
                GpuTgpUnlockBusy = false;
            }
        }

        /// <summary>True when the override can actually be attempted right now.</summary>
        public bool CanApplyAdapterOverride =>
            CanOfferAdapterOverride && !_adapterOverrideBusy && _adapterOverrideService.IsAvailable(out _);

        /// <summary>Why the button is disabled, or empty when it is not.</summary>
        public string AdapterOverrideBlockedReason =>
            !CanOfferAdapterOverride || _adapterOverrideBusy || _adapterOverrideService.IsAvailable(out var reason)
                ? string.Empty
                : reason;

        public bool HasAdapterOverrideBlockedReason => AdapterOverrideBlockedReason.Length > 0;

        /// <summary>The result of the last attempt, or empty before the first one.</summary>
        public string AdapterOverrideStatus
        {
            get => _adapterOverrideStatus;
            private set
            {
                if (_adapterOverrideStatus == value) return;
                _adapterOverrideStatus = value;
                OnPropertyChanged(nameof(AdapterOverrideStatus));
                OnPropertyChanged(nameof(HasAdapterOverrideStatus));
            }
        }

        public bool HasAdapterOverrideStatus => !string.IsNullOrEmpty(_adapterOverrideStatus);

        /// <summary>True while a restart is in flight, so the UI can say so and refuse a second.</summary>
        public bool AdapterOverrideBusy
        {
            get => _adapterOverrideBusy;
            private set
            {
                if (_adapterOverrideBusy == value) return;
                _adapterOverrideBusy = value;
                OnPropertyChanged(nameof(AdapterOverrideBusy));
                OnPropertyChanged(nameof(CanApplyAdapterOverride));
                _applyAdapterOverrideCommand?.RaiseCanExecuteChanged();

                // The restart takes the device away and brings it back. Reading the limit through
                // the middle of that is the crash the whole quiesce dance exists to prevent.
                OnGpuPowerLimitChanged();
            }
        }

        /// <summary>
        /// Restart the dGPU so its driver reloads without the adapter verdict. Destructive enough to
        /// need the confirmation the view puts in front of it: it drops the display outputs for a few
        /// seconds and destroys every graphics and compute context on the device.
        /// </summary>
        public ICommand ApplyAdapterOverrideCommand =>
            _applyAdapterOverrideCommand ??=
                new AsyncRelayCommand(_ => ApplyAdapterOverrideAsync(), _ => CanApplyAdapterOverride);

        private async Task ApplyAdapterOverrideAsync()
        {
            AdapterOverrideBusy = true;
            AdapterOverrideStatus = "Restarting the GPU...";

            try
            {
                var result = await _adapterOverrideService.ApplyAsync().ConfigureAwait(true);
                AdapterOverrideStatus = result.Message;

                // The restart already read the limit on its way out, so take that reading rather
                // than asking the card a second time for an answer we were just given. The card's
                // default limit is a property of the card and does not move across a restart.
                if (result.EnforcedWattsAfter is double after)
                {
                    _gpuPowerLimits = new AdapterPowerOverrideService.PowerLimits(
                        after, _gpuPowerLimits?.DefaultWatts);
                    _gpuPowerLimitReadAt = DateTime.Now;
                    _gpuPowerLimitUnanswered = false;
                }

                // The CPU half, after the GPU is back rather than before it. The two are independent
                // - no EC write, no driver state, nothing the restart touches - so the only thing
                // ordering buys is not having SMU traffic in flight while the device is down. Only
                // on a restart that reported success: with the dGPU in an unknown state, raising the
                // CPU limit is not the next thing anyone needs.
                if (result.Applied && CanOfferApuClampLift && ApuClamp is ApuPowerClampService clamp)
                {
                    AdapterOverrideStatus = result.Message + " Now raising the CPU power limits...";

                    var lift = await Task.Run(() => clamp.Engage(ApuClampTargetWatts))
                                         .ConfigureAwait(true);

                    ApuClampStatus = lift.Message;
                    OnPropertyChanged(nameof(IsApuClampHeld));
                    _releaseApuClampCommand?.RaiseCanExecuteChanged();
                    AdapterOverrideStatus = result.Message;
                }

                // The verdict itself has not changed - the supply is the same one - but the limit
                // has, and the panel is the place someone will look to see whether it worked.
                RefreshPowerAdapterStatus();
            }
            finally
            {
                AdapterOverrideBusy = false;
            }
        }

        // ── Noticing when the clamp comes back ───────────────────────────────────────────────────
        //
        // The GPU clamp returns when the firmware tells the driver about the adapter again, and
        // there is no event for that on this side - the first sign is the enforced limit dropping.
        // So this watches for it, and only announces.
        //
        // It can watch at all because of an asymmetry worth stating: reading the limit costs a wake
        // on a parked GPU and costs nothing on an awake one. So this reads only when the card is
        // already awake, which the PnP power state answers for free. A parked GPU is never touched,
        // never woken, and never watched - and that is not a gap, because a clamp on a GPU nobody is
        // using is not costing anyone anything yet.
        //
        // It does not restart the device. Being able to see the clamp requires the GPU to be awake,
        // which means something is using it, which is the exact circumstance in which pulling it out
        // from under them would be wrong.

        private Timer? _gpuClampWatchTimer;
        private const string GpuClampWatchTimerName = "GpuClampWatch";

        // Only a transition is worth announcing. Starting up already clamped is the ordinary state
        // this panel exists to explain, not news; the notification is for the clamp coming back
        // after it had been dropped, which is the case nothing else would tell you about.
        private bool _gpuClampSeenLifted;

        private static readonly TimeSpan GpuClampWatchInterval = TimeSpan.FromSeconds(60);

        private void EnsureGpuClampWatch()
        {
            if (_gpuClampWatchTimer != null) return;

            _gpuClampWatchTimer = new Timer(_ => PollGpuClampState(), null,
                                             GpuClampWatchInterval, GpuClampWatchInterval);

            BackgroundTimerRegistry.Register(
                GpuClampWatchTimerName,
                nameof(GpuClampViewModel),
                "Watches for the adapter's GPU power clamp returning. Reads nothing while the dGPU " +
                "is parked, so it cannot hold the card out of RTD3.",
                (int)GpuClampWatchInterval.TotalMilliseconds,
                BackgroundTimerTier.Optional);
        }

        private void PollGpuClampState()
        {
            try
            {
                if (!CanOfferAdapterOverride) return;

                // Free, and answered from the PnP manager's bookkeeping rather than by asking the
                // device. This is the line that keeps the whole watcher honest.
                if (_adapterOverrideService.DiscreteGpuIsParked()) return;

                // Also free, because something else already has the card awake.
                var limits = _adapterOverrideService.ReadPowerLimitsWatts();
                if (limits.EnforcedWatts is not double enforced) return;

                _gpuPowerLimits = limits;
                _gpuPowerLimitReadAt = DateTime.Now;
                _gpuPowerLimitUnanswered = false;
                OnGpuPowerLimitChanged();

                if (!limits.EnforcedIsBelowDefault)
                {
                    _gpuClampSeenLifted = true;
                    return;
                }

                if (_gpuClampSeenLifted && limits.DefaultWatts is double standard)
                {
                    _gpuClampSeenLifted = false;
                    _logging.Info($"Adapter GPU clamp returned: enforced {enforced:F2} W against a " +
                                  $"{standard:F2} W default. Not restarting the GPU - something is using it.");
                    _notificationService.ShowAdapterClampReturned(enforced, standard);
                }
            }
            catch (Exception ex)
            {
                _logging.Debug($"GPU clamp watch failed: {ex.Message}");
            }
        }

        // ── Lifting it without being asked each time ─────────────────────────────────────────────
        //
        // A clamp that has to be dismissed by hand every boot is a clamp that is on most of the
        // time. Both halves are off by default and both stay off until someone turns them on; what
        // they change is whether the same two actions happen on their own afterwards.
        //
        // The two halves are not equally free, and the settings reflect that rather than hiding it.
        // The CPU half is an SMU message that takes nothing away from anything, so it runs whenever
        // the supply qualifies. The GPU half destroys every context on the device, so it runs only
        // while the device is parked - which is to say, only when the driver has already decided
        // nothing is using it.

        public AdapterClampLiftSettings ClampLiftSettings => _config.AdapterClampLift;

        /// <summary>Write and hold the CPU limits whenever the supply is clamped.</summary>
        public bool AutoLiftCpuLimits
        {
            get => _config.AdapterClampLift.LiftCpuLimits;
            set
            {
                if (_config.AdapterClampLift.LiftCpuLimits == value) return;
                _config.AdapterClampLift.LiftCpuLimits = value;
                _configService.Save(_config);
                OnPropertyChanged(nameof(AutoLiftCpuLimits));

                // Turning it on is the consent; waiting for the next reboot to act on it would be
                // a setting that appears not to work.
                if (value) _ = RunAutomaticClampLiftAsync("setting enabled");
            }
        }

        /// <summary>Restart the dGPU to drop its clamp, but only while it is parked.</summary>
        public bool AutoLiftGpuWhenParked
        {
            get => _config.AdapterClampLift.LiftGpuWhenParked;
            set
            {
                if (_config.AdapterClampLift.LiftGpuWhenParked == value) return;
                _config.AdapterClampLift.LiftGpuWhenParked = value;
                _configService.Save(_config);
                OnPropertyChanged(nameof(AutoLiftGpuWhenParked));

                if (value) _ = RunAutomaticClampLiftAsync("setting enabled");
            }
        }

        /// <summary>What the automatic path last did, or empty if it has not run.</summary>
        public string AutoClampLiftStatus
        {
            get => _autoClampLiftStatus;
            private set
            {
                if (_autoClampLiftStatus == value) return;
                _autoClampLiftStatus = value;
                OnPropertyChanged(nameof(AutoClampLiftStatus));
                OnPropertyChanged(nameof(HasAutoClampLiftStatus));
            }
        }

        private string _autoClampLiftStatus = string.Empty;
        public bool HasAutoClampLiftStatus => _autoClampLiftStatus.Length > 0;

        // One at a time. Startup, a resume and a power-state change can arrive within a second of
        // each other, and two of these overlapping would have two restarts racing for one device.
        private readonly SemaphoreSlim _autoClampLiftGate = new(1, 1);

        private void OnPowerStateChangedForClampLift(object? sender, PowerStateChangedEventArgs e)
        {
            _ = RunAutomaticClampLiftAsync(e.IsOnAcPower ? "adapter connected" : "on battery");
        }

        /// <summary>
        /// Act on the automatic settings, if they are on and the machine is in a state they apply to.
        ///
        /// Runs at startup, when the verified power state changes - an adapter swap is the event
        /// that puts the GPU clamp back - and on resume, where the platform has re-asserted its own
        /// SMU numbers. Never on a timer: nothing here polls the hardware to find work.
        /// </summary>
        public async Task RunAutomaticClampLiftAsync(string trigger)
        {
            var settings = _config.AdapterClampLift;
            if (!settings.LiftCpuLimits && !settings.LiftGpuWhenParked) return;

            if (!await _autoClampLiftGate.WaitAsync(0).ConfigureAwait(true)) return;

            try
            {
                // A fresh read rather than whatever the Diagnostics tab last saw: this can run before
                // that tab has ever been opened, and on a resume the supply may not be the one that
                // went to sleep. Off the calling thread because it is a WMI round trip and one of
                // the callers is a system power-event handler.
                await Task.Run(RefreshPowerAdapterStatus).ConfigureAwait(true);

                if (_adapterInfo is not HpWmiBios.AdapterInfo info) return;

                // Both halves are answers to an under-rated adapter, so neither runs without one.
                // Taken from the debounced state the power service already maintains rather than
                // from the trigger string, because three of the four callers - startup, resume and
                // the settings toggle - carry no power state of their own.
                var onAcPower = _powerAutomationService?.IsOnAcPower ?? true;

                var cpu = AdapterClampLiftPolicy.DecideCpu(settings, info, CanOfferApuClampLift, onAcPower);

                // Read from the firmware rather than inferred from which GPU happens to be awake -
                // that inference is what this app used to do and it was wrong.
                var displayMode = await Task.Run(() => _wmiBios?.GetGpuMode()).ConfigureAwait(true);

                var gpu = AdapterClampLiftPolicy.DecideGpu(
                    settings, info, CanOfferAdapterOverride,
                    gpuIsParked: _adapterOverrideService.DiscreteGpuIsParked(),
                    displayMode: displayMode,
                    onAcPower: onAcPower);

                _logging.Info($"Automatic clamp lift ({trigger}): CPU {cpu}, GPU {gpu}");

                if (cpu == ClampLiftDecision.Apply && ApuClamp is ApuPowerClampService clamp)
                {
                    var lift = await Task.Run(() => clamp.Engage(ApuClampTargetWatts)).ConfigureAwait(true);
                    ApuClampStatus = lift.Message;
                    OnPropertyChanged(nameof(IsApuClampHeld));
                    _releaseApuClampCommand?.RaiseCanExecuteChanged();
                }

                if (gpu == ClampLiftDecision.Apply)
                {
                    AdapterOverrideBusy = true;
                    try
                    {
                        // onlyIfParked: this decision was taken seconds ago and the service re-checks
                        // it at the last instant. A GPU that woke in between keeps its clamp, which
                        // is the right way round - the clamp costs watts, the restart costs someone's
                        // work.
                        var result = await _adapterOverrideService
                            .ApplyAsync(CancellationToken.None, onlyIfParked: true)
                            .ConfigureAwait(true);

                        AdapterOverrideStatus = result.Message;

                        if (result.EnforcedWattsAfter is double after)
                        {
                            _gpuPowerLimits = new AdapterPowerOverrideService.PowerLimits(
                                after, _gpuPowerLimits?.DefaultWatts);
                            _gpuPowerLimitReadAt = DateTime.Now;
                            OnGpuPowerLimitChanged();
                        }
                    }
                    finally
                    {
                        AdapterOverrideBusy = false;
                    }
                }

                AutoClampLiftStatus = DescribeAutoLift(cpu, gpu, trigger);
            }
            catch (Exception ex)
            {
                _logging.Error($"Automatic clamp lift failed ({trigger})", ex);
            }
            finally
            {
                _autoClampLiftGate.Release();
            }
        }

        /// <summary>
        /// One line about what just happened without being asked, in particular when the answer is
        /// "nothing". A GPU half that silently declines because the device was awake looks exactly
        /// like a setting that does not work.
        /// </summary>
        private static string DescribeAutoLift(ClampLiftDecision cpu, ClampLiftDecision gpu, string trigger)
        {
            var parts = new List<string>();

            if (cpu == ClampLiftDecision.Apply) parts.Add("CPU limits raised and held");
            if (gpu == ClampLiftDecision.Apply) parts.Add("GPU restarted while it was parked");

            if (gpu == ClampLiftDecision.DeferredGpuAwake)
            {
                parts.Add("the GPU was awake, so it was left alone - use the button when nothing needs it");
            }

            if (gpu == ClampLiftDecision.DeferredDiscreteDisplay)
            {
                parts.Add("the display is running on the discrete GPU, so restarting it would black " +
                          "the screen - that one stays manual");
            }

            // Reported once for both halves: they refuse together, and saying it twice reads as two
            // separate problems rather than one machine that is simply not plugged in.
            if (cpu == ClampLiftDecision.DeferredOnBattery || gpu == ClampLiftDecision.DeferredOnBattery)
            {
                parts.Add("running on battery, so there is no adapter clamp to lift");
            }

            return parts.Count == 0
                ? string.Empty
                : $"Automatic ({trigger}): {string.Join("; ", parts)}.";
        }

        /// <summary>
        /// Re-read the adapter state. Read-only WMI query; called when the Diagnostics tab is opened
        /// rather than polled, because the value only changes when someone physically plugs or
        /// unplugs something and this costs a WMI round trip.
        /// </summary>
        public void RefreshPowerAdapterStatus()
        {
            try
            {
                _adapterInfo = _wmiBios?.GetAdapter();
            }
            catch (Exception ex)
            {
                _logging.Debug($"Adapter status refresh failed: {ex.Message}");
                _adapterInfo = null;
            }

            OnPropertyChanged(nameof(HasPowerAdapterInfo));
            OnPropertyChanged(nameof(PowerAdapterSummary));
            OnPropertyChanged(nameof(PowerAdapterRequirement));
            OnPropertyChanged(nameof(PowerAdapterExplanation));
            OnPropertyChanged(nameof(HasPowerAdapterExplanation));

            OnPropertyChanged(nameof(CanOfferAdapterOverride));
            OnPropertyChanged(nameof(CanApplyAdapterOverride));
            OnPropertyChanged(nameof(AdapterOverrideBlockedReason));
            OnPropertyChanged(nameof(HasAdapterOverrideBlockedReason));
            _applyAdapterOverrideCommand?.RaiseCanExecuteChanged();
            OnGpuPowerLimitChanged();

            // Started once the panel has something to watch, rather than at construction: on a
            // machine with an adequate supply there is no clamp and nothing to notice.
            if (CanOfferAdapterOverride) EnsureGpuClampWatch();

            OnPropertyChanged(nameof(CanOfferApuClampLift));
            OnPropertyChanged(nameof(ApuClampTargetWatts));
            OnPropertyChanged(nameof(AdapterOverrideScope));
            OnPropertyChanged(nameof(HasAdapterOverrideScope));
            OnPropertyChanged(nameof(IsApuClampHeld));
            _releaseApuClampCommand?.RaiseCanExecuteChanged();

            // Take one reading where the panel is actually offering the restart, so the number is
            // on screen before the decision is. Only there: a machine with a supply that meets its
            // requirement has no clamp to look for, and waking its dGPU every time someone opens
            // Diagnostics would cost idle power all session to answer a question nobody asked.
            //
            // Fire and forget on purpose. This is called from the view's tab-open handler, which
            // cannot wait tens of seconds for a GPU that is coming back from D3.
            if (CanOfferAdapterOverride && !HasGpuPowerLimitReading && !_gpuPowerLimitUnanswered)
            {
                _ = ReadGpuPowerLimitAsync(onlyIfAwake: true);
            }
        }

        public void Dispose()
        {
            if (_powerAutomationService != null)
            {
                _powerAutomationService.PowerStateChanged -= OnPowerStateChangedForClampLift;
            }

            _gpuClampWatchTimer?.Dispose();
            _gpuClampWatchTimer = null;
            BackgroundTimerRegistry.Unregister(GpuClampWatchTimerName);

            _apuClampService?.Dispose();
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
