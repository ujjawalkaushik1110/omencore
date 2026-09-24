# Opt-In Automatic Fan-Curve Controller — Design Doc (GitHub #189)

**Status:** Design only. No code has landed. Written to satisfy the v4.3.0 cycle's own deferral
note ("needs a dedicated design pass") now that the reverse-engineering risk that justified
deferring it is largely gone — see §0.
**Target for the initial MVP:** board `8D87` (OMEN MAX 16-ak0xxx, 2025 AMD) only. Everything below
is scoped narrowly on purpose; widening to the other 16 board IDs #189 lists as running the same
firmware mechanism is future work, not part of this doc.
**Source evidence:** [GitHub #189](https://github.com/theantipopau/omencore/issues/189) — an
exceptionally well-researched report combining ACPI/HP WMI decompilation, a decompile of OMEN
Gaming Hub's own `PerformanceControl` assemblies, and a validated standalone Linux prototype. Full
detail (exact factory curve tables, WMI payload bytes, timing data) lives in the issue; this doc
is about how it maps onto OmenCore's actual code, not a restatement of the research itself.

---

## 0. Why this is more ready now than when it was deferred

The v4.3.0 cycle's deferral reasoning was: "what you're proposing is a genuinely new automatic
write-loop ... it needs its own dedicated design pass: a real model-allowlist mechanism, curve
validation, a crash/SIGKILL-safe recovery story, and hardware to validate against." Since then:

- The reporter built and published a working reference daemon
  (`github.com/mbilykov/omen-fanctl`) reproducing the exact factory-curve behavior on real
  hardware — most of the "does this actually work" risk is retired.
- Re-reading the issue against OmenCore's *current* code (this doc, §2) found that OmenCore
  already has almost every low-level primitive this feature needs — it was never really a "new
  write loop," it's a new *curve source* feeding an existing write path. That materially changes
  the shape of the remaining work.
- `FanService` already has a general-purpose "restore firmware Auto" safety net that runs from
  `Dispose()` regardless of which mode was active (§3) — the crash/exit-safety story this feature
  needs is not new infrastructure, it's inheriting infrastructure that already exists for every
  other fan-control mode in the app.

What's still genuinely missing is enumerated in §4, and it's smaller than the original ask made it
look.

---

## 1. The mechanism, compressed (from #189)

On `8D87`, HP OMEN Gaming Hub's Auto mode is not a firmware behavior — it's a userspace controller
that Gaming Hub itself runs while active:

1. Samples CPU, GPU, and IR/chassis temperature once per second (IR via HP WMI `0x23`, index `0` —
   not the same as OmenCore's own `GetTemperature()`/`GetGpuTemperature()`, which use indices `1`
   and `2`; see `docs/ROADMAP_v4.4.0.md`'s open `0x23` investigation for why that distinction
   matters here specifically).
2. Runs each sensor's raw/EWMA-smoothed temperature through its own asymmetric rise/fall curve
   table (exact Performance-mode tables for `8D87` are in the issue — CPU, GPU, and IR each map a
   temperature to a fan *level*, not an RPM).
3. Takes the highest of the three requested levels.
4. Maps that CPU-side level to the paired GPU level via a **factory mapping table** read from HP
   WMI command `0x2F` — the mapping is *not* 1:1 (e.g. CPU level 47 pairs with GPU level 49, CPU
   60 pairs with GPU 58 on this board's exact table).
5. Writes the two levels via HP WMI command `0x2E` — the same command OmenCore's own
   `HpWmiBios.SetFanLevel()` already sends.

Firmware Auto (`pwm1_enable=2` / OmenCore's own "Auto" fan mode) with nobody running this
controller settles at a fixed ceiling regardless of temperature — measured on `8D87` at a flat
3,400/3,600 RPM while CPU sat at 92-99°C under sustained load. This is not a bug in OmenCore or in
the firmware; it's firmware Auto doing exactly what it's designed to do without the userspace
policy layered on top of it by Gaming Hub. The reproduced controller held CPU at 85.5°C under the
same load by commanding real, temperature-responsive fan levels instead.

---

## 2. What OmenCore already has (checked against current code, not assumed)

| Piece the feature needs | OmenCore's current state |
|---|---|
| Write discrete CPU/GPU fan levels via HP WMI | **Have it.** `HpWmiBios.SetFanLevel()` sends command `0x2E` (`CMD_FAN_SET_LEVEL`) — the exact command #189's reverse-engineering identifies. `FanService.SetFanSpeeds(cpuPercent, gpuPercent)` already exposes independent CPU/GPU level setting, not a single shared value. |
| Read back current fan levels | **Have it.** `HpWmiBios.cs`'s `CMD_FAN_GET_LEVEL` (`0x2D`) and `CMD_FAN_GET_LEVEL_V2`/`CMD_FAN_GET_RPM` (`0x37`/`0x38`, OMEN Max 2025+ — covers `8D87` specifically) are already implemented. |
| A "curve mode" concept distinct from Auto/Manual/Max | **Have it.** `FanService` already has `_curveEnabled`, `ApplyCustomCurve()`, and an independent-curves path (`ApplyIndependentCurvesAsync`, added this cycle for the `#198` investigation) that evaluates separate CPU/GPU curves and applies both levels — structurally the same shape #189's factory-curve engine needs, just fed by a user-drawn curve today instead of a factory table. |
| Restore firmware Auto on exit/crash | **Have it, for free.** `FanService.Dispose()` already calls `RestoreAutoControlSerialized()` unconditionally as part of its general shutdown path — this isn't mode-specific, so a new automatic-curve mode inherits this safety net automatically by being *another mode inside FanService*, not a separate process. |
| Reject a proven-frozen/wrong sensor reading | **Have it.** `WmiBiosMonitor`'s frozen-sensor detector and (as of this cycle's `#198` fix) `ShouldAcceptAcpiCpuReading` already exist for CPU temperature; the same discipline should extend to whatever feeds this controller's CPU/GPU/IR inputs. |
| A model-allowlist / conservative-by-default gating pattern | **Have it, as a pattern.** Every capability in `ModelCapabilityDatabase` already defaults to unsupported until a specific board's entry says otherwise (`SupportsGpuPowerBoost`, `SupportsUndervolt`, etc., all default `false`). A new `SupportsAutomaticFanCurve` flag (default `false`, set `true` only on `8D87`'s entry to start) follows the exact existing convention — no new gating mechanism needs inventing. |
| Read the IR sensor (WMI `0x23` index `0`) | **Partially have it.** This cycle added `HpWmiBios.ProbeAllTemperatureSensors()` (diagnostics-only, all four indices) — the same underlying WMI call this feature needs for its IR input, just not yet exposed as a live-monitoring value. |

## 3. What's genuinely missing

| Gap | Scope |
|---|---|
| Read the `0x2C` (fan types/capabilities) and `0x2F` (fan mapping table) commands | New, small, **read-only** additions to `HpWmiBios.cs`, same shape as the `0x23` probe added this cycle. `0x2F`'s response format is fully documented in #189 (8-byte header + up to 15 three-value records, zero-terminated) — no guessing required. |
| The curve-evaluation engine itself (EWMA smoothing, asymmetric rise/fall, max-of-sensors selection, IR/CPU/GPU curve tables) | New. This is genuinely new logic, but it's pure temperature-in/level-out math with no hardware access — fully unit-testable using the *exact* factory table numbers #189 already extracted, the same way `SelectCpuThermalZone`/`ShouldAcceptAcpiCpuReading` are tested against real field numbers rather than synthetic ones. |
| A live IR/chassis temperature reading in `HardwareMonitoringService`/`WmiBiosMonitor` | New. Currently only probed for diagnostics; this feature needs it sampled continuously, with the same frozen/outlier defenses already applied to CPU temperature. |
| `SupportsAutomaticFanCurve` capability flag + `8D87` database entry | New, one flag following the established pattern, one board entry update. |
| A Settings UI toggle, scoped to "explicitly validated boards only, opt-in, off by default" | New UI surface — small, follows existing Settings-page patterns for other opt-in features (e.g. `EnableStartupHardwareRestore`). |
| Deciding *when* the controller is allowed to run (Performance mode only? Any mode where firmware is in Auto?) | Design decision, not yet made — see §5. |

Nothing in this list requires a new process, a new IPC surface, or new crash-recovery
infrastructure — the earlier framing of this as "a genuinely new automatic write-loop" undersold
how much of it is already-existing `FanService` machinery wearing a new curve source.

---

## 4. Proposed architecture

Add automatic-curve as a **mode inside `FanService`**, not a separate service or daemon:

```
FanService
  ├─ existing modes: Auto, Manual, Max, Curve (user-drawn), Independent Curves
  └─ new mode: AutomaticFactoryCurve
        ├─ gated by Capabilities.SupportsAutomaticFanCurve (model-database flag, default false)
        ├─ gated by a Settings toggle, default off even on a supported board
        ├─ only engages while PerformanceMode == "Performance" (see §5 for why)
        ├─ reads CPU/GPU temperature through the same WmiBiosMonitor path everything else uses
        │   (inheriting its frozen-sensor/outlier defenses), plus a new IR reading added
        │   specifically for this feature
        ├─ evaluates the three curve tables + EWMA smoothing (new, pure, unit-tested engine)
        ├─ maps the winning CPU level to a GPU level via the `0x2F` table (read once per session,
        │   cached — the table is a firmware constant, not something that changes at runtime)
        └─ calls the SAME FanService.SetFanSpeeds(cpuPercent, gpuPercent) / SetFanLevel path every
            other mode already uses — no new hardware-write code path
```

Falling out of the existing mode machinery for free:
- Switching away from Performance mode, or the user picking any other fan mode, already runs
  through `FanService`'s existing mode-transition logic — same place `_curveEnabled` gets cleared
  today.
- `Dispose()`'s unconditional `RestoreAutoControlSerialized()` call covers app exit and crash
  paths without new code, exactly as it does for every existing mode.
- The existing `EcOperationCoordinator`/command-serialization the class already wraps every write
  in applies here too — no new concurrency story needed.

## 5. Open design decisions (not resolved by this doc — need a maintainer call or more evidence)

- **Scope of engagement.** #189's own prototype ran continuously while a chosen profile was
  active. OmenCore's equivalent decision: engage only in Performance mode (narrowest, matches the
  original report exactly, lowest risk), or in any mode where firmware Auto is selected (broader,
  matches user intent more closely, more exposure)? Recommend starting with **Performance-mode
  only** — it's the literal scope of the bug report, and widening later is a strict expansion, not
  a breaking change.
