// LagWatch — shared recorder control (start/stop/status/mark) used by both the CLI and the GUI.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace LagWatch
{
    public class RecState
    {
        public int Pid, Interval;
        public long Samples;
        public DateTime Started, Last;
        public bool Stopping;
    }

    public static class Controller
    {
        public static bool IsRunning(out RecState st)
        {
            st = null;
            try
            {
                if (!File.Exists(App.StateFile)) return false;
                Dictionary<string, object> o = Json.Parse(File.ReadAllText(App.StateFile)) as Dictionary<string, object>;
                if (o == null) return false;
                st = new RecState();
                st.Pid = (int)Json.Num(o, "pid");
                st.Interval = (int)Json.Num(o, "interval", App.IntervalSec);
                st.Samples = (long)Json.Num(o, "samples");
                st.Started = App.ParseTs(Json.Str(o, "started"));
                st.Last = App.ParseTs(Json.Str(o, "last"));
                st.Stopping = Json.Num(o, "stopping") > 0;
                if (st.Last == DateTime.MinValue) return false;
                return !st.Stopping && (DateTime.Now - st.Last).TotalSeconds <= st.Interval * 4 + 5;
            }
            catch { return false; }
        }

        public static string Start(int intervalSec)
        {
            App.EnsureDirs();
            RecState st;
            if (IsRunning(out st))
                return "Already recording (pid " + st.Pid + ", " + st.Samples + " samples, last " +
                    st.Last.ToString("HH:mm:ss") + ").";

            string ivals = (intervalSec > 0 && intervalSec != App.IntervalSec) ? " --interval " + intervalSec : "";
            string host = Environment.GetEnvironmentVariable("LAGWATCH_HOST");
            ProcessStartInfo pi = new ProcessStartInfo();
            pi.UseShellExecute = false;
            pi.CreateNoWindow = true;
            pi.WorkingDirectory = App.Root;
            if (host == "ps1" && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LAGWATCH_PS1")))
            {
                // hosted in-memory by PowerShell (Smart App Control safe): spawn through the same host
                pi.FileName = "powershell.exe";
                pi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" +
                    Environment.GetEnvironmentVariable("LAGWATCH_PS1") + "\" record" + ivals;
            }
            else
            {
                string exe = typeof(Controller).Assembly.Location;
                if (string.IsNullOrEmpty(exe))
                    return "Cannot start recorder: no host or executable location available.";
                pi.FileName = exe;
                pi.Arguments = "record" + ivals;
            }
            Process.Start(pi);
            Thread.Sleep(1800);
            if (IsRunning(out st))
                return "Recording started in the background (pid " + st.Pid + ", every " + st.Interval + "s). Data: " + App.LogDir;
            return "Recorder was launched but has not written a heartbeat yet — check status in a moment.";
        }

        public static string Stop(bool force)
        {
            RecState st;
            if (!IsRunning(out st)) return "Recorder is not running.";
            if (force)
            {
                try { Process.GetProcessById(st.Pid).Kill(); } catch { }
                return "Killed pid " + st.Pid + ".";
            }
            try { File.WriteAllText(App.StopFlag, "stop"); } catch (Exception ex) { return "Could not write stop flag: " + ex.Message; }
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(500);
                if (!IsRunning(out st)) return "Recorder stopped.";
            }
            return "Recorder did not exit within 10s; pid " + st.Pid + ". Use force or: taskkill /F /PID " + st.Pid;
        }

        public static string Mark(string note)
        {
            App.EnsureDirs();
            if (string.IsNullOrEmpty(note)) note = "user mark";
            EventRec e = new EventRec();
            e.T = DateTime.Now; e.Type = "mark"; e.Val = 0; e.Info = note;
            try
            {
                using (FileStream fs = new FileStream(App.MarksFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false)))
                    w.WriteLine(e.ToJson());
            }
            catch (Exception ex) { return "Mark failed: " + ex.Message; }
            return "Marked " + e.T.ToString("HH:mm:ss") + " — " + note;
        }

        public static string StatusText()
        {
            RecState st;
            if (IsRunning(out st))
                return "Recording — pid " + st.Pid + ", every " + st.Interval + "s, " + st.Samples +
                    " samples, last " + st.Last.ToString("HH:mm:ss");
            if (st != null)
                return "Not running (last heartbeat " + st.Last.ToString("yyyy-MM-dd HH:mm:ss") + ") — start it!";
            return "Not running (no state) — start it!";
        }
    }
}
