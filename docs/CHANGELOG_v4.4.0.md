# OmenCore v4.4.0

**Release Date:** TBD — just started, nothing shipped yet. Rolling changelog, updated as work lands.
**Release Status:** In progress. Started 2026-09-16, one day after v4.3.1 shipped.
**Type:** Grown well past the original two field reports. Now includes three real v4.3.1 regressions
(fan control disabled on V0-thermal-policy boards, a watchdog/cadence conflict forcing fans to 90%
every two minutes, a monitor loop that couldn't be woken) affecting at least seven distinct boards,
a hardware-worker crash on hot-plugged drives, a large community-contributed port covering 8D87
keyboard lighting and AMD SMU power-limit corrections, and a handful of model-database identity
fixes. No separate 4.3.2 hotfix — 4.4.0 itself is carrying the regression fixes and will ship as
the combined fix/improvement release. See the roadmap for full detail.
**Base Version:** v4.3.1
**Tracking doc:** `docs/ROADMAP_v4.4.0.md` — full investigation detail, rejected options, and evidence trails live there; this file stays short.

---

## Added

### Diagnostics Now Capture All Four HP WMI Temperature Sensor Indices

Toward resolving the `0x23` sensor-index question below with real evidence instead of guessing:
every diagnostics export now includes `wmi-temperature-sensors.txt`, a read-only probe of all four
documented indices for HP's WMI temperature command (`0x23`), labeled against both the IR/Ambient/
PCH/VR mapping two independent research efforts found and the CPU/GPU indices OmenCore itself uses.
Writes nothing to the firmware; exists purely so a future report can show what every index actually
returns on that board.

### Board `8D87` GPU Power Unlock: Built, Then Switched Off Before Release

`docs/8D87-OMEN-MAX-16-SUPPORT-PLAN.md` explains why this board's RTX 5080 sits at 80-105 W where
OMEN Gaming Hub reaches 175 W: two EC bits (`OGHP`, `PROH`) gate a configurable-TGP adder that OGH
holds open and OmenCore never did. This cycle added the pieces - `PawnIOEcAccess.HoldByteAndFire`
(one EC-mutex hold across a pin loop, since the normal write path's trailing sleep alone exceeds the
window), `GpuTgpUnlockService` (8D87-only, adapter safety bar, outcome verification), and a
Diagnostics entry behind a confirmation dialog.

**It does not ship enabled in 4.4.0.** Pre-release review against the design doc found three problems,
any one of which is disqualifying:

1. **The EC write path is unproven.** The doc proved the ACPI EC ports alias the MMIO window for
   *reads*, on one adapter state, and says "nothing may write EC RAM that way yet". This writes it.
2. **The pin model is wrong.** The EC clears `OGHP` on ~98% of 2 ms cycles and the doc's result is
   "won by repetition" - a loop that keeps re-pinning across the WMI call. This pinned for 2 ms, then
   fired, then restored the byte, so it would almost certainly never have worked.
3. **It verified the wrong number.** The doc measures the *enforced* power limit (`>= 170 W`); this read
   NVAPI power *draw*, which stays low on an idle GPU whether or not the limit moved.

A hard gate (`GpuTgpUnlockService.EcWritePathValidated = false`, pinned by a test) makes `Engage` refuse
and keeps the UI entry hidden. The adapter figures in the code were also corrected: 8D87 ships with a
330 W adapter, not 230 W. Re-enabling needs an 8D87 owner to validate EC writes and the rewritten pin
loop against the enforced limit first.

### Backlog Swept: 68 Open Issues Closed or Labeled

Most open issues (85 of 124) predated the 4.0 rewrite. Closed 53 with no activity in a long time and
no board-identity value to keep — a handful with a specific pointer to what's fixed since (the
antivirus false-positive class, the config-persistence bug, the watchdog/cadence regression, ignored-Max),
the rest with a plain "reopen with a current diagnostics export if this is still happening." Left
every model-support/hardware-verification request open regardless of age — those stay useful however
old they are — and labeled the genuine feature requests and the reports worth a closer look
(`#60`, `#103`, `#133`) rather than closing them.

---

## Fixed

### Three v4.3.1 Regressions Found in Field Bundles (All Caused by v4.3.1's Own Changes)