- **Cadence.** #189's controller samples every second, recalculates roughly every five. OmenCore's
  general telemetry poll cadence varies by UI state (`GetEffectiveCadenceInterval()`); this feature
  likely needs its own fixed, tighter cadence while active, independent of UI-visibility-driven
  backoff — thermal safety shouldn't slow down because the window isn't focused.
- **Per-board curve tables vs. reading `0x2F`/deriving live.** The MVP can ship with `8D87`'s exact
  extracted tables hardcoded (matches this project's existing pattern of board-specific constants
  in `ModelCapabilityDatabase`), deferring "derive the mapping generically from any board's `0x2F`
  response" to a later widening pass once more boards are validated.
- **Emergency override.** #189's prototype has a raw 92°C override to 100%. OmenCore already has
  this at the `FanService` level (`FanService`'s real thermal-protection ramp, independent of
  whatever mode is active) — needs confirming the two don't fight each other rather than building
  a second one.

## 6. Suggested first implementation slice (if/when this gets picked up)

Smallest slice that's independently mergeable and testable, matching this project's "one item at a
time, verified before moving on" discipline:

1. `HpWmiBios`: add read-only `0x2C`/`0x2F` command wrappers (mirrors the `0x23` probe pattern from
   this cycle). No behavior change, diagnostics-visible only, same risk profile as work already
   shipped this cycle.
2. The curve-evaluation engine as a pure, static, fully unit-tested class — no hardware access,
   tested against #189's exact extracted numbers (the factory table, the EWMA coefficients, the
   sample sequences in the issue that show expected fan levels at given temperatures).
3. Wire it into `FanService` as a new mode, gated behind `SupportsAutomaticFanCurve` (default
   false everywhere, including `8D87`, until a real device confirms the wiring), a Settings
   toggle, and Performance-mode-only engagement.
4. Only once 1-3 are merged and build/test-verified: flip `8D87`'s `SupportsAutomaticFanCurve` to
   `true` and ask `#189`'s reporter (who has real hardware and a working reference implementation
   to compare against) to validate before it ships in a release.

Each step should land as its own reviewed, tested change — not one large PR — consistent with how
this cycle's other work landed.
