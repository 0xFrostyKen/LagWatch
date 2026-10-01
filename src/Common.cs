// LagWatch — lightweight background telemetry recorder + on-demand lag analyzer for Windows
// Common types, paths, and a minimal JSON reader/writer (no external dependencies).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LagWatch
{
    public static class App
    {
        public static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LagWatch");
        public static readonly string LogDir = Path.Combine(Root, "log");
        public static readonly string ReportsDir = Path.Combine(Root, "reports");
        public static readonly string RunDir = Path.Combine(Root, "run");

        public static string StateFile { get { return Path.Combine(Root, "state.json"); } }
        public static string StopFlag { get { return Path.Combine(Root, "stop.flag"); } }

        public static int IntervalSec = 3;
        public static int RetentionDays = 14;

        public static void EnsureDirs()
        {
            if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
            if (!Directory.Exists(ReportsDir)) Directory.CreateDirectory(ReportsDir);
            if (!Directory.Exists(RunDir)) Directory.CreateDirectory(RunDir);
        }

        public static string TelemetryFile(DateTime d)
        {
            return Path.Combine(LogDir, "telemetry-" + d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
        }
        public static string EventsFile(DateTime d)
        {
            return Path.Combine(LogDir, "events-" + d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
        }
        public static string MarksFile { get { return Path.Combine(LogDir, "marks.jsonl"); } }
        public static string LeaderboardFile(DateTime d)
        {
            return Path.Combine(LogDir, "leaderboard-" + d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
        }

        public static string F(double v, string fmt)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            return v.ToString(fmt, CultureInfo.InvariantCulture);
        }
        public static string F1(double v) { return F(v, "0.#"); }
        public static string F0(double v) { return F(v, "0"); }

        public static string Ts(DateTime t)
        {
            return t.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
        public static DateTime ParseTs(string s)
        {
            DateTime r;
            if (DateTime.TryParseExact(s, "yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out r)) return r;
            return DateTime.MinValue;
        }

        public static string HumanMB(double mb)
        {
            if (mb >= 1024) return F(mb / 1024.0, "0.0") + " GB";
            return F0(mb) + " MB";
        }

        public static string HumanCpuSec(double s)
        {
            if (s >= 3600) return F(s / 3600.0, "0.00") + " h";
            if (s >= 60) return F0(s / 60.0) + " min";
            return F1(s) + " s";
        }

        public static string FileDateString(DateTime d)
        {
            return d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }
    }

    public class NameVal
    {
        public string N;
        public double V;
    }

    // One telemetry sample. All numeric fields default 0 = "unknown/not collected".
    public class Sample
    {
        public DateTime T;
        public double Cpu;        // % total CPU
        public double Pql;        // processor queue length
        public double AvailMB;    // physical RAM available
        public double TotalMB;    // physical RAM total
        public double Commit;     // commit charge %
        public double PagesIn;    // hard faults served from disk / sec
        public double PoolNP;     // nonpaged pool bytes
        public double Busy;       // disk % busy
        public double QLen;       // disk queue length
        public double Bps;        // disk bytes/sec
        public double LatMs;      // avg disk latency ms per transfer
        public double Dpc;        // % DPC time
        public double Intr;       // % interrupt time
        public double Ctx;        // context switches/sec
        public double Freq;       // % of maximum frequency
        public double Gpu3d;      // GPU 3D engine utilization %
        public double GpuMem;     // GPU dedicated memory MB
        public int Ac;            // 1 on AC power, 0 battery, -1 unknown
        public int Batt;          // battery percent (-1 unknown)
        public int Procs;         // process count
        public string Fg;         // foreground process name
        public double Therm;      // max thermal zone temp C (-1 unknown)
        public List<NameVal> TopCpu;
        public List<NameVal> TopMem;
        public List<NameVal> TopIo;
        public List<NameVal> TopGpu;

        public Sample()
        {
            Fg = "";
            Therm = -1;
            Ac = -1; Batt = -1;
            TopCpu = new List<NameVal>();
            TopMem = new List<NameVal>();
            TopIo = new List<NameVal>();
            TopGpu = new List<NameVal>();
        }

        public string ToJson()
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"t\":\"").Append(App.Ts(T)).Append('"');
            sb.Append(",\"cpu\":").Append(App.F1(Cpu));
            sb.Append(",\"pql\":").Append(App.F1(Pql));
            sb.Append(",\"availMB\":").Append(App.F1(AvailMB));
            sb.Append(",\"totalMB\":").Append(App.F1(TotalMB));
            sb.Append(",\"commit\":").Append(App.F1(Commit));
            sb.Append(",\"pagesIn\":").Append(App.F1(PagesIn));
            sb.Append(",\"poolNP\":").Append(App.F0(PoolNP));
            sb.Append(",\"busy\":").Append(App.F1(Busy));
            sb.Append(",\"qlen\":").Append(App.F1(QLen));
            sb.Append(",\"bps\":").Append(App.F0(Bps));
            sb.Append(",\"latMs\":").Append(App.F1(LatMs));
            sb.Append(",\"dpc\":").Append(App.F1(Dpc));
            sb.Append(",\"intr\":").Append(App.F1(Intr));
            sb.Append(",\"ctx\":").Append(App.F0(Ctx));
            sb.Append(",\"freq\":").Append(App.F1(Freq));
            sb.Append(",\"gpu3d\":").Append(App.F1(Gpu3d));
            sb.Append(",\"gpuMem\":").Append(App.F1(GpuMem));
            sb.Append(",\"ac\":").Append(Ac);
            sb.Append(",\"batt\":").Append(Batt);
            sb.Append(",\"procs\":").Append(Procs);
            sb.Append(",\"fg\":\"").Append(Json.Esc(Fg)).Append('"');
            sb.Append(",\"therm\":").Append(App.F1(Therm));
            AppendNV(sb, "topCpu", TopCpu);
            AppendNV(sb, "topMem", TopMem);
            AppendNV(sb, "topIo", TopIo);
            if (TopGpu.Count > 0) AppendNV(sb, "topGpu", TopGpu); // only when sampled, keeps lines small
            sb.Append('}');
            return sb.ToString();
        }

        static void AppendNV(StringBuilder sb, string key, List<NameVal> list)
        {
            sb.Append(",\"").Append(key).Append("\":[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"n\":\"").Append(Json.Esc(list[i].N)).Append("\",\"v\":").Append(App.F1(list[i].V)).Append('}');
            }
            sb.Append(']');
        }

        public static Sample FromJson(Dictionary<string, object> o)
        {
            Sample s = new Sample();
            s.T = App.ParseTs(Json.Str(o, "t"));
            s.Cpu = Json.Num(o, "cpu");
            s.Pql = Json.Num(o, "pql");
            s.AvailMB = Json.Num(o, "availMB");
            s.TotalMB = Json.Num(o, "totalMB");
            s.Commit = Json.Num(o, "commit");
            s.PagesIn = Json.Num(o, "pagesIn");
            s.PoolNP = Json.Num(o, "poolNP");
            s.Busy = Json.Num(o, "busy");
            s.QLen = Json.Num(o, "qlen");
            s.Bps = Json.Num(o, "bps");
            s.LatMs = Json.Num(o, "latMs");
            s.Dpc = Json.Num(o, "dpc");
            s.Intr = Json.Num(o, "intr");
            s.Ctx = Json.Num(o, "ctx");
            s.Freq = Json.Num(o, "freq");
            s.Gpu3d = Json.Num(o, "gpu3d");
            s.GpuMem = Json.Num(o, "gpuMem");
            s.Ac = (int)Json.Num(o, "ac", -1);
            s.Batt = (int)Json.Num(o, "batt", -1);
            s.Procs = (int)Json.Num(o, "procs");
            s.Fg = Json.Str(o, "fg");
            s.Therm = Json.Num(o, "therm", -1);
            FillNV(o, "topCpu", s.TopCpu);
            FillNV(o, "topMem", s.TopMem);
            FillNV(o, "topIo", s.TopIo);
            FillNV(o, "topGpu", s.TopGpu);
            return s;
        }

        static void FillNV(Dictionary<string, object> o, string key, List<NameVal> into)
        {
            object arr;
            if (!o.TryGetValue(key, out arr)) return;
            List<object> l = arr as List<object>;
            if (l == null) return;
            foreach (object e in l)
            {
                Dictionary<string, object> d = e as Dictionary<string, object>;
                if (d == null) continue;
                NameVal nv = new NameVal();
                nv.N = Json.Str(d, "n");
                nv.V = Json.Num(d, "v");
                into.Add(nv);
            }
        }
    }

    // A flagged event from the recorder (cheap threshold hit) or a user "mark".
    public class EventRec
    {
        public DateTime T;
        public string Type;
        public double Val;
        public string Info;

        public string ToJson()
        {
            return "{\"t\":\"" + App.Ts(T) + "\",\"type\":\"" + Json.Esc(Type) +
                   "\",\"val\":" + App.F1(Val) + ",\"info\":\"" + Json.Esc(Info == null ? "" : Info) + "\"}";
        }

        public static EventRec FromJson(Dictionary<string, object> o)
        {
            EventRec e = new EventRec();
            e.T = App.ParseTs(Json.Str(o, "t"));
            e.Type = Json.Str(o, "type");
            e.Val = Json.Num(o, "val");
            e.Info = Json.Str(o, "info");
            return e;
        }
    }

    // Minimal JSON parser sufficient for LagWatch's own files.
    public static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            return ParseValue(s, ref i);
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return ParseObj(s, ref i);
            if (c == '[') return ParseArr(s, ref i);
            if (c == '"') return ParseStr(s, ref i);
            if (c == 't') { i += 4; return (object)true; }
            if (c == 'f') { i += 5; return (object)false; }
            if (c == 'n') { i += 4; return null; }
            return ParseNum(s, ref i);
        }

        static Dictionary<string, object> ParseObj(string s, ref int i)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            i++; // {
            Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                Ws(s, ref i);
                string key = ParseStr(s, ref i);
                Ws(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                object v = ParseValue(s, ref i);
                d[key] = v;
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return d;
        }

        static List<object> ParseArr(string s, ref int i)
        {
            List<object> l = new List<object>();
            i++; // [
            Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (i < s.Length)
            {
                object v = ParseValue(s, ref i);
                l.Add(v);
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
            }
            return l;
        }

        static string ParseStr(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { i++; break; }
                if (c == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char e = s[i];
                    if (e == '"') sb.Append('"');
                    else if (e == '\\') sb.Append('\\');
                    else if (e == '/') sb.Append('/');
                    else if (e == 'b') sb.Append('\b');
                    else if (e == 'f') sb.Append('\f');
                    else if (e == 'n') sb.Append('\n');
                    else if (e == 'r') sb.Append('\r');
                    else if (e == 't') sb.Append('\t');
                    else if (e == 'u' && i + 4 < s.Length)
                    {
                        int code;
                        if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out code)) sb.Append((char)code);
                        i += 4;
                    }
                    i++;
                }
                else { sb.Append(c); i++; }
            }
            return sb.ToString();
        }

        static double ParseNum(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && ("-+.eE0123456789".IndexOf(s[i]) >= 0)) i++;
            double v;
            if (double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        public static string Esc(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string Str(Dictionary<string, object> o, string key)
        {
            if (o == null) return "";
            object v;
            if (!o.TryGetValue(key, out v) || v == null) return "";
            return v as string ?? "";
        }

        public static double Num(Dictionary<string, object> o, string key) { return Num(o, key, 0); }

        public static double Num(Dictionary<string, object> o, string key, double def)
        {
            if (o == null) return def;
            object v;
            if (!o.TryGetValue(key, out v) || v == null) return def;
            if (v is double) return (double)v;
            if (v is bool) return (bool)v ? 1 : 0;
            double r;
            if (double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return def;
        }
    }
}
