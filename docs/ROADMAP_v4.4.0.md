# OmenCore v4.4.0 Roadmap

**Status:** In progress. Opened 2026-09-16, one day after v4.3.1 shipped.
**Base version:** v4.3.1
**Predecessor doc:** `docs/ROADMAP_v4.3.1.md` — carried the 4.3.0 → 4.3.1 cycle. That document is now historical record.

---

## Why This Cycle Exists

v4.3.1 shipped 2026-09-15. The next day, two new GitHub issues came in: a routine model-support
request (#197) and a field report (#198) that looked routine on the surface — "Model Verification"
in the title — but whose body described real, safety-adjacent fan/thermal behavior (wild
temperature swings, fans stuck at max, suspected bad RPM readback). Read the actual diagnostics
export rather than triaging from the title alone, per this project's standing discipline, and it
turned into the most substantial open item of the two by a wide margin — digging into the session
log found and fixed a real, reproducible bug (see Done below) rather than just filing the report.

Separately, asked directly whether anything significant had been deliberately left off recent
cycles that was now worth pulling forward — three items came back re-evaluated as more ready than
when they were deferred, plus two smaller pickups. See "Candidates Pulled Forward" below.

---

## Done

### GitHub #198: Frozen ACPI Thermal Zone Could Win CPU-Temperature Authority Back Forever

**Investigation.** The report read as routine ("Model Verification") but described real behavior:
wild CPU temperature swings (~40-95°C), fans stuck at max not responding to profile switches,
suspected bad RPM readback, on Victus 16-r0xxx board `8BBE` (Intel i7-13700H + RTX 4060). Checked
identity resolution first — confirmed this board correctly falls to generic Family fallback rather
than incorrectly inheriting the AMD-only `8C2F` profile a sibling board with the same WMI name
pattern (`16-r0xxx`) uses; that guard (`RequiredCpuVendor = AMD`, added after #172, the *other* open
report on this exact board) works as designed here. Not the cause.

Then read the actual session log (`OmenCore_20260915_134957.log`) as one continuous timeline rather
than isolated warning lines, and grepped every "CPU thermal authority switched" line across the
full session:

```
CPU thermal authority switched: WMI BIOS -> ACPI Thermal Zone
CPU temp fallback active: WMI/ACPI reading looked invalid (27.9°C), using LibreHardwareMonitor (64.0°C)
CPU thermal authority switched: ACPI Thermal Zone -> LHM Fallback; reason: ... (27.9C) vs fallback (56.0C)
CPU thermal authority switched: LHM Fallback -> ACPI Thermal Zone; reason: ACPI thermal zone accepted for CPU authority
CPU thermal authority switched: ACPI Thermal Zone -> LHM Fallback; reason: ... (27.9C) vs fallback (62.0C)
... (repeats roughly every 30-90 seconds for the entire session, every single "ACPI rejected"
     line showing exactly 27.9C against a varying, load-correlated LHM reading of 56-74°C)
```

**Root cause, traced to source.** `WmiBiosMonitor`'s ACPI-zone temperature path has an outlier
filter (reject a reading more than `MaxAcpiDeltaFromWmiC` = 18°C away from the currently-trusted
temperature) that gets *disabled entirely* once the existing frozen-sensor detector (the "🥶 CPU
temperature appears frozen" warning) fires — the intent being to let a genuinely-recovered sensor
back in without waiting through a full rejection cycle. The bug: the bypass never checked whether
the *new* reading had actually changed from the one that triggered the freeze warning. On this
board, ACPI was stuck at exactly 27.9°C for the whole session, so every single poll while frozen
sailed straight through the bypass, got accepted as CPU-temperature authority, got demoted a
reading or two later by the separate WMI/fallback-mismatch check (which correctly noticed 27.9°C
didn't match LHM's real readings), and got re-accepted the very next poll because the bypass was
still wide open — an unbounded flip-flop with no way to ever resolve. Since `FanService`'s thermal
protection reacts to whichever temperature is briefly authoritative, this produces exactly the
reported symptoms: the *displayed* temperature swings wildly (jumping between ~28°C and the real
56-95°C range as authority flips), and fan behavior looks stuck/unresponsive because the value
driving thermal-protection decisions keeps changing out from under it.

**Fix.** Extracted the acceptance decision out of the inline polling loop into a pure, static,
independently-testable method (`ShouldAcceptAcpiCpuReading`), matching this file's own established
pattern (`SelectCpuThermalZone`, `IsIdenticalTempSuspicious`) rather than leaving it as inline
boolean logic. The recovery bypass now only applies when the new reading has genuinely moved (≥
0.1°C) from the value that triggered the freeze warning — a repeat of the exact stuck value is held
to the ordinary outlier check like any other tick, so a proven-frozen zone can never re-win
authority by repeating the value that proved it frozen in the first place. A genuinely-recovered
sensor is still believed immediately, since that's the one real job the bypass has to do.

**Verification.** 5 new tests in `WmiBiosMonitorAcpiOutlierAcceptanceTests` (added to the existing
`WmiBiosMonitorFreezeHeuristicTests.cs`), covering: the exact #198 shape (frozen zone repeating its
stuck value against a real cached temperature — rejected), a genuine recovery (a materially
different reading while frozen — accepted), and the unchanged baseline behavior outside
frozen-recovery mode. Confirmed the key test fails against the pre-fix logic (temporarily reverted
the fix, re-ran, watched it fail with the exact #198 numbers, restored the fix) before considering
this done — same "prove the regression test actually has teeth" discipline used for the config-
persistence fix last cycle. Full solution build clean, full test suite green.

**What this does not close.** The flip-flop mechanism is fixed with high confidence from real log
evidence, but *why* this board's WMI BIOS temperature path (the normal first choice) got rejected
in the first place this session — forcing the fallback chain down through LHM to a broken ACPI zone
at all — is still unexplained. See "Open Investigations" below. Also not yet confirmed on the
reporter's actual hardware; this fix is implemented-pending-confirmation, not closed.

### GitHub #197: Board `8DD2` Promoted From Name-Pattern Match to an Exact Entry

Confirmed the exact ProductId (`8DD2`) that `#148`'s earlier WMI-name-pattern entry (`15-fb3`) had
been waiting on. No diagnostics export accompanied the report, so the new entry inherits `#148`'s
flags verbatim rather than widening anything — this is purely an identity-confidence upgrade (exact
match instead of pattern match). Reporter's "no RGB keyboard" observation matches `#148`'s own
independent report of the same thing, good corroboration. 1 new test
(`GetCapabilities_8DD2_VictusFb3xxx_ResolvesToExactEntryInsteadOfNamePattern`).

### `ThermalMonitoringService`'s Warning Toast Now Explains Itself

Both smaller pickups from "Candidates Pulled Forward" below are done as of this entry. First: two
independent reports (`#191`, `#142`) showed the same real confusion — the "High Temperature
Warning" toast read as tied to fan behavior or a BIOS TCC limit, when it's a wholly separate,
informational-only threshold with no connection to `FanService`'s real 90°C thermal-protection
ramp. Rather than reconsidering the 85°C default itself (a judgment call affecting every existing
user, not something to change on the strength of two reports that were both about wording, not the
number), fixed the actual point of confusion: the toast text and in-app notification history now
say directly that it's informational and independent of fan/BIOS behavior, right at the moment the
notification fires — not only after someone asks on GitHub. No behavior change, no existing test
depended on the exact wording.

### Log Buffer Performance Fix Picked Up From `PR #147`

Second smaller pickup: reviewed `PR #147` last cycle and found its log-buffer `StringBuilder` change
correct, but the PR's other two changes had real bugs (a tray-icon cache that never populates in the
default configuration, a dashboard uptime timer that can't restart once paused) and it was never
merged as-is. Cherry-picked just the good change into `MainViewModel`: the in-app log view no longer
rebuilds its entire displayed buffer with `string.Join` on every single incoming log line, only on
the rarer tick where the 200-line cap is actually exceeded. Extracted into a pure, static, testable
method (`AppendLogLineAndRebuildBuffer`) rather than leaving it inline inside the WPF
`Dispatcher.BeginInvoke` callback the original code ran in — this test suite has no `Application`
shim, so testing it required pulling the logic out from behind the dispatcher dependency, not just
copying the PR's diff verbatim. 5 new tests confirm the output is byte-identical to the old
`string.Join` behavior in every case, including past the cap.

### Diagnostics: All Four HP WMI Temperature Sensor Indices Now Captured

First concrete step on the `0x23` sensor-index question (see "Candidates Pulled Forward" below):
added `HpWmiBios.ProbeAllTemperatureSensors()`, a read-only probe of all four documented indices,
and wired it into the diagnostics export as a new `wmi-temperature-sensors.txt` file labeled against
both the IR/Ambient/PCH/VR mapping two independent research efforts found and OmenCore's own CPU/GPU
convention. Writes nothing to the firmware, changes no existing temperature-reading behavior —
purely so the next round of field reports can finally carry the evidence needed to answer the
question with data instead of guessing.

### Everything Landed 2026-09-20 → 09-23 (detail in `CHANGELOG_v4.4.0.md`)

- **Three v4.3.1 regressions** (V0 SystemDesignData bit disabling fan control on 7+ boards; 2-min
  cadence tripping the 90s watchdog; un-wakeable monitor loop) — fixed, tests from real bytes.
  Re-confirmed live on 4.3.1 in `#191` (8C9C: 90-106s stall cycle, one 96°C emergency) and `#199`
  (8BA9: 257/155/253/35 "frozen" events per log). Still only on `main`; **no hotfix cut yet.**
- **Worker crash on hot-plugged unpartitioned drives** (`#199`) — LHM storage/SMART off by default.
- **PR #176 ported** (8D87 lighting, AMD SMU four-limit write + readback, iGPU CO family gate,
  WMI process-trace subscription, worker path under single-file publish, two CI jobs), plus both
  defects the v4.3.0 review of it flagged (effect freeze after backlight toggle; iGPU slider on
  parts that always refuse). PR #196 merged; PR #200 re-implemented; PR #150 already adopted.
- **Ohman v1.0.6–v1.2.1 cross-check:** ignored-Max escalation, Max latched after quick exit,
  tray refresh-rate targeting the internal panel. Three other Ohman fixes checked, not affected.
- **Boards:** `8CC0`, `8DD0`, `88ED` added; `8E35` WMI policy fallback on after `#195`'s controlled
  0.0 W test (awaiting re-test); `8BB1` Victus side no longer claims "(2022)" (`#202`).
- **`#206`** game-exit restore marshalled to the UI thread (reporter's tested fix).
- **Board `8D87` GPU TGP unlock** — built, then gated off in pre-release review (unproven EC write
  path, wrong pin model, wrong verification target). See Open Investigations.
- **Backlog swept:** 53 stale pre-4.0 issues closed, model-support requests left open and labeled.

---

## Open Investigations

### Board `8D87` GPU TGP unlock — code exists, disabled until validated on hardware

`GpuTgpUnlockService.EcWritePathValidated` stays false until, on a real 8D87: (1) EC writes through
the ACPI ports are shown to land in the MMIO window (design doc T3.1 covered reads only); (2) the pin
is rewritten as a loop that keeps re-pinning `OGHP`/`PROH` every <=2 ms across the GC22 call instead of
before it; (3) success is read from the enforced power limit (`nvidia-smi enforced.power.limit`
>= 170 W), not NVAPI draw. The under-rated-adapter case (the one users actually hit) also needs the
doc's T3.5 proportional cap before it can be offered at all. Related: `#123` (8D41, TGP stuck at 80 W
even though hp-wmi sends 0x22) looks like the same `OGHP` gate on a different board.

### #198 follow-up — why did WMI BIOS temperature get rejected in the first place?

The flip-flop mechanism itself is fixed (see Done above), traced from
`OmenCore_20260915_134957.log`, all times local `-06:00`. But the session log shows the fallback
chain had *already* moved past WMI BIOS before the excerpt in the fix write-up even starts: `WMI
BIOS -> ACPI Thermal Zone` at session start, then a wobble between ACPI and LHM. That means this
board's normal first-choice temperature source (`HpWmiBios.GetTemperature()`, command `0x23`) was
rejected as invalid from very early in the session — not investigated yet, and the actual reason
matters:

- If WMI BIOS temp works reliably on this board's firmware, it should be preferred outright rather
  than falling through to LHM (which itself timed out) and then a proven-unreliable ACPI zone —
  fixing this could make the whole fallback chain unnecessary on this board.
- If WMI BIOS temp doesn't work here either, that's a separate, board-specific gap worth its own
  conservative database entry once confirmed.
- Also unconfirmed: why LibreHardwareMonitor itself times out for CPU temp on this specific
  board/CPU combination (13th Gen Intel i7-13700H) — reproducible, or a one-off?

**What this is not (checked, ruled out):** not a capability-database identity bug, and not the
`8C2F`/AMD-profile crossover bug from #172 — both confirmed in the Done write-up above. Also worth
noting: Ohman's own board list (`docs/laptops.md`) marks this exact board id (`8BBE`, a different
owner's i5-13500H variant) "verified... nothing wrong on it" via their generic firmware-driven
approach as of 2026-09-16 — supporting evidence that this is an OmenCore-specific telemetry-chain
bug rather than a hardware limitation on the board itself. Ohman's approach also has no equivalent
of OmenCore's three-way WMI/LHM/ACPI fallback dance at all — it reads ACPI zones directly (hottest
valid) or WMI with no separate LibreHardwareMonitor layer, which structurally can't produce this
exact flip-flop failure mode. Not a case for ripping out OmenCore's fallback chain (LHM genuinely
earns its place as a cross-vendor safety net on boards where neither WMI nor ACPI works at all),
but worth keeping in mind if this class of bug resurfaces elsewhere in the chain.

