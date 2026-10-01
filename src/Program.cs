// LagWatch — CLI entry point.
//   record            run the sampler in this console (Ctrl+C to stop)
//   start             launch the recorder detached & hidden
//   stop              ask the recorder to stop gracefully
//   status            is it recording? + a live snapshot
//   mark [note]       write a timestamped marker ("lag happened NOW")
//   ask               interactive: "it lagged — explain" menu
//   analyze [opts]    produce a report for a time range
//   purge [--days N]  delete telemetry older than N days
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LagWatch
{
    public static class Program
    {
        static int Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "gui";
            try
            {
                switch (mode)
                {
                    case "gui": return CmdGui(Rest(args));
                    case "record": return CmdRecord(Rest(args));
                    case "start": return CmdStart(Rest(args));
                    case "stop": return CmdStop(Rest(args));
                    case "status": return CmdStatus();
                    case "mark": return CmdMark(Rest(args));
                    case "ask": return CmdAsk();
                    case "analyze": return CmdAnalyze(Rest(args));
                    case "lb": case "leaderboard": return CmdLb(Rest(args));
                    case "trends": return CmdTrends(Rest(args));
                    case "doctor": return CmdDoctor();
                    case "purge": return CmdPurge(Rest(args));
                    case "help": case "--help": case "-h": case "/?": PrintHelp(); return 0;
                    default:
                        Console.WriteLine("Unknown command '" + mode + "'.");
                        PrintHelp();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        static string[] Rest(string[] args)
        {
            if (args.Length <= 1) return new string[0];
            string[] r = new string[args.Length - 1];
            Array.Copy(args, 1, r, 0, r.Length);
            return r;
        }

        static string ExePath
        {
            get { return typeof(Program).Assembly.Location; }
        }

        static void PrintHelp()
        {
            Console.WriteLine(@"LagWatch — find out why your PC lags. Records light telemetry in the background;
when it lags, ask it what happened.

  LagWatch start              start recording in the background (hidden)
  LagWatch status             is it recording? + live CPU/RAM snapshot
  LagWatch ask                it lagged! interactive menu -> full report
  LagWatch mark [note]        timestamp a marker (""lag NOW"")
  LagWatch stop               stop the background recorder
  LagWatch record [--interval N]   run in this console (Ctrl+C to stop)
  LagWatch analyze [--last 30m|2h|today|all] [--from ""yyyy-MM-dd HH:mm"" --to ""...""] [--auto] [--no-open]
  LagWatch lb [--last 2h]     resource leaderboard: who ate the CPU/GPU/IO/RAM
  LagWatch trends [--days 7]  day-over-day: CPU, commit, storms, top eaters
  LagWatch purge [--days 14]  delete telemetry older than N days

Data: " + App.Root);
        }

        // ---- gui ----
        static int CmdGui(string[] rest)
        {
            if (Array.IndexOf(rest, "lb") >= 0 || Array.IndexOf(rest, "leaderboard") >= 0)
                Environment.SetEnvironmentVariable("LAGWATCH_TAB", "lb");
            if (Array.IndexOf(rest, "trends") >= 0)
                Environment.SetEnvironmentVariable("LAGWATCH_TAB", "trends");
            // WinForms wants STA; PowerShell's main thread usually is, but be explicit.
            Thread t = new Thread((ThreadStart)delegate { GuiEntry.Run(); });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return 0;
        }

        // ---- record / start / stop / status ----
        static int CmdRecord(string[] args)
        {
            int interval = App.IntervalSec;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--interval" && i + 1 < args.Length)
                {
                    int v;
                    if (int.TryParse(args[i + 1], out v) && v >= 1 && v <= 60) interval = v;
                }
                if (args[i] == "--days" && i + 1 < args.Length)
                {
                    int v;
                    if (int.TryParse(args[i + 1], out v) && v >= 1 && v <= 365) App.RetentionDays = v;
                }
            }
            return Recorder.Run(interval);
        }

        static int CmdStart(string[] args)
        {
            int interval = 0;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--interval" && i + 1 < args.Length)
                {
                    int v;
                    if (int.TryParse(args[i + 1], out v) && v >= 1 && v <= 60) interval = v;
                }
            Console.WriteLine(Controller.Start(interval));
            return 0;
        }

        static int CmdStop(string[] args)
        {
            Console.WriteLine(Controller.Stop(Array.IndexOf(args, "--force") >= 0));
            return 0;
        }

        static int CmdStatus()
        {
            App.EnsureDirs();
            Console.WriteLine(Controller.StatusText());

            // live snapshot (two quick samples so deltas are valid)
            try
            {
                SysMonitor mon = new SysMonitor();
                mon.Take(false, false);
                Thread.Sleep(700);
                Sample s = mon.Take(false, false);
                NameVal top = s.TopCpu.Count > 0 ? s.TopCpu[0] : null;
                Console.WriteLine("Now: CPU " + App.F0(s.Cpu) + "% | RAM " + App.HumanMB(s.AvailMB) + " free of " +
                    App.HumanMB(s.TotalMB) + ", commit " + App.F0(s.Commit) + "% | disk " + App.F0(s.Busy) + "% @ " +
                    App.F1(s.LatMs) + "ms | DPC " + App.F1(s.Dpc) + "%" +
                    (top != null ? " | top: " + top.N + " " + App.F0(top.V) + "%" : "") +
                    " | uptime " + App.F1(Native.UptimeHours()) + "h");
            }
            catch (Exception ex)
            {
                Console.WriteLine("live snapshot failed: " + ex.Message);
            }
            return 0;
        }

        // ---- mark ----
        static int CmdMark(string[] args)
        {
            string note = args.Length > 0 ? string.Join(" ", args) : null;
            Console.WriteLine(Controller.Mark(note));
            return 0;
        }

        // ---- analyze ----
        static int CmdAnalyze(string[] args)
        {
            App.EnsureDirs();
            bool auto = Array.IndexOf(args, "--auto") >= 0;
            bool open = Array.IndexOf(args, "--no-open") < 0;
            DateTime to = DateTime.Now;
            DateTime from = to.Date; // default: today
            string outPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--last" && i + 1 < args.Length) { from = ParseSince(args[i + 1], to); }
                else if (args[i] == "--from" && i + 1 < args.Length) { from = DateTime.Parse(args[i + 1], CultureInfo.InvariantCulture); }
                else if (args[i] == "--to" && i + 1 < args.Length) { to = DateTime.Parse(args[i + 1], CultureInfo.InvariantCulture); }
                else if (args[i] == "--out" && i + 1 < args.Length) { outPath = args[i + 1]; }
            }

            if (auto)
            {
                DateTime dataStart = Analyzer.DataStartTime();
                if (dataStart == DateTime.MinValue) dataStart = from;
                from = dataStart.Date;
                if ((to - from).TotalDays > 7) { from = to.AddDays(-7); Console.WriteLine("(limiting auto window to the last 7 days)"); }
            }

            Console.WriteLine("Analyzing " + from.ToString("yyyy-MM-dd HH:mm") + " to " + to.ToString("yyyy-MM-dd HH:mm") + " ...");
            AnalysisData data = Analyzer.Analyze(from, to, false);
            if (data.SampleCount == 0)
            {
                Console.WriteLine("No telemetry recorded in this window. Was the recorder running? (LagWatch start)");
                return 1;
            }

            string report = Report.Render(data);
            if (outPath == null)
                outPath = Path.Combine(App.ReportsDir, "report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
            File.WriteAllText(outPath, report, new UTF8Encoding(true));

            Console.WriteLine();
            Console.WriteLine("Report: " + outPath);
            Console.WriteLine();
            PrintTldr(data);
            if (open) TryOpen(outPath);
            return 0;
        }

        static void PrintTldr(AnalysisData data)
        {
            if (data.SampleCount == 0) return;
            Console.WriteLine("TL;DR:");
            if (data.Findings.Count == 0)
            {
                Console.WriteLine("  Nothing clearly abnormal recorded in this window.");
                return;
            }
            int shown = 0;
            foreach (Finding f in data.Findings)
            {
                if (shown >= 3) break;
                Console.WriteLine("  [" + f.SevName + "] " + f.Title);
                if (f.Evidence.Count > 0) Console.WriteLine("          " + Trunc(f.Evidence[0], 100));
                shown++;
            }
            if (data.Findings.Count > 3) Console.WriteLine("  ... +" + (data.Findings.Count - 3) + " more findings in the report");
        }

        static string Trunc(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        static void TryOpen(string path)
        {
            if ((Environment.GetEnvironmentVariable("LAGWATCH_NOOPEN") ?? "").Trim() == "1") return;
            try { Process.Start("notepad.exe", path); } catch { }
        }

        static DateTime ParseSince(string spec, DateTime now)
        {
            spec = spec.Trim().ToLowerInvariant();
            if (spec == "today") return now.Date;
            if (spec == "all") return DateTime.MinValue;
            if (spec.Length >= 2 && "mhd".IndexOf(spec[spec.Length - 1]) >= 0)
            {
                double v;
                if (double.TryParse(spec.Substring(0, spec.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                {
                    switch (spec[spec.Length - 1])
                    {
                        case 'm': return now.AddMinutes(-v);
                        case 'h': return now.AddHours(-v);
                        case 'd': return now.AddDays(-v);
                    }
                }
            }
            return now.AddHours(-2);
        }

        // ---- leaderboard ----
        static int CmdLb(string[] args)
        {
            App.EnsureDirs();
            DateTime to = DateTime.Now;
            DateTime from = to.Date;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--last" && i + 1 < args.Length) from = ParseSince(args[i + 1], to);
                else if (args[i] == "--from" && i + 1 < args.Length) from = DateTime.Parse(args[i + 1], CultureInfo.InvariantCulture);
                else if (args[i] == "--to" && i + 1 < args.Length) to = DateTime.Parse(args[i + 1], CultureInfo.InvariantCulture);
            }

            DateTime df, dt;
            List<LbRow> rows = Analyzer.Leaderboard(from, to, out df, out dt);
            if (rows.Count == 0 || df == DateTime.MinValue)
            {
                Console.WriteLine("No leaderboard data in this window. The recorder builds it every minute while running (LagWatch start).");
                return 1;
            }

            double winSec = Math.Max(1, (dt - df).TotalSeconds);
            int cores = Environment.ProcessorCount;
            string winLbl = winSec >= 5400 ? App.F1(winSec / 3600.0) + " h" : App.F0(winSec / 60.0) + " min";
            Console.WriteLine();
            Console.WriteLine("Resource leaderboard  " + df.ToString("yyyy-MM-dd HH:mm") + " -> " + dt.ToString("HH:mm") +
                "  (" + winLbl + ", " + cores + " cores)");
            Console.WriteLine();
            Console.WriteLine("  #  process             CPU time    CPU share   Disk IO     Peak RAM");
            for (int i = 0; i < rows.Count && i < 25; i++)
            {
                LbRow r = rows[i];
                string nm = r.Name.Length > 18 ? r.Name.Substring(0, 15) + "..." : r.Name;
                Console.WriteLine("  " + (i + 1).ToString().PadRight(3) +
                    nm.PadRight(20) +
                    App.HumanCpuSec(r.CpuSec).PadRight(12) +
                    (App.F1(r.CpuSec / winSec / cores * 100.0) + "%").PadRight(12) +
                    App.HumanMB(r.IoMB).PadRight(12) +
                    App.HumanMB(r.PeakMB));
            }
            return 0;
        }

        // ---- trends ----
        static int CmdTrends(string[] args)
        {
            App.EnsureDirs();
            int days = 7;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--days" && i + 1 < args.Length)
                {
                    int v;
                    if (int.TryParse(args[i + 1], out v) && v >= 1 && v <= 30) days = v;
                }

            Console.WriteLine();
            Console.WriteLine("Day-over-day trends (last " + days + " days)");
            Console.WriteLine();
            Console.WriteLine("  day        samples  CPU avg/peak   commit avg  storms  top CPU eater            peak RAM holder");
            foreach (DayTrend t in Analyzer.Trends(days))
            {
                string top = "-", peak = "-";
                if (t.TopCpuProcs.Count > 0)
                {
                    top = t.TopCpuProcs[0].N + " (" + App.HumanCpuSec(t.TopCpuProcs[0].V) + ")";
                    if (top.Length > 24) top = top.Substring(0, 24);
                }
                if (!string.IsNullOrEmpty(t.PeakRam.N))
                {
                    peak = t.PeakRam.N + " (" + App.HumanMB(t.PeakRam.V) + ")";
                    if (peak.Length > 24) peak = peak.Substring(0, 24);
                }
                Console.WriteLine("  " + t.Day.ToString("yyyy-MM-dd") + "  " +
                    t.Samples.ToString().PadRight(8) + " " +
                    (App.F0(t.AvgCpu) + "% / " + App.F0(t.PeakCpu) + "%").PadRight(14) + " " +
                    (App.F0(t.AvgCommit) + "%").PadRight(12) +
                    t.Storms.ToString().PadRight(8) +
                    top.PadRight(25) +
                    peak);
            }
            Console.WriteLine();
            Console.WriteLine("(storms = paging/DPC/disk-latency/disk/thermal flags; empty days = no data)");
            return 0;
        }

        // ---- doctor: one-shot environment check for any machine ----
        static int CmdDoctor()
        {
            int pass = 0, warn = 0, fail = 0;
            Console.WriteLine();
            Console.WriteLine("LagWatch doctor \u2014 environment check");
            Console.WriteLine("------------------------------------------------------------");

            Console.WriteLine("  [OK]   .NET Framework runtime " + Environment.Version + " (required 4.x)"); pass++;

            try
            {
                string sac = "?";
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Defender", "SELECT SmartAppControlState FROM MSFT_MpComputerStatus"))
                    foreach (System.Management.ManagementObject o in s.Get())
                        sac = Convert.ToString(o["SmartAppControlState"]);
                if (sac == "On")
                {
                    Console.WriteLine("  [WARN] Smart App Control is ON \u2014 unsigned exes are blocked; run LagWatch via LagWatch.ps1 (this package does)"); warn++;
                }
                else Console.WriteLine("  [OK]   Smart App Control: " + sac + " (exe usable directly)"); pass++;
            }
            catch { Console.WriteLine("  [WARN] could not read Smart App Control state"); warn++; }

            try
            {
                SysMonitor mon = new SysMonitor();
                mon.Take(false, false);
                Thread.Sleep(600);
                Sample s = mon.Take(true, true);
                Console.WriteLine("  [OK]   CPU/RAM sampling (cpu " + App.F0(s.Cpu) + "%, " + App.HumanMB(s.AvailMB) +
                    " of " + App.HumanMB(s.TotalMB) + " free, " + s.Procs + " processes)"); pass++;
                if (mon.Degraded)
                {
                    Console.WriteLine("  [WARN] some performance counters missing (non-English Windows?) \u2014 disk/DPC may read 0"); warn++;
                }
                else Console.WriteLine("  [OK]   system counters (disk busy/latency, DPC, paging) working"); pass++;
                if (mon.GpuAvailable) { Console.WriteLine("  [OK]   GPU engine counters available"); pass++; }
                else { Console.WriteLine("  [WARN] GPU counters unavailable \u2014 GPU columns will stay empty"); warn++; }
                if (s.Therm >= 0) { Console.WriteLine("  [OK]   thermal sensor readable (" + App.F1(s.Therm) + " C)"); pass++; }
                else { Console.WriteLine("  [WARN] no ACPI thermal sensor on this board \u2014 temperature checks skipped"); warn++; }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] system monitoring failed: " + ex.Message); fail++;
            }

            try
            {
                App.EnsureDirs();
                string probe = Path.Combine(App.Root, "doctor-probe.tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                Console.WriteLine("  [OK]   data folder writable: " + App.Root); pass++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL] data folder not writable: " + ex.Message); fail++; }

            try
            {
                int n = 0;
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage", "SELECT FriendlyName FROM MSFT_PhysicalDisk"))
                    foreach (System.Management.ManagementObject o in s.Get()) n++;
                Console.WriteLine("  [OK]   disk health query OK (" + n + " drive" + (n == 1 ? "" : "s") + ")"); pass++;
            }
            catch { Console.WriteLine("  [WARN] disk health query denied"); warn++; }

            try
            {
                int n = 0;
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage", "SELECT Temperature FROM MSFT_StorageReliabilityCounter"))
                    foreach (System.Management.ManagementObject o in s.Get()) n++;
                if (n > 0) { Console.WriteLine("  [OK]   SMART/reliability counters readable (admin detail available)"); pass++; }
                else { Console.WriteLine("  [WARN] SMART/reliability counters empty \u2014 run analyze from an admin prompt for drive temps/wear"); warn++; }
            }
            catch { Console.WriteLine("  [WARN] SMART/reliability needs admin \u2014 reports will note it"); warn++; }

            try
            {
                System.Diagnostics.Eventing.Reader.EventLogQuery q = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                    "System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                    "*[System[TimeCreated[@SystemTime>='" + DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'") + "']]]");
                using (System.Diagnostics.Eventing.Reader.EventLogReader r = new System.Diagnostics.Eventing.Reader.EventLogReader(q))
                {
                    int n = 0;
                    while (r.ReadEvent() != null && n < 1) n++;
                }
                Console.WriteLine("  [OK]   Windows event log access OK"); pass++;
            }
            catch (Exception ex) { Console.WriteLine("  [WARN] event log access limited: " + ex.Message); warn++; }

            RecState st;
            if (Controller.IsRunning(out st))
            {
                Console.WriteLine("  [OK]   recorder RUNNING (pid " + st.Pid + ", " + st.Samples + " samples, last " +
                    st.Last.ToString("HH:mm:ss") + ")"); pass++;
            }
            else { Console.WriteLine("  [WARN] recorder not running \u2014 start it: INSTALL-AUTO-START.bat (or LagWatch.ps1 start)"); warn++; }

            string startupVbs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "LagWatch-bg.vbs");
            if (File.Exists(startupVbs)) { Console.WriteLine("  [OK]   autostart installed (recording resumes at login)"); pass++; }
            else { Console.WriteLine("  [WARN] autostart not installed \u2014 run INSTALL-AUTO-START.bat"); warn++; }

            try
            {
                int days = Directory.GetFiles(App.LogDir, "telemetry-*.jsonl").Length;
                int lbs = Directory.GetFiles(App.LogDir, "leaderboard-*.jsonl").Length;
                if (days > 0) { Console.WriteLine("  [OK]   recorded data present (" + days + " day file(s), " + lbs + " leaderboard file(s))"); pass++; }
                else { Console.WriteLine("  [WARN] no data recorded yet"); warn++; }
            }
            catch { }

            Console.WriteLine("------------------------------------------------------------");
            Console.WriteLine(fail == 0
                ? "VERDICT: READY" + (warn > 0 ? " (" + warn + " note" + (warn == 1 ? "" : "s") + " \u2014 nothing blocking)" : "")
                : "VERDICT: " + fail + " problem(s) found \u2014 see [FAIL] above");
            Console.WriteLine();
            return fail == 0 ? 0 : 1;
        }

        // ---- purge ----
        static int CmdPurge(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--days" && i + 1 < args.Length)
                {
                    int v;
                    if (int.TryParse(args[i + 1], out v) && v >= 1) App.RetentionDays = v;
                }
            Recorder.PurgeNow();
            Console.WriteLine("Purged telemetry older than " + App.RetentionDays + " days.");
            return 0;
        }

        // ---- interactive ask ----
        static int CmdAsk()
        {
            App.EnsureDirs();
            Console.WriteLine();
            Console.WriteLine("LagWatch — so, it lagged or crashed. What do you want explained?");
            Console.WriteLine();
            Console.WriteLine("  [1] It just happened (last 10 minutes)");
            Console.WriteLine("  [2] Within the last 30 minutes");
            Console.WriteLine("  [3] Within the last 2 hours");
            Console.WriteLine("  [4] Today so far");
            Console.WriteLine("  [5] Since a specific time today (enter HH:mm)");
            Console.WriteLine("  [6] Scan everything recorded, find the worst windows automatically");
            Console.WriteLine("  [7] Just mark 'it happened now' (analyze later)");
            Console.WriteLine("  [0] Cancel");
            Console.Write("> ");
            string choice = Console.ReadLine();
            if (choice == null) return 0;
            choice = choice.Trim();

            if (choice == "7") { CmdMark(new string[0]); return 0; }
            if (choice == "0" || choice.Length == 0) return 0;

            DateTime to = DateTime.Now.AddMinutes(1);
            DateTime from;
            bool auto = false;
            switch (choice)
            {
                case "1": from = to.AddMinutes(-10); break;
                case "2": from = to.AddMinutes(-30); break;
                case "3": from = to.AddHours(-2); break;
                case "4": from = to.Date; break;
                case "5":
                    Console.Write("Time since when to analyze (HH:mm, e.g. 14:05): ");
                    string ts = Console.ReadLine();
                    DateTime t;
                    if (!DateTime.TryParseExact((ts ?? "").Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                    {
                        Console.WriteLine("Could not parse that time.");
                        return 1;
                    }
                    from = to.Date.AddHours(t.Hour).AddMinutes(t.Minute);
                    if (from > to) from = from.AddDays(-1);
                    break;
                case "6": auto = true; from = DateTime.MinValue; break;
                default: Console.WriteLine("Unknown choice."); return 1;
            }

            if (auto)
            {
                DateTime dataStart = Analyzer.DataStartTime();
                if (dataStart == DateTime.MinValue) { Console.WriteLine("No telemetry found. Was the recorder running?"); return 1; }
                from = dataStart.Date;
                if ((to - from).TotalDays > 7) { from = to.AddDays(-7); Console.WriteLine("(limiting to the last 7 days)"); }
            }

            Console.WriteLine();
            Console.WriteLine("Analyzing " + from.ToString("yyyy-MM-dd HH:mm") + " to " + to.ToString("HH:mm") + " ...");
            AnalysisData data = Analyzer.Analyze(from, to, false);
            if (data.SampleCount == 0)
            {
                Console.WriteLine("No telemetry in that window. Was LagWatch recording then? (LagWatch status)");
                return 1;
            }

            string path = Path.Combine(App.ReportsDir, "report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
            File.WriteAllText(path, Report.Render(data), new UTF8Encoding(true));
            Console.WriteLine();
            Console.WriteLine("Full report: " + path);
            Console.WriteLine();
            PrintTldr(data);
            TryOpen(path);
            return 0;
        }
    }
}
