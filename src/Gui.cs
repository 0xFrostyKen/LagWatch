// LagWatch - WinForms GUI: live charts, top-process list, flagged events, one-click
// "it lagged - analyze now", report history, and a tray icon. Runs hosted in-memory
// by PowerShell (Smart App Control safe) - see GuiEntry.Run / LagWatch.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LagWatch
{
    public static class GuiEntry
    {
        public static void LogCrash(object ex)
        {
            Exception e = ex as Exception;
            if (e == null) return;
            try
            {
                File.AppendAllText(Path.Combine(App.Root, "gui-crash.log"),
                    App.Ts(DateTime.Now) + "  " + e + "\r\n\r\n");
            }
            catch { }
        }

        public static void Run()
        {
            bool created;
            Mutex mx = new Mutex(true, "LagWatchGuiMutex", out created);
            if (!created)
            {
                MessageBox.Show("LagWatch GUI is already running (check the system tray).", "LagWatch");
                return;
            }
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            { LogCrash(e.Exception); };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            { LogCrash(e.ExceptionObject); };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            GC.KeepAlive(mx);
        }
    }

    // ---- rolling mini chart ----
    public class SeriesDef
    {
        public string Name;
        public Color Col;
        public List<double> Vals = new List<double>();
    }

    public class MiniChart
    {
        public string Title;
        public double FixedMax;   // 0 = auto scale (with floor)
        public double AutoFloor = 100;
        public List<SeriesDef> S = new List<SeriesDef>();

        public double Max()
        {
            if (FixedMax > 0) return FixedMax;
            double m = AutoFloor;
            foreach (SeriesDef sd in S)
                foreach (double v in sd.Vals)
                    if (v > m) m = v;
            return m * 1.15;
        }
    }

    public class ChartPanel : Panel
    {
        public List<MiniChart> Charts = new List<MiniChart>();
        public int Capacity = 200; // ~5 min at 1.5s

        public ChartPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(22, 22, 27);
        }

        public void Push(Sample s, double ramMaxGB)
        {
            lock (Charts)
            {
                foreach (MiniChart c in Charts)
                    foreach (SeriesDef sd in c.S)
                    {
                        sd.Vals.Add(ValueFor(sd.Name, s));
                        while (sd.Vals.Count > Capacity) sd.Vals.RemoveAt(0);
                    }
            }
            Invalidate();
        }

        double ValueFor(string series, Sample s)
        {
            // cases must match the SeriesDef.Name values used in BuildCharts
            switch (series)
            {
                case "CPU %": return s.Cpu;
                case "DPC %": return s.Dpc;
                case "Commit %": return s.Commit;
                case "RAM free GB": return s.AvailMB / 1024.0;
                case "busy %": return s.Busy;
                case "latency ms": return s.LatMs;
                case "GPU %": return s.Gpu3d;
                default: return 0;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            lock (Charts)
            {
                int n = Charts.Count;
                if (n == 0) return;
                float rowH = (Height - 8) / n;
                for (int i = 0; i < n; i++) DrawChart(g, Charts[i], 4, 4 + i * rowH, Width - 8, rowH - 4);
            }
        }

        void DrawChart(Graphics g, MiniChart c, float x, float y, float w, float h)
        {
            float headH = 16;
            float plotY = y + headH, plotH = h - headH;
            if (plotH < 12) return;

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(28, 28, 34)))
                g.FillRectangle(bg, x, plotY, w, plotH);

            double max = c.Max();
            using (Pen grid = new Pen(Color.FromArgb(48, 48, 56)))
            {
                for (int i = 1; i <= 3; i++)
                {
                    float gy = plotY + plotH * i / 4f;
                    g.DrawLine(grid, x, gy, x + w, gy);
                }
            }

            lock (c.S)
            {
                foreach (SeriesDef sd in c.S)
                {
                    if (sd.Vals.Count < 2) continue;
                    using (Pen p = new Pen(sd.Col, 1.6f))
                    {
                        int n = sd.Vals.Count;
                        PointF prev = PointF.Empty;
                        for (int i = 0; i < n; i++)
                        {
                            float px = x + w * i / (float)(Capacity - 1);
                            float py = plotY + plotH - (float)(Math.Min(sd.Vals[i], max) / max) * plotH;
                            PointF cur = new PointF(px, py);
                            if (i > 0) g.DrawLine(p, prev, cur);
                            prev = cur;
                        }
                    }
                }
            }

            // title + legend + current values
            using (Font f = new Font("Segoe UI", 8.25f))
            {
                float cx = x + 2;
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(200, 200, 208)))
                    g.DrawString(c.Title, f, tb, cx, y + 1);
                cx += 8 + g.MeasureString(c.Title, f).Width;
                foreach (SeriesDef sd in c.S)
                {
                    if (cx > x + w - 90) break;
                    using (SolidBrush cb = new SolidBrush(sd.Col))
                        g.FillRectangle(cb, cx, y + 6, 8, 8);
                    cx += 10;
                    string label = sd.Name;
                    if (sd.Vals.Count > 0)
                    {
                        double cur = sd.Vals[sd.Vals.Count - 1];
                        label += " " + (sd.Name.EndsWith("ms") ? App.F1(cur) : App.F0(cur));
                    }
                    using (SolidBrush lb = new SolidBrush(Color.FromArgb(160, 160, 168)))
                        g.DrawString(label, f, lb, cx, y + 1);
                    cx += 6 + g.MeasureString(label, f).Width;
                }
            }
        }
    }

    // Sorts the leaderboard ListView by the clicked column (numeric via Tag).
    public class LbSorter : System.Collections.IComparer
    {
        public int Col;
        public bool Desc = true;

        public int Compare(object a, object b)
        {
            ListViewItem x = (ListViewItem)a;
            ListViewItem y = (ListViewItem)b;
            LbRow rx = x.Tag as LbRow;
            LbRow ry = y.Tag as LbRow;
            int c;
            if (rx == null || ry == null)
                c = string.Compare(x.Text, y.Text, StringComparison.OrdinalIgnoreCase);
            else
                switch (Col)
                {
                    case 1: c = rx.CpuSec.CompareTo(ry.CpuSec); break;
                    case 2: c = rx.Share.CompareTo(ry.Share); break;
                    case 3: c = rx.GpuSec.CompareTo(ry.GpuSec); break;
                    case 4: c = rx.IoMB.CompareTo(ry.IoMB); break;
                    case 5: c = rx.PeakMB.CompareTo(ry.PeakMB); break;
                    default: c = string.Compare(x.Text, y.Text, StringComparison.OrdinalIgnoreCase); break;
                }
            if (Col > 0) return Desc ? -c : c;
            return Desc ? -c : c;
        }
    }

    // Draws day-over-day stacked bars of the top CPU consumers (see Analyzer.Trends).
    public class TrendsPanel : Panel
    {
        public List<DayTrend> Data = new List<DayTrend>();
        static readonly Color[] Pal = new Color[]
        {
            Color.FromArgb(79, 195, 247), Color.FromArgb(255, 183, 77), Color.FromArgb(129, 199, 132),
            Color.FromArgb(186, 104, 200), Color.FromArgb(239, 115, 115), Color.FromArgb(77, 208, 225)
        };
        readonly Dictionary<string, Color> _colors = new Dictionary<string, Color>();

        public TrendsPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(22, 22, 27);
        }

        public void SetData(List<DayTrend> data)
        {
            Data = data ?? new List<DayTrend>();
            _colors.Clear();
            int i = 0;
            foreach (DayTrend t in Data)
                foreach (NameVal nv in t.TopCpuProcs)
                    if (!_colors.ContainsKey(nv.N)) { _colors[nv.N] = Pal[i % Pal.Length]; i++; }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            using (Font f = new Font("Segoe UI", 8.25f))
            {
                if (Data.Count == 0)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(140, 140, 150)))
                        g.DrawString("Press Refresh to compute day-over-day trends (on-demand, heavier).", f, b, 12, 10);
                    return;
                }

                // consistent color per process name
                double maxH = 0.5;
                foreach (DayTrend t in Data)
                {
                    double s = 0;
                    foreach (NameVal nv in t.TopCpuProcs) s += nv.V;
                    if (s > maxH) maxH = s;
                }

                float left = 44, top = 24, bottom = 22;
                float pw = Width - left - 10, ph = Height - top - bottom;
                using (Pen grid = new Pen(Color.FromArgb(48, 48, 56)))
                {
                    for (int i = 0; i <= 4; i++)
                    {
                        float gy = top + ph * i / 4f;
                        g.DrawLine(grid, left, gy, left + pw, gy);
                        // values are CPU-seconds per day; label in human units
                        string lbl = App.HumanCpuSec(maxH * (1 - i / 4f));
                        using (SolidBrush tb = new SolidBrush(Color.FromArgb(130, 130, 140)))
                            g.DrawString(lbl, f, tb, 4, gy - 6);
                    }
                }

                int n = Data.Count;
                float slot = pw / n;
                float barW = Math.Min(64, slot * 0.62f);
                for (int i = 0; i < n; i++)
                {
                    DayTrend t = Data[i];
                    float x = left + slot * i + (slot - barW) / 2;
                    float y = top + ph;
                    foreach (NameVal nv in t.TopCpuProcs)
                    {
                        Color c;
                        if (!_colors.TryGetValue(nv.N, out c)) continue;
                        float h = (float)(nv.V / maxH) * ph;
                        y -= h;
                        using (SolidBrush b = new SolidBrush(c))
                            g.FillRectangle(b, x, y, barW, h);
                    }
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(160, 160, 170)))
                    {
                        string dl = t.Day.ToString("MM-dd");
                        g.DrawString(dl, f, tb, x + barW / 2 - g.MeasureString(dl, f).Width / 2, top + ph + 4);
                    }
                }

                // legend
                float lx = left + 2;
                int shown = 0;
                foreach (KeyValuePair<string, Color> kv in _colors)
                {
                    if (shown >= 6 || lx > Width - 60) break;
                    using (SolidBrush cb = new SolidBrush(kv.Value))
                        g.FillRectangle(cb, lx, 6, 9, 9);
                    string lbl = kv.Key.Length > 14 ? kv.Key.Substring(0, 13) + "\u2026" : kv.Key;
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(170, 170, 178)))
                        g.DrawString(lbl, f, tb, lx + 12, 4);
                    lx += 20 + g.MeasureString(lbl, f).Width;
                    shown++;
                }
            }
        }
    }

    public class MainForm : Form
    {
        Label _status;
        Button _btnStartStop, _btnLagged, _btnMark;
        ChartPanel _charts;
        ListView _lvProcs, _lvEvents;
        Label _progress;
        TextBox _tldr;
        ComboBox _range;
        ListBox _reports;
        Button _btnOpenReport;
        ComboBox _lbRange;
        ListView _lbList;
        LbSorter _lbSorter = new LbSorter();
        ComboBox _trDays;
        TrendsPanel _trPanel;
        ListView _trList;
        DateTime _lastEventSeen = DateTime.MinValue;
        DateTime _lastAlert = DateTime.MinValue;
        bool _autoTrends;

        // Cross-thread marshal that never throws when the form is gone or not yet up.
        void SafeInvoke(MethodInvoker mi)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(mi);
            }
            catch { }
        }
        NotifyIcon _tray;
        SysMonitor _mon;
        Sample _last;
        Thread _worker;
        volatile bool _closing;
        int _iter;
        string _lastReport;
        bool _balloonShown;

        public MainForm()
        {
            Text = "LagWatch";
            Size = new Size(1000, 700);
            MinimumSize = new Size(860, 580);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(24, 24, 28);

            BuildUi();
            BuildTray();

            _mon = new SysMonitor();
            _last = null;
            Shown += delegate
            {
                StartWorker();
                if (_autoTrends) RefreshTrends();
            };
            FormClosing += OnClosing;
        }

        static Button MakeBtn(string text, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Padding = new Padding(4, 2, 4, 2);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 92);
            b.BackColor = Color.FromArgb(38, 38, 46);
            b.ForeColor = Color.FromArgb(225, 225, 232);
            b.Cursor = Cursors.Hand;
            b.Click += onClick;
            return b;
        }

        void BuildUi()
        {
            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top;
            top.Height = 46;
            top.Padding = new Padding(10, 8, 8, 4);
            top.BackColor = Color.FromArgb(30, 30, 36);
            top.FlowDirection = FlowDirection.LeftToRight;

            _status = new Label();
            _status.Text = "...";
            _status.AutoSize = true;
            _status.ForeColor = Color.FromArgb(140, 220, 140);
            _status.Margin = new Padding(2, 10, 14, 0);

            _btnStartStop = MakeBtn("Stop recording", delegate { DoStartStop(); });
            _btnLagged = MakeBtn("It lagged \u2014 analyze now!", delegate { AnalyzeNow(10); });
            _btnMark = MakeBtn("Mark this moment", delegate { Controller.Mark(null); Flash("Marked."); });

            top.Controls.Add(_status);
            top.Controls.Add(_btnLagged);
            top.Controls.Add(_btnStartStop);
            top.Controls.Add(_btnMark);

            _progress = new Label();
            _progress.Dock = DockStyle.Bottom;
            _progress.Height = 26;
            _progress.TextAlign = ContentAlignment.MiddleLeft;
            _progress.Padding = new Padding(10, 0, 0, 0);
            _progress.ForeColor = Color.FromArgb(255, 213, 128);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            TabPage live = new TabPage("Live");
            TabPage lbTab = new TabPage("Leaderboard");
            TabPage trendsTab = new TabPage("Trends");
            TabPage reports = new TabPage("Reports & analysis");
            tabs.TabPages.Add(live);
            tabs.TabPages.Add(lbTab);
            tabs.TabPages.Add(trendsTab);
            tabs.TabPages.Add(reports);

            // ---- live tab ----
            TableLayoutPanel liveLayout = new TableLayoutPanel();
            liveLayout.Dock = DockStyle.Fill;
            liveLayout.ColumnCount = 2;
            liveLayout.RowCount = 1;
            liveLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            liveLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            liveLayout.BackColor = Color.FromArgb(24, 24, 28);

            _charts = new ChartPanel();
            _charts.Dock = DockStyle.Fill;
            _charts.Margin = new Padding(0);
            BuildCharts();

            Panel rightHolder = new Panel();
            rightHolder.Dock = DockStyle.Fill;
            rightHolder.BackColor = Color.FromArgb(24, 24, 28);
            rightHolder.Padding = new Padding(6, 4, 6, 4);

            TableLayoutPanel right = new TableLayoutPanel();
            right.Dock = DockStyle.Fill;
            right.ColumnCount = 1;
            right.RowCount = 4;
            right.BackColor = Color.FromArgb(24, 24, 28);
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 55));

            Label l1 = new Label();
            l1.Text = "Top processes right now";
            l1.AutoSize = false;
            l1.Dock = DockStyle.Fill;
            l1.TextAlign = ContentAlignment.MiddleLeft;
            l1.ForeColor = Color.FromArgb(160, 160, 170);

            _lvProcs = NewList(new string[] { "Process", "CPU %", "GPU %", "RAM" });
            _lvProcs.Dock = DockStyle.Fill;

            Label l2 = new Label();
            l2.Text = "Flags & marks (latest first)";
            l2.AutoSize = false;
            l2.Dock = DockStyle.Fill;
            l2.TextAlign = ContentAlignment.MiddleLeft;
            l2.ForeColor = Color.FromArgb(160, 160, 170);

            _lvEvents = NewList(new string[] { "Time", "Event", "Detail" });
            _lvEvents.Dock = DockStyle.Fill;

            right.Controls.Add(l1, 0, 0);
            right.Controls.Add(_lvProcs, 0, 1);
            right.Controls.Add(l2, 0, 2);
            right.Controls.Add(_lvEvents, 0, 3);
            rightHolder.Controls.Add(right);

            liveLayout.Controls.Add(_charts, 0, 0);
            liveLayout.Controls.Add(rightHolder, 1, 0);
            live.Controls.Add(liveLayout);

            // ---- reports tab ----
            FlowLayoutPanel rp = new FlowLayoutPanel();
            rp.Dock = DockStyle.Top;
            rp.Height = 40;
            rp.Padding = new Padding(10, 8, 8, 4);
            rp.BackColor = Color.FromArgb(24, 24, 28);

            _range = new ComboBox();
            _range.DropDownStyle = ComboBoxStyle.DropDownList;
            _range.Items.AddRange(new object[] { "Last 10 minutes", "Last 30 minutes", "Last 2 hours", "Today", "Everything recorded (auto)" });
            _range.SelectedIndex = 1;
            _range.Width = 210;
            _range.BackColor = Color.FromArgb(38, 38, 46);
            _range.ForeColor = Color.FromArgb(225, 225, 232);
            _range.FlatStyle = FlatStyle.Flat;

            Button btnAnalyze = MakeBtn("Analyze", delegate { AnalyzeNow(0); });
            Button btnData = MakeBtn("Open data folder", delegate { OpenFolder(App.LogDir); });
            Button btnReps = MakeBtn("Open reports folder", delegate { OpenFolder(App.ReportsDir); });

            rp.Controls.Add(_range);
            rp.Controls.Add(btnAnalyze);
            rp.Controls.Add(btnReps);
            rp.Controls.Add(btnData);

            _tldr = new TextBox();
            _tldr.Multiline = true;
            _tldr.ReadOnly = true;
            _tldr.ScrollBars = ScrollBars.Vertical;
            _tldr.BackColor = Color.FromArgb(28, 28, 34);
            _tldr.ForeColor = Color.FromArgb(220, 220, 226);
            _tldr.BorderStyle = BorderStyle.FixedSingle;
            _tldr.Font = new Font("Consolas", 9F);
            _tldr.Text = "Analysis results will appear here.\r\n\r\nTip: when the lag hits, hit \"It lagged \u2014 analyze now!\" on the Live tab.";

            _reports = new ListBox();
            _reports.BackColor = Color.FromArgb(28, 28, 34);
            _reports.ForeColor = Color.FromArgb(220, 220, 226);
            _reports.BorderStyle = BorderStyle.FixedSingle;
            _reports.DoubleClick += delegate { OpenSelectedReport(); };

            _btnOpenReport = MakeBtn("Open selected report", delegate { OpenSelectedReport(); });

            Panel repLayout = new Panel();
            repLayout.Dock = DockStyle.Fill;
            repLayout.BackColor = Color.FromArgb(24, 24, 28);
            repLayout.Controls.Add(_tldr);
            repLayout.Controls.Add(_reports);
            repLayout.Controls.Add(_btnOpenReport);
            repLayout.Resize += delegate
            {
                _tldr.SetBounds(8, 8, repLayout.Width - 16, repLayout.Height / 2 - 16);
                _reports.SetBounds(8, _tldr.Bottom + 8, repLayout.Width - 130, repLayout.Height - _tldr.Height - 24);
                _btnOpenReport.SetBounds(_reports.Right + 8, _reports.Top, 114, 30);
            };

            reports.Controls.Add(repLayout);
            reports.Controls.Add(rp);

            // ---- leaderboard tab ----
            FlowLayoutPanel lbt = new FlowLayoutPanel();
            lbt.Dock = DockStyle.Top;
            lbt.Height = 40;
            lbt.Padding = new Padding(10, 8, 8, 4);
            lbt.BackColor = Color.FromArgb(24, 24, 28);

            _lbRange = new ComboBox();
            _lbRange.DropDownStyle = ComboBoxStyle.DropDownList;
            _lbRange.Items.AddRange(new object[] { "Last 10 minutes", "Last 30 minutes", "Last 2 hours", "Today", "Everything recorded" });
            _lbRange.SelectedIndex = 3;
            _lbRange.Width = 210;
            _lbRange.BackColor = Color.FromArgb(38, 38, 46);
            _lbRange.ForeColor = Color.FromArgb(225, 225, 232);
            _lbRange.FlatStyle = FlatStyle.Flat;

            Button btnLb = MakeBtn("Refresh", delegate { RefreshLb(); });
            Label lbHint = new Label();
            lbHint.Text = "Exact totals while recording (click a column to sort)";
            lbHint.AutoSize = true;
            lbHint.ForeColor = Color.FromArgb(140, 140, 150);
            lbHint.Margin = new Padding(10, 10, 4, 0);

            lbt.Controls.Add(_lbRange);
            lbt.Controls.Add(btnLb);
            lbt.Controls.Add(lbHint);

            Panel lbWrap = new Panel();
            lbWrap.Dock = DockStyle.Fill;
            lbWrap.BackColor = Color.FromArgb(24, 24, 28);
            lbWrap.Padding = new Padding(8, 8, 8, 8);

            _lbList = NewList(new string[] { "Process", "CPU time", "CPU share", "GPU time", "Disk IO", "Peak RAM" });
            _lbList.Dock = DockStyle.Fill;
            _lbList.ColumnClick += delegate(object s, ColumnClickEventArgs e)
            {
                if (_lbSorter.Col == e.Column) _lbSorter.Desc = !_lbSorter.Desc;
                else { _lbSorter.Col = e.Column; _lbSorter.Desc = e.Column != 0; }
                _lbSorter.Col = e.Column;
                _lbList.ListViewItemSorter = _lbSorter;
            };

            lbWrap.Controls.Add(_lbList);
            lbTab.Controls.Add(lbWrap);
            lbTab.Controls.Add(lbt);

            // ---- trends tab ----
            FlowLayoutPanel trt = new FlowLayoutPanel();
            trt.Dock = DockStyle.Top;
            trt.Height = 40;
            trt.Padding = new Padding(10, 8, 8, 4);
            trt.BackColor = Color.FromArgb(24, 24, 28);

            _trDays = new ComboBox();
            _trDays.DropDownStyle = ComboBoxStyle.DropDownList;
            _trDays.Items.AddRange(new object[] { "Last 7 days", "Last 14 days", "Last 30 days" });
            _trDays.SelectedIndex = 0;
            _trDays.Width = 140;
            _trDays.BackColor = Color.FromArgb(38, 38, 46);
            _trDays.ForeColor = Color.FromArgb(225, 225, 232);
            _trDays.FlatStyle = FlatStyle.Flat;

            Button btnTr = MakeBtn("Refresh trends", delegate { RefreshTrends(); });
            Label trHint = new Label();
            trHint.Text = "Stacked bars = top CPU eaters per day (computed on demand)";
            trHint.AutoSize = true;
            trHint.ForeColor = Color.FromArgb(140, 140, 150);
            trHint.Margin = new Padding(10, 10, 4, 0);

            trt.Controls.Add(_trDays);
            trt.Controls.Add(btnTr);
            trt.Controls.Add(trHint);

            TableLayoutPanel trLayout = new TableLayoutPanel();
            trLayout.Dock = DockStyle.Fill;
            trLayout.ColumnCount = 1;
            trLayout.RowCount = 2;
            trLayout.BackColor = Color.FromArgb(24, 24, 28);
            trLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            trLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));

            _trPanel = new TrendsPanel();
            _trPanel.Dock = DockStyle.Fill;

            _trList = NewList(new string[] { "Day", "CPU avg/peak", "Commit avg", "Storms", "Top CPU eater", "Peak RAM holder" });
            _trList.Dock = DockStyle.Fill;

            trLayout.Controls.Add(_trPanel, 0, 0);
            trLayout.Controls.Add(_trList, 0, 1);
            trendsTab.Controls.Add(trLayout);
            trendsTab.Controls.Add(trt);

            Controls.Add(tabs);
            Controls.Add(_progress);
            Controls.Add(top);

            string startTab = (Environment.GetEnvironmentVariable("LAGWATCH_TAB") ?? "");
            if (startTab == "lb") tabs.SelectedTab = lbTab;
            if (startTab == "trends")
            {
                tabs.SelectedTab = trendsTab;
                _autoTrends = true; // compute once the form is shown (handle must exist)
            }
        }

        static ListView NewList(string[] cols)
        {
            ListView lv = new ListView();
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HideSelection = true;
            lv.BorderStyle = BorderStyle.FixedSingle;
            lv.BackColor = Color.FromArgb(28, 28, 34);
            lv.ForeColor = Color.FromArgb(220, 220, 226);
            foreach (string c in cols)
                lv.Columns.Add(c, c.Length * 8 + 28);
            return lv;
        }

        void BuildCharts()
        {
            MiniChart cpu = new MiniChart(); cpu.Title = "CPU / driver DPC time"; cpu.FixedMax = 100;
            cpu.S.Add(new SeriesDef { Name = "CPU %", Col = Color.FromArgb(79, 195, 247) });
            cpu.S.Add(new SeriesDef { Name = "DPC %", Col = Color.FromArgb(255, 213, 79) });
            _charts.Charts.Add(cpu);

            MiniChart commit = new MiniChart(); commit.Title = "Memory commit pressure"; commit.FixedMax = 100;
            commit.S.Add(new SeriesDef { Name = "Commit %", Col = Color.FromArgb(255, 183, 77) });
            _charts.Charts.Add(commit);

            MiniChart ram = new MiniChart(); ram.Title = "RAM free"; ram.FixedMax = 0; ram.AutoFloor = 16;
            ram.S.Add(new SeriesDef { Name = "RAM free GB", Col = Color.FromArgb(129, 199, 132) });
            _charts.Charts.Add(ram);

            MiniChart disk = new MiniChart(); disk.Title = "Disk"; disk.FixedMax = 0; disk.AutoFloor = 100;
            disk.S.Add(new SeriesDef { Name = "busy %", Col = Color.FromArgb(186, 104, 200) });
            disk.S.Add(new SeriesDef { Name = "latency ms", Col = Color.FromArgb(239, 115, 115) });
            _charts.Charts.Add(disk);

            MiniChart gpu = new MiniChart(); gpu.Title = "GPU 3D"; gpu.FixedMax = 100;
            gpu.S.Add(new SeriesDef { Name = "GPU %", Col = Color.FromArgb(77, 208, 225) });
            _charts.Charts.Add(gpu);
        }

        void BuildTray()
        {
            _tray = new NotifyIcon();
            try { _tray.Icon = System.Drawing.SystemIcons.Shield; }
            catch { _tray.Icon = System.Drawing.SystemIcons.Application; }
            _tray.Text = "LagWatch";
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open LagWatch", null, delegate { ShowSelf(); });
            menu.Items.Add("It lagged \u2014 analyze last 10 min", null, delegate { ShowSelf(); AnalyzeNow(10); });
            menu.Items.Add("Mark this moment", null, delegate { Controller.Mark(null); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { _closing = true; _tray.Visible = false; Application.Exit(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowSelf(); };
        }

        void ShowSelf()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (_closing || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
            if (!_balloonShown)
            {
                _balloonShown = true;
                _tray.BalloonTipTitle = "LagWatch is still watching";
                _tray.BalloonTipText = "Recording continues in the background. Double-click the tray icon to reopen.";
                _tray.ShowBalloonTip(3000);
            }
        }

        // ---- background sampler ----
        void StartWorker()
        {
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Start();
        }

        void WorkerLoop()
        {
            while (!_closing)
            {
                Sample s = null;
                try
                {
                    bool wantGpu = (_iter % 5 == 2);
                    s = _mon.Take(wantGpu, false);
                }
                catch { }

                if (s != null && !_closing)
                {
                    Sample captured = s;
                    SafeInvoke(delegate { ApplySample(captured); });
                }

                for (int i = 0; i < 15 && !_closing; i++) Thread.Sleep(100); // ~1.5s
                _iter++;
            }
        }

        void ApplySample(Sample s)
        {
            _last = s;
            double ramMaxGB = s.TotalMB > 0 ? s.TotalMB / 1024.0 : 16;
            _charts.Push(s, ramMaxGB);
            FillProcs(s);

            // refresh status + events + reports less often
            if (_iter % 4 == 0) UpdateStatus();
            if (_iter % 8 == 1) RefreshEvents();
            if (_iter % 20 == 3) RefreshReports();
            if (_iter % 40 == 10) RefreshLb();
        }

        void FillProcs(Sample s)
        {
            if (_lvProcs.Items.Count > 0) _lvProcs.Items.Clear();
            for (int i = 0; i < s.TopCpu.Count; i++)
            {
                NameVal c = s.TopCpu[i];
                string mem = "-", gpu = "-";
                foreach (NameVal m in s.TopMem)
                    if (m.N == c.N) { mem = App.HumanMB(m.V); break; }
                foreach (NameVal g in s.TopGpu)
                    if (g.N == c.N) { gpu = App.F0(g.V) + "%"; break; }
                ListViewItem it = new ListViewItem(c.N);
                it.SubItems.Add(App.F1(c.V));
                it.SubItems.Add(gpu);
                it.SubItems.Add(mem);
                _lvProcs.Items.Add(it);
            }
            if (_lvProcs.Items.Count == 0)
                _lvProcs.Items.Add(new ListViewItem(new string[] { "(idle)", "", "", "" }));
            if (_lvProcs.Columns[0].Width < 110) _lvProcs.Columns[0].Width = 110;
            _lvProcs.Columns[1].Width = 52;
            _lvProcs.Columns[2].Width = 52;
            _lvProcs.Columns[3].Width = 70;
        }

        void RefreshTrends()
        {
            int days = _trDays.SelectedIndex == 1 ? 14 : _trDays.SelectedIndex == 2 ? 30 : 7;
            Flash("Computing " + days + "-day trends (on-demand, a few seconds)...");
            Thread t = new Thread((ThreadStart)delegate
            {
                List<DayTrend> data = null;
                string err = null;
                try { data = Analyzer.Trends(days); }
                catch (Exception ex) { err = ex.Message; }
                SafeInvoke(delegate
                {
                    if (err != null) { Flash("Trends failed: " + err); return; }
                    _trPanel.SetData(data);
                    FillTrendList(data);
                    Flash("Trends updated (last " + days + " days).");
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void FillTrendList(List<DayTrend> data)
        {
            _trList.BeginUpdate();
            _trList.Items.Clear();
            foreach (DayTrend t in data)
            {
                string top = "-", peak = "-";
                if (t.TopCpuProcs.Count > 0)
                    top = t.TopCpuProcs[0].N + " (" + App.HumanCpuSec(t.TopCpuProcs[0].V) + ")";
                if (!string.IsNullOrEmpty(t.PeakRam.N))
                    peak = t.PeakRam.N + " (" + App.HumanMB(t.PeakRam.V) + ")";
                ListViewItem it = new ListViewItem(t.Day.ToString("yyyy-MM-dd"));
                it.SubItems.Add(App.F0(t.AvgCpu) + "% / " + App.F0(t.PeakCpu) + "%");
                it.SubItems.Add(App.F0(t.AvgCommit) + "%");
                it.SubItems.Add(t.Storms.ToString());
                it.SubItems.Add(t.Samples == 0 ? "(no data)" : top);
                it.SubItems.Add(peak);
                if (t.Storms >= 5) it.ForeColor = Color.FromArgb(255, 150, 150);
                _trList.Items.Add(it);
            }
            _trList.EndUpdate();
            _trList.Columns[0].Width = 82;
            _trList.Columns[1].Width = 100;
            _trList.Columns[2].Width = 82;
            _trList.Columns[3].Width = 60;
            _trList.Columns[4].Width = 190;
            _trList.Columns[5].Width = -2;
        }

        static bool IsStormType(string type)
        {
            switch (type)
            {
                case "paging-storm":
                case "cpu-spike":
                case "disk-latency":
                case "disk-saturation":
                case "dpc-storm":
                case "thermal":
                case "memory-low":
                case "commit-pressure":
                    return true;
                default:
                    return false;
            }
        }

        void RefreshEvents()
        {
            List<EventRec> evs;
            try { evs = Analyzer.LoadEvents(DateTime.Today.AddDays(-1), DateTime.Now); } // span midnight + show yesterday
            catch { return; }
            evs.Sort(delegate(EventRec a, EventRec b) { return b.T.CompareTo(a.T); });

            SafeInvoke(delegate
            {
                _lvEvents.BeginUpdate();
                _lvEvents.Items.Clear();
                for (int i = 0; i < evs.Count && i < 40; i++)
                {
                    EventRec e = evs[i];
                    ListViewItem it = new ListViewItem(e.T.ToString("HH:mm:ss"));
                    it.SubItems.Add(e.Type);
                    it.SubItems.Add(e.Info == null ? "" : e.Info);
                    if (e.Type == "mark") it.ForeColor = Color.FromArgb(130, 200, 255);
                    else if (e.Type == "cpu-spike" || e.Type == "thermal") it.ForeColor = Color.FromArgb(255, 150, 150);
                    _lvEvents.Items.Add(it);
                }
                _lvEvents.EndUpdate();
                _lvEvents.Columns[0].Width = 58;
                _lvEvents.Columns[1].Width = 100;
                _lvEvents.Columns[2].Width = 150;

                // tray balloon on new storm-class events, throttled to one per 5 minutes
                if (evs.Count > 0)
                {
                    DateTime newest = evs[0].T;
                    if (_lastEventSeen != DateTime.MinValue && newest > _lastEventSeen &&
                        (DateTime.Now - _lastAlert).TotalMinutes >= 5)
                    {
                        foreach (EventRec e in evs)
                        {
                            if (e.T <= _lastEventSeen) break;
                            if (IsStormType(e.Type))
                            {
                                _lastAlert = DateTime.Now;
                                _tray.ShowBalloonTip(8000, "LagWatch: " + e.Type,
                                    string.IsNullOrEmpty(e.Info) ? "storm in progress" : e.Info, ToolTipIcon.Warning);
                                break;
                            }
                        }
                    }
                    if (newest > _lastEventSeen) _lastEventSeen = newest;
                }
            });
        }

        void UpdateStatus()
        {
            string st = Controller.StatusText();
            if (IsDisposed) return;
            _status.Text = st;
            bool running = st.StartsWith("Recording");
            _status.ForeColor = running ? Color.FromArgb(140, 220, 140) : Color.FromArgb(255, 128, 128);
            _btnStartStop.Text = running ? "Stop recording" : "Start recording";
            _tray.Text = st.Length > 63 ? st.Substring(0, 63) : st;
        }

        void RefreshReports()
        {
            List<string> files = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(App.ReportsDir, "report-*.md")) files.Add(f);
            }
            catch { }
            files.Sort(delegate(string a, string b) { return string.Compare(b, a, StringComparison.Ordinal); });
            SafeInvoke(delegate
            {
                _reports.BeginUpdate();
                _reports.Items.Clear();
                for (int i = 0; i < files.Count && i < 40; i++)
                {
                    string name = Path.GetFileNameWithoutExtension(files[i]).Replace("report-", "");
                    DateTime t;
                    string label = name;
                    if (DateTime.TryParseExact(name, "yyyyMMdd-HHmmss", null,
                        System.Globalization.DateTimeStyles.None, out t))
                        label = t.ToString("MM-dd  HH:mm:ss");
                    _reports.Items.Add(label);
                }
                _reports.EndUpdate();
            });
        }

        // ---- actions ----
        void DoStartStop()
        {
            string st = _btnStartStop.Text;
            Flash(st == "Stop recording" ? "Stopping recorder..." : "Starting recorder...");
            Thread t = new Thread((ThreadStart)delegate
            {
                string msg = st == "Stop recording" ? Controller.Stop(false) : Controller.Start(0);
                SafeInvoke(delegate { Flash(msg); UpdateStatus(); });
            });
            t.IsBackground = true;
            t.Start();
        }

        void AnalyzeNow(int minutes)
        {
            DateTime to = DateTime.Now.AddMinutes(1);
            DateTime from;
            bool auto = minutes <= 0;
            if (auto)
            {
                if (_range.SelectedIndex == 4)
                {
                    DateTime ds = Analyzer.DataStartTime();
                    if (ds == DateTime.MinValue) { Flash("No telemetry recorded yet."); return; }
                    from = ds.Date;
                    if ((to - from).TotalDays > 7) from = to.AddDays(-7);
                }
                else from = ParseRangeIndex(_range.SelectedIndex, to);
            }
            else from = to.AddMinutes(-minutes);

            tabsGotoReports();
            Flash("Analyzing " + from.ToString("HH:mm") + " to now \u2014 heavy part, hang on...");
            _btnLagged.Enabled = false;

            DateTime fFrom = from, fTo = to;
            Thread t = new Thread((ThreadStart)delegate
            {
                try
                {
                    Analyzer.Progress = delegate(string m) { SafeFlash(m); };
                    AnalysisData data = Analyzer.Analyze(fFrom, fTo, false);
                    if (data.SampleCount == 0)
                    {
                        SafeInvoke(delegate { Flash("No telemetry in that window \u2014 is the recorder running?"); });
                        return;
                    }
                    string path = Path.Combine(App.ReportsDir, "report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
                    File.WriteAllText(path, Report.Render(data), new UTF8Encoding(true));
                    _lastReport = path;
                    SafeInvoke(delegate { ShowResult(data, path); });
                }
                catch (Exception ex)
                {
                    string msg = "Analysis failed: " + ex.Message;
                    SafeInvoke(delegate { Flash(msg); });
                }
                finally
                {
                    Analyzer.Progress = null;
                    SafeInvoke(delegate { _btnLagged.Enabled = true; });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        void tabsGotoReports()
        {
            foreach (Control c in Controls)
            {
                TabControl tc = c as TabControl;
                if (tc == null) continue;
                foreach (TabPage p in tc.TabPages)
                    if (p.Text.StartsWith("Reports")) { tc.SelectedTab = p; return; }
            }
        }

        void RefreshLb()
        {
            DateTime to = DateTime.Now.AddMinutes(1);
            DateTime from;
            if (_lbRange.SelectedIndex == 4)
            {
                DateTime ds = Analyzer.DataStartTime();
                if (ds == DateTime.MinValue) { Flash("No data recorded yet."); return; }
                from = ds.Date;
                if ((to - from).TotalDays > 7) from = to.AddDays(-7);
            }
            else
            {
                switch (_lbRange.SelectedIndex)
                {
                    case 0: from = to.AddMinutes(-10); break;
                    case 1: from = to.AddMinutes(-30); break;
                    case 2: from = to.AddHours(-2); break;
                    default: from = to.Date; break;
                }
            }

            DateTime df, dt;
            List<LbRow> rows = Analyzer.Leaderboard(from, to, out df, out dt);
            _lbList.BeginUpdate();
            _lbList.Items.Clear();
            if (df != DateTime.MinValue && rows.Count > 0)
            {
                double winSec = Math.Max(1, (dt - df).TotalSeconds);
                int cores = Environment.ProcessorCount;
                foreach (LbRow r in rows)
                {
                    r.Share = r.CpuSec / winSec / cores * 100.0;
                    ListViewItem it = new ListViewItem(r.Name);
                    it.SubItems.Add(App.HumanCpuSec(r.CpuSec));
                    it.SubItems.Add(App.F1(r.Share) + "%");
                    it.SubItems.Add(r.GpuSec > 0.05 ? App.HumanCpuSec(r.GpuSec) : "\u2014");
                    it.SubItems.Add(App.HumanMB(r.IoMB));
                    it.SubItems.Add(App.HumanMB(r.PeakMB));
                    it.Tag = r;
                    _lbList.Items.Add(it);
                }
                Flash("Leaderboard: " + rows.Count + " processes over " + App.F1(winSec / 3600.0) +
                    " h (" + df.ToString("HH:mm") + " to " + dt.ToString("HH:mm") + ")");
            }
            else Flash("No leaderboard data yet \u2014 recorder flushes totals every minute.");
            _lbList.EndUpdate();
            _lbList.Columns[0].Width = 110;
            _lbList.Columns[1].Width = 74;
            _lbList.Columns[2].Width = 74;
            _lbList.Columns[3].Width = 74;
            _lbList.Columns[4].Width = 74;
            _lbList.Columns[5].Width = -2;
        }

        DateTime ParseRangeIndex(int idx, DateTime now)
        {
            switch (idx)
            {
                case 0: return now.AddMinutes(-10);
                case 2: return now.AddHours(-2);
                case 3: return now.Date;
                default: return now.AddMinutes(-30);
            }
        }

        void ShowResult(AnalysisData data, string path)
        {
            Flash("Report written: " + path);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Report: " + path);
            sb.AppendLine();
            sb.AppendLine("TL;DR \u2014 most likely causes:");
            if (data.Findings.Count == 0)
                sb.AppendLine("  Nothing clearly abnormal recorded in this window.");
            int shown = 0;
            foreach (Finding f in data.Findings)
            {
                if (shown >= 5) break;
                sb.AppendLine("  [" + f.SevName + "] " + f.Title);
                if (f.Evidence.Count > 0) sb.AppendLine("        " + f.Evidence[0]);
                shown++;
            }
            sb.AppendLine();
            sb.AppendLine("Worst windows:");
            if (data.Episodes != null)
                for (int i = 0; i < data.Episodes.Count && i < 5; i++)
                {
                    Episode ep = data.Episodes[i];
                    sb.AppendLine("  " + ep.Start.ToString("HH:mm:ss") + " for " + ep.DurationSec + "s \u2014 " +
                        string.Join(", ", new List<string>(ep.Reasons).ToArray()) +
                        (string.IsNullOrEmpty(ep.Fg) ? "" : " (fg: " + ep.Fg + ")"));
                }
            _tldr.Text = sb.ToString();
            RefreshReports();
            try { Process.Start("notepad.exe", path); } catch { }
        }

        void OpenSelectedReport()
        {
            if (_reports.SelectedIndex < 0) return;
            string label = _reports.SelectedItem.ToString();
            string name = "report-" + label.Replace("-", "").Replace("  ", "-");
            // label is "MM-dd  HH:mm:ss" -> reconstruct file
            string[] parts = label.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                string[] d = parts[0].Split('-');
                string[] t = parts[1].Split(':');
                if (d.Length == 2 && t.Length == 3)
                    name = "report-" + DateTime.Now.Year + d[0] + d[1] + "-" + t[0] + t[1] + t[2];
            }
            string path = Path.Combine(App.ReportsDir, name + ".md");
            if (!File.Exists(path)) { Flash("Report file not found: " + name); return; }
            try { Process.Start("notepad.exe", path); } catch { }
        }

        static void OpenFolder(string path)
        {
            try { Process.Start("explorer.exe", path); } catch { }
        }

        void Flash(string msg)
        {
            if (IsDisposed || !IsHandleCreated) return;
            _progress.Text = msg;
        }

        void SafeFlash(string msg)
        {
            try { SafeInvoke(delegate { Flash(msg); }); } catch { }
        }
    }
}

