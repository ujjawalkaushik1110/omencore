# OmenCore v4.3.1

**Release Date:** 2026-09-15.
**Release Status:** Shipping. Started 2026-09-09, two days after v4.3.0 shipped. Every fan/EC/
thermal/OC/UV-behavior change in this release has field validation per this project's evidence-gate
convention, with one exception noted explicitly: the OMEN-key WMI fix (`#193`) is root-caused with
strong evidence (the reporter's own independent, OmenCore-free WMI listener proved the OS/firmware
side was fine and isolated the bug to OmenCore's own event-discarding logic), but the reporter had
not yet re-tested the actual fix as of release; treat as implemented-pending-confirmation for that
specific board (`8D2F`) until they report back. Board `8C9C`'s fan-control entry has real field
confirmation (two Guided Fan Verification runs, 84-88/100); GPU boost, undervolt, and RGB remain
unconfirmed on that board and stay conservative.
**Type:** Patch release grown into a broader maintenance cycle. Started as field-report fixes from
GitHub issues opened after v4.3.0 (#190, #191) plus a pending field-confirmation follow-up (#186);
expanded to include a real, provable config-persistence data-loss bug (#191), a broken auto-updater
SHA256 check (#192), an OMEN-key WMI event bug (#193), the `MainViewModel` and
`SystemControlViewModel` decompositions (four extraction steps total), three board database entries
(`8BAD`, `8C9C`, plus an `8E35` identity-conflict flag), two rounds of cross-project review against
a similar tool ("Ohman") that produced a GPU-idle-polling fix, a firmware-aware unverified-board
capability fix, and a fixed release-notes SHA256 process (`#194`), and a Dashboard styling
improvement.
**Base Version:** v4.3.0
**Tracking doc:** `docs/ROADMAP_v4.3.1.md` — full investigation detail, rejected options, and evidence trails live there; this file stays short.

### Erratum — Changes in This Release Were Wrong (Fixed on `main`, Not Yet Released)

Found from field bundles after release ([#203](https://github.com/theantipopau/omencore/issues/203),
[#202](https://github.com/theantipopau/omencore/issues/202)); details in `docs/CHANGELOG_v4.4.0.md`.

- **"Unverified Boards No Longer Assume Fan Control Works When Firmware Says It Doesn't"** (below) is
  wrong. HP's `SystemDesignData` software-fan-control bit reads false on V0-thermal-policy firmware
  where fans demonstrably respond, so this disabled working fan control and Custom Fan Curve on
  boards such as `8C2F` and `8BB1`. **Workaround on 4.3.1:** none in the UI; affected users can
  stay on 4.3.0 until the next release.
- **"GPU Telemetry Backs Off Further Once Confirmed Idle"** (below) shipped a 2-minute cadence that
  is longer than the hardware watchdog's 90-second "monitoring frozen" limit, so tray-only idle
  periods triggered a false watchdog failsafe that set fans to 90% roughly every two minutes.
  The long sleep also couldn't be interrupted, so opening the window or showing the OSD left stale or
  zero fan/CPU/GPU numbers for up to that long.
  **Workaround on 4.3.1:** keep the OmenCore window open (the backoff only applies while the window is
  hidden/tray-only), or stay on 4.3.0.

### Downloads

Computed by the release workflow itself from the exact files attached to the
[v4.3.1 release](https://github.com/theantipopau/omencore/releases/tag/v4.3.1) — see the SHA256
fix above ([#194](https://github.com/theantipopau/omencore/issues/194)) for why that distinction
matters this time.

| Artifact | SHA256 |
|---|---|
| `OmenCoreSetup-4.3.1.exe` | `B232BBA0181062DCA9D7CB746B7BF5DF369B2B0D2DB807A979A86793C2ED1AD0` |
| `OmenCore-4.3.1-win-x64.zip` | `41290B284C38AD66458AF2AD42F90B73444C054B9B00CBF811D2D9D04FAD2FA5` |
| `OmenCore-4.3.1-linux-x64.zip` | `72A4163F5175E772815A1583BABE0B610C5BD58ED86DB5A19FA14E37B5F1BA9E` |

---

## Added

### Architecture: MainViewModel Decomposition, Step 1

`MainViewModel.cs` (6,275 lines) has been flagged across several cycles as the thing that makes
everything else expensive to change. First step at decomposing it: deleted a confirmed-dead
~200-line Corsair/Logitech/Macro RGB cluster (duplicated, more completely, by the existing
`LightingViewModel`, with zero UI bindings anywhere pointing at it), and extracted the
update-checking/installing cluster (~250 lines) into a new `UpdateViewModel`, wired in as an
eagerly-constructed `MainViewModel.Update` property. Also deleted `MacroService.cs`, itself fully
dead code. Net: `MainViewModel.cs` 6,275 → 5,636 lines. Pure structural refactor, no fan/EC/thermal
write path touched. 8 new tests; full suite 1423/1423. See the roadmap for the full trace and why
the GPU power-limit/adapter-clamp cluster was deliberately left for its own follow-up pass.

### Architecture: MainViewModel Decomposition, Step 2

The follow-up from step 1: extracted the GPU power-limit / adapter power-override clamp cluster
(~970 lines — reading the GPU's actual power limit, the power-adapter verdict/explanation, the
adapter override that restarts the GPU driver for a fresh verdict, the related AMD CPU power
clamp, and the automatic clamp-lift watcher) into a new `GpuClampViewModel`, wired in as an
eagerly-constructed `MainViewModel.GpuClamp` property. `DiagnosticsView`'s Power Adapter panel
bindings and its two code-behind hard-casts were repointed one level deeper rather than retargeting
the whole page. Also fixed a pre-existing bug found while mapping the cluster: three event
subscriptions were never unsubscribed anywhere in `Dispose()` — fixed as part of this work, not
left for later. Net: `MainViewModel.cs` 5,636 → 4,665 lines (6,275 → 4,665 combined across both
steps, -26%). Pure structural refactor plus a lifecycle-cleanup fix, no fan/EC/thermal write
*behavior* touched. 2 new tests, 6 migrated; full suite 1425/1425.

### Architecture: SystemControlViewModel Decomposition, Step 1

Now that `MainViewModel` is smaller, `SystemControlViewModel.cs` (5,610 lines) became the largest
file in the app. Extracted the four features proven independent of its undervolt/GPU-OC/power-limit
tuning core — GPU mode switching, display panel overdrive, the OMEN Gaming Hub cleanup wizard, and
manual restore-point creation — into a new `SystemMaintenanceViewModel`, wired in as
`SystemControlViewModel.Maintenance`. `AdvancedView.xaml` and `SettingsView.xaml`'s bindings were
repointed one level deeper. Also deleted a confirmed-dead, zero-reference duplicate
`CleanupOmenHubCommand` found while mapping the cluster. Net: `SystemControlViewModel.cs` 5,610 →
5,290 lines. Pure structural refactor, no undervolt/GPU-OC/power-limit write path touched. 6 new
tests; full suite 1441/1441.

### Dashboard: Bigger CPU/GPU Temperature Readouts

Comparing OmenCore's Dashboard against a similar tool (Ohman, see the GPU-idle-polling entry
below) surfaced a real, low-risk styling gap: its primary temperature/RPM numbers are the
dominant element on the page, while OmenCore's equivalent (`MonospaceValueLarge`, used only for
the Dashboard's CPU/GPU temperature headlines) sat at 32px — a supporting stat next to the chart
rather than a hero number. Bumped to 52px. One shared style resource, two call sites affected,
no layout or architecture change.

### Four-Zone Keyboard Lighting Gets a Real Keyboard Visual

OmenCore already had a genuine, physically-drawn per-key keyboard editor
(`KeyboardMapEditor`/`KeyboardMapViewModel`) with click-select, drag-to-select, and a
measured-vs-inferred honesty banner — arguably more capable than similar tools' equivalents. It
only ever applied to per-key RGB hardware with a measured hardware map, though; the far more
common four-zone boards got four plain rectangles with text listing which keys were roughly
inside each one, not a drawn keyboard.

Added a new `FourZoneKeyboardLayout` — a small, pure, side-effect-free generator (no device
involved, unlike the measured per-key case) that lays out a standard TKL laptop keyboard shape and
tags every key to the zone it falls under, matching the exact key lists the old text schematic
already used ("TAB Q W E R T" for Zone 1, etc.). Replaced the four-rectangle schematic in
`LightingView.xaml` with a drawn keyboard using the same Canvas/ItemsControl rendering approach as
the existing per-key editor; clicking a key opens that zone's existing color picker. Zero changes
to the underlying zone-coloring logic — every key's fill and click action bind straight through to
the `Zone1Brush`..`Zone4Brush` properties and `SetZone1ColorCommand`..`SetZone4ColorCommand`
already driving the rest of the page, via one new one-line dispatch command
(`SetZoneColorByIndexCommand`) that routes a key's zone index to the matching existing command.

5 new tests for the layout generator, including bounds-checking every key against the canvas —
which caught a real off-by-3-units sizing bug in the arrow-cluster column before it shipped. Full
suite 1455/1455.

### Real Razer Logo in the RGB Section

The Razer Devices card used a generic "green circle/square with a bold R" placeholder in two
spots (the section header and every device card). Replaced both with Razer's actual triple-snake
mark, traced from the brand's own official logo file and added as a new `IconRazer` Geometry
resource — a single-color vector, matching every other icon in `ModernStyles.xaml`, recolorable
via `Fill` the same way. No behavior change.

---

## Fixed

### Release Notes' SHA256 Hashes Will No Longer Silently Diverge From the Published Assets

[#194](https://github.com/theantipopau/omencore/issues/194): v4.3.0's release notes listed SHA256
hashes that didn't match the actual published Windows or Linux assets, found by RobRobM while
independently verifying #186's fix. Root cause: `build-installer.ps1` never computed a hash for
either Windows artifact at all (unlike the Linux packaging script), so the hashes in past release
notes had been hand-copied from a separate local build run — which produces different bytes, and
therefore a different hash, than what the CI release job actually built and published. Fixed at
the source: `build-installer.ps1` now writes a `.sha256` sidecar next to both the Windows zip and
the installer exe, matching the pattern the Linux script already used. `release.yml` uploads all
three `.sha256` files as release assets and now reads them back to populate a hash table directly
in the published release notes, computed from the exact files attached to that release — nothing
hand-copied, nothing that can drift out of sync with what CI actually built.

### Custom Settings Silently Reverting After Restart — A Real Config-Persistence Bug

Two independent users (Discord, board `8BAD`; a comment on
[#191](https://github.com/theantipopau/omencore/issues/191), board `8C9C`) reported saved settings
— a custom fan curve, a custom AMD CPU Power Limit — vanishing after doing something unrelated
elsewhere in the app, "as if it had never been saved." Traced to `ConfigurationService` handing out
a brand-new, detached copy of the config on every load instead of a shared instance — at least
three parts of the app (Settings, System Control/tuning, the main window group) each held their own
stale snapshot, and whichever saved last silently overwrote every field a *different* part had just
changed. Fixed by making config loads merge onto one shared, always-current object instead of
forking a new one each time — no other file needed to change. 4 new tests, including a direct
regression test verified to fail on the old code and pass on the fix. 1430/1430 tests.

### Auto-Update's SHA256 Check Never Actually Matched Our Own Release Notes

Reported via [#192](https://github.com/theantipopau/omencore/issues/192): checking for updates
showed "missing SHA256 in release notes" and refused to auto-install, even though the hash was
genuinely present. Confirmed by fetching the actual published v4.3.0 release body: the hash is
there as a markdown table (`| Artifact | SHA256 |`), but the extraction regex's separator pattern
only accounted for colons and whitespace, not a table cell's `` ` | ` `` boundary — so it silently
matched nothing against every release since the table format was introduced, and the updater
always fell back to manual-download-only regardless of content. Fixed the regex; replaced a test
that claimed to cover this but never actually invoked the method with two that do, including one
against the real three-artifact release-notes table. Doesn't retroactively fix the 4.2.0 → 4.3.0
upgrade that prompted the report (the fix ships in the version after the one being checked from),
but auto-update should work correctly from 4.3.0 onward.

### Board `8BAD` Misnamed a Real 17" Owner's Laptop as "OMEN 15"

A Discord report (OMEN 17 CK-2013nl) showed the app correctly resolving hardware via Exact
ProductId but displaying "OMEN 15 (2021) Intel" — the capability database's `ModelName` never
picked up the "15/17" shared-chassis naming `KeyboardModelDatabase`'s own entry for the same
ProductId already used. Renamed to match; no capability flags changed, and the entry was already
`UserVerified`.

### The Entire "Notifications" Settings Section Did Nothing

Reported indirectly via [#191](https://github.com/theantipopau/omencore/issues/191) ("high
temperature warnings even with Extreme fan preset active"). Every toggle in Settings →
Notifications — the master switch, Game profile notifications, Mode change notifications, and
Temperature warnings — saved correctly but was never applied to the app's actual notification
service, at startup or after being changed. Turning any of them off had no effect, even after
restarting the app. `MainViewModel` now applies these settings to the live service at startup, and
immediately whenever one is changed in Settings — no restart required. 1 new test.

Separately: the "High Temperature Warning" toast in that report is a display-only notification
(`ThermalMonitoringService`, default 85°C) and is completely independent from the fan-boosting
thermal-protection logic that actually reacts to heat (`FanService`, default 90°C ramp / 95°C
emergency) — the fan behavior itself was not affected by this bug, and turning off "Temperature
warnings" (now that it actually works) does not disable that safety protection.

### Fan Control Page Now Explains When a Preset Won't Survive a Restart

Also from the Discord board-`8BAD` report: fans not being controlled before Windows login is
expected behavior (`EnableStartupHardwareRestore` defaults off), but nothing on the Fan Control
page said so — a saved preset silently not reapplying at boot read as a bug rather than an unset
toggle. Added a dismissible hint, gated on the same `StartupRestorePolicy` check the rest of the
app already uses for this decision, that tells the user exactly which Settings toggle governs it.
4 new tests.

### Cleanup: Redundant Manual Field Copy Removed From Config Reload

A small follow-up to the config-persistence fix above: `MainViewModel.ReloadConfiguration()`
manually copied six fields from a fresh `Load()` onto `_config` — now dead weight, since `Load()`
merges onto the same shared object `_config` already points at. Removed; no behavior change. 1 new
test.

### OMEN Key WMI Events Were Being Discarded on Boards Where the Keyboard Hook Never Sees the Key

Reported in [#193](https://github.com/theantipopau/omencore/issues/193) (HP OMEN 16-am0000, board
`8D2F`). The reporter ran an independent, OmenCore-free WMI listener and it received the correct
event on every OMEN key press, proving the OS/firmware side was fine — the bug was in OmenCore.
`OnWmiEventArrived` was unconditionally discarding real OMEN-key WMI events whenever the keyboard
hook was active, assuming the hook would catch the key itself; on boards where the OMEN key
produces no keyboard-observable code at all, that threw away the only real signal. A second, less
consequential gap skipped starting the WMI watcher entirely under the same hook-active condition
when the experimental Fn+P feature was off. Both removed — the existing debounce shared between
the hook and WMI code paths already prevents a double-fire on boards where both genuinely catch
the same key press. 1 existing test updated to match the corrected behavior; full suite 1441/1441.

### GPU Telemetry Backs Off Further Once Confirmed Idle

Reviewing a similar open-source tool ([Ohman](https://github.com/P4R1H/Ohman)) surfaced a real gap:
polling the GPU at all, even at a background cadence, can keep a discrete GPU out of its deepest
idle power state, artificially raising chassis temperature with nothing running. OmenCore's
telemetry cadence was keyed on UI visibility only, never on actual GPU load. Once 3 consecutive
samples show the GPU at ≤1% utilization and ≤12W package power, cadence now backs off to once
every 2 minutes — only when the window isn't actively being watched, and reset instantly the
moment real GPU activity shows up. Confirmed this is fully decoupled from fan-curve control, which
reads temperatures through its own independent, already-adaptive polling loop. 3 new tests.

### Unverified Boards No Longer Assume Fan Control Works When Firmware Says It Doesn't

The other half of the Ohman review: OmenCore already parses HP's firmware-authored
`SystemDesignData` block (including an explicit "software fan control supported" flag) but never
used it — an unverified board falling back to a same-family template just inherited that
template's guess about fan control regardless of what its own firmware actually reported.
`RefineCapabilitiesFromModel()` now checks that flag: if the firmware itself says software fan
control isn't supported, fan control is forced to monitoring-only, overriding the template's
assumption. Scoped narrowly on purpose — only for boards nobody has hand-verified yet (a verified
board's own confirmed flags are never second-guessed), and it can only ever narrow a capability a
template guessed at, never grant one the template didn't already claim. 4 new tests.

### Board `8C9C` Given a Real Database Entry

[#191](https://github.com/theantipopau/omencore/issues/191)'s board (Victus, Ryzen 7 8845HS + RTX
4070) was resolving via generic Family fallback. Cross-referencing another project's board list
(itself sourced from Linux's `hp-wmi` driver) confirmed it as a Victus 16 S/R (2023-2024) board
sharing a firmware generation with the already-known `8BD4` entry. Added a conservative entry
inheriting `8BD4`'s flags — the firmware generation is now evidence-backed, but GPU boost,
undervolt, and exact fan behavior remain unconfirmed on this specific board pending real field
data. 2 new tests.

The requested field data arrived before shipping: two Guided Fan Verification runs scored 84–88/100
with consistent RPM-vs-request scaling, and the reporter's real `FanService` thermal-protection
system was observed engaging and auto-releasing correctly. Good supporting evidence for the
conservative flags already given — not upgraded to `UserVerified` off one report, since GPU boost,
undervolt, and RGB are still unconfirmed on this board.

### Board `8E35`'s Notes Corrected to Flag a CPU-Identity Conflict, Not Assert One

[#195](https://github.com/theantipopau/omencore/issues/195) reported the same ProductId *and* SKU
as this entry's original source report, but with native diagnostics showing a different CPU
(`Ryzen 9 8940HX` vs. the `Ryzen AI 9 365` this entry's `Notes` claimed). Traced every place that
text could matter first: it doesn't drive any capability decision (undervolt gating resolves from
the live-detected CPU string, never this database's notes), so this is a documentation correction
with zero functional effect — but a soldered laptop CPU shouldn't have two reports disagreeing
about what it is, so the notes now say so explicitly instead of picking one.

---

## Investigated, Not Yet Actioned

- **[#142](https://github.com/theantipopau/omencore/issues/142)** — new field data on an unconfirmed 2026 flagship board (`8E9A`, HyperX OMEN MAX, RTX 5090): fan control "hit or miss," RGB limited to static red only, CPU/GPU power not scaling together under combined load. Not enough yet for a database entry — asked for a diagnostics export, physical RGB-zone confirmation, and an OMEN Gaming Hub baseline comparison for the power question. Separately, clarified for this reporter that the temperature-warning toast shows OmenCore's own independent notification threshold, not their BIOS TCC offset — same distinction as #191 below.
- **[PR #147](https://github.com/theantipopau/omencore/pull/147)** — reviewed in full before considering a merge. The log-buffer `StringBuilder` change is correct and worth keeping, but two bugs found in the other two changes: the tray-icon change-detection cache never actually populates in the default configuration (so the optimization never engages for most users), and the dashboard uptime timer can never restart once paused once (a hard freeze of `SessionUptime`/`LastSampleAge` for the rest of the session). Posted a specific review comment; not merged as-is.
- **[#191](https://github.com/theantipopau/omencore/issues/191) follow-up: does Curve Optimizer actually do anything on Ryzen 7 8845HS?** A third commenter claimed AMD Curve Optimizer only works on HX-tier and Ryzen 9 HS parts, implying the reporter's −80 mV offset is a silent no-op. Traced the actual write path (`AmdUndervoltProvider`): it doesn't gate by product tier, only by silicon family, and — more fundamentally — the SMU mailbox this project uses has no independent CO readback for *any* Ryzen chip (`UndervoltStatus.HasIndependentReadback = false`, already documented in code from an earlier false-positive found on a different board). So the tier claim can't be confirmed or ruled out from the code; asked for an empirical before/after clock-speed comparison under sustained load (or `tools/SmuProbe --outcome` for anyone comfortable building from source) rather than accepting a tier-based rule of thumb on faith. No code change made without evidence either way.
- **`ThermalMonitoringService`'s 85°C default CPU/GPU warning threshold** — arguably low relative to `FanService`'s own 90°C ramp-start point and its documented "85°C is normal" conclusion. Not changed on the strength of one report; see roadmap.
- **[#192](https://github.com/theantipopau/omencore/issues/192)** — the SmartScreen/Error 4551 install block (separate from the SHA256 bug fixed above) is likely a Windows AppLocker/CodeIntegrity policy or third-party AV reacting to the unsigned installer, not something in the installer script itself. Asked the reporter to check Event Viewer and try the portable ZIP as a workaround.
- **Tray icon's refresh-rate menu can target the wrong display when docked.** A second Ohman cross-check found a real, credible gap: the "Display: [rate]" tray menu's High/Low/Toggle actions default to whatever Windows currently calls the primary display, which can silently be an external monitor rather than the laptop panel when docked (Ohman had two independent field reports of the same class of bug). The newer Quick Popup display control already avoids this by letting you target a specific display. Not fixed this cycle — the correct fix needs real Win32 display-connector-type detection, which this project doesn't ship without hardware verification, and no docked rig was available. Checked three other candidate gaps from the same review round and found them not applicable: a fan-write-spam bug on unsupported boards (already closed architecturally by this cycle's `FanControllerFactory` design), a fan-curve-unlink bug (OmenCore's independent-curves path already keeps CPU/GPU levels separate), and a Windows Dynamic Lighting device-handoff bug (OmenCore doesn't integrate with that OS feature). Also flagged, not investigated: whether HP's WMI temperature-sensor command (`0x23`) actually means what this codebase assumes for its sensor-index parameter — a different open-source tool's findings suggest the same opcode may address a different sensor set than OmenCore/OmenMon's existing CPU/GPU convention assumes; the stakes are high enough (this feeds real thermal protection) that it needs independent verification before any change, not a guess either way.

---