All three came out of diagnostics bundles and logs for [#203](https://github.com/theantipopau/omencore/issues/203),
[#202](https://github.com/theantipopau/omencore/issues/202) and a Victus 16 (`8BD4`) bundle, and all
are **my own v4.3.1 changes being wrong**, not board quirks. Recorded plainly because the changelog
that shipped them presented them as safe, evidence-backed improvements.

**1. Fan control was switched off on V0-thermal-policy boards where it works (`#203`, `#202`).**
v4.3.1 forced monitoring-only whenever HP's `SystemDesignData` "software fan control supported" bit
read false on an unverified board, reasoning that a firmware-authored "no" outranks a template's
guess. On V0 (legacy) firmware that byte simply isn't populated — the captured block is
`C8 00 00 00 00 …`, policy 0, flag 0 — yet WMI `0x2E` level writes are accepted, read back from
firmware, and audibly move the fans. `#203`'s reporter had Custom Fan Curve working on 4.3.0 on
board `8C2F` and lost it on 4.3.1; Guided Fan Verification then scored 5/100 ("Backend: None"). The
bit is now diagnostic-only: logged when it disagrees, never acted on, and write outcomes decide.
My own test for the old behavior used a default-constructed struct — i.e. policy V0 — which is
exactly the case that turned out to be wrong. Replaced with tests built from the real captured
bytes (V0 blocks from `#203`/`#202`, V1 block from `8BD4`/`8CC0`).

**2. The 2-minute GPU-idle cadence tripped the hardware watchdog, forcing real fans to 90% every
~2 minutes.** v4.3.1's "GPU telemetry backs off once confirmed idle" set the tray-only cadence to
120s, but `HardwareWatchdogService` declares monitoring frozen after 90s without a sample and applies
a 90% failsafe. Every tray-only idle stretch therefore produced `WATCHDOG: Temperature monitoring
frozen for ~101s → Fans set to 90%` about every two minutes (32 events in one `#203` log; 3 real 90%
failsafes in the `8BD4` bundle) — on boards where fan control was available, the fans were actually
pinned. I verified the new cadence was decoupled from `FanService`'s own polling loop but never
checked the watchdog. Deep-idle cadence is now 30s, and a test asserts it stays at least 2x under
the watchdog threshold so the two can't drift apart again. A long cadence also delays thermal
protection's view of a sudden load, which is the other reason it shouldn't be minutes.

The GPU-idle backoff itself is unproven for OmenCore (the "wakes the dGPU" measurement was Ohman's,
for `nvidia-smi`, not our NVAPI path) and is now much smaller; whether it's worth keeping at all
should be decided by measurement, not by analogy.

**How widespread #1 is.** Every V0-thermal-policy board seen since release lost fan control on 4.3.1,
not just the two boards in the reports: `8C2F` (#203), `8BB1`/`8C3F`-class Victus 15 (#202),
`8C30` (#208 - "Fan Verification 5/100, Backend: None"), `8A25` (a Discord report), `88F8`
(#207) and `8EDC` (#201) all show `SW fan control support: False`, policy V0, and
`Backend: None (monitoring only)` in their bundles. Any of them that used Custom Fan Curve, linked
fan profiles to performance modes, or the Max preset was affected. (`#205`, a Victus 15-fa1xxx, was
first counted here too, but its reporter saw the same problem on 4.3.0, before this regression
existed - so it is something else, still awaiting a diagnostics export.)

**3. The monitor loop couldn't be woken, so "open the window / show the OSD" waited out the sleep
already in progress.** Reported on `8A25` (OSD takes minutes to show numbers, fan shows 0 on the
OSD, General page slow to populate, all "unlike 4.3.0"). The loop chose its sleep once per cycle;
`SetUiWindowActive`/`SetTrayOnlyMode`/`SetOverlayRealtimeMode` changed the mode but never ended the
current sleep. At the old 10s tray cadence that was invisible; with the 2-minute deep-idle sleep it
became minutes of stale or zero telemetry after opening the window or the OSD. Every cadence-
affecting setter now cuts the current sleep short (only on a real change), so the new cadence
applies and a fresh sample is taken immediately. The same `8A25` log also explains "linked fan
profiles stopped following performance mode": `Link sync: Performance -> Fan 'Extreme'` fired, then
`Fan preset skipped; fan control unavailable (monitoring only)` - regression #1 again, not a
separate bug. 2 new tests.

### OMEN MAX Per-Key Keyboard: Uniform Colour Now Falls Back to the LampArray Interface When the MCU Refuses

Reported on `8D87` (ThaMadRus, v4.3.1): RGB worked after install, then after a full shutdown only the
light bar responded. The log shows `[DojoPerKey] Uniform fill via mi_03: REFUSED`, after which nothing
else was tried, so the V2 engine fell back to the four-zone WMI ColorTable - which on this chassis
drives only the light bar (documented in the 8D87 support plan), so the keyboard silently stopped
changing. A refused mi_03 fill now falls through to painting the same keys over mi_04 (HID LampArray),
before giving up. The known trade-off of mi_04 - no MCU redraw after an Fn overlay - applies only on
this recovery path; the normal path is unchanged. **Not root-caused:** why the MCU refuses after a
cold power-up isn't visible in the log (`REFUSED` carries no reason), and there's no hardware here to
test the fallback, so this is implemented-pending-confirmation. A diagnostics export from the
affected machine after a cold boot would show what the device actually returns.

### Game-Profile Exit Now Runs the Full Balanced Restore on the UI Thread ([#206](https://github.com/theantipopau/omencore/issues/206))

Diagnosed, fixed and validated under real gaming load on a v4.3.1 build by the reporter (Karoth89,
board `8BD4`); I checked their diagnosis against the code and applied it. On game exit,
`RestoreDefaultSettingsAsync` set `FanControl.SelectedPreset` / `SystemControl.SelectedPerformanceMode`
directly from the game-profile monitor's thread — WPF-bound properties, so it could throw
`InvalidOperationException` mid-restore, and even when it didn't, selecting them isn't the full
Balanced restore, so Performance mode could stay active with fans back on Auto. It now marshals
onto the UI dispatcher and calls `GeneralViewModel.ApplyBalancedProfile()` (power mode, Auto cooling,
runtime sync, persistence). Side effect worth knowing: that path also sets GPU Power Boost to Medium,
as the Balanced profile always has. The dispatcher path itself has no test shim in this suite; a
test covers the no-dispatcher case (returns cleanly instead of touching UI state).

**Confirmed on the owner-verified `8D87` (OMEN MAX 16-ak0xxx, thermal policy V1).** Its log shows 149
false watchdog failsafes and 150 "MAX mode reset sequence" runs in about seven hours. Each reset
drops a user-selected Max hold back to BIOS Auto, which is exactly the report "I set MAX, but after a
period it goes back to idle speeds". So this regression isn't limited to boards that lost fan control
- it also silently cancelled Max on boards where fan control works. (The reporter's other complaint,
quiet fans in Auto under a game, is the firmware-Auto under-cooling described in #189 and its design
doc, not this bug.)

### Community PR #196 Merged: Correct SKU Reporting, Measured-vs-Estimated Fan RPM, Honest Performance-Mode Trace

From WoofahRayetCode, the same reporter as #195, with on-device validation on an `8E35` laptop.
`SystemInfoService` took `Win32_ComputerSystemProduct.IdentifyingNumber` (`1H85430PWY`, a serial-like
asset value) as the "System SKU"; the real SKU is `Win32_ComputerSystem.SystemSKUNumber`
(`BP1Q1UA#ABA`), and the identifying number is now reported separately. Exports also label each fan
RPM as measured or a fan-level estimate, and the performance-mode apply trace now says whether any
firmware policy was actually applied instead of letting a selected mode read as proof that limits
changed. **This corrects my own earlier "CPU-identity conflict" note for `8E35`:** the two reports
weren't disagreeing about one laptop's CPU — they were two different configurations (different real
SKUs) and the SKU I'd matched them on was the wrong field. Notes now say so.

### Board `8DD0` (Victus 15-fb3xxx, Ryzen 7 7445HS + RTX 2050) Added, From PR #200

The contributor hit the v4.3.1 fan-control regression on this board and tested Max fan through WMI
with readback, manual level writes and keepalive on real hardware. The regression is fixed at the
source, so this is an identity entry mirroring `8DD2`. The PR marked it `UserVerified`; one
contributor's run isn't the full verification card, so it stays false. 1 new test.

### Board `8CC0` (OMEN 16-ae0xxx, i7-14650HX + RTX 4060) Given an Exact Entry

[#204](https://github.com/theantipopau/omencore/issues/204): resolved via OMEN16 family fallback,
where the log showed "Performance mode: nothing was applied (Direct EC writes disabled)". Exact
entry mirrors same-generation, same-thermal-policy `8D2F` for the one flag that makes Performance
take effect (WMI thermal-policy fallback) and stays conservative everywhere else — fan curves, GPU
boost, undervolt unclaimed until a verification run shows them working. 1 new test.

### A Frozen ACPI Thermal Zone Could Win CPU-Temperature Authority Back Forever, Causing an Unbounded Flip-Flop

[#198](https://github.com/theantipopau/omencore/issues/198) reported wildly fluctuating CPU
temperatures (~40°C to ~95°C) and fans stuck at max not responding to profile switches, on a Victus
16-r0xxx (board `8BBE`). Traced through the actual session log rather than guessing: the board's
ACPI Thermal Zone was pinned at a stuck 27.9°C for the entire session while LibreHardwareMonitor
tracked real, load-correlated readings from 56-95°C. `WmiBiosMonitor` already had a "frozen sensor"
detector for exactly this, but once it fired, the resulting recovery bypass disabled outlier
rejection *entirely* — with no check that a new reading had actually changed from the one that
triggered the freeze warning. The same stuck 27.9°C sailed through every poll, won CPU-temperature
authority, lost it a couple of readings later to a separate WMI/fallback-mismatch check, and won it
right back the very next poll — an unbounded flip-flop between a frozen wrong value and a real one.
Since thermal protection reacts to whichever value is briefly authoritative, this is what read to
the user as both "wildly fluctuating temperature" and fan behavior that wouldn't settle down.

Fixed by extracting the acceptance decision into a pure, testable `ShouldAcceptAcpiCpuReading`
method: the recovery bypass now only applies to a reading that has genuinely moved since the one
that triggered the freeze warning — a repeat of the exact stuck value is held to the normal outlier
check like any other tick, so a proven-frozen zone can never re-win authority by repeating the
value that proved it frozen. 5 new tests, including one confirmed to fail against the old logic
and pass against the fix. Full suite green.

**Not fully closed.** This fixes the flip-flop mechanism with high confidence from real log
evidence, but doesn't explain *why* this board's primary WMI BIOS temperature path was rejected in
the first place, forcing the fallback chain down to LHM and then ACPI — that deeper question (see
roadmap) is still open, and this fix hasn't been confirmed on the reporter's actual hardware yet.
Treat as implemented-pending-confirmation.

### Board `8DD2` (HP Victus 15-fb3xxx) Given an Exact Database Entry

[#197](https://github.com/theantipopau/omencore/issues/197) confirmed the exact ProductId that an
earlier pattern-matched entry (`#148`) had been waiting on. Same flags, inherited verbatim — this
is an identity-confidence upgrade (exact match instead of WMI-name-pattern match), not a widened
capability claim. Reporter's "no RGB keyboard" observation matches `#148`'s own report. 1 new test.

### Temperature-Warning Toast Now Says It's Informational, Right Where the Confusion Happens

Two independent reports ([#191](https://github.com/theantipopau/omencore/issues/191),
[#142](https://github.com/theantipopau/omencore/issues/142)) showed the same real confusion: the
"High Temperature Warning" toast read as if it were tied to fan behavior or a BIOS TCC/thermal
limit. It's neither — it's a separate, informational-only threshold with no connection to
`FanService`'s real thermal-protection ramp. Both reports got the same explanation in a GitHub
reply after the fact; the toast and in-app notification history now say so directly, at the moment
the confusion would actually happen, instead of only after someone asks.

### A Hot-Plugged Unpartitioned Drive Could Crash the Entire Hardware Worker

[#199](https://github.com/theantipopau/omencore/issues/199)'s field bundle showed
`WORKER CRASH: NullReferenceException` inside LibreHardwareMonitor's SMART backend
(`DiskInfoToolkit.Smart.SmartAttributeHandler.CheckSmartAttributeCorrect`, via
`StorageManager.HandleUnpartitionedDrive`) — a background hot-plug listener thread the library
spins up internally once storage monitoring is enabled, outside any of our own try/catch. An
unhandled exception there is fatal to the whole worker process, not just SSD-temperature readback,
so something as ordinary as plugging in a USB drive Windows sees as unpartitioned could take down
live fan/temperature telemetry until the worker respawned. Storage/SMART monitoring is off by
default now; the only cost is the SSD-temperature warning, which wasn't feeding any fan logic.

### Community PR #176 Ported: 8D87 Keyboard Lighting, AMD SMU Corrections, a Universal WMI Process-Monitor Fix

From tempestnano — an exceptionally well-evidenced PR, re-applied by hand rather than merged
because every file it touched had since moved from `src/OmenCoreApp` to `src/OmenCore.Core` (the
project-extraction refactor happened after it was branched). Four independent tracks, all measured
on `8D87` (OMEN MAX 16-ak0098nr) except the last:

- **Per-key keyboard brightness was a no-op.** `_brightness` was written on the per-key path and
  never read — now host-side arithmetic scales the colour map on its way to the MCU, separate from
  the per-cell level a user can paint into one key. Windows Dynamic Lighting silently repaints the
  keyboard within 33ms of OmenCore releasing host control (it's the second owner of the device and
  wins whenever nothing else holds it) — now reported read-only rather than fought over. New
  Fn+1/Fn+2 cycle staging matches measured firmware behavior (duplicate effect types collapse to
  the later one at the earlier position; the "Showing" profile is written last). This session's own
  unconfirmed mi_04 fallback (for when the MCU refuses a uniform fill at runtime, from a separate
  8D87 Discord report) is preserved and stays explicitly flagged as unconfirmed, distinct from this
  PR's hardware-measured claims.
- **"Apply AMD Limits" only ever sent the STAPM message**, which returns `Ok` on Strix Point and
  moves nothing. Now writes all four limits and verifies by reading the SMU's own power-metrics
  table back (read-only, measured index layout for table version `0x5D000B`). iGPU Curve Optimizer
  was being sent to Strix Point, Strix Halo and Mendocino, none of which upstream RyzenAdj supports
  there — now gated to the families that actually accept it, returning "unknown command" elsewhere
  instead of a write measured to do nothing. Strix Halo's MP1 mailbox address moved to the correct
  address set per two independent implementations agreeing (RyzenAdj, UXTU) — **not verified on
  real Strix Halo hardware**, flagged as such in the code.
- **Game-launch detection was polling the entire process table twice a second inside
  `WmiPrvSE.exe`**, invisible to Task Manager's view of our own process — measured at 14.2% of a
  core continuously. Not board-specific; every install paid this. Switched from the intrinsic WMI
  event classes (which have no real notification source and are serviced by full re-enumeration) to
  the extrinsic, kernel-pushed `Win32_ProcessStartTrace`/`Win32_ProcessStopTrace` — measured at 2.6%
  of a core after.
- **The hardware worker silently failed to start on exactly the builds users install.** A
  self-contained single-file publish points `AppDomain.CurrentDomain.BaseDirectory` at a temp
  extraction directory, not the real exe directory, so the worker lookup failed quietly (logged
  only at `Debug`) and telemetry degraded to slower in-process readings. Now tries the running exe's
  own directory first.

**Two bugs from the v4.3.0 review of this PR, fixed after the port.** That review (in
`ROADMAP_v4.3.0.md`) recommended against merging as-is over two specific defects, and I ported the
PR before re-reading it — both had survived:

- **A device effect could freeze into a still frame after a backlight toggle.** Unblanking rebuilt
  "is the painted map what's showing" from `_mapR != null`, which only says a map was *ever*
  painted. Paint → apply an effect → backlight off → on → change brightness re-sent the stale map
  over the running effect. The blank is now its own flag, so a blank/unblank never changes which
  picture is the base. Tested at the decision point; the device end has no colour readback.
- **The iGPU Curve Optimizer slider was shown on CPUs where every write is refused.** A stale
  CPU-name allowlist (`RYZEN AI MAX`, `7945H`, `7845H`, `6900H`) ran ahead of the new cited family
  table, so Strix Halo and Dragon Range showed a slider `SetIgpuCO` always rejects. It's now the
  intersection of both lists — narrowing only, no CPU gains a write it didn't have. Phoenix/Hawk
  Point owners were also told there's "no confirmed iGPU CO message" for their CPU, which is false
  (upstream maps PSMU `0xB7`); the reason now says OmenCore hasn't validated it yet.

Also fixes two CI jobs that were red independently of this work: `linux-qa.yml` pointed at the
Windows-only solution and had never once passed on Linux; `GameLibraryViewModelTests` was flaky on
a bare CI runner with no game platforms installed. Full suite: 1476 → 1592 passing (including the review fixes above).

### Board `88ED` (HP Victus 16-e0005np, Ryzen 7 5800H + RTX 3050 Ti) Given an Exact Entry

[#209](https://github.com/theantipopau/omencore/issues/209): exact conservative sibling of
`88EC`/`88EE`, resolving by ProductId instead of the low-confidence `16-e0` name pattern. Flags
mirror `88EC`'s, including the WMI thermal-policy fallback that board needed for Performance mode
to do anything at all — nothing granted beyond that. 1 new test.

### Board `8E35`: WMI Thermal-Policy Fallback Enabled After a Controlled Before/After Test

[#195](https://github.com/theantipopau/omencore/issues/195) (WoofahRayetCode) ran the exact
controlled test this was waiting on: Quiet vs Performance CPU package power, with the Apply Trace
confirming Direct EC writes are disabled and the WMI thermal-policy fallback was never attempted —
a 0.0 W difference between modes. Performance mode was doing nothing on this board.
`AllowDecoupledWmiThermalPolicyFallback` is now enabled, the same fallback `88EC`/`8CC0` needed for
the same "EC disabled, no fallback" shape. Narrowly conditioned (only engages when EC limits are
unavailable), so it can't override a working EC path elsewhere. **Awaiting a re-run of the same
controlled test to confirm it actually moves CPU power** rather than just being attempted. 1 new
test.

### Board `8BB1` (Victus 15-fa1xxx): No Longer Claims a Specific Model Year

[#202](https://github.com/theantipopau/omencore/issues/202): the ambiguous-ProductId entry for
this board's Victus side hardcoded "(2022)" into its name regardless of which year actually
resolved to it. Reporter's own diagnostics confirmed a `fa1082wm` unit — a 2024 model — resolving
here, which the name flatly contradicted. The `15-fa1` name pattern spans more than one year and
can't tell them apart, so it no longer asserts one; capability flags are unchanged. 1 new test.

### Three Fixes Found by Cross-Checking Ohman's Last Week of Releases Against Our Code

Ohman (the alternative OMEN Gaming Hub replacement) shipped v1.0.6 → v1.2.1 between 2026-09-15 and
09-22. Each fix was checked against OmenCore's own code; three matched real bugs here. Ohman's
repository carries no licence, so nothing was copied — these are OmenCore's own implementations of
the same findings.

- **Max could be accepted by the firmware and silently ignored, forever.** When Max-mode telemetry
  dropped, OmenCore re-sent `SetFanMax(true)` and only fell back to writing the top fan level if that
  command *returned false*. Ohman found boards (`8A26`, `8E5E`) where the firmware accepts Max and
  does nothing; our own `#178` bundle for `8E5E` fits (Guided Fan Verification failed at CPU@100%).
  Two consecutive accepted-but-ineffective reasserts now escalate to the level-ceiling write that
  already existed for the refused case. **Pending field confirmation** on an affected board.
- **Auto → Max → quit within 5 seconds could leave the firmware holding Max after exit.** The
  auto-restore's reset cooldown (meant to stop repeated resets hammering the firmware) also skipped
  the `SetFanMax(false)` step, then marked Max inactive anyway. A live Max now always gets its reset.
- **The tray's refresh-rate shortcuts changed the external monitor on a docked laptop.** Carried
  forward since v4.3.1 as "needs a docked rig". The root cause turned out to be visible in code:
  every tray action passed no device, which Windows reads as "primary display". The built-in panel
  is now found through the Windows display-configuration API's output-technology field (embedded
  DisplayPort / internal / LVDS); if none is active (lid closed, desktop) behavior is unchanged. The
  live query needs real display hardware; tests pin the classification and the marshalled struct
  sizes it depends on. Still wants a docked-laptop confirmation.

Checked and **not** affected: Ohman's dark spacebar/Copilot keys on OMEN MAX 16 (our measured `8D87`
layout covers all 176 LEDs; Ohman painted through the 120-lamp interface), its battery-time firmware
handoff on the Transcend 14 (OmenCore only switches user-chosen presets on AC/battery), and its
thermal guard lowering already-faster fans (OmenCore has refused to since bug fix #32).

### Log Buffer No Longer Rebuilds the Entire Displayed Buffer on Every Single Log Line

Picked up standalone from community [PR #147](https://github.com/theantipopau/omencore/pull/147) —
reviewed in full last cycle; its other two changes had real bugs (a tray-icon cache that never
populates, a dashboard uptime timer that can't restart once paused) and it was never merged as-is,
but this one change was correct. `MainViewModel`'s in-app log view rebuilt its whole displayed
buffer with `string.Join` on every single incoming log line; now appends incrementally to a
`StringBuilder` and only pays for a full rebuild on the rarer tick where the 200-line cap is
actually exceeded. Output is byte-identical to the old behavior in every case — a performance
change, not a behavior change. 5 new tests, extracted into a pure testable method since the
original code was wrapped in a WPF `Dispatcher.BeginInvoke` this test suite has no shim for.

---

## Investigated, Not Yet Actioned

- **[#198](https://github.com/theantipopau/omencore/issues/198) — remaining question after the fix above.** Why did this board's WMI BIOS CPU-temperature path get rejected in the first place this session, forcing the fallback chain all the way down through LHM to a broken ACPI zone? Board has no exact database entry yet (resolves via generic Family fallback; the `RequiredCpuVendor` guard correctly prevents it from inheriting the AMD-only `8C2F` profile a sibling board with the same WMI name pattern uses — verified working as intended, not a suspect here).
- **HP WMI command `0x23`'s sensor-index semantics — now backed by two independent sources, and diagnostics can finally collect the evidence.** Flagged last cycle from Ohman's decompiled OGH device-library strings (`0=IR, 1=Ambient, 2=PCH, 3=VR`). An unrelated, independently-researched community report on `#189` (board `8D87`) reverse-engineered the same firmware command and confirmed index 0 = IR used as Gaming Hub's own fan-curve input — and found no Linux ACPI zone exposes the same reading on that board. The new `wmi-temperature-sensors.txt` diagnostic (above) is the first step toward answering whether OmenCore's own CPU/GPU indices (currently 1/2, per OmenMon's convention) are right for every board this project supports, or only some — no code changed on the indices themselves yet, deliberately, pending real per-board evidence.
- **Opt-in automatic software fan-curve controller for boards where firmware Auto under-cools (`#189`).** Deferred last cycle for "a dedicated design pass"; now considerably de-risked — the reporter has since built and shared a working reference daemon (`omen-fanctl`) on top of an already-extracted factory fan-curve table and WMI payload format. Still needs OmenCore's own model-allowlist, curve-validation, and crash-safe-recovery design before any code lands.
- **Board `8D87`'s RTX 5080 stays capped near 80-105 W in Performance mode; OMEN Gaming Hub and third-party tools reach 175 W** (Discord, papap). Not guesswork here — `docs/8D87-OMEN-MAX-16-SUPPORT-PLAN.md` already reverse-engineered the exact mechanism: two EC bits (`OGHP`, `PROH`) gate a configurable-TGP adder that OmenCore has never driven. The same investigation found a real hazard: forcing the unlock on an undersized adapter left the GPU in a degraded state that persisted after the manipulation stopped and only cleared on reboot, on hardware with a history of power-related BSODs. Not implemented pending a deliberate, explicit-opt-in, adapter-wattage-proportional design (the doc's own T3 plan) rather than a blind unlock — this needs a dedicated pass, not a quick patch.

---
