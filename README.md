# LagWatch

Your PC randomly lags or freezes. LagWatch sits in the background recording a
lightweight heartbeat of what the machine is doing (CPU, RAM, paging, disk, GPU,
DPC/driver load, which process is in front, top CPU/IO/memory hogs). **When the lag
happens, you ask it what happened** — that's when it does the heavy computation:
it digs through the recording, finds the worst windows, correlates them with the
Windows event log, crash/hang reports (WER) and memory dumps, and writes you a
ranked Markdown report with likely causes and fixes.

Everything runs locally. No telemetry leaves the machine, nothing is installed,
no admin rights needed.

## GUI (the easy way)

Double-click **`Start LagWatch (GUI).vbs`** (pin it to Start/taskbar). You get:

- **Live tab** — rolling 5-minute charts (CPU + DPC, commit pressure, RAM free,
  disk busy/latency, GPU), top processes right now, and a feed of flagged events
  & marks. Buttons: **"It lagged — analyze now!"**, start/stop recording, mark.
- **Leaderboard tab** — exact per-process totals over any range: CPU time, CPU
  share of your total capacity, GPU time, disk read+write, peak RAM. Click a
  column to sort. Built from cumulative totals the recorder flushes every minute.
- **Trends tab** — day-over-day stacked bars of the top CPU eaters, plus daily
  averages, commit pressure, storm counts and peak-RAM holders. Computed on demand.
- **Storm alerts** — the tray icon pops a warning balloon when a paging/DPC/disk
  storm is detected live (throttled to one per 5 minutes).
- **Post-crash auto-triage** — after a reboot that follows an unexpected shutdown
  or BSOD, the recorder automatically analyzes the 30 minutes before the crash
  once and drops a report (marked in the flags feed).
- **Reports & analysis tab** — pick a range, hit Analyze, read the TL;DR, open
  past reports.
- **Tray icon** — close the window and it keeps watching from the tray; the tray
  menu has "It lagged — analyze last 10 min" and Exit.

The GUI runs through the same PowerShell host as everything else (Smart App
Control safe). Closing the window does NOT stop recording — only tray → Exit
closes the GUI, and `Stop recording`/`LagWatch.ps1 stop` stops the recorder.

## Quick start (command line)

```bat
:: start recording in the background (hidden)
powershell -NoProfile -ExecutionPolicy Bypass -File LagWatch.ps1 start

:: ... later, right after a lag or crash:
powershell -NoProfile -ExecutionPolicy Bypass -File LagWatch.ps1 ask
```

`ask` opens a menu:

```
[1] It just happened (last 10 minutes)
[2] Within the last 30 minutes
[3] Within the last 2 hours
[4] Today so far
[5] Since a specific time today (HH:mm)
[6] Scan everything recorded, find the worst windows automatically
[7] Just mark 'it happened now' (analyze later)
```

It prints a TL;DR and opens the full report (stored in
`%LOCALAPPDATA%\LagWatch\reports`).

## All commands

| Command | What it does |
|---|---|
| `start` | start the background recorder (hidden) |
| `status` | is it recording? + a live CPU/RAM/disk snapshot |
| `ask` | interactive "it lagged — explain" menu |
| `mark [note]` | timestamp a marker right now ("`mark` game stuttered") |
| `stop` | stop the background recorder |
| `analyze --last 30m\|2h\|today\|all` | report for a range (also `--from`, `--to`, `--auto`) |
| `lb --last 2h` | resource leaderboard in the console (CPU / GPU / IO / peak RAM) |
| `trends --days 7` | day-over-day table: CPU, commit, storms, top eaters |
| `record --interval N` | run the recorder in this console (Ctrl+C stops) |
| `purge --days 14` | delete telemetry older than N days |

Prefix every command with
`powershell -NoProfile -ExecutionPolicy Bypass -File LagWatch.ps1`
(or use `LagWatch.exe` directly if Smart App Control is off — see below).

## What it records (and what it costs)