**Requested from reporter:** a fresh diagnostics export captured *while* fans are stuck at max (not
after, to catch the "stuck for minutes" version of the symptom this session's capture didn't), and
— if comfortable — a second export with Intel XTU's service fully stopped (already flagged in
diagnostics as blocking MSR access for undervolt; unrelated to this bug but worth ruling out as a
confound on the same machine, since XTU is known to interfere with other vendor tools' hardware
access on some systems).

---

## Candidates Pulled Forward From Past Cycles

Asked directly whether anything significant had been left off recent builds that was now worth
investigating — these had genuinely new evidence behind them since being deferred. Two smaller
pickups from this list are already done (see Done above); the rest are recorded here so they don't
get lost.

### HP WMI command `0x23`'s sensor-index semantics — two independent sources now agree, diagnostics can finally collect the evidence

Flagged in the v4.3.1 cycle from Ohman's own decompiled OGH device-library strings, found in its
`Support.cs`: sensor index `0=IR, 1=Ambient, 2=PCH, 3=VR`, with Ohman's own code comment noting "on
a board whose ACPI zone reports a constant, [index 1] is the only moving number we have — so
collect all four and find out which do move." Independently, `#189`'s community researcher
reverse-engineered the same firmware command on a *different* board (`8D87`) and confirmed index 0
= IR is exactly what OMEN Gaming Hub's own fan-curve engine uses as its thermal input — and that
neither Linux ACPI thermal zone on that board exposes the same reading. Two unrelated research
efforts landing on the same mapping is real corroboration, not coincidence.

