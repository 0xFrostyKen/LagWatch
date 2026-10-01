// LagWatch — on-demand analyzer: loads recorded telemetry, finds the worst windows,
// correlates them with Windows event logs, WER crash/hang reports and memory dumps,
// and produces ranked findings. This is the heavy part, run only when you ask.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LagWatch
{
    public class Episode
    {
        public DateTime Start, End;
        public List<Sample> Samples = new List<Sample>();
        public HashSet<string> Reasons = new HashSet<string>();
        public double Severity;
        public string Fg = "";
        public List<EventRec> Events = new List<EventRec>();

        public int DurationSec { get { return Math.Max(1, (int)(End - Start).TotalSeconds); } }
    }

    public class LogEvt
    {
        public DateTime T;
        public int Level;
        public string Provider;
        public int Id;
        public string Channel;
        public string Desc;
        public bool Notable;
        public string Why;
    }

    public class Finding
    {
        public int Sev; // 0 critical, 1 high, 2 medium, 3 low
        public string Title;
        public List<string> Evidence = new List<string>();
        public string Advice;

        public string SevName
        {
            get { return Sev == 0 ? "CRITICAL" : Sev == 1 ? "HIGH" : Sev == 2 ? "MEDIUM" : "LOW"; }
        }
    }

    public class RangeAgg
    {
        public List<NameVal> Cpu = new List<NameVal>();  // mean % while in top-8
        public List<NameVal> MaxCpu = new List<NameVal>();
        public List<NameVal> Io = new List<NameVal>();   // mean KB/s while in top-6
        public List<NameVal> MemGrow = new List<NameVal>();
        public double PoolDeltaMB;
    }

    public class LbRow
    {
        public string Name;
        public double CpuSec, IoMB, PeakMB, GpuSec, Share;
    }

    public class DayTrend
    {
        public DateTime Day;
        public int Samples;
        public double AvgCpu, PeakCpu, AvgCommit;
        public int Storms;
        public List<NameVal> TopCpuProcs = new List<NameVal>(); // top 5 by CPU time
        public NameVal PeakRam = new NameVal();                 // name + peak MB
    }

    public class AnalysisData
    {
        public DateTime From, To;
        public int SampleCount;
        public double MedianDt;
        public DateTime DataStart, DataEnd;
        public List<Episode> Episodes;
        public List<KeyValuePair<DateTime, double>> Gaps = new List<KeyValuePair<DateTime, double>>();
        public List<LogEvt> Events = new List<LogEvt>();
        public List<string> Wer = new List<string>();
        public List<string> Dumps = new List<string>();
        public List<string> DiskHealth = new List<string>();
        public string PowerPlan = "";
        public RangeAgg Agg = new RangeAgg();
        public List<Finding> Findings = new List<Finding>();
        public List<string> Notes = new List<string>();
        public bool DegradedCounters;
        public List<LbRow> Lb = new List<LbRow>();
        public DateTime LbFrom, LbTo;
    }

    public static class Analyzer
    {
        // Optional progress sink so GUIs can show analysis stages.
        public static Action<string> Progress;

        static void Stage(string msg)
        {
            Console.WriteLine(msg);
            Action<string> p = Progress;
            if (p != null) p(msg);
        }
        public static List<Sample> LoadSamples(DateTime from, DateTime to)
        {
            List<Sample> all = new List<Sample>();
            for (DateTime d = from.Date; d <= to; d = d.AddDays(1))
            {
                string f = App.TelemetryFile(d);
                if (!File.Exists(f)) continue;
                foreach (string line in ReadLinesShared(f))
                {
                    if (line.Length < 30) continue;
                    try
                    {
                        Sample s = Sample.FromJson(Json.Parse(line) as Dictionary<string, object>);
                        if (s.T == DateTime.MinValue) continue;
                        if (s.T >= from && s.T <= to) all.Add(s);
                    }
                    catch { }
                }
            }
            all.Sort(delegate(Sample a, Sample b) { return a.T.CompareTo(b.T); });
            return all;
        }

        public static List<EventRec> LoadEvents(DateTime from, DateTime to)
        {
            List<EventRec> all = new List<EventRec>();
            for (DateTime d = from.Date; d <= to; d = d.AddDays(1))
            {
                string f = App.EventsFile(d);
                if (!File.Exists(f)) continue;
                foreach (string line in ReadLinesShared(f))
                {
                    if (line.Length < 20) continue;
                    try
                    {
                        EventRec e = EventRec.FromJson(Json.Parse(line) as Dictionary<string, object>);
                        if (e.T >= from && e.T <= to) all.Add(e);
                    }
                    catch { }
                }
            }
            if (File.Exists(App.MarksFile))
            {
                foreach (string line in ReadLinesShared(App.MarksFile))
                {
                    if (line.Length < 20) continue;
                    try
                    {
                        EventRec e = EventRec.FromJson(Json.Parse(line) as Dictionary<string, object>);
                        if (e.T >= from && e.T <= to) all.Add(e);
                    }
                    catch { }
                }
            }
            all.Sort(delegate(EventRec a, EventRec b) { return a.T.CompareTo(b.T); });
            return all;
        }

        static IEnumerable<string> ReadLinesShared(string path)
        {
            // Share read/write so we can analyze while the recorder keeps writing.
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader r = new StreamReader(fs))
            {
                string line;
                while ((line = r.ReadLine()) != null) yield return line;
            }
        }

        public static DateTime DataStartTime()
        {
            DateTime best = DateTime.MaxValue;
            try
            {
                foreach (string f in Directory.GetFiles(App.LogDir, "telemetry-*.jsonl"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    DateTime d;
                    if (DateTime.TryParseExact(name.Substring(name.Length - 8), "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out d) && d < best) best = d;
                }
            }
            catch { }
            return best == DateTime.MaxValue ? DateTime.MinValue : best;
        }

        // ---- episode detection ----
        struct MetricDef
        {
            public string Name; public string Key; public double Abs; public bool Low;
            public MetricDef(string name, string key, double abs, bool low) { Name = name; Key = key; Abs = abs; Low = low; }
        }

        static double GetVal(Sample s, string key)
        {
            switch (key)
            {
                case "cpu": return s.Cpu;
                case "pql": return s.Pql;
                case "pagesIn": return s.PagesIn;
                case "availMB": return s.AvailMB;
                case "commit": return s.Commit;
                case "busy": return s.Busy;
                case "latMs": return s.LatMs;
                case "dpc": return s.Dpc;
                case "therm": return s.Therm;
                default: return 0;
            }
        }

        public static List<Episode> DetectEpisodes(List<Sample> samples, List<EventRec> events)
        {
            int n = samples.Count;
            List<Episode> outv = new List<Episode>();
            if (n < 5) return outv;

            MetricDef[] defs = new MetricDef[]
            {
                new MetricDef("cpu", "cpu", 85, false),
                new MetricDef("paging", "pagesIn", 800, false),
                new MetricDef("low-ram", "availMB", 600, true),
                new MetricDef("commit", "commit", 92, false),
                new MetricDef("disk", "busy", 90, false),
                new MetricDef("disk-latency", "latMs", 60, false),
                new MetricDef("dpc", "dpc", 20, false),
                new MetricDef("thermal", "therm", 92, false),
            };

            bool[] flag = new bool[n];
            foreach (MetricDef d in defs)
            {
                double[] vals = new double[n];
                double valid = 0;
                for (int i = 0; i < n; i++) { vals[i] = GetVal(samples[i], d.Key); if (vals[i] > 0) valid++; }
                if (valid < 5 && d.Key != "therm") continue;

                double p95 = 0;
                if (n >= 40 && !d.Low)
                {
                    double[] sorted = (double[])vals.Clone();
                    Array.Sort(sorted);
                    p95 = sorted[(int)(0.95 * (n - 1))];
                }

                for (int i = 0; i < n; i++)
                {
                    double v = vals[i];
                    // flag on absolute breach OR unusual-for-this-machine values (p95 ratio)
                    bool hit = d.Low
                        ? (v > 0 && v <= d.Abs)
                        : (v >= d.Abs || (p95 > 0 && v >= p95 * 1.4));
                    if (hit) flag[i] = true;
                }
            }

            // merge flagged samples into episodes (gap tolerance 4 samples, pad 2)
            int gapTol = 4;
            int start = -1, last = -1;
            for (int i = 0; i < n; i++)
            {
                if (flag[i])
                {
                    if (start < 0) { start = i; last = i; }
                    else if (i - last <= gapTol) last = i;
                    else { outv.Add(BuildEpisode(samples, start, last, events)); start = i; last = i; }
                }
            }
            if (start >= 0) outv.Add(BuildEpisode(samples, start, last, events));

            // user marks always become episodes
            foreach (EventRec e in events)
            {
                if (e.Type != "mark") continue;
                bool covered = false;
                foreach (Episode ep in outv)
                    if (e.T >= ep.Start.AddSeconds(-20) && e.T <= ep.End.AddSeconds(20)) { covered = true; ep.Events.Add(e); break; }
                if (!covered)
                {
                    Episode ep = new Episode();
                    ep.Start = e.T.AddSeconds(-15); ep.End = e.T.AddSeconds(15);
                    foreach (Sample s in samples)
                        if (s.T >= ep.Start && s.T <= ep.End) { ep.Samples.Add(s); ep.Reasons.Add("user-mark"); }
                    if (ep.Samples.Count > 0) { ep.Severity = 5; ep.Fg = ep.Samples[ep.Samples.Count / 2].Fg; ep.Events.Add(e); outv.Add(ep); }
                }
            }

            outv.Sort(delegate(Episode a, Episode b) { return b.Severity.CompareTo(a.Severity); });
            return outv;
        }

        static Episode BuildEpisode(List<Sample> samples, int a, int b, List<EventRec> events)
        {
            int n = samples.Count;
            a = Math.Max(0, a - 2); b = Math.Min(n - 1, b + 2);
            Episode ep = new Episode();
            ep.Start = samples[a].T; ep.End = samples[b].T;
            ep.Samples = new List<Sample>();
            for (int i = a; i <= b; i++) ep.Samples.Add(samples[i]);

            MetricDef[] defs = new MetricDef[]
            {
                new MetricDef("cpu", "cpu", 85, false),
                new MetricDef("paging", "pagesIn", 800, false),
                new MetricDef("low-ram", "availMB", 600, true),
                new MetricDef("commit", "commit", 92, false),
                new MetricDef("disk", "busy", 90, false),
                new MetricDef("disk-latency", "latMs", 60, false),
                new MetricDef("dpc", "dpc", 20, false),
                new MetricDef("thermal", "therm", 92, false),
            };
            foreach (MetricDef d in defs)
            {
                double worst = 0;
                foreach (Sample s in ep.Samples)
                {
                    double v = GetVal(s, d.Key);
                    if (v <= 0) continue;
                    double ratio = d.Low ? (v <= d.Abs ? 2.0 : 0) : v / d.Abs;
                    if (ratio >= 1) // only actual threshold breaches count as triggers
                    {
                        ep.Reasons.Add(d.Name);
                        if (ratio > worst) worst = ratio;
                    }
                }
                ep.Severity += Math.Min(10, worst);
            }

            // dominant foreground app during the episode
            Dictionary<string, int> fgCount = new Dictionary<string, int>();
            foreach (Sample s in ep.Samples)
                if (!string.IsNullOrEmpty(s.Fg))
                {
                    int c; fgCount.TryGetValue(s.Fg, out c); fgCount[s.Fg] = c + 1;
                }
            int bestC = 0; string bestFg = "";
            foreach (KeyValuePair<string, int> kv in fgCount)
                if (kv.Value > bestC) { bestC = kv.Value; bestFg = kv.Key; }
            ep.Fg = bestFg;

            foreach (EventRec e in events)
                if (e.T >= ep.Start.AddSeconds(-5) && e.T <= ep.End.AddSeconds(5)) ep.Events.Add(e);

            return ep;
        }

        // ---- windows event log / WER / dumps / disk health ----
        public static void CollectSources(AnalysisData data)
        {
            CollectEventLog(data, "System");
            CollectEventLog(data, "Application");
            CollectWer(data);
            CollectDumps(data);
            CollectDiskHealth(data);
            CollectStorageReliability(data);
            CollectGpuTemp(data);
            CollectPowerPlan(data);
        }

        // Drive-level SMART-style counters (temperature, wear, uncorrected errors).
        // Analyze-time only. Many fields need admin — anything null is skipped silently.
        static void CollectStorageReliability(AnalysisData data)
        {
            try
            {
                using (System.Management.ManagementObjectSearcher s =
                    new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                        "SELECT * FROM MSFT_StorageReliabilityCounter"))
                {
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        List<string> bits = new List<string>();
                        object wear = o["Wear"];
                        object temp = o["Temperature"];
                        object rUnc = o["ReadErrorsUncorrected"];
                        object wUnc = o["WriteErrorsUncorrected"];
                        object hours = o["PowerOnHours"];
                        if (wear != null && Convert.ToDouble(wear) > 0) bits.Add("wear " + Convert.ToString(wear) + "%");
                        if (temp != null && Convert.ToDouble(temp) > 0) bits.Add("temp " + Convert.ToString(temp) + " C");
                        if (rUnc != null && Convert.ToDouble(rUnc) > 0) bits.Add("read errors uncorrected " + Convert.ToString(rUnc));
                        if (wUnc != null && Convert.ToDouble(wUnc) > 0) bits.Add("write errors uncorrected " + Convert.ToString(wUnc));
                        if (hours != null && Convert.ToDouble(hours) > 0) bits.Add("powered " + Convert.ToString(hours) + " h");
                        if (bits.Count == 0) continue;
                        string line = "SMART/reliability: " + string.Join(", ", bits.ToArray());
                        if (!data.DiskHealth.Contains(line)) data.DiskHealth.Add(line);
                        if ((rUnc != null && Convert.ToDouble(rUnc) > 0) || (wUnc != null && Convert.ToDouble(wUnc) > 0))
                        {
                            Finding f = new Finding();
                            f.Sev = 1;
                            f.Title = "Disk reports UNCORRECTED read/write errors";
                            f.Evidence.Add(line);
                            f.Advice = "Back up immediately; uncorrected errors on an SSD often precede failure. Check warranty.";
                            data.Findings.Add(f);
                        }
                        if (temp != null && Convert.ToDouble(temp) >= 70)
                        {
                            Finding f = new Finding();
                            f.Sev = 2;
                            f.Title = "Drive running hot (" + Convert.ToString(temp) + " C)";
                            f.Evidence.Add(line);
                            f.Advice = "Check airflow over the drive; sustained heat throttles and ages SSDs/NVMe.";
                            data.Findings.Add(f);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                data.Notes.Add("Storage reliability counters unavailable: " + ex.Message);
            }
        }

        // GPU name/temperature/VRAM via nvidia-smi when present. Analyze-time only.
        static void CollectGpuTemp(AnalysisData data)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo pi = new System.Diagnostics.ProcessStartInfo("nvidia-smi",
                    "--query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total --format=csv,noheader");
                pi.UseShellExecute = false;
                pi.RedirectStandardOutput = true;
                pi.CreateNoWindow = true;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(pi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(4000)) { p.Kill(); return; }
                    foreach (string line in outp.Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.Length == 0) continue;
                        data.Notes.Add("GPU: " + t);
                        string[] parts = t.Split(',');
                        if (parts.Length >= 2)
                        {
                            double temp;
                            if (double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out temp) && temp >= 85)
                            {
                                Finding f = new Finding();
                                f.Sev = 2;
                                f.Title = "GPU running hot at analysis time (" + App.F0(temp) + " C)";
                                f.Evidence.Add(t);
                                f.Advice = "Dust/thermal paste/fan curve; hot GPUs throttle and can trigger display driver timeouts.";
                                data.Findings.Add(f);
                            }
                        }
                    }
                }
            }
            catch { } // no NVIDIA GPU or tool missing
        }

        // ---- day-over-day trends (on demand only) ----
        public static List<DayTrend> Trends(int days)
        {
            List<DayTrend> outv = new List<DayTrend>();
            if (days < 1) days = 1;
            if (days > 30) days = 30;
            for (int i = days - 1; i >= 0; i--)
            {
                DateTime day = DateTime.Today.AddDays(-i);
                DateTime end = day.AddDays(1).AddSeconds(-1);
                DayTrend t = new DayTrend();
                t.Day = day;

                List<Sample> samples = LoadSamples(day, end);
                t.Samples = samples.Count;
                if (samples.Count > 0)
                {
                    double sum = 0, peak = 0, csum = 0;
                    int cn = 0;
                    foreach (Sample s in samples)
                    {
                        sum += s.Cpu;
                        if (s.Cpu > peak) peak = s.Cpu;
                        if (s.Commit > 0) { csum += s.Commit; cn++; }
                    }
                    t.AvgCpu = sum / samples.Count;
                    t.PeakCpu = peak;
                    t.AvgCommit = cn > 0 ? csum / cn : 0;
                }

                List<EventRec> evs = LoadEvents(day, end);
                foreach (EventRec e in evs)
                    if (e.Type == "paging-storm" || e.Type == "dpc-storm" || e.Type == "disk-latency" ||
                        e.Type == "disk-saturation" || e.Type == "thermal") t.Storms++;

                DateTime df, dt;
                List<LbRow> rows = Leaderboard(day, end, out df, out dt);
                foreach (LbRow r in rows)
                {
                    if (r.CpuSec > 0.5 && t.TopCpuProcs.Count < 5)
                        t.TopCpuProcs.Add(new NameVal { N = r.Name, V = r.CpuSec });
                    if (r.PeakMB > t.PeakRam.V) { t.PeakRam.N = r.Name; t.PeakRam.V = r.PeakMB; }
                }
                outv.Add(t);
            }
            return outv;
        }

        static string Xpath(DateTime from, DateTime to)
        {
            string f = from.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'", CultureInfo.InvariantCulture);
            string t = to.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'", CultureInfo.InvariantCulture);
            return "*[System[TimeCreated[@SystemTime>='" + f + "' and @SystemTime<='" + t +
                   "'] and (Level=1 or Level=2 or Level=3)]]";
        }

        static void CollectEventLog(AnalysisData data, string channel)
        {
            try
            {
                System.Diagnostics.Eventing.Reader.EventLogQuery q =
                    new System.Diagnostics.Eventing.Reader.EventLogQuery(channel,
                        System.Diagnostics.Eventing.Reader.PathType.LogName, Xpath(data.From, data.To));
                q.TolerateQueryErrors = true;
                using (System.Diagnostics.Eventing.Reader.EventLogReader r =
                    new System.Diagnostics.Eventing.Reader.EventLogReader(q))
                    {
                        System.Diagnostics.Eventing.Reader.EventRecord rec;
                        int count = 0;
                        while ((rec = r.ReadEvent()) != null)
                        {
                            using (rec)
                            {
                                LogEvt e = new LogEvt();
                                e.T = rec.TimeCreated.HasValue ? rec.TimeCreated.Value : DateTime.MinValue;
                                e.Level = rec.Level.HasValue ? rec.Level.Value : 0;
                                e.Provider = rec.ProviderName ?? "";
                                e.Id = rec.Id;
                                e.Channel = channel;
                                try { e.Desc = rec.FormatDescription() ?? ""; }
                                catch
                                {
                                    StringBuilder sb = new StringBuilder();
                                    if (rec.Properties != null)
                                        for (int i = 0; i < rec.Properties.Count && i < 4; i++)
                                            sb.Append(rec.Properties[i].Value).Append(" ");
                                    e.Desc = sb.ToString();
                                }
                                e.Desc = e.Desc.Replace("\r", " ").Replace("\n", " ").Trim();
                                if (e.Desc.Length > 400) e.Desc = e.Desc.Substring(0, 400) + "...";
                                Classify(e);
                                data.Events.Add(e);
                            }
                            if (++count > 900) break;
                        }
                    }
            }
            catch (Exception ex)
            {
                data.Notes.Add("Could not read " + channel + " event log: " + ex.Message);
            }
        }

        static void Classify(LogEvt e)
        {
            string p = e.Provider ?? "";
            if (p.IndexOf("WHEA-Logger", StringComparison.OrdinalIgnoreCase) >= 0)
            { e.Notable = true; e.Why = "Hardware error (WHEA)"; return; }
            if (p.IndexOf("Kernel-Power", StringComparison.OrdinalIgnoreCase) >= 0 && e.Id == 41)
            { e.Notable = true; e.Why = "Unexpected shutdown / power loss"; return; }
            if (p == "EventLog" && e.Id == 6008)
            { e.Notable = true; e.Why = "Unexpected shutdown"; return; }
            if (p.IndexOf("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase) >= 0 || p == "BugCheck")
            { e.Notable = true; e.Why = "Blue screen (BugCheck)"; return; }
            if (p == "Display" && e.Id == 4101)
            { e.Notable = true; e.Why = "GPU driver timeout (TDR) - display driver stopped responding"; return; }
            if ((p == "disk" || p == "storahci" || p == "stornvme") &&
                (e.Id == 7 || e.Id == 51 || e.Id == 153 || e.Id == 129))
            { e.Notable = true; e.Why = "Disk error/reset"; return; }
            if (p == "Ntfs" && e.Id == 55)
            { e.Notable = true; e.Why = "File system corruption warning"; return; }
            if (p.IndexOf("Resource-Exhaustion-Detector", StringComparison.OrdinalIgnoreCase) >= 0)
            { e.Notable = true; e.Why = "Windows ran out of commit (virtual memory)"; return; }
            if (p == "Application Error" && e.Id == 1000)
            { e.Notable = true; e.Why = "Application crash"; return; }
            if (p == "Application Hang" && e.Id == 1002)
            { e.Notable = true; e.Why = "Application hang (not responding)"; return; }
            if (p == "Windows Error Reporting" && e.Id == 1001)
            { e.Notable = true; e.Why = "Windows Error Reporting bucket"; return; }
            if (p.IndexOf(".NET Runtime", StringComparison.OrdinalIgnoreCase) >= 0)
            { e.Notable = true; e.Why = ".NET application crash"; return; }
            if (e.Level == 1)
            { e.Notable = true; e.Why = "Critical event"; return; }
        }

        static void CollectWer(AnalysisData data)
        {
            string[] roots = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Microsoft\\Windows\\WER\\ReportArchive"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Microsoft\\Windows\\WER\\ReportQueue"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft\\Windows\\WER\\ReportArchive"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft\\Windows\\WER\\ReportQueue"),
            };
            Dictionary<string, DateTime> latest = new Dictionary<string, DateTime>();
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (string root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        DateTime t;
                        try { t = Directory.GetLastWriteTime(dir); } catch { continue; }
                        if (t < data.From || t > data.To) continue;
                        string name = Path.GetFileName(dir);
                        string[] parts = name.Split('_');
                        string type = parts.Length > 0 ? parts[0] : name;
                        string app = parts.Length > 1 ? parts[1] : "";
                        string key = type + "  " + app;
                        counts[key] = counts.ContainsKey(key) ? counts[key] + 1 : 1;
                        DateTime cur;
                        if (!latest.TryGetValue(key, out cur) || t > cur) latest[key] = t;
                    }
                }
                catch { }
            }
            List<string> keys = new List<string>(counts.Keys);
            keys.Sort(delegate(string a, string b) { return latest[b].CompareTo(latest[a]); });
            for (int i = 0; i < keys.Count && i < 25; i++)
            {
                string line = keys[i] + "  x" + counts[keys[i]] + "  (latest " + latest[keys[i]].ToString("MM-dd HH:mm") + ")";
                data.Wer.Add(line);
                if (keys[i].StartsWith("LiveKernelEvent") || keys[i].StartsWith("Kernel"))
                {
                    Finding f = new Finding();
                    f.Sev = 1;
                    f.Title = "Kernel-level " + keys[i].Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries)[0] +
                        " report (driver or hardware live-dump)";
                    f.Evidence.Add("WER: " + line);
                    f.Advice = "Update GPU/storage/chipset drivers; check Windows Update for firmware and driver updates.";
                    data.Findings.Add(f);
                }
                else if (keys[i].StartsWith("AppHang"))
                {
                    Finding f = new Finding();
                    f.Sev = 1;
                    f.Title = "Application hang: " + keys[i];
                    f.Evidence.Add("WER: " + line);
                    f.Advice = "Update or reinstall the hanging app; if it happens under load, check its logs and version changelogs.";
                    data.Findings.Add(f);
                }
                else if (keys[i].StartsWith("AppCrash"))
                {
                    Finding f = new Finding();
                    f.Sev = 2;
                    f.Title = "Application crash: " + keys[i];
                    f.Evidence.Add("WER: " + line);
                    f.Advice = "Update the crashing app; check its faulting module in the full report.";
                    data.Findings.Add(f);
                }
            }
        }

        static void CollectDumps(AnalysisData data)
        {
            string[] paths = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
                @"C:\Windows\Minidump",
                @"C:\Windows",
            };
            foreach (string dir in paths)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string f in Directory.GetFiles(dir, "*.dmp"))
                    {
                        DateTime t = File.GetLastWriteTime(f);
                        if (t >= data.From && t <= data.To)
                        {
                            FileInfo fi = new FileInfo(f);
                            data.Dumps.Add(f + "  (" + App.HumanMB(fi.Length / 1024.0 / 1024.0) + ", " + t.ToString("MM-dd HH:mm") + ")");
                        }
                    }
                }
                catch { }
            }
            try
            {
                string m = @"C:\Windows\MEMORY.DMP";
                if (File.Exists(m))
                {
                    DateTime t = File.GetLastWriteTime(m);
                    if (t >= data.From && t <= data.To)
                        data.Dumps.Add(m + "  (" + App.HumanMB(new FileInfo(m).Length / 1024.0 / 1024.0) + ", " + t.ToString("MM-dd HH:mm") + ")");
                }
            }
            catch { }
            if (data.Dumps.Count > 0)
            {
                Finding f = new Finding();
                f.Sev = 0;
                f.Title = "Crash dumps written during this window";
                f.Evidence.AddRange(data.Dumps);
                f.Advice = "A process or Windows itself crashed hard. If it's a minidump under C:\\Windows\\Minidump, analyze with WinDbg (`!analyze -v`) or run `verifier` if it recurs.";
                data.Findings.Add(f);
            }
        }

        static void CollectDiskHealth(AnalysisData data)
        {
            try
            {
                using (System.Management.ManagementObjectSearcher s =
                    new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                        "SELECT FriendlyName, MediaType, HealthStatus FROM MSFT_PhysicalDisk"))
                {
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        string name = Convert.ToString(o["FriendlyName"]);
                        string med = Convert.ToString(o["MediaType"]);
                        // HealthStatus is a UInt16 enum: 0 Healthy, 1 Warning, 2 Unhealthy
                        string hs;
                        try
                        {
                            ushort hv = Convert.ToUInt16(o["HealthStatus"], CultureInfo.InvariantCulture);
                            hs = hv == 0 ? "Healthy" : hv == 1 ? "Warning" : hv == 2 ? "Unhealthy" : ("Unknown(" + hv + ")");
                        }
                        catch { hs = Convert.ToString(o["HealthStatus"]); }
                        data.DiskHealth.Add(name + " (" + med + "): " + hs);
                        if (hs != null && hs.IndexOf("Healthy", StringComparison.OrdinalIgnoreCase) < 0 &&
                            hs.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            Finding f = new Finding();
                            f.Sev = 0;
                            f.Title = "Disk reports health status: " + hs + " (" + name + ")";
                            f.Evidence.Add("MSFT_PhysicalDisk HealthStatus = " + hs);
                            f.Advice = "Back up your data NOW, then run `Repair-Volume` / chkdsk and check SMART details.";
                            data.Findings.Add(f);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                data.Notes.Add("Disk health query unavailable: " + ex.Message);
            }
        }

        static void CollectPowerPlan(AnalysisData data)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo pi = new System.Diagnostics.ProcessStartInfo("powercfg", "/getactivescheme");
                pi.UseShellExecute = false;
                pi.RedirectStandardOutput = true;
                pi.CreateNoWindow = true;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(pi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    int a = outp.IndexOf('(');
                    int b = outp.LastIndexOf(')');
                    if (a >= 0 && b > a) data.PowerPlan = outp.Substring(a + 1, b - a - 1);
                }
            }
            catch { }
        }

        // ---- whole-range aggregates ----
        public static RangeAgg Aggregate(List<Sample> samples)
        {
            RangeAgg agg = new RangeAgg();
            if (samples.Count == 0) return agg;

            Dictionary<string, double> cpuSum = new Dictionary<string, double>();
            Dictionary<string, double> cpuMax = new Dictionary<string, double>();
            Dictionary<string, double> ioSum = new Dictionary<string, double>();
            Dictionary<string, double[]> memFirstLast = new Dictionary<string, double[]>();
            int n = samples.Count;

            for (int i = 0; i < n; i++)
            {
                Sample s = samples[i];
                double frac = (double)i / Math.Max(1, n - 1);

                foreach (NameVal nv in s.TopCpu)
                {
                    double v;
                    cpuSum.TryGetValue(nv.N, out v); cpuSum[nv.N] = v + nv.V;
                    double m;
                    cpuMax.TryGetValue(nv.N, out m); cpuMax[nv.N] = Math.Max(m, nv.V);
                }
                foreach (NameVal nv in s.TopIo)
                {
                    double v;
                    ioSum.TryGetValue(nv.N, out v); ioSum[nv.N] = v + nv.V;
                }
                foreach (NameVal nv in s.TopMem)
                {
                    double[] fl;
                    if (!memFirstLast.TryGetValue(nv.N, out fl)) { fl = new double[2]; fl[0] = -1; fl[1] = -1; memFirstLast[nv.N] = fl; }
                    if (frac <= 0.1 && nv.V > fl[0]) fl[0] = nv.V;
                    if (frac >= 0.9 && nv.V > fl[1]) fl[1] = nv.V;
                }
            }

            foreach (KeyValuePair<string, double> kv in cpuSum)
                agg.Cpu.Add(new NameVal { N = kv.Key, V = kv.Value / n });
            agg.Cpu.Sort(delegate(NameVal a, NameVal b) { return b.V.CompareTo(a.V); });
            foreach (NameVal nv in agg.Cpu)
            {
                double m;
                if (cpuMax.TryGetValue(nv.N, out m)) agg.MaxCpu.Add(new NameVal { N = nv.N, V = m });
            }
            agg.MaxCpu.Sort(delegate(NameVal a, NameVal b) { return b.V.CompareTo(a.V); });

            foreach (KeyValuePair<string, double> kv in ioSum)
                agg.Io.Add(new NameVal { N = kv.Key, V = kv.Value / n });
            agg.Io.Sort(delegate(NameVal a, NameVal b) { return b.V.CompareTo(a.V); });

            foreach (KeyValuePair<string, double[]> kv in memFirstLast)
            {
                if (kv.Value[0] >= 0 && kv.Value[1] >= 0)
                {
                    double grow = kv.Value[1] - kv.Value[0];
                    if (grow > 50) agg.MemGrow.Add(new NameVal { N = kv.Key, V = grow });
                }
            }
            agg.MemGrow.Sort(delegate(NameVal a, NameVal b) { return b.V.CompareTo(a.V); });

            double pool0 = -1, pool1 = -1;
            foreach (Sample s in samples)
            {
                if (s.PoolNP > 0)
                {
                    if (pool0 < 0) pool0 = s.PoolNP;
                    pool1 = s.PoolNP;
                }
            }
            if (pool0 > 0) agg.PoolDeltaMB = (pool1 - pool0) / (1024.0 * 1024.0);

            return agg;
        }

        // ---- findings from telemetry ----
        static readonly Dictionary<string, string> KnownNotes = new Dictionary<string, string>
        {
            { "MsMpEng", "Windows Defender real-time scanning — consider adding dev/game folders to exclusions or scheduling scans." },
            { "Antimalware Service Executable", "Windows Defender real-time scanning — consider exclusions or scheduled scans." },
            { "TiWorker", "Windows Update / servicing is working — let it finish or set active hours." },
            { "TrustedInstaller", "Windows Update / servicing is working — let it finish." },
            { "SetupHost", "A Windows update is installing." },
            { "SearchIndexer", "Windows Search indexing — rebuild the index or exclude large folders." },
            { "SearchProtocolHost", "Windows Search indexing." },
            { "OneDrive", "OneDrive is syncing files — pause it during gaming/work sessions." },
            { "Dropbox", "Dropbox is syncing — pause during sessions." },
            { "Memory Compression", "Windows is compressing memory — a strong sign of RAM pressure." },
            { "MemCompression", "Windows is compressing memory — a strong sign of RAM pressure." },
            { "chrome", "Chrome with many tabs/extensions can eat CPU and RAM." },
            { "msedge", "Edge with many tabs/extensions can eat CPU and RAM." },
            { "steamwebhelper", "Steam's web views — close the store/library overlays when gaming." },
            { "vmmem", "WSL2/Docker VM is consuming RAM — shut it down when not needed (wsl --shutdown)." },
            { "docker", "Docker Desktop is running." },
            { "CompatTelRunner", "Windows telemetry collection task." },
            { "System", "High 'System' CPU usually means drivers or the kernel (see DPC/interrupt findings)." },
            { "audiodg", "Audio device driver DSP — audio driver effect processing." },
            { "NVDisplay.Container", "NVIDIA driver services — update or clean-reinstall the driver." },
            { " ArmouryCrate", "ASUS Armoury Crate is known to cause stutters — consider removing it." },
        };

        static string NoteFor(string name)
        {
            string v;
            if (KnownNotes.TryGetValue(name, out v)) return v;
            if (name != null && name.StartsWith(" ", StringComparison.Ordinal))
                return KnownNotes.TryGetValue(name.Trim(), out v) ? v : null;
            return null;
        }

        public static void BuildFindings(AnalysisData data, List<Sample> samples, List<Episode> episodes)
        {
            // category stats across episodes
            HashSet<string> seen = new HashSet<string>();
            List<Finding> telFindings = new List<Finding>();

            foreach (Episode ep in episodes)
            {
                double maxCpu = 0, maxPages = 0, minAvail = double.MaxValue, maxBusy = 0, maxLat = 0, maxDpc = 0, maxCommit = 0, maxTherm = -1;
                Dictionary<string, double> cpuBy = new Dictionary<string, double>();
                Dictionary<string, double> ioBy = new Dictionary<string, double>();
                foreach (Sample s in ep.Samples)
                {
                    maxCpu = Math.Max(maxCpu, s.Cpu);
                    maxPages = Math.Max(maxPages, s.PagesIn);
                    if (s.AvailMB > 0) minAvail = Math.Min(minAvail, s.AvailMB);
                    maxBusy = Math.Max(maxBusy, s.Busy);
                    maxLat = Math.Max(maxLat, s.LatMs);
                    maxDpc = Math.Max(maxDpc, s.Dpc);
                    maxCommit = Math.Max(maxCommit, s.Commit);
                    maxTherm = Math.Max(maxTherm, s.Therm);
                    foreach (NameVal nv in s.TopCpu)
                    { double v; cpuBy.TryGetValue(nv.N, out v); cpuBy[nv.N] = Math.Max(v, nv.V); }
                    foreach (NameVal nv in s.TopIo)
                    { double v; ioBy.TryGetValue(nv.N, out v); ioBy[nv.N] = Math.Max(v, nv.V); }
                }
                string when = ep.Start.ToString("HH:mm:ss") + " (" + ep.DurationSec + "s)";
                string fg = string.IsNullOrEmpty(ep.Fg) ? "" : " while you were in '" + ep.Fg + "'";

                if (ep.Reasons.Contains("paging") || ep.Reasons.Contains("low-ram") || ep.Reasons.Contains("commit"))
                {
                    string key = "ram";
                    if (!seen.Contains(key))
                    {
                        seen.Add(key);
                        Finding f = new Finding();
                        f.Sev = 1;
                        f.Title = "Memory pressure / paging storms";
                        f.Evidence.Add("Episode(s) with hard faults from disk up to " + App.F0(maxPages) +
                            "/s, RAM down to " + App.HumanMB(minAvail) + " free, commit up to " + App.F0(maxCommit) + "%");
                        foreach (Episode e2 in episodes)
                            if (e2.Reasons.Contains("paging") || e2.Reasons.Contains("low-ram"))
                                f.Evidence.Add("  " + e2.Start.ToString("HH:mm:ss") + " for " + e2.DurationSec + "s" + (e2.Fg.Length > 0 ? " (fg: " + e2.Fg + ")" : ""));
                        if (data.Agg.MemGrow.Count > 0)
                            f.Evidence.Add("RAM growth over the window: " + GrowStr(data.Agg));
                        f.Advice = "Free RAM: close background apps, check startup programs (Task Manager > Startup), or add RAM. " +
                            "Watch the memory-growth table below for a leaking app.";
                        telFindings.Add(f);
                    }
                }

                if (ep.Reasons.Contains("cpu") && maxCpu >= 85)
                {
                    string key = "cpu";
                    if (!seen.Contains(key))
                    {
                        seen.Add(key);
                        List<string> names = new List<string>(cpuBy.Keys);
                        names.Sort(delegate(string a, string b) { return cpuBy[b].CompareTo(cpuBy[a]); });
                        Finding f = new Finding();
                        f.Sev = maxCpu >= 97 ? 1 : 2;
                        f.Title = "CPU saturation";
                        StringBuilder sb = new StringBuilder();
                        for (int i = 0; i < names.Count && i < 3; i++)
                        {
                            if (sb.Length > 0) sb.Append(", ");
                            sb.Append(names[i]).Append(' ').Append(App.F0(cpuBy[names[i]])).Append('%');
                            string note = NoteFor(names[i]);
                            if (note != null) f.Advice = (f.Advice == null ? "" : f.Advice) + note + " ";
                        }
                        f.Evidence.Add("CPU peaked at " + App.F0(maxCpu) + "% — top: " + sb.ToString());
                        foreach (Episode e2 in episodes)
                            if (e2.Reasons.Contains("cpu"))
                                f.Evidence.Add("  " + e2.Start.ToString("HH:mm:ss") + " for " + e2.DurationSec + "s" +
                                    (e2.Fg.Length > 0 ? " (fg: " + e2.Fg + ")" : ""));
                        if (f.Advice == null) f.Advice = "Identify whether the top process is expected; if not, check its publisher and uninstall/disable it.";
                        telFindings.Add(f);
                    }
                }

                if (ep.Reasons.Contains("disk") || ep.Reasons.Contains("disk-latency"))
                {
                    string key = "disk";
                    if (!seen.Contains(key))
                    {
                        seen.Add(key);
                        List<string> names = new List<string>(ioBy.Keys);
                        names.Sort(delegate(string a, string b) { return ioBy[b].CompareTo(ioBy[a]); });
                        Finding f = new Finding();
                        f.Sev = maxLat >= 150 ? 1 : 2;
                        f.Title = "Disk saturation / latency";
                        StringBuilder sb = new StringBuilder();
                        for (int i = 0; i < names.Count && i < 3; i++)
                        {
                            if (sb.Length > 0) sb.Append(", ");
                            sb.Append(names[i]).Append(' ').Append(App.F0(ioBy[names[i]])).Append("KB/s");
                        }
                        f.Evidence.Add("Disk up to " + App.F0(maxBusy) + "% busy, latency up to " + App.F1(maxLat) + "ms — top IO: " + sb.ToString());
                        foreach (Episode e2 in episodes)
                            if (e2.Reasons.Contains("disk") || e2.Reasons.Contains("disk-latency"))
                                f.Evidence.Add("  " + e2.Start.ToString("HH:mm:ss") + " for " + e2.DurationSec + "s" + (e2.Fg.Length > 0 ? " (fg: " + e2.Fg + ")" : ""));
                        f.Advice = maxLat >= 150
                            ? "High latency on top of saturation can indicate a failing or over-full drive: keep 15%+ free, run chkdsk, check the health table below."
                            : "Some process is hammering the disk (see top IO). Windows Search, updates, and sync clients are usual suspects.";
                        telFindings.Add(f);
                    }
                }

                if (ep.Reasons.Contains("dpc") && maxDpc >= 20)
                {
                    string key = "dpc";
                    if (!seen.Contains(key))
                    {
                        seen.Add(key);
                        Finding f = new Finding();
                        f.Sev = 1;
                        f.Title = "Driver DPC storm (classic cause of random stutters and audio crackle)";
                        f.Evidence.Add("% DPC time peaked at " + App.F1(maxDpc) + "% during " + when);
                        f.Evidence.Add("DPC time is driver work at high priority — a misbehaving network/ Wi-Fi, audio, GPU or storage driver delays everything else.");
                        f.Advice = "Run LatencyMon for 15 minutes to name the exact driver; then update or roll back that device's driver.";
                        telFindings.Add(f);
                    }
                }

                if (ep.Reasons.Contains("thermal"))
                {
                    string key = "thermal";
                    if (!seen.Contains(key))
                    {
                        seen.Add(key);
                        Finding f = new Finding();
                        f.Sev = 1;
                        f.Title = "High temperature reported";
                        f.Evidence.Add("Thermal zone reached " + App.F1(maxTherm) + " C");
                        f.Advice = "Clean fans/vents, re-paste if old, check fan curves — thermal throttling causes exactly this kind of random lag.";
                        telFindings.Add(f);
                    }
                }
            }
            data.Findings.AddRange(telFindings);

            // event-log findings
            int bugchecks = 0, shutdowns = 0, tdrs = 0, diskerrs = 0, resEx = 0;
            foreach (LogEvt e in data.Events)
            {
                if (!e.Notable) continue;
                if (e.Why == "Blue screen (BugCheck)") bugchecks++;
                else if (e.Why == "Unexpected shutdown / power loss" || e.Why == "Unexpected shutdown") shutdowns++;
                else if (e.Why.StartsWith("GPU driver timeout")) tdrs++;
                else if (e.Why == "Disk error/reset" || e.Why == "File system corruption warning") diskerrs++;
                else if (e.Why.StartsWith("Windows ran out of commit")) resEx++;
            }
            if (bugchecks > 0)
                data.Findings.Insert(0, Mk(0, "Blue screen (BugCheck) occurred in this window", bugchecks + " bugcheck event(s)", "After a BSOD, read the minidump with WinDbg or check WhoCrashed; note the stop code."));
            if (shutdowns > 0)
                data.Findings.Insert(0, Mk(0, "Unexpected shutdown / power loss in this window", shutdowns + " Kernel-Power 41/6008 event(s)", "If it happened under load, suspect PSU/overheating; if random, check Windows Update and memory (Windows Memory Diagnostic)."));
            if (tdrs > 0)
                data.Findings.Insert(0, Mk(1, "GPU driver stopped responding (TDR) — top cause of random freezes", tdrs + " Display 4101 event(s)", "Clean-reinstall the GPU driver with DDU, or roll back a recent driver; check GPU temperatures too."));
            if (diskerrs > 0)
                data.Findings.Insert(0, Mk(1, "Disk reported errors in this window", diskerrs + " disk/Ntfs event(s)", "Back up data, run `chkdsk /scan`, verify cables, check SMART."));
            if (resEx > 0)
                data.Findings.Insert(0, Mk(1, "Windows ran out of commit (virtual memory)", "Resource-Exhaustion-Detector fired", "Close apps, increase pagefile (or set it to system-managed), find the memory hog in the growth table."));

            // pool leak
            if (data.Agg.PoolDeltaMB > 300)
            {
                Finding f = new Finding();
                f.Sev = 2;
                f.Title = "Nonpaged kernel pool grew " + App.F0(data.Agg.PoolDeltaMB) + " MB during the window — possible driver leak";
                f.Evidence.Add("Nonpaged pool: " + App.F0(data.Agg.PoolDeltaMB) + " MB net growth");
                f.Advice = "A leaking driver holds kernel memory. `poolmon` (WDK) can name the pool tag; often fixed by updating network/graphics drivers.";
                data.Findings.Add(f);
            }

            // battery / power plan
            if (data.PowerPlan != null && data.PowerPlan.IndexOf("power saver", StringComparison.OrdinalIgnoreCase) >= 0)
                data.Findings.Add(Mk(2, "Active power plan is Power saver", "powercfg: " + data.PowerPlan, "Switch to Balanced/High performance; power saver throttles the CPU."));
            foreach (Sample s in samples)
            {
                if (s.Ac == 0)
                {
                    data.Findings.Add(Mk(2, "System was on battery during part of this window", "AC=battery samples present", "Laptops throttle hard on battery — plug in when gaming/working."));
                    break;
                }
            }
            if (data.DegradedCounters)
                data.Notes.Add("Some performance counters were unavailable (often a non-English Windows locale) — disk/DPC figures may be missing.");

            data.Findings.Sort(delegate(Finding a, Finding b)
            {
                int c = a.Sev.CompareTo(b.Sev);
                return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.Ordinal);
            });
        }

        static string GrowStr(RangeAgg agg)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < agg.MemGrow.Count && i < 3; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(agg.MemGrow[i].N).Append(" +").Append(App.F0(agg.MemGrow[i].V)).Append("MB");
            }
            return sb.ToString();
        }

        static Finding Mk(int sev, string title, string evidence, string advice)
        {
            Finding f = new Finding();
            f.Sev = sev; f.Title = title; f.Advice = advice;
            f.Evidence.Add(evidence);
            return f;
        }

        // ---- exact resource leaderboard from the recorder's minute flushes ----
        // Flush lines are cumulative per recorder session; we diff consecutive lines and
        // treat a decreasing counter as a session restart (add the new absolute value).
        public static double ToD(object o) { return o is double ? (double)o : 0; }

        public static List<LbRow> Leaderboard(DateTime from, DateTime to, out DateTime dataFrom, out DateTime dataTo)
        {
            List<KeyValuePair<DateTime, Dictionary<string, double[]>>> lines =
                new List<KeyValuePair<DateTime, Dictionary<string, double[]>>>();
            dataFrom = DateTime.MinValue;
            dataTo = DateTime.MinValue;

            for (DateTime d = from.Date; d <= to; d = d.AddDays(1))
            {
                string f = App.LeaderboardFile(d);
                if (!File.Exists(f)) continue;
                foreach (string line in ReadLinesShared(f))
                {
                    if (line.Length < 12) continue;
                    try
                    {
                        Dictionary<string, object> o = Json.Parse(line) as Dictionary<string, object>;
                        if (o == null) continue;
                        DateTime t = App.ParseTs(Json.Str(o, "t"));
                        if (t < from || t > to) continue;
                        object tmp;
                        Dictionary<string, object> tot = o.TryGetValue("tot", out tmp) ? tmp as Dictionary<string, object> : null;
                        if (tot == null) continue;
                        Dictionary<string, double[]> conv = new Dictionary<string, double[]>();
                        foreach (KeyValuePair<string, object> kv in tot)
                        {
                            List<object> arr = kv.Value as List<object>;
                            if (arr == null || arr.Count < 3) continue;
                            double[] v = new double[] { ToD(arr[0]), ToD(arr[1]), ToD(arr[2]),
                                arr.Count >= 4 ? ToD(arr[3]) : 0 };
                            conv[kv.Key] = v;
                        }
                        if (conv.Count > 0)
                        {
                            lines.Add(new KeyValuePair<DateTime, Dictionary<string, double[]>>(t, conv));
                            if (dataFrom == DateTime.MinValue || t < dataFrom) dataFrom = t;
                            if (t > dataTo) dataTo = t;
                        }
                    }
                    catch { }
                }
            }
            lines.Sort(delegate(KeyValuePair<DateTime, Dictionary<string, double[]>> a,
                                KeyValuePair<DateTime, Dictionary<string, double[]>> b)
            { return a.Key.CompareTo(b.Key); });

            Dictionary<string, double[]> prev = null;
            Dictionary<string, LbRow> map = new Dictionary<string, LbRow>();
            foreach (KeyValuePair<DateTime, Dictionary<string, double[]>> line in lines)
            {
                foreach (KeyValuePair<string, double[]> kv in line.Value)
                {
                    LbRow r;
                    if (!map.TryGetValue(kv.Key, out r)) { r = new LbRow(); r.Name = kv.Key; map[kv.Key] = r; }
                    if (kv.Value[2] > r.PeakMB) r.PeakMB = kv.Value[2];
                    if (prev != null)
                    {
                        double[] pv;
                        if (prev.TryGetValue(kv.Key, out pv))
                        {
                            double dcpu = kv.Value[0] - pv[0];
                            double dio = kv.Value[1] - pv[1];
                            double dgpu = kv.Value[3] - pv[3];
                            if (dcpu < 0) dcpu = kv.Value[0];
                            if (dio < 0) dio = kv.Value[1];
                            if (dgpu < 0) dgpu = kv.Value[3];
                            r.CpuSec += dcpu;
                            r.IoMB += dio;
                            r.GpuSec += dgpu;
                        }
                    }
                }
                prev = line.Value;
            }

            List<LbRow> rows = new List<LbRow>(map.Values);
            rows.Sort(delegate(LbRow a, LbRow b) { return b.CpuSec.CompareTo(a.CpuSec); });
            return rows;
        }

        // ---- main entry ----
        public static AnalysisData Analyze(DateTime from, DateTime to, bool degradedCountersKnown)
        {
            AnalysisData data = new AnalysisData();
            data.From = from; data.To = to;
            data.DegradedCounters = degradedCountersKnown;

            List<Sample> samples = LoadSamples(from, to);
            data.SampleCount = samples.Count;
            if (samples.Count == 0) return data;
            data.DataStart = samples[0].T;
            data.DataEnd = samples[samples.Count - 1].T;

            // median sampling gap + freeze-gap detection
            List<double> dts = new List<double>();
            for (int i = 1; i < samples.Count; i++)
            {
                double dt = (samples[i].T - samples[i - 1].T).TotalSeconds;
                if (dt > 0) dts.Add(dt);
            }
            if (dts.Count > 0)
            {
                dts.Sort();
                data.MedianDt = dts[dts.Count / 2];
                double gapThr = Math.Max(15, data.MedianDt * 8);
                for (int i = 1; i < samples.Count; i++)
                {
                    double dt = (samples[i].T - samples[i - 1].T).TotalSeconds;
                    if (dt >= gapThr)
                        data.Gaps.Add(new KeyValuePair<DateTime, double>(samples[i - 1].T, dt));
                }
            }

            List<EventRec> events = LoadEvents(from, to);
            data.Agg = Aggregate(samples);
            data.Lb = Leaderboard(from, to, out data.LbFrom, out data.LbTo);

            Console.WriteLine("  telemetry: " + samples.Count + " samples from " + data.DataStart.ToString("MM-dd HH:mm") + " to " + data.DataEnd.ToString("MM-dd HH:mm"));
            Stage("  detecting episodes...");
            data.Episodes = DetectEpisodes(samples, events);
            Stage("  reading Windows event logs...");
            CollectSources(data);
            Stage("  building findings...");
            BuildFindings(data, samples, data.Episodes);
            return data;
        }
    }
}
