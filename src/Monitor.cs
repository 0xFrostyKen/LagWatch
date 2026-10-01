// LagWatch — system monitoring layer: native process snapshot (fast, exception-free hot path)
// plus performance counters, GPU, thermal, and power state.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LagWatch
{
    public class ProcStat
    {
        public string Name;
        public double CpuPct;
        public double PrivMB;
        public double IoKBps;
        public double CpuSec;  // exact CPU-seconds used since previous sample (valid from 2nd sample)
        public double IoMB;    // exact MB read+written since previous sample
    }

    public class ProcessSnapshot
    {
        public List<ProcStat> Procs = new List<ProcStat>();
        public Dictionary<int, string> NameByPid = new Dictionary<int, string>();
    }

    // Native APIs — all access uses PROCESS_QUERY_LIMITED_INFORMATION so we stay unprivileged.
    public static class Native
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS ioc);

        [StructLayout(LayoutKind.Sequential)]
        public class PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb;
            public uint PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
            public IntPtr QuotaPeakPagedPoolUsage;
            public IntPtr QuotaPagedPoolUsage;
            public IntPtr QuotaPeakNonPagedPoolUsage;
            public IntPtr QuotaNonPagedPoolUsage;
            public IntPtr PagefileUsage;
            public IntPtr PeakPagefileUsage;
            public IntPtr PrivateUsage;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool GetProcessMemoryInfo(IntPtr h, [In, Out] PROCESS_MEMORY_COUNTERS_EX pmc, uint cb);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EnumProcesses([Out] int[] pids, uint size, out uint needed);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref uint len);

        [StructLayout(LayoutKind.Sequential)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX b);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS s);

        public static double UptimeHours()
        {
            return GetTickCount64() / 3600000.0;
        }
    }

    // Rolling performance counters created lazily and resiliently (missing/renamed
    // counters degrade to 0 instead of breaking recording).
    public class PerfCounters
    {
        System.Diagnostics.PerformanceCounter _cpu, _pql, _ctx, _pagesIn, _poolNP, _busy, _qlen, _bps, _lat, _dpc, _intr, _freq;
        public bool Degraded;

        public PerfCounters()
        {
            _cpu = Make("Processor", "% Processor Time", "_Total");
            _pql = Make("System", "Processor Queue Length", null);
            _ctx = Make("System", "Context Switches/sec", null);
            _pagesIn = Make("Memory", "Pages Input/sec", null);
            _poolNP = Make("Memory", "Pool Nonpaged Bytes", null);
            _busy = Make("PhysicalDisk", "% Idle Time", "_Total");
            _qlen = Make("PhysicalDisk", "Current Disk Queue Length", "_Total");
            _bps = Make("PhysicalDisk", "Disk Bytes/sec", "_Total");
            _lat = Make("PhysicalDisk", "Avg. Disk sec/Transfer", "_Total");
            _dpc = Make("Processor", "% DPC Time", "_Total");
            _intr = Make("Processor", "% Interrupt Time", "_Total");
            _freq = Make("Processor Information", "% of Maximum Frequency", "_Total");
            // Prime two-sample counters so the first recorded values are valid.
            Prime();
        }

        System.Diagnostics.PerformanceCounter Make(string cat, string counter, string inst)
        {
            try
            {
                System.Diagnostics.PerformanceCounter c = inst == null
                    ? new System.Diagnostics.PerformanceCounter(cat, counter, true)
                    : new System.Diagnostics.PerformanceCounter(cat, counter, inst, true);
                c.NextValue();
                return c;
            }
            catch
            {
                Degraded = true;
                return null;
            }
        }

        void Prime()
        {
            System.Threading.Thread.Sleep(150);
            SafeNext(_cpu); SafeNext(_busy); SafeNext(_dpc); SafeNext(_intr); SafeNext(_freq);
        }

        double SafeNext(System.Diagnostics.PerformanceCounter c)
        {
            if (c == null) return 0;
            try { return c.NextValue(); }
            catch { return 0; }
        }

        public void Fill(Sample s)
        {
            s.Cpu = SafeNext(_cpu);
            s.Pql = SafeNext(_pql);
            s.Ctx = SafeNext(_ctx);
            s.PagesIn = SafeNext(_pagesIn);
            s.PoolNP = SafeNext(_poolNP);
            double idle = SafeNext(_busy);
            s.Busy = Math.Max(0, 100.0 - idle);
            s.QLen = SafeNext(_qlen);
            s.Bps = SafeNext(_bps);
            s.LatMs = SafeNext(_lat) * 1000.0;
            s.Dpc = SafeNext(_dpc);
            s.Intr = SafeNext(_intr);
            s.Freq = SafeNext(_freq);
        }
    }

    // GPU engine utilization (sampled on a slow cadence — instance enumeration is costly).
    // Also attributes utilization per PID by parsing the pid_ from each instance name,
    // which costs nothing extra: same counters, different aggregation.
    public class GpuMon
    {
        class EngCounter { public System.Diagnostics.PerformanceCounter C; public int Pid; }
        List<EngCounter> _eng = new List<EngCounter>();
        string[] _inst;
        System.Diagnostics.PerformanceCounter[] _mem;
        public bool Available = true;

        // Returns pid -> utilization % for this tick (empty when GPU counters unavailable).
        public Dictionary<int, double> SamplePerPid()
        {
            Dictionary<int, double> outv = new Dictionary<int, double>();
            if (!Available) return outv;
            try
            {
                System.Diagnostics.PerformanceCounterCategory cat =
                    new System.Diagnostics.PerformanceCounterCategory("GPU Engine");
                string[] now = cat.GetInstanceNames();
                if (_inst == null || !SameSet(now, _inst)) Rebuild(cat, now);

                foreach (EngCounter ec in _eng)
                {
                    double v = SafeNext(ec.C);
                    if (v <= 0.2) continue;
                    double cur;
                    outv.TryGetValue(ec.Pid, out cur);
                    double nv = cur + v;
                    outv[ec.Pid] = nv > 100 ? 100 : nv;
                }

                double mem = 0;
                if (_mem != null)
                    foreach (System.Diagnostics.PerformanceCounter c in _mem)
                        mem += SafeNext(c);
                _lastMemMB = mem / (1024.0 * 1024.0);
            }
            catch
            {
                Available = false; // no GPU counters on this machine
            }
            return outv;
        }

        double _lastMemMB;

        public double LastMemMB { get { return _lastMemMB; } }

        static bool SameSet(string[] a, string[] b)
        {
            if (b == null || a.Length != b.Length) return false;
            HashSet<string> hs = new HashSet<string>(a);
            foreach (string x in b) if (!hs.Contains(x)) return false;
            return true;
        }

        static int PidOf(string instance)
        {
            // "pid_4296_luid_..._engtype_3D"
            try
            {
                string[] parts = instance.Split('_');
                if (parts.Length > 2 && parts[0] == "pid") return int.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            catch { }
            return -1;
        }

        void Rebuild(System.Diagnostics.PerformanceCounterCategory cat, string[] inst)
        {
            List<EngCounter> eng = new List<EngCounter>();
            List<System.Diagnostics.PerformanceCounter> mem = new List<System.Diagnostics.PerformanceCounter>();
            foreach (string i in inst)
            {
                try
                {
                    if (i.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) >= 0 && eng.Count < 96)
                    {
                        EngCounter ec = new EngCounter();
                        ec.C = new System.Diagnostics.PerformanceCounter("GPU Engine", "Utilization Percentage", i, true);
                        ec.Pid = PidOf(i);
                        eng.Add(ec);
                    }
                }
                catch { }
            }
            foreach (string i in inst)
            {
                try
                {
                    if (i.IndexOf("phys_0", StringComparison.OrdinalIgnoreCase) >= 0 && mem.Count < 32)
                        mem.Add(new System.Diagnostics.PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", i, true));
                }
                catch { }
            }
            _eng = eng;
            _mem = mem.ToArray();
            _inst = inst;
        }

        static double SafeNext(System.Diagnostics.PerformanceCounter c)
        {
            try { return c.NextValue(); }
            catch { return 0; }
        }
    }

    // Everything sampled per tick, plus process CPU/IO deltas between ticks.
    public class SysMonitor
    {
        PerfCounters _pc;
        GpuMon _gpu;
        Dictionary<int, double> _prevCpuSec = new Dictionary<int, double>();
        Dictionary<int, double> _prevIoBytes = new Dictionary<int, double>();
        Dictionary<int, string> _nameCache = new Dictionary<int, string>();
        DateTime _lastSnap = DateTime.MinValue;
        bool _thermalOk = true;
        DateTime _lastThermal = DateTime.MinValue;
        double _lastThermC = -1;

        // Full per-name snapshot of the last Take(): exact CPU-seconds and IO deltas per name.
        public List<ProcStat> LastAll = new List<ProcStat>();
        // GPU-seconds used per process name during the last wantGpu Take (empty otherwise).
        public Dictionary<string, double> LastGpuSec = new Dictionary<string, double>();
        double _lastElapsedSec;

        public bool Degraded
        {
            get { return _pc != null && _pc.Degraded; }
        }

        public bool GpuAvailable
        {
            get { return _gpu.Available; }
        }

        public SysMonitor()
        {
            _pc = new PerfCounters();
            _gpu = new GpuMon();
        }

        // ---- process snapshot (native, fast) ----
        ProcessSnapshot SnapshotProcesses()
        {
            ProcessSnapshot snap = new ProcessSnapshot();
            int[] pids = new int[8192];
            uint needed;
            if (!Native.EnumProcesses(pids, (uint)(pids.Length * 4), out needed)) return snap;
            int count = Math.Min((int)(needed / 4), pids.Length);

            DateTime nowUtc = DateTime.UtcNow;
            double elapsed = (_lastSnap == DateTime.MinValue) ? 0 : (nowUtc - _lastSnap).TotalSeconds;
            _lastElapsedSec = elapsed;
            double cores = Environment.ProcessorCount;

            Dictionary<int, double> newCpu = new Dictionary<int, double>();
            Dictionary<int, double> newIo = new Dictionary<int, double>();

            Native.PROCESS_MEMORY_COUNTERS_EX pmc = new Native.PROCESS_MEMORY_COUNTERS_EX();
            pmc.cb = (uint)Marshal.SizeOf(typeof(Native.PROCESS_MEMORY_COUNTERS_EX));

            for (int i = 0; i < count; i++)
            {
                int pid = pids[i];
                if (pid <= 0) continue;
                IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) continue;

                try
                {
                    string name;
                    if (!_nameCache.TryGetValue(pid, out name))
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        uint len = 1024;
                        if (Native.QueryFullProcessImageName(h, 0, sb, ref len))
                            name = System.IO.Path.GetFileNameWithoutExtension(sb.ToString(0, (int)len));
                        else
                            name = "pid" + pid.ToString(CultureInfo.InvariantCulture);
                        _nameCache[pid] = name;
                    }
                    snap.NameByPid[pid] = name;

                    double cpuSec = 0;
                    long c, e, k, u;
                    if (Native.GetProcessTimes(h, out c, out e, out k, out u))
                        cpuSec = (k + u) / 10000000.0;

                    double privMB = 0;
                    if (Native.GetProcessMemoryInfo(h, pmc, pmc.cb))
                        privMB = pmc.PrivateUsage.ToInt64() / (1024.0 * 1024.0);

                    double ioBytes = 0;
                    Native.IO_COUNTERS ioc;
                    if (Native.GetProcessIoCounters(h, out ioc))
                        ioBytes = (double)(ioc.ReadTransferCount + ioc.WriteTransferCount);

                    newCpu[pid] = cpuSec;
                    newIo[pid] = ioBytes;

                    ProcStat st = new ProcStat();
                    st.Name = name;
                    st.PrivMB = privMB;
                    if (elapsed > 0)
                    {
                        double prev;
                        if (_prevCpuSec.TryGetValue(pid, out prev))
                        {
                            double d = Math.Max(0, cpuSec - prev);
                            st.CpuSec = d;
                            st.CpuPct = d / elapsed / cores * 100.0;
                        }
                        double prevIo;
                        if (_prevIoBytes.TryGetValue(pid, out prevIo))
                        {
                            double dio = Math.Max(0, ioBytes - prevIo);
                            st.IoMB = dio / (1024.0 * 1024.0);
                            st.IoKBps = dio / elapsed / 1024.0;
                        }
                    }
                    snap.Procs.Add(st);
                }
                finally
                {
                    Native.CloseHandle(h);
                }
            }

            _prevCpuSec = newCpu;
            _prevIoBytes = newIo;
            _lastSnap = nowUtc;
            if (_nameCache.Count > 4096) _nameCache.Clear(); // pid reuse safety
            return snap;
        }

        // Build a full sample; process deltas are valid from the second call onward.
        public Sample Take(bool wantGpu, bool wantThermal)
        {
            Sample s = new Sample();
            ProcessSnapshot snap = SnapshotProcesses();

            Native.MEMORYSTATUSEX mem = new Native.MEMORYSTATUSEX();
            mem.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (Native.GlobalMemoryStatusEx(mem))
            {
                s.TotalMB = mem.ullTotalPhys / (1024.0 * 1024.0);
                s.AvailMB = mem.ullAvailPhys / (1024.0 * 1024.0);
                s.Commit = mem.dwMemoryLoad;
            }

            Native.SYSTEM_POWER_STATUS ps = new Native.SYSTEM_POWER_STATUS();
            if (Native.GetSystemPowerStatus(ref ps))
            {
                s.Ac = ps.ACLineStatus == 1 ? 1 : (ps.ACLineStatus == 0 ? 0 : -1);
                s.Batt = ps.BatteryLifePercent <= 100 ? ps.BatteryLifePercent : -1;
            }

            _pc.Fill(s);

            // aggregate process stats by name: [cpuPct, privMB, ioKBps, cpuSec, ioMB]
            Dictionary<string, double[]> agg = new Dictionary<string, double[]>();
            foreach (ProcStat p in snap.Procs)
            {
                double[] v;
                if (!agg.TryGetValue(p.Name, out v)) { v = new double[5]; agg[p.Name] = v; }
                v[0] += p.CpuPct; v[1] += p.PrivMB; v[2] += p.IoKBps; v[3] += p.CpuSec; v[4] += p.IoMB;
            }
            s.Procs = snap.Procs.Count;

            LastAll = new List<ProcStat>();
            foreach (KeyValuePair<string, double[]> kv in agg)
            {
                ProcStat np = new ProcStat();
                np.Name = kv.Key;
                np.CpuPct = kv.Value[0];
                np.PrivMB = kv.Value[1];
                np.IoKBps = kv.Value[2];
                np.CpuSec = kv.Value[3];
                np.IoMB = kv.Value[4];
                LastAll.Add(np);
            }

            List<KeyValuePair<string, double[]>> list = new List<KeyValuePair<string, double[]>>(agg);
            list.Sort(delegate(KeyValuePair<string, double[]> a, KeyValuePair<string, double[]> b)
            { return b.Value[0].CompareTo(a.Value[0]); });
            for (int i = 0; i < list.Count && i < 8; i++)
                if (list[i].Value[0] >= 0.5)
                    s.TopCpu.Add(new NameVal { N = list[i].Key, V = list[i].Value[0] });

            list.Sort(delegate(KeyValuePair<string, double[]> a, KeyValuePair<string, double[]> b)
            { return b.Value[1].CompareTo(a.Value[1]); });
            for (int i = 0; i < list.Count && i < 8; i++)
                s.TopMem.Add(new NameVal { N = list[i].Key, V = list[i].Value[1] });

            list.Sort(delegate(KeyValuePair<string, double[]> a, KeyValuePair<string, double[]> b)
            { return b.Value[2].CompareTo(a.Value[2]); });
            for (int i = 0; i < list.Count && i < 6; i++)
                if (list[i].Value[2] >= 1)
                    s.TopIo.Add(new NameVal { N = list[i].Key, V = list[i].Value[2] });

            // foreground process
            try
            {
                int fpid;
                IntPtr fw = Native.GetForegroundWindow();
                if (fw != IntPtr.Zero && Native.GetWindowThreadProcessId(fw, out fpid) != 0)
                {
                    string n;
                    if (snap.NameByPid.TryGetValue(fpid, out n)) s.Fg = n;
                }
            }
            catch { }

            if (wantGpu)
            {
                Dictionary<int, double> perPid = _gpu.SamplePerPid();
                double total = 0;
                Dictionary<string, double> gpuSecByName = new Dictionary<string, double>();
                foreach (KeyValuePair<int, double> kv in perPid)
                {
                    total += kv.Value;
                    string n;
                    if (kv.Key > 0 && snap.NameByPid.TryGetValue(kv.Key, out n))
                    {
                        double cur;
                        gpuSecByName.TryGetValue(n, out cur);
                        gpuSecByName[n] = cur + kv.Value / 100.0 * _lastElapsedSec;
                    }
                }
                s.Gpu3d = Math.Min(100, total);
                s.GpuMem = _gpu.LastMemMB;

                List<NameVal> topGpu = new List<NameVal>();
                foreach (KeyValuePair<string, double> kv in gpuSecByName)
                {
                    double pct = _lastElapsedSec > 0 ? kv.Value * 100.0 / _lastElapsedSec : 0;
                    if (pct >= 0.5) topGpu.Add(new NameVal { N = kv.Key, V = pct });
                }
                topGpu.Sort(delegate(NameVal a, NameVal b) { return b.V.CompareTo(a.V); });
                s.TopGpu = topGpu;
                LastGpuSec = gpuSecByName;
            }
            if (wantThermal) { _lastThermC = ReadThermal(); _lastThermal = DateTime.Now; }
            if ((DateTime.Now - _lastThermal).TotalMinutes < 2) s.Therm = _lastThermC;

            return s;
        }

        double ReadThermal()
        {
            if (!_thermalOk) return -1;
            try
            {
                using (System.Management.ManagementObjectSearcher searcher =
                    new System.Management.ManagementObjectSearcher(@"root\wmi",
                        "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    double maxC = -1;
                    foreach (System.Management.ManagementObject o in searcher.Get())
                    {
                        object v = o["CurrentTemperature"];
                        if (v == null) continue;
                        double c = Convert.ToDouble(v, CultureInfo.InvariantCulture) / 10.0 - 273.15;
                        if (c > maxC) maxC = c;
                    }
                    if (maxC <= 0) { _thermalOk = false; return -1; }
                    return maxC;
                }
            }
            catch
            {
                _thermalOk = false;
                return -1;
            }
        }
    }
}
