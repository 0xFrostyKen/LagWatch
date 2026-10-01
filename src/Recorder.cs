// LagWatch — background recorder: samples every few seconds, appends JSONL, flags
// threshold events cheaply. Designed to cost <0.1% CPU and a few MB of disk per day.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LagWatch
{
    public static class Recorder
    {
        static volatile bool _cancel;
        static readonly Dictionary<string, int> _consec = new Dictionary<string, int>();
        static readonly Dictionary<string, DateTime> _lastFire = new Dictionary<string, DateTime>();

        // Exact per-process resource totals for the leaderboard (cumulative per session).
        class LbEntry { public double Cpu, Io, Peak, Gpu; }
        static readonly Dictionary<string, LbEntry> _lb = new Dictionary<string, LbEntry>();

        static void LbAdd(SysMonitor mon)
        {
            foreach (ProcStat p in mon.LastAll)
            {
                if (p.CpuSec < 0.02 && p.IoMB < 0.05 && p.PrivMB < 8) continue; // skip idle noise
                LbEntry e;
                if (!_lb.TryGetValue(p.Name, out e))
                {
                    if (_lb.Count > 1200) continue; // bound memory on process-churning machines
                    e = new LbEntry();
                    _lb[p.Name] = e;
                }
                e.Cpu += p.CpuSec;
                e.Io += p.IoMB;
                if (p.PrivMB > e.Peak) e.Peak = p.PrivMB;
            }
            foreach (KeyValuePair<string, double> kv in mon.LastGpuSec)
            {
                LbEntry e;
                if (!_lb.TryGetValue(kv.Key, out e))
                {
                    if (_lb.Count > 1200) continue;
                    e = new LbEntry();
                    _lb[kv.Key] = e;
                }
                e.Gpu += kv.Value;
            }
        }

        // One cumulative line per minute: {"t":...,"tot":{"name":[cpuSec,ioMB,peakMB,gpuSec],...}}
        static void LbFlush()
        {
            if (_lb.Count == 0) return;
            try
            {
                DateTime now = DateTime.Now;
                StringBuilder sb = new StringBuilder(4096);
                sb.Append("{\"t\":\"").Append(App.Ts(now)).Append("\",\"tot\":{");
                bool first = true;
                foreach (KeyValuePair<string, LbEntry> kv in _lb)
                {
                    if (kv.Value.Cpu < 0.01 && kv.Value.Io < 0.1 && kv.Value.Peak < 8 && kv.Value.Gpu < 0.05) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(Json.Esc(kv.Key)).Append("\":[")
                       .Append(App.F1(kv.Value.Cpu)).Append(',')
                       .Append(App.F1(kv.Value.Io)).Append(',')
                       .Append(App.F1(kv.Value.Peak)).Append(',')
                       .Append(App.F1(kv.Value.Gpu)).Append(']');
                }
                sb.Append("}}");
                using (FileStream fs = new FileStream(App.LeaderboardFile(now), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false)))
                    w.WriteLine(sb.ToString());
            }
            catch { }
        }

        public static int Run(int intervalSec)
        {
            bool created;
            using (Mutex mx = new Mutex(true, "LagWatchRecorderMutex", out created))
            {
                if (!created)
                {
                    Console.Error.WriteLine("LagWatch recorder is already running.");
                    return 2;
                }

                // Stay at Normal priority: the sampler costs ~2ms every few seconds, so it
                // cannot contribute to lag — but BelowNormal gets STARVED during exactly the
                // 100%-CPU storms we most need to record (verified in testing).
                Thread.CurrentThread.IsBackground = false;

                App.EnsureDirs();
                App.IntervalSec = intervalSec;
                _cancel = false;
                Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e) { e.Cancel = true; _cancel = true; };

                DateTime started = DateTime.Now;
                SysMonitor mon = new SysMonitor(); // warm up counters before the loop
                Sample warm = mon.Take(false, false);

                long samples = 0;
                DateTime lastPurgeDay = DateTime.Now.Date;
                StreamWriter tel = null, evt = null;
                DateTime telDay = DateTime.MinValue, evtDay = DateTime.MinValue;

                if (mon.Degraded)
                    Console.Error.WriteLine("Note: some performance counters are unavailable (possibly a non-English Windows locale); recording what we can.");

                Console.WriteLine("Recording every " + intervalSec + "s to " + App.LogDir + "  (Ctrl+C to stop)");

                WriteState(started, samples, DateTime.Now);

                // One-shot per boot: if Windows logged an unexpected shutdown/BSOD at this
                // boot, analyze the 30 minutes before it in the background. Marker file
                // keeps this from ever running twice for the same boot.
                Thread triage = new Thread((ThreadStart)delegate { Thread.Sleep(20000); AutoTriage(); });
                triage.IsBackground = true;
                triage.Start();

                while (!_cancel)
                {
                    if (File.Exists(App.StopFlag))
                    {
                        try { File.Delete(App.StopFlag); } catch { }
                        break;
                    }

                    try
                    {
                        DateTime now = DateTime.Now;
                        if (now.Date != lastPurgeDay) { Purge(); lastPurgeDay = now.Date; }

                        bool wantGpu = (samples % 10 == 3);
                        bool wantTherm = (samples % 20 == 7);
                        Sample s = mon.Take(wantGpu, wantTherm);
                        s.T = now;

                        if (s.T.Date != telDay) { SafeClose(ref tel); tel = Open(App.TelemetryFile(s.T), ref telDay, s.T); }
                        if (s.T.Date != evtDay) { SafeClose(ref evt); evt = Open(App.EventsFile(s.T), ref evtDay, s.T); }

                        if (tel != null) { tel.WriteLine(s.ToJson()); }
                        LbAdd(mon);
                        if (samples % 20 == 19) LbFlush(); // every ~1 minute
                        EvaluateThresholds(s, evt);

                        samples++;
                        if (samples % 5 == 0) WriteState(started, samples, now);
                        if (samples % 5 == 0)
                        {
                            NameVal top = s.TopCpu.Count > 0 ? s.TopCpu[0] : null;
                            Console.WriteLine(App.Ts(now).Substring(11, 8) +
                                "  CPU " + App.F0(s.Cpu) + "%" +
                                " | RAM " + App.HumanMB(s.AvailMB) + " free, commit " + App.F0(s.Commit) + "%" +
                                " | Disk " + App.F0(s.Busy) + "% " + App.F1(s.LatMs) + "ms" +
                                " | DPC " + App.F1(s.Dpc) + "%" +
                                (s.Gpu3d > 0 ? " | GPU " + App.F0(s.Gpu3d) + "%" : "") +
                                (top != null ? " | top " + top.N + " " + App.F0(top.V) + "%" : "") +
                                (s.Fg.Length > 0 ? " | fg " + s.Fg : ""));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("sample error: " + ex.Message);
                    }

                    // sleep in slices so Ctrl+C and the stop flag stay responsive
                    DateTime until = DateTime.Now.AddSeconds(intervalSec);
                    while (!_cancel && DateTime.Now < until)
                    {
                        Thread.Sleep(200);
                        if (File.Exists(App.StopFlag)) break;
                    }
                }

                SafeClose(ref tel);
                SafeClose(ref evt);
                LbFlush(); // capture short sessions too
                WriteState(started, samples, DateTime.Now, true);
                Console.WriteLine("Recorder stopped after " + samples + " samples.");
            }
            return 0;
        }

        static StreamWriter Open(string path, ref DateTime day, DateTime t)
        {
            day = t.Date;
            StreamWriter w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));
            w.AutoFlush = true; // survive hard crashes/BSOD without losing samples
            return w;
        }

        static void SafeClose(ref StreamWriter w)
        {
            if (w != null) { try { w.Dispose(); } catch { } w = null; }
        }

        static void WriteState(DateTime started, long samples, DateTime now) { WriteState(started, samples, now, false); }

        static void WriteState(DateTime started, long samples, DateTime now, bool stopping)
        {
            try
            {
                string json = "{\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id +
                              ",\"interval\":" + App.IntervalSec +
                              ",\"samples\":" + samples +
                              ",\"started\":\"" + App.Ts(started) + "\"" +
                              ",\"last\":\"" + App.Ts(now) + "\"" +
                              (stopping ? ",\"stopping\":true" : "") + "}";
                File.WriteAllText(App.StateFile, json);
            }
            catch { }
        }

        static void Purge()
        {
            try
            {
                if (!Directory.Exists(App.LogDir)) return;
                DateTime cutoff = DateTime.Now.Date.AddDays(-App.RetentionDays);
                foreach (string f in Directory.GetFiles(App.LogDir, "*.jsonl"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    int dash = name.LastIndexOf('-');
                    if (dash < 0) continue;
                    DateTime day;
                    if (DateTime.TryParseExact(name.Substring(dash + 1), "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out day) && day < cutoff)
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        // ---- cheap threshold flagging ----
        static void Fire(StreamWriter evt, Sample s, string type, double val, string info)
        {
            DateTime last;
            DateTime now = s.T;
            if (_lastFire.TryGetValue(type, out last) && (now - last).TotalSeconds < 90) return;
            _lastFire[type] = now;
            EventRec e = new EventRec();
            e.T = now; e.Type = type; e.Val = val; e.Info = info;
            if (evt != null) evt.WriteLine(e.ToJson());
        }

        // Sustained conditions must hold for `need` consecutive samples before firing.
        static void Check(StreamWriter evt, Sample s, string type, bool cond, double val, int need, string info)
        {
            int c;
            _consec.TryGetValue(type, out c);
            c = cond ? c + 1 : 0;
            _consec[type] = c;
            if (c >= need)
            {
                DateTime last;
                if (!_lastFire.TryGetValue(type, out last) || (s.T - last).TotalSeconds >= 90)
                    Fire(evt, s, type, val, info);
            }
        }

        static string TopStr(Sample s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.TopCpu.Count && i < 2; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(s.TopCpu[i].N).Append(' ').Append(App.F0(s.TopCpu[i].V)).Append('%');
            }
            return sb.ToString();
        }

        static string TopIoStr(Sample s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.TopIo.Count && i < 2; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(s.TopIo[i].N).Append(' ').Append(App.F0(s.TopIo[i].V)).Append("KB/s");
            }
            return sb.ToString();
        }

        static void EvaluateThresholds(Sample s, StreamWriter evt)
        {
            Check(evt, s, "cpu-spike", s.Cpu >= 85, s.Cpu, 3,
                "CPU " + App.F0(s.Cpu) + "%; top: " + TopStr(s));
            Check(evt, s, "paging-storm", s.PagesIn >= 800, s.PagesIn, 2,
                App.F0(s.PagesIn) + " hard faults/s from disk" + (s.Fg.Length > 0 ? "; fg " + s.Fg : ""));
            Check(evt, s, "memory-low", s.AvailMB > 0 && s.AvailMB <= 600, s.AvailMB, 2,
                "only " + App.HumanMB(s.AvailMB) + " RAM free; commit " + App.F0(s.Commit) + "%");
            Check(evt, s, "commit-pressure", s.Commit >= 92, s.Commit, 3,
                "commit " + App.F0(s.Commit) + "%");
            Check(evt, s, "disk-saturation", s.Busy >= 90, s.Busy, 3,
                "disk " + App.F0(s.Busy) + "% busy; io: " + TopIoStr(s));
            Check(evt, s, "disk-latency", s.LatMs >= 60, s.LatMs, 2,
                "avg disk latency " + App.F1(s.LatMs) + "ms");
            Check(evt, s, "dpc-storm", s.Dpc >= 20, s.Dpc, 3,
                App.F1(s.Dpc) + "% DPC time (driver/interrupt load)");
            if (s.Therm >= 92)
                Fire(evt, s, "thermal", s.Therm, "thermal zone " + App.F1(s.Therm) + " C");
        }

        public static void PurgeNow() { Purge(); }

        // ---- post-crash auto-triage (runs once per boot, in background) ----
        static bool CrashDetected(DateTime boot)
        {
            try
            {
                string f = boot.AddMinutes(-1).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'", CultureInfo.InvariantCulture);
                string t = boot.AddMinutes(15).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'", CultureInfo.InvariantCulture);
                string xpath = "*[System[TimeCreated[@SystemTime>='" + f + "' and @SystemTime<='" + t + "']]]";
                System.Diagnostics.Eventing.Reader.EventLogQuery q =
                    new System.Diagnostics.Eventing.Reader.EventLogQuery("System",
                        System.Diagnostics.Eventing.Reader.PathType.LogName, xpath);
                q.TolerateQueryErrors = true;
                using (System.Diagnostics.Eventing.Reader.EventLogReader r =
                    new System.Diagnostics.Eventing.Reader.EventLogReader(q))
                {
                    System.Diagnostics.Eventing.Reader.EventRecord rec;
                    while ((rec = r.ReadEvent()) != null)
                    {
                        using (rec)
                        {
                            string p = rec.ProviderName ?? "";
                            int id = rec.Id;
                            if (id == 41 && p.IndexOf("Kernel-Power", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                            if (id == 6008 && p == "EventLog") return true;
                            if (id == 1001 && (p.IndexOf("BugCheck", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.IndexOf("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        static void AutoTriage()
        {
            try
            {
                DateTime boot = DateTime.Now.AddHours(-Native.UptimeHours());
                string stamp = boot.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
                string marker = Path.Combine(App.Root, "triage-done.txt");
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() == stamp) return;
                bool crash = CrashDetected(boot);
                try { File.WriteAllText(marker, stamp); } catch { }
                if (!crash) return;

                Console.WriteLine("Boot follows an unexpected shutdown — auto-triaging the 30 min before it...");
                AnalysisData data = Analyzer.Analyze(boot.AddMinutes(-30), boot.AddMinutes(2), false);
                if (data.SampleCount == 0) return;
                string path = Path.Combine(App.ReportsDir, "report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
                File.WriteAllText(path, Report.Render(data), new UTF8Encoding(true));
                Controller.Mark("auto-triage: unexpected shutdown before " + boot.ToString("HH:mm") + " — report written");
                Console.WriteLine("Auto-triage report: " + path);
            }
            catch { }
        }
    }
}
