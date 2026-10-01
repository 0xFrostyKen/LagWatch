// LagWatch — renders the analysis into a readable Markdown report.
using System;
using System.Collections.Generic;
using System.Text;

namespace LagWatch
{
    public static class Report
    {
        public static string Render(AnalysisData d)
        {
            StringBuilder sb = new StringBuilder(16384);
            string range = d.From.ToString("yyyy-MM-dd HH:mm") + " to " + d.To.ToString("HH:mm");

            sb.AppendLine("# LagWatch report — " + range);
            sb.AppendLine();
            sb.AppendLine("_Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                ". All times are local. Telemetry recorded at ~" + App.F0(d.MedianDt <= 0 ? App.IntervalSec : d.MedianDt) + "s intervals._");
            sb.AppendLine();

            // ---- coverage ----
            sb.AppendLine("## Recording coverage");
            sb.AppendLine();
            if (d.SampleCount == 0)
            {
                sb.AppendLine("**No telemetry in this window.** Start the recorder first: `LagWatch start`");
                sb.AppendLine();
                return sb.ToString();
            }
            double hours = (d.DataEnd - d.DataStart).TotalHours;
            sb.AppendLine("- Data spans " + d.DataStart.ToString("yyyy-MM-dd HH:mm:ss") + " to " + d.DataEnd.ToString("HH:mm:ss") +
                " (" + App.F1(hours) + " h, " + d.SampleCount + " samples, median interval " + App.F1(d.MedianDt) + "s)");
            if (d.From < d.DataStart)
                sb.AppendLine("- Note: you asked for " + d.From.ToString("HH:mm") + " but recording only starts at " + d.DataStart.ToString("HH:mm") +
                    " — earlier evidence comes from the Windows event logs below.");
            if (d.Gaps.Count == 0)
            {
                sb.AppendLine("- No gaps in recording — the machine never stopped responding long enough to stall the recorder.");
            }
            else
            {
                sb.AppendLine("- **Recording gaps (the whole system, including LagWatch, was frozen/stalled)** — these are freeze candidates:");
                for (int i = 0; i < d.Gaps.Count && i < 15; i++)
                    sb.AppendLine("  - " + d.Gaps[i].Key.ToString("HH:mm:ss") + " — " + App.F0(d.Gaps[i].Value) + "s of missing samples");
                if (d.Gaps.Count > 15) sb.AppendLine("  - ... and " + (d.Gaps.Count - 15) + " more");
            }
            sb.AppendLine();

            // ---- TL;DR ----
            sb.AppendLine("## TL;DR — most likely causes");
            sb.AppendLine();
            if (d.Findings.Count == 0)
            {
                sb.AppendLine("Nothing clearly abnormal was recorded in this window (no spikes, no crash/hang reports). " +
                    "Keep the recorder running and analyze again right after the next lag episode — or use a longer window with `--last today`.");
            }
            else
            {
                int shown = 0;
                foreach (Finding f in d.Findings)
                {
                    if (shown >= 3) break;
                    sb.AppendLine((shown + 1) + ". **[" + f.SevName + "]** " + f.Title);
                    shown++;
                }
            }
            sb.AppendLine();

            // ---- findings ----
            if (d.Findings.Count > 0)
            {
                sb.AppendLine("## Findings (ranked)");
                sb.AppendLine();
                foreach (Finding f in d.Findings)
                {
                    sb.AppendLine("### [" + f.SevName + "] " + f.Title);
                    foreach (string e in f.Evidence) sb.AppendLine("- " + e);
                    if (!string.IsNullOrEmpty(f.Advice)) sb.AppendLine("- _Fix:_ " + f.Advice);
                    sb.AppendLine();
                }
            }

            // ---- episodes ----
            if (d.Episodes != null && d.Episodes.Count > 0)
            {
                sb.AppendLine("## Worst windows detected in telemetry");
                sb.AppendLine();
                for (int i = 0; i < d.Episodes.Count && i < 5; i++)
                {
                    Episode ep = d.Episodes[i];
                    sb.AppendLine("### Window " + (i + 1) + ": " + ep.Start.ToString("HH:mm:ss") + " → " + ep.End.ToString("HH:mm:ss") +
                        " (" + ep.DurationSec + "s, severity " + App.F1(ep.Severity) + ")");
                    List<string> reasons = new List<string>(ep.Reasons);
                    reasons.Sort();
                    sb.AppendLine("- Triggers: " + string.Join(", ", reasons.ToArray()));
                    if (!string.IsNullOrEmpty(ep.Fg)) sb.AppendLine("- You were in: **" + ep.Fg + "**");

                    double maxCpu = 0, maxPages = 0, minAvail = double.MaxValue, maxBusy = 0, maxLat = 0;
                    Dictionary<string, double> cpuBy = new Dictionary<string, double>();
                    Dictionary<string, double> memBy = new Dictionary<string, double>();
                    foreach (Sample s in ep.Samples)
                    {
                        maxCpu = Math.Max(maxCpu, s.Cpu);
                        maxPages = Math.Max(maxPages, s.PagesIn);
                        if (s.AvailMB > 0) minAvail = Math.Min(minAvail, s.AvailMB);
                        maxBusy = Math.Max(maxBusy, s.Busy);
                        maxLat = Math.Max(maxLat, s.LatMs);
                        foreach (NameVal nv in s.TopCpu)
                        { double v; cpuBy.TryGetValue(nv.N, out v); cpuBy[nv.N] = Math.Max(v, nv.V); }
                        foreach (NameVal nv in s.TopMem)
                        { double v; memBy.TryGetValue(nv.N, out v); memBy[nv.N] = Math.Max(v, nv.V); }
                    }
                    sb.AppendLine("- CPU peak " + App.F0(maxCpu) + "% | hard faults " + App.F0(maxPages) + "/s | RAM free " +
                        (minAvail == double.MaxValue ? "n/a" : App.HumanMB(minAvail)) + " | disk " + App.F0(maxBusy) + "% @ " + App.F1(maxLat) + "ms");
                    sb.AppendLine("- CPU hogs: " + TopLine(cpuBy, " %"));
                    sb.AppendLine("- RAM hogs: " + TopLine(memBy, " MB"));
                    foreach (EventRec e in ep.Events)
                        sb.AppendLine("- Flagged: " + e.T.ToString("HH:mm:ss") + " " + e.Type + " — " + e.Info);
                    sb.AppendLine();
                }
            }

            // ---- event log ----
            sb.AppendLine("## Windows event log (errors & warnings in window)");
            sb.AppendLine();
            List<LogEvt> notable = new List<LogEvt>();
            foreach (LogEvt e in d.Events) if (e.Notable) notable.Add(e);
            notable.Sort(delegate(LogEvt a, LogEvt b) { return b.T.CompareTo(a.T); });
            if (notable.Count == 0)
            {
                sb.AppendLine("No notable errors/warnings (no crashes, hangs, TDRs, disk or hardware errors) in this window.");
            }
            else
            {
                sb.AppendLine("| Time | Source | ID | Why | Detail |");
                sb.AppendLine("|---|---|---|---|---|");
                for (int i = 0; i < notable.Count && i < 60; i++)
                {
                    LogEvt e = notable[i];
                    sb.AppendLine("| " + e.T.ToString("MM-dd HH:mm:ss") + " | " + Pipe(e.Provider) + " | " + e.Id + " | " +
                        Pipe(e.Why) + " | " + Pipe(Trunc(e.Desc, 140)) + " |");
                }
                if (notable.Count > 60) sb.AppendLine();
                sb.AppendLine("(" + notable.Count + " notable events total)");
            }
            sb.AppendLine();

            // ---- WER ----
            sb.AppendLine("## Crash & hang reports (Windows Error Reporting)");
            sb.AppendLine();
            if (d.Wer.Count == 0) sb.AppendLine("None in this window.");
            else foreach (string w in d.Wer) sb.AppendLine("- " + w);
            sb.AppendLine();

            // ---- dumps ----
            if (d.Dumps.Count > 0)
            {
                sb.AppendLine("## Memory dumps written in this window");
                sb.AppendLine();
                foreach (string s in d.Dumps) sb.AppendLine("- " + s);
                sb.AppendLine();
            }

            // ---- aggregates ----
            if (d.Agg.Cpu.Count > 0)
            {
                sb.AppendLine("## CPU per process (mean % over window, while observed in top-8)");
                sb.AppendLine();
                sb.AppendLine("| Process | mean % | peak % |");
                sb.AppendLine("|---|---|---|");
                for (int i = 0; i < d.Agg.Cpu.Count && i < 15; i++)
                {
                    double mx = 0;
                    foreach (NameVal m in d.Agg.MaxCpu) if (m.N == d.Agg.Cpu[i].N) { mx = m.V; break; }
                    sb.AppendLine("| " + Pipe(d.Agg.Cpu[i].N) + " | " + App.F1(d.Agg.Cpu[i].V) + " | " + App.F1(mx) + " |");
                }
                sb.AppendLine();
            }
            if (d.Agg.Io.Count > 0)
            {
                sb.AppendLine("## Disk IO per process (mean KB/s while observed in top-6)");
                sb.AppendLine();
                for (int i = 0; i < d.Agg.Io.Count && i < 10; i++)
                    sb.AppendLine("- " + d.Agg.Io[i].N + ": " + App.F1(d.Agg.Io[i].V) + " KB/s");
                sb.AppendLine();
            }
            if (d.Agg.MemGrow.Count > 0)
            {
                sb.AppendLine("## Memory growth over window (top growers)");
                sb.AppendLine();
                for (int i = 0; i < d.Agg.MemGrow.Count && i < 10; i++)
                    sb.AppendLine("- " + d.Agg.MemGrow[i].N + ": +" + App.HumanMB(d.Agg.MemGrow[i].V));
                sb.AppendLine();
            }

            // ---- exact resource leaderboard ----
            if (d.Lb != null && d.Lb.Count > 0 && d.LbFrom != DateTime.MinValue)
            {
                double winSec = Math.Max(1, (d.LbTo - d.LbFrom).TotalSeconds);
                int cores = Environment.ProcessorCount;
                sb.AppendLine("## Resource leaderboard (exact totals, " + d.LbFrom.ToString("HH:mm") + " to " + d.LbTo.ToString("HH:mm") + ")");
                sb.AppendLine();
                sb.AppendLine("| # | Process | CPU time | CPU share | GPU time | Disk IO | Peak RAM |");
                sb.AppendLine("|---|---|---|---|---|---|---|");
                for (int i = 0; i < d.Lb.Count && i < 12; i++)
                {
                    LbRow r = d.Lb[i];
                    sb.AppendLine("| " + (i + 1) + " | " + Pipe(r.Name) + " | " + App.HumanCpuSec(r.CpuSec) + " | " +
                        App.F1(r.CpuSec / winSec / cores * 100.0) + "% | " +
                        (r.GpuSec > 0.05 ? App.HumanCpuSec(r.GpuSec) : "—") + " | " +
                        App.HumanMB(r.IoMB) + " | " + App.HumanMB(r.PeakMB) + " |");
                }
                sb.AppendLine();
                sb.AppendLine("_CPU share = CPU-seconds / window / " + cores + " cores. Disk IO = read+write. Peak RAM = private bytes._");
                sb.AppendLine();
            }

            // ---- environment / notes ----
            sb.AppendLine("## Environment notes");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(d.PowerPlan)) sb.AppendLine("- Active power plan: " + d.PowerPlan);
            foreach (string h in d.DiskHealth) sb.AppendLine("- Disk: " + h);
            foreach (string n in d.Notes) sb.AppendLine("- " + n);
            sb.AppendLine();

            // ---- advice ----
            List<string> advices = new List<string>();
            foreach (Finding f in d.Findings)
                if (!string.IsNullOrEmpty(f.Advice) && !advices.Contains(f.Advice)) advices.Add(f.Advice);
            if (advices.Count > 0)
            {
                sb.AppendLine("## Action checklist");
                sb.AppendLine();
                for (int i = 0; i < advices.Count; i++) sb.AppendLine((i + 1) + ". " + advices[i]);
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine("_Telemetry data: " + App.LogDir + "  |  Reports: " + App.ReportsDir + "_");
            return sb.ToString();
        }

        static string TopLine(Dictionary<string, double> by, string unit)
        {
            List<string> names = new List<string>(by.Keys);
            names.Sort(delegate(string a, string b) { return by[b].CompareTo(by[a]); });
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < names.Count && i < 4; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(names[i]).Append(' ').Append(App.F0(by[names[i]])).Append(unit);
            }
            return sb.Length == 0 ? "n/a" : sb.ToString();
        }

        static string Pipe(string s)
        {
            if (s == null) return "";
            return s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }
    }
}