Every 3 seconds, one ~400-byte JSON line: CPU %, processor queue, RAM free/commit,
hard-fault rate (paging from disk), nonpaged pool, disk busy/queue/bytes/latency,
DPC & interrupt time, CPU frequency %, GPU 3D utilization & memory, power state,
process count, foreground app, and the top CPU/IO/RAM processes.

Cost: well under 0.1% CPU, roughly 10 MB of disk per day, old files auto-purged
after 14 days. Threshold hits (CPU spikes, paging storms, disk saturation, disk
latency, DPC storms, low RAM) are flagged cheaply as they happen, so the analyzer
knows where to look even if you don't remember when the lag was.

If the whole system freezes hard, the recorder itself stalls — the resulting gap
in the recording is reported as a **freeze candidate** with its exact duration.

## The report

- Recording coverage + freeze-candidate gaps
- TL;DR: ranked likely causes (CRITICAL/HIGH/MEDIUM/LOW)
- Evidence per finding: which process, how bad, when, what you were doing
- Worst telemetry windows, fully quantified
- Windows event log errors/warnings in the window: BSODs, unexpected shutdowns,
  GPU driver timeouts (TDR 4101), app crashes/hangs (1000/1002), disk errors
  (7/51/153/129), resource exhaustion (2004), WHEA hardware errors
- Windows Error Reporting crash/hang buckets + memory dumps written in the window
- Per-process CPU/IO/memory-growth aggregates for the whole window
- Action checklist

## Move it to another PC

Run `make-dist.ps1` (or just copy `LagWatch-portable.zip`). On the target PC:

1. Extract the zip anywhere (no install, no admin, nothing downloaded).
2. Double-click **`CHECK-IT-WORKS.bat`** — runs the `doctor` check: counters,
   GPU, SMART access, event log, recorder, autostart. Ends with a READY verdict.
3. Double-click **`INSTALL-AUTO-START.bat`** — starts recording now and makes it
   resume at every login on that PC.
4. Open the GUI anytime with **`Start LagWatch (GUI).vbs`**.

Requires only what Windows 10/11 already ships (.NET Framework 4.8 + PowerShell).
Smart App Control on the target machine is fine — everything runs through the
signed PowerShell host. Uninstall = `uninstall-startup.ps1`, then delete the folder.

## Building

`build.ps1` compiles `src\*.cs` with the C# compiler that ships with Windows
(no SDK, no internet):

- `LagWatch.exe` — standalone, ~15 MB RAM. **Blocked on this machine** because
  Smart App Control is On and it blocks unsigned, locally-compiled executables.
- `LagWatch.dll` + `LagWatch.ps1` — the same code hosted in-memory by signed
  PowerShell; Smart App Control does not block it. This is the supported way to
  run LagWatch on this machine. The PowerShell host uses ~80 MB RAM.

If you turn Smart App Control off
(Windows Security → App & browser control → Smart App Control; **irreversible**),
`LagWatch.exe` works directly and the background recorder drops to ~15 MB RAM.

## Autostart at login

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install-startup.ps1
```

Drops a hidden launcher into your Startup folder (no admin). Remove with
`uninstall-startup.ps1`. Autostart matters for crash analysis: after a BSOD,
the recorder starts again at login, and the event log + last samples before the
crash are what the report uses.

## Files

- Telemetry: `%LOCALAPPDATA%\LagWatch\log\telemetry-*.jsonl` (+ `events-*.jsonl`,
  `marks.jsonl`)
- Reports: `%LOCALAPPDATA%\LagWatch\reports\report-*.md`
- State: `%LOCALAPPDATA%\LagWatch\state.json`

## Notes & limits

- Performance-counter names are English-only; on a localized Windows some counters
  degrade gracefully (the report notes it).
- Thermal readings depend on the motherboard exposing `MSAcpi_ThermalZoneTemperature`
  (many desktops don't; laptop users usually get it).
- Analyze is CPU-heavy for a few seconds — that's by design (record light, analyze hard).