OmenCore's own `HpWmiBios.GetTemperature()`/`GetGpuTemperature()` also use command `0x23`, sending
index `0x01` for CPU and `0x02` for GPU — sourced from OmenMon's own convention, not from either of
the sources above. Whether that's simply a *different* board generation's mapping (plausible — HP's
own IR/Ambient/PCH/VR sensor set may not exist identically on older boards OmenMon supports) or an
actual mismatch worth fixing is still unresolved. Directly relevant to #198's open question above:
understanding which WMI temperature index is trustworthy per board could explain why that board's
WMI path gets rejected while a working index might exist. Worth a dedicated investigation pass —
survey which boards in the database use which convention, and whether `0x23` index `1` (`CPU` in
OmenCore's assumption) ever demonstrably disagrees with a confirmed-good reading on real hardware —
before touching the indices OmenCore currently ships with.

**First step taken this cycle (see Done above):** added `HpWmiBios.ProbeAllTemperatureSensors()` and
a new `wmi-temperature-sensors.txt` diagnostic that captures all four raw index readings on every
export, going forward. Deliberately not touching the CPU=1/GPU=2 indices themselves yet — this is
evidence-gathering only, so the actual question above gets answered from real per-board data over
the next few cycles' worth of diagnostics exports, not guessed at now.

### Opt-in automatic software fan-curve controller (`#189`) — considerably de-risked since deferral

Deferred in the v4.3.0 cycle for "a dedicated design pass": an exceptionally well-researched report
showing HP OMEN Gaming Hub runs a userspace controller that evaluates CPU/GPU/IR curves and writes
discrete fan levels through HP WMI, reproduced in a standalone Linux prototype that raised sustained
CPU temperature by curve accuracy alone (85.5°C vs. firmware Auto's 92-99°C) on board `8D87`. Since
then, the reporter has built and published a working reference implementation
(`github.com/mbilykov/omen-fanctl`) on top of an already-extracted 128-byte factory fan-table and
WMI payload format. Most of the reverse-engineering risk that justified deferring this is now gone;
what remains is a genuine OmenCore design exercise — a real model-allowlist mechanism (opt-in,
explicitly-validated boards only, matching this project's evidence-gate convention for anything
that writes fan commands), curve validation against the factory table, and a crash/SIGKILL-safe
recovery story so an interrupted daemon never leaves fans in an unexpected state. Not started; worth
scoping as its own design doc before any code lands, given it's a new automatic write-loop rather
than a fix to an existing one.

Both smaller pickups originally listed here (`PR #147`'s log-buffer fix, `ThermalMonitoringService`'s
toast wording) are done — see Done above.

### Carried forward, still blocked on evidence neither cycle has had

- ~~Tray refresh-rate targeting the wrong display when docked~~ — fixed 2026-09-23 via internal-panel
  detection; still wants one docked-laptop confirmation.
- ~~`8E35` Performance mode applying nothing~~ — evidence received (`#195`, 0.0 W), flag enabled
  2026-09-23; awaiting the reporter's re-test. Siblings `8D24`/`8D26` deliberately unchanged until
  someone on those boards shows the same trace.

---

## Standing Rules (unchanged, carried from v4.3.1)

- **Evidence gate.** Fan/EC/thermal/OC/UV *behavior* changes need field validation before shipping.
  Architecture, performance, display-honesty, and pure-UI items do not.
- **Search every other open/closed issue mentioning the same ProductId before adding or editing a
  board entry** — mandatory since two near-misses several cycles back; still in force. (Caught
  `#172` as the sibling report on `8BBE` this way before assuming #198 was a fresh identity issue.)
- **Read the actual diagnostics/logs before triaging a report by its title.** #198's title read
  as routine "model verification"; the body and attached logs described something much more
  substantial. Same discipline that caught #191 was a real data-loss bug last cycle, not a one-off
  complaint.
- **One item at a time, verified before moving on.** Build clean, full suite green.
- **Update this document as you go.** Check items off only once verified, with a one-line note on
  what changed and which files.
