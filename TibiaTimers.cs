// Tibia Timers - sound alerts for potions / rings / amulets.
// Passive helper: it only listens to mouse clicks / key presses (global hooks) and plays sounds.
// It never reads or writes the Tibia client and never sends input.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TibiaTimers
{
    static class Native
    {
        public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int mciSendString(string cmd, StringBuilder ret, int retLen, IntPtr cb);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int idx, int v);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    }

    enum Retrigger { Restart = 0, Ignore = 1, PauseResume = 2 }

    class TimerData
    {
        static int nextId;
        public readonly int Id = ++nextId;

        public string Name = "Timer";
        public bool Enabled = true;
        public Rectangle Region = Rectangle.Empty;   // screen pixels
        public Keys Hotkey = Keys.None;
        public int DurationSec = 600;
        public int WarnSec = 30;
        public string SoundPath = "";
        public int Volume = 80;
        public Retrigger Mode = Retrigger.Restart;
        public bool Silent;   // plain countdown: no sound, no warning highlight

        // runtime state
        public bool Running, Paused, Alerted, Expired;
        public DateTime EndUtc;
        public int PausedRemainingMs;
        public DateTime LastTriggerUtc = DateTime.MinValue;
        public DateTime ExpiredUtc = DateTime.MinValue;

        public int RemainingMs()
        {
            if (!Running) return 0;
            if (Paused) return PausedRemainingMs;
            double ms = (EndUtc - DateTime.UtcNow).TotalMilliseconds;
            return ms <= 0 ? 0 : (int)ms;
        }

        public void Start()
        {
            Running = true; Paused = false; Alerted = false; Expired = false;
            EndUtc = DateTime.UtcNow.AddSeconds(DurationSec);
        }

        public void Reset() { Running = false; Paused = false; Alerted = false; Expired = false; }

        public void TogglePause()
        {
            if (!Running) return;
            if (Paused) { EndUtc = DateTime.UtcNow.AddMilliseconds(PausedRemainingMs); Paused = false; }
            else { PausedRemainingMs = RemainingMs(); Paused = true; }
        }

        // Called on click / hotkey. Returns false when ignored.
        public bool Trigger()
        {
            DateTime now = DateTime.UtcNow;
            // debounce double-clicks and click+key combos
            if ((now - LastTriggerUtc).TotalMilliseconds < 700) return false;
            LastTriggerUtc = now;
            if (!Running) { Start(); return true; }
            switch (Mode)
            {
                case Retrigger.Restart: Start(); return true;
                case Retrigger.PauseResume: TogglePause(); return true;
                default: return false;
            }
        }

        // Advances state; returns true when the alert sound should play now.
        public bool Update(out bool stateChanged)
        {
            stateChanged = false;
            if (!Running || Paused) return false;
            int rem = RemainingMs();
            bool play = false;
            int warn = Math.Min(WarnSec, DurationSec - 1);
            if (!Alerted && rem <= warn * 1000) { Alerted = true; play = true; stateChanged = true; }
            if (rem <= 0) { Running = false; Expired = true; ExpiredUtc = DateTime.UtcNow; stateChanged = true; }
            return play;
        }

        public void Write(StringBuilder sb)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            sb.AppendLine("[Timer]");
            sb.AppendLine("Name=" + Name);
            sb.AppendLine("Enabled=" + (Enabled ? 1 : 0));
            sb.AppendLine("Region=" + Region.X + "," + Region.Y + "," + Region.Width + "," + Region.Height);
            sb.AppendLine("Hotkey=" + (int)Hotkey);
            sb.AppendLine("Duration=" + DurationSec);
            sb.AppendLine("Warn=" + WarnSec);
            sb.AppendLine("Sound=" + SoundPath);
            sb.AppendLine("Volume=" + Volume);
            sb.AppendLine("Mode=" + (int)Mode);
            sb.AppendLine("Silent=" + (Silent ? 1 : 0));
            sb.AppendLine("Running=" + (Running ? 1 : 0));
            sb.AppendLine("Paused=" + (Paused ? 1 : 0));
            sb.AppendLine("Alerted=" + (Alerted ? 1 : 0));
            sb.AppendLine("Expired=" + (Expired ? 1 : 0));
            sb.AppendLine("EndUtc=" + EndUtc.Ticks.ToString(ci));
            sb.AppendLine("PausedMs=" + PausedRemainingMs);
        }

        public void Read(string k, string v)
        {
            switch (k)
            {
                case "Name": Name = v; break;
                case "Enabled": Enabled = v == "1"; break;
                case "Region":
                    string[] p = v.Split(',');
                    Region = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                    break;
                case "Hotkey": Hotkey = (Keys)int.Parse(v); break;
                case "Duration": DurationSec = Math.Max(1, int.Parse(v)); break;
                case "Warn": WarnSec = Math.Max(0, int.Parse(v)); break;
                case "Sound": SoundPath = v; break;
                case "Volume": Volume = Math.Max(0, Math.Min(100, int.Parse(v))); break;
                case "Mode": Mode = (Retrigger)int.Parse(v); break;
                case "Silent": Silent = v == "1"; break;
                case "Running": Running = v == "1"; break;
                case "Paused": Paused = v == "1"; break;
                case "Alerted": Alerted = v == "1"; break;
                case "Expired": Expired = v == "1"; break;
                case "EndUtc": EndUtc = new DateTime(long.Parse(v, CultureInfo.InvariantCulture), DateTimeKind.Utc); break;
                case "PausedMs": PausedRemainingMs = int.Parse(v); break;
            }
        }

        // After loading from disk: a countdown that ran out while the app was closed is just "expired", no sound.
        public void FixAfterLoad()
        {
            if (Running && !Paused && EndUtc <= DateTime.UtcNow) { Running = false; Expired = true; Alerted = true; }
        }
    }

    static class Sound
    {
        static SoundPlayer beepPlayer;

        public static void Play(TimerData t)
        {
            string alias = "tt" + t.Id;
            Native.mciSendString("close " + alias, null, 0, IntPtr.Zero);
            if (!string.IsNullOrEmpty(t.SoundPath) && File.Exists(t.SoundPath))
            {
                int r = Native.mciSendString("open \"" + t.SoundPath + "\" type mpegvideo alias " + alias, null, 0, IntPtr.Zero);
                if (r == 0)
                {
                    Native.mciSendString("setaudio " + alias + " volume to " + (t.Volume * 10), null, 0, IntPtr.Zero);
                    if (Native.mciSendString("play " + alias + " from 0", null, 0, IntPtr.Zero) == 0) return;
                }
            }
            PlayBeep(t.Volume);   // no file / missing file / unsupported format
        }

        public static void CloseAll(IEnumerable<TimerData> timers)
        {
            foreach (TimerData t in timers) Native.mciSendString("close tt" + t.Id, null, 0, IntPtr.Zero);
        }

        // Built-in three-tone chime so the app works before any file is chosen.
        static void PlayBeep(int volume)
        {
            const int rate = 22050;
            double[] tones = { 880, 0, 880, 0, 1320 };
            double[] lens = { 0.15, 0.07, 0.15, 0.07, 0.3 };
            List<short> samples = new List<short>();
            double amp = 32767 * 0.8 * volume / 100.0;
            for (int i = 0; i < tones.Length; i++)
            {
                int n = (int)(rate * lens[i]);
                for (int s = 0; s < n; s++)
                {
                    double env = Math.Min(1.0, Math.Min(s, n - s) / 300.0);   // avoid clicks
                    double v = tones[i] == 0 ? 0 : Math.Sin(2 * Math.PI * tones[i] * s / rate) * amp * env;
                    samples.Add((short)v);
                }
            }
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            int dataLen = samples.Count * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataLen); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataLen);
            foreach (short s in samples) w.Write(s);
            ms.Position = 0;
            beepPlayer = new SoundPlayer(ms);
            beepPlayer.Play();
        }
    }

    static class Util
    {
        public static float Scale = 1f;
        public static int S(int v) { return (int)Math.Round(v * Scale); }

        public static string Fmt(int ms)
        {
            int total = (ms + 999) / 1000;
            int h = total / 3600, m = total % 3600 / 60, s = total % 60;
            return h > 0 ? string.Format("{0}:{1:00}:{2:00}", h, m, s) : string.Format("{0}:{1:00}", m, s);
        }

        // Accepts "10" (minutes), "9:30", "1:00:00".
        public static bool TryParseDuration(string text, out int sec)
        {
            sec = 0;
            string[] p = text.Trim().Split(':');
            int h = 0, m = 0, s = 0;
            try
            {
                if (p.Length == 1) m = int.Parse(p[0]);
                else if (p.Length == 2) { m = int.Parse(p[0]); s = int.Parse(p[1]); }
                else if (p.Length == 3) { h = int.Parse(p[0]); m = int.Parse(p[1]); s = int.Parse(p[2]); }
                else return false;
            }
            catch { return false; }
            if (h < 0 || m < 0 || s < 0 || (p.Length > 1 && s > 59) || (p.Length > 2 && m > 59)) return false;
            sec = h * 3600 + m * 60 + s;
            return sec > 0 && sec <= 24 * 3600;
        }

        public static string KeyName(Keys k)
        {
            if (k == Keys.None) return "(no key)";
            StringBuilder sb = new StringBuilder();
            if ((k & Keys.Control) != 0) sb.Append("Ctrl+");
            if ((k & Keys.Shift) != 0) sb.Append("Shift+");
            if ((k & Keys.Alt) != 0) sb.Append("Alt+");
            Keys code = k & Keys.KeyCode;
            if (code >= Keys.D0 && code <= Keys.D9) sb.Append((int)(code - Keys.D0));
            else sb.Append(code.ToString());
            return sb.ToString();
        }

        public static string RegionText(Rectangle r)
        {
            return r.IsEmpty ? "(no region)" : string.Format("{0},{1}  {2}x{3}", r.X, r.Y, r.Width, r.Height);
        }
    }

    // Full-screen dim layer: drag to pick a region, or (preview mode) show all regions.
    class Overlay : Form
    {
        public Rectangle Result = Rectangle.Empty;
        readonly bool preview;
        readonly string title;
        readonly List<KeyValuePair<string, Rectangle>> shown;
        readonly Rectangle vs = SystemInformation.VirtualScreen;
        bool dragging;
        Point start;
        Rectangle cur;

        public Overlay(bool preview, string title, List<KeyValuePair<string, Rectangle>> shown)
        {
            this.preview = preview; this.title = title; this.shown = shown;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = vs;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            Opacity = 0.5;
            DoubleBuffered = true;
            KeyPreview = true;
            Cursor = preview ? Cursors.Default : Cursors.Cross;
            Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            if (preview)
            {
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 5000;
                t.Tick += delegate { t.Stop(); Close(); };
                t.Start();
            }
        }

        Rectangle ToClientRect(Rectangle screen) { Rectangle r = screen; r.Offset(-vs.X, -vs.Y); return r; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (preview || e.Button != MouseButtons.Left) { Close(); return; }
            dragging = true; start = e.Location; cur = new Rectangle(start, Size.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            cur = Rectangle.FromLTRB(Math.Min(start.X, e.X), Math.Min(start.Y, e.Y), Math.Max(start.X, e.X), Math.Max(start.Y, e.Y));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            Rectangle r = cur;
            if (r.Width < 5 || r.Height < 5)
            {
                int sz = Util.S(36);   // single click = one action bar slot
                r = new Rectangle(e.X - sz / 2, e.Y - sz / 2, sz, sz);
            }
            r.Offset(vs.X, vs.Y);
            Result = r;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle prim = ToClientRect(Screen.PrimaryScreen.Bounds);
            string text = preview
                ? "Configured regions (click or Esc to close)"
                : "Drag a box over the hotkey slot for: " + title + "     (single click = one slot, Esc = cancel)";
            SizeF ts = g.MeasureString(text, Font);
            float tx = prim.X + (prim.Width - ts.Width) / 2, ty = prim.Y + Util.S(40);
            g.FillRectangle(Brushes.Black, tx - 10, ty - 6, ts.Width + 20, ts.Height + 12);
            g.DrawString(text, Font, Brushes.White, tx, ty);

            using (Pen pen = new Pen(Color.Yellow, 2))
            using (Pen red = new Pen(Color.Red, 3))
            {
                foreach (KeyValuePair<string, Rectangle> kv in shown)
                {
                    Rectangle r = ToClientRect(kv.Value);
                    g.FillRectangle(Brushes.DarkBlue, r);
                    g.DrawRectangle(pen, r);
                    g.DrawString(kv.Key, Font, Brushes.Yellow, r.X, r.Bottom + 2);
                }
                if (dragging && cur.Width > 0)
                {
                    g.FillRectangle(Brushes.White, cur);
                    g.DrawRectangle(red, cur);
                }
            }
        }
    }

    // Small always-on-top countdown list. Drag to move; when locked it is click-through.
    class CountdownOverlay : Form
    {
        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const int ExpiredVisibleSec = 30;
        readonly Font nameFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        readonly Font smallFont = new Font("Segoe UI", 8.5f);
        List<TimerData> items = new List<TimerData>();
        bool locked;
        int lastTopMostFix;

        public event EventHandler Moved;

        public CountdownOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(24, 24, 28);
            Opacity = 0.85;
            DoubleBuffered = true;
            Width = Util.S(210);
            Height = Util.S(30);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                if (locked) cp.ExStyle |= WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public bool Locked
        {
            get { return locked; }
            set
            {
                locked = value;
                if (!IsHandleCreated) return;
                int ex = Native.GetWindowLong(Handle, GWL_EXSTYLE);
                ex = locked ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
                Native.SetWindowLong(Handle, GWL_EXSTYLE, ex);
                Cursor = locked ? Cursors.Default : Cursors.SizeAll;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (locked || e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, new IntPtr(2 /*HTCAPTION*/), IntPtr.Zero);
            if (Moved != null) Moved(this, EventArgs.Empty);
        }

        // Called every UI tick with all timers.
        public void UpdateItems(List<TimerData> all)
        {
            List<TimerData> vis = new List<TimerData>();
            DateTime now = DateTime.UtcNow;
            foreach (TimerData d in all)
                if (d.Running || (d.Expired && (now - d.ExpiredUtc).TotalSeconds < ExpiredVisibleSec)) vis.Add(d);
            items = vis;
            int h = Util.S(10) + Math.Max(1, vis.Count) * Util.S(vis.Count == 0 ? 20 : 26);
            if (Height != h) Height = h;
            Invalidate();

            // Games in borderless fullscreen can push us down; re-assert topmost every ~2 s.
            if (Environment.TickCount - lastTopMostFix > 2000 && IsHandleCreated && Visible)
            {
                lastTopMostFix = Environment.TickCount;
                Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int pad = Util.S(8), rowH = Util.S(26);
            using (Pen border = new Pen(locked ? Color.FromArgb(60, 60, 66) : Color.FromArgb(110, 110, 120)))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            if (items.Count == 0)
            {
                g.DrawString(locked ? "No timers running" : "No timers running (drag to move)", smallFont, Brushes.Gray, pad, Util.S(6));
                return;
            }

            bool blink = DateTime.Now.Millisecond < 500;
            int y = Util.S(5);
            foreach (TimerData d in items)
            {
                Color c; string time;
                if (d.Expired) { time = "EXPIRED"; c = blink ? Color.FromArgb(255, 80, 80) : Color.FromArgb(150, 40, 40); }
                else if (d.Paused) { time = "II " + Util.Fmt(d.RemainingMs()); c = Color.Gold; }
                else if (d.Alerted && !d.Silent) { time = Util.Fmt(d.RemainingMs()); c = blink ? Color.FromArgb(255, 110, 90) : Color.Orange; }
                else { time = Util.Fmt(d.RemainingMs()); c = Color.FromArgb(120, 230, 120); }

                SizeF ts = g.MeasureString(time, nameFont);
                float timeX = Width - pad - ts.Width;
                RectangleF nameRect = new RectangleF(pad, y, timeX - pad - Util.S(4), rowH);
                using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
                {
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    g.DrawString(d.Name, nameFont, Brushes.Gainsboro, nameRect, sf);
                }
                using (SolidBrush b = new SolidBrush(c)) g.DrawString(time, nameFont, b, timeX, y);

                // thin progress line under each entry
                int barY = y + rowH - Util.S(6), barW = Width - pad * 2;
                float frac = d.Running ? Math.Min(1f, d.RemainingMs() / (float)Math.Max(1, d.DurationSec * 1000)) : 0f;
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(55, 55, 60))) g.FillRectangle(bg, pad, barY, barW, Util.S(3));
                using (SolidBrush fg = new SolidBrush(c)) g.FillRectangle(fg, pad, barY, barW * frac, Util.S(3));
                y += rowH;
            }
        }
    }

    class TimerRow : Panel
    {
        public readonly TimerData D;
        readonly MainForm F;
        CheckBox chkEnabled, chkSilent;
        TextBox txtName, txtDuration;
        Label lblTime, lblRegion, lblKey, lblSound;
        ProgressBar bar;
        Button btnStart, btnPause, btnReset, btnRemove, btnRegion, btnKey, btnSound, btnPlay;
        NumericUpDown numWarn, numVol;
        ComboBox cmbMode;

        T Add<T>(T c, int x, int y, int w, int h) where T : Control
        {
            c.Location = new Point(Util.S(x), Util.S(y));
            c.Size = new Size(Util.S(w), Util.S(h));
            Controls.Add(c);
            return c;
        }

        Label Lbl(string text, int x, int y, int w)
        {
            Label l = Add(new Label(), x, y, w, 25);
            l.Text = text; l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }

        Button Btn(string text, int x, int y, int w, EventHandler click)
        {
            Button b = Add(new Button(), x, y, w, 27);
            b.Text = text; b.Click += click;
            return b;
        }

        public TimerRow(MainForm f, TimerData d)
        {
            F = f; D = d;
            Size = new Size(Util.S(800), Util.S(108));
            BorderStyle = BorderStyle.FixedSingle;
            Margin = new Padding(Util.S(4));

            // line 1: name, countdown, controls
            chkEnabled = Add(new CheckBox(), 8, 8, 20, 22);
            chkEnabled.Checked = d.Enabled;
            chkEnabled.CheckedChanged += delegate { D.Enabled = chkEnabled.Checked; F.Dirty = true; };
            new ToolTip().SetToolTip(chkEnabled, "Detect clicks / key for this timer");
            txtName = Add(new TextBox(), 30, 7, 170, 25);
            txtName.Text = d.Name;
            txtName.TextChanged += delegate { D.Name = txtName.Text; F.Dirty = true; };
            lblTime = Add(new Label(), 206, 2, 125, 34);
            lblTime.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
            lblTime.TextAlign = ContentAlignment.MiddleCenter;
            bar = Add(new ProgressBar(), 335, 9, 220, 20);
            bar.Maximum = 1000;
            btnStart = Btn("Start", 562, 5, 60, delegate { D.Start(); F.Dirty = true; });
            btnPause = Btn("Pause", 626, 5, 68, delegate { D.TogglePause(); F.Dirty = true; });
            btnReset = Btn("Reset", 698, 5, 58, delegate { D.Reset(); F.Dirty = true; });
            btnRemove = Btn("X", 762, 5, 30, delegate { F.RemoveRow(this); });

            // line 2: timing
            Lbl("Duration (m:ss)", 8, 42, 97);
            txtDuration = Add(new TextBox(), 105, 43, 62, 25);
            txtDuration.Text = Util.Fmt(d.DurationSec * 1000);
            txtDuration.TextChanged += delegate
            {
                int sec;
                if (Util.TryParseDuration(txtDuration.Text, out sec)) { D.DurationSec = sec; txtDuration.BackColor = SystemColors.Window; F.Dirty = true; }
                else txtDuration.BackColor = Color.MistyRose;
            };
            txtDuration.Leave += delegate { txtDuration.Text = Util.Fmt(D.DurationSec * 1000); };
            Lbl("Alert", 178, 42, 38);
            numWarn = Add(new NumericUpDown(), 216, 43, 60, 25);
            numWarn.Maximum = 3600;
            numWarn.Value = Math.Min(3600, d.WarnSec);
            numWarn.ValueChanged += delegate { D.WarnSec = (int)numWarn.Value; F.Dirty = true; };
            Lbl("s before end", 280, 42, 85);
            Lbl("Re-click:", 372, 42, 60);
            cmbMode = Add(new ComboBox(), 432, 43, 235, 25);
            cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMode.Items.AddRange(new object[] { "Restart countdown (potions)", "Ignore while running", "Pause / resume (rings, amulets)" });
            cmbMode.SelectedIndex = (int)d.Mode;
            cmbMode.SelectedIndexChanged += delegate { D.Mode = (Retrigger)cmbMode.SelectedIndex; F.Dirty = true; };
            chkSilent = Add(new CheckBox(), 680, 44, 110, 24);
            chkSilent.Text = "Silent";
            chkSilent.Checked = d.Silent;
            chkSilent.CheckedChanged += delegate { D.Silent = chkSilent.Checked; UpdateSilentControls(); F.Dirty = true; };
            new ToolTip().SetToolTip(chkSilent, "Just count down: no sound and no warning highlight");

            // line 3: triggers and sound
            btnRegion = Btn("Set region", 8, 75, 82, delegate { F.PickRegion(this); });
            lblRegion = Lbl("", 94, 76, 132);
            btnKey = Btn("Set key", 228, 75, 66, delegate { F.StartKeyCapture(this); });
            lblKey = Lbl("", 297, 76, 105);
            btnSound = Btn("Sound...", 404, 75, 68, delegate { PickSound(); });
            lblSound = Lbl("", 475, 76, 150);
            lblSound.AutoEllipsis = true;
            btnPlay = Btn("Test", 628, 75, 48, delegate { Sound.Play(D); });
            Lbl("Vol %", 680, 76, 42);
            numVol = Add(new NumericUpDown(), 722, 77, 55, 25);
            numVol.Maximum = 100;
            numVol.Increment = 10;
            numVol.Value = d.Volume;
            numVol.ValueChanged += delegate { D.Volume = (int)numVol.Value; F.Dirty = true; };

            RefreshLabels();
            UpdateSilentControls();
            UpdateView();
        }

        void UpdateSilentControls()
        {
            bool on = !D.Silent;
            numWarn.Enabled = btnSound.Enabled = btnPlay.Enabled = numVol.Enabled = lblSound.Enabled = on;
        }

        void PickSound()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Choose alert sound for " + D.Name;
                dlg.Filter = "Audio files (*.wav;*.mp3;*.wma;*.m4a)|*.wav;*.mp3;*.wma;*.m4a;*.aac;*.flac;*.ogg|All files (*.*)|*.*";
                if (!string.IsNullOrEmpty(D.SoundPath) && File.Exists(D.SoundPath)) dlg.FileName = D.SoundPath;
                if (dlg.ShowDialog(F) != DialogResult.OK) return;
                D.SoundPath = dlg.FileName;
                F.Dirty = true;
                RefreshLabels();
                Sound.Play(D);
            }
        }

        public void RefreshLabels()
        {
            lblRegion.Text = Util.RegionText(D.Region);
            lblKey.Text = F.CapturingRow == this ? "press a key..." : Util.KeyName(D.Hotkey);
            bool hasFile = !string.IsNullOrEmpty(D.SoundPath);
            lblSound.Text = !hasFile ? "(built-in chime)" : File.Exists(D.SoundPath) ? Path.GetFileName(D.SoundPath) : "MISSING: " + Path.GetFileName(D.SoundPath);
            lblSound.ForeColor = hasFile && !File.Exists(D.SoundPath) ? Color.Red : SystemColors.ControlText;
            new ToolTip().SetToolTip(lblSound, hasFile ? D.SoundPath : "No file chosen - a built-in chime is used");
        }

        static void SetIfChanged(Control c, string text) { if (c.Text != text) c.Text = text; }

        public void UpdateView()
        {
            Color back, fore = SystemColors.ControlText;
            string time;
            int barVal = 0;
            if (D.Expired) { time = "EXPIRED"; back = Color.FromArgb(255, 190, 190); fore = Color.DarkRed; }
            else if (!D.Running) { time = Util.Fmt(D.DurationSec * 1000); back = SystemColors.Control; fore = Color.Gray; }
            else
            {
                int rem = D.RemainingMs();
                time = Util.Fmt(rem);
                barVal = (int)Math.Max(0, Math.Min(1000, rem * 1000L / Math.Max(1, D.DurationSec * 1000L)));
                if (D.Paused) { back = Color.LightYellow; fore = Color.DarkGoldenrod; }
                else if (D.Alerted && !D.Silent) { back = DateTime.Now.Millisecond < 500 ? Color.LightSalmon : Color.MistyRose; fore = Color.DarkRed; }
                else { back = Color.Honeydew; fore = Color.DarkGreen; }
            }
            SetIfChanged(lblTime, time);
            if (lblTime.ForeColor != fore) lblTime.ForeColor = fore;
            if (BackColor != back) BackColor = back;
            if (bar.Value != barVal) bar.Value = barVal;
            SetIfChanged(btnPause, D.Paused ? "Resume" : "Pause");
            if (btnPause.Enabled != D.Running) btnPause.Enabled = D.Running;
        }
    }

    class MainForm : Form
    {
        public bool Dirty;
        public TimerRow CapturingRow;
        readonly List<TimerRow> rows = new List<TimerRow>();
        readonly FlowLayoutPanel flow;
        readonly CheckBox chkDetect, chkOnlyTibia, chkTopMost, chkOverlay, chkLockOverlay;
        readonly CountdownOverlay overlay = new CountdownOverlay();
        readonly List<TimerData> allData = new List<TimerData>();
        readonly Label lblStatus;
        readonly System.Windows.Forms.Timer tick;
        readonly string iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TibiaTimers.ini");
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        readonly Dictionary<uint, string> procNames = new Dictionary<uint, string>();
        readonly HashSet<int> heldKeys = new HashSet<int>();
        Native.HookProc mouseProc, keyProc;   // kept as fields so the GC doesn't collect them
        IntPtr mouseHook, keyHook;
        bool loaded;

        public MainForm()
        {
            Text = "Tibia Timers";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Util.S(836), Util.S(420));
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            overlay.Location = new Point(wa.Right - overlay.Width - Util.S(30), wa.Top + Util.S(120));
            overlay.Moved += delegate { Dirty = true; };
            MinimumSize = new Size(Util.S(560), Util.S(250));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = Util.S(40);
            Button btnAdd = new Button();
            btnAdd.Text = "+ Add timer";
            btnAdd.Bounds = new Rectangle(Util.S(8), Util.S(7), Util.S(95), Util.S(27));
            btnAdd.Click += delegate { AddRow(new TimerData()); Dirty = true; };
            Button btnShow = new Button();
            btnShow.Text = "Show regions";
            btnShow.Bounds = new Rectangle(Util.S(108), Util.S(7), Util.S(100), Util.S(27));
            btnShow.Click += delegate { ShowRegions(); };
            chkDetect = MakeCheck("Detection on", 222, 110, true);
            chkOnlyTibia = MakeCheck("Only in Tibia window", 335, 150, true);
            chkTopMost = MakeCheck("Always on top", 490, 110, false);
            chkTopMost.CheckedChanged += delegate { TopMost = chkTopMost.Checked; };
            chkOverlay = MakeCheck("Overlay", 604, 75, true);
            chkOverlay.CheckedChanged += delegate { UpdateOverlayVisibility(); };
            chkLockOverlay = MakeCheck("Lock overlay", 682, 110, false);
            chkLockOverlay.CheckedChanged += delegate { overlay.Locked = chkLockOverlay.Checked; };
            ToolTip tips = new ToolTip();
            tips.SetToolTip(chkOverlay, "Small always-on-top countdown window (drag it where you like)");
            tips.SetToolTip(chkLockOverlay, "Locked = clicks pass through the overlay to the game");
            top.Controls.AddRange(new Control[] { btnAdd, btnShow, chkDetect, chkOnlyTibia, chkTopMost, chkOverlay, chkLockOverlay });

            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Bottom;
            lblStatus.Height = Util.S(24);
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.Padding = new Padding(Util.S(6), 0, 0, 0);
            lblStatus.BorderStyle = BorderStyle.Fixed3D;

            flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.AutoScroll = true;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;

            Controls.Add(flow);
            Controls.Add(top);
            Controls.Add(lblStatus);

            LoadSettings();
            overlay.Locked = chkLockOverlay.Checked;
            loaded = true;
            Status("Ready. Click a hotkey region in Tibia (or press its key) to start a countdown.");

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 100;
            tick.Tick += OnTick;
            tick.Start();
        }

        CheckBox MakeCheck(string text, int x, int w, bool on)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Bounds = new Rectangle(Util.S(x), Util.S(9), Util.S(w), Util.S(24));
            c.Checked = on;
            c.CheckedChanged += delegate { Dirty = true; };
            return c;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            mouseProc = MouseProc;
            keyProc = KeyProc;
            IntPtr mod = Native.GetModuleHandle(null);
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
            keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
            if (mouseHook == IntPtr.Zero || keyHook == IntPtr.Zero)
                MessageBox.Show("Could not install the global mouse/keyboard listener.", "Tibia Timers", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateOverlayVisibility();
        }

        void UpdateOverlayVisibility()
        {
            if (!IsHandleCreated) return;
            overlay.UpdateItems(allData);
            if (chkOverlay.Checked && !overlay.Visible) overlay.Show();
            else if (!chkOverlay.Checked && overlay.Visible) overlay.Hide();
            chkLockOverlay.Enabled = chkOverlay.Checked;
            Dirty = true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            overlay.Close();
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
            mouseHook = keyHook = IntPtr.Zero;
            SaveSettings();
            List<TimerData> all = new List<TimerData>();
            foreach (TimerRow r in rows) all.Add(r.D);
            Sound.CloseAll(all);
            base.OnFormClosing(e);
        }

        // ---------- hooks (must return fast: real work is posted to the UI queue) ----------

        IntPtr MouseProc(int n, IntPtr w, IntPtr l)
        {
            if (n >= 0 && (int)w == Native.WM_LBUTTONDOWN)
            {
                Native.MSLLHOOKSTRUCT s = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(Native.MSLLHOOKSTRUCT));
                BeginInvoke(new Action<Point>(OnGlobalClick), new Point(s.pt.X, s.pt.Y));
            }
            return Native.CallNextHookEx(mouseHook, n, w, l);
        }

        IntPtr KeyProc(int n, IntPtr w, IntPtr l)
        {
            if (n >= 0)
            {
                int msg = (int)w;
                Native.KBDLLHOOKSTRUCT s = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(Native.KBDLLHOOKSTRUCT));
                int vk = (int)s.vkCode;
                if (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP) heldKeys.Remove(vk);
                else if ((msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN) && heldKeys.Add(vk) && !IsModifier(vk))
                {
                    Keys k = (Keys)vk;
                    if (Native.GetAsyncKeyState(0x11) < 0) k |= Keys.Control;
                    if (Native.GetAsyncKeyState(0x10) < 0) k |= Keys.Shift;
                    if (Native.GetAsyncKeyState(0x12) < 0) k |= Keys.Alt;
                    BeginInvoke(new Action<Keys>(OnGlobalKey), k);
                }
            }
            return Native.CallNextHookEx(keyHook, n, w, l);
        }

        static bool IsModifier(int vk)
        {
            return (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;
        }

        // ---------- trigger handling ----------

        uint RootPid(IntPtr hwnd, out IntPtr root)
        {
            root = hwnd == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hwnd, 2 /*GA_ROOT*/);
            uint pid;
            Native.GetWindowThreadProcessId(root, out pid);
            return pid;
        }

        bool IsTibia(IntPtr root, uint pid)
        {
            if (root == IntPtr.Zero) return false;
            StringBuilder sb = new StringBuilder(256);
            Native.GetWindowText(root, sb, 256);
            if (sb.ToString().StartsWith("Tibia", StringComparison.OrdinalIgnoreCase)) return true;
            string name;
            if (!procNames.TryGetValue(pid, out name))
            {
                try { name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { name = ""; }
                procNames[pid] = name;
            }
            return name == "client" || name.Contains("tibia");
        }

        void OnGlobalClick(Point p)
        {
            if (!chkDetect.Checked || CapturingRow != null) return;
            Native.POINT np; np.X = p.X; np.Y = p.Y;
            IntPtr root;
            uint pid = RootPid(Native.WindowFromPoint(np), out root);
            if (pid == ownPid) return;   // clicks on this app never count
            if (chkOnlyTibia.Checked && !IsTibia(root, pid)) return;
            foreach (TimerRow r in rows)
                if (r.D.Enabled && !r.D.Region.IsEmpty && r.D.Region.Contains(p)) Fire(r, "click");
        }

        void OnGlobalKey(Keys k)
        {
            if (CapturingRow != null)
            {
                TimerRow row = CapturingRow;
                CapturingRow = null;
                Keys code = k & Keys.KeyCode;
                row.D.Hotkey = code == Keys.Escape || code == Keys.Back ? Keys.None : k;
                row.RefreshLabels();
                Dirty = true;
                Status(row.D.Hotkey == Keys.None ? "Key cleared for " + row.D.Name : row.D.Name + " key set to " + Util.KeyName(k));
                return;
            }
            if (!chkDetect.Checked) return;
            IntPtr root;
            uint pid = RootPid(Native.GetForegroundWindow(), out root);
            if (pid == ownPid) return;   // typing in this app never counts
            if (chkOnlyTibia.Checked && !IsTibia(root, pid)) return;
            foreach (TimerRow r in rows)
                if (r.D.Enabled && r.D.Hotkey != Keys.None && r.D.Hotkey == k) Fire(r, Util.KeyName(k));
        }

        void Fire(TimerRow r, string source)
        {
            bool wasPaused = r.D.Paused, wasRunning = r.D.Running;
            if (!r.D.Trigger()) return;
            string what = !wasRunning ? "started" : r.D.Mode == Retrigger.Restart ? "restarted" : wasPaused ? "resumed" : "paused";
            Status(DateTime.Now.ToString("HH:mm:ss") + "  " + r.D.Name + " " + what + " (" + source + ")");
            Dirty = true;
            r.UpdateView();
        }

        void OnTick(object sender, EventArgs e)
        {
            foreach (TimerRow r in rows)
            {
                bool changed;
                if (r.D.Update(out changed) && !r.D.Silent)
                {
                    Sound.Play(r.D);
                    Status(DateTime.Now.ToString("HH:mm:ss") + "  ALERT: " + r.D.Name + " expires in " + Util.Fmt(r.D.RemainingMs()));
                }
                if (changed) Dirty = true;
                r.UpdateView();
            }
            if (overlay.Visible) overlay.UpdateItems(allData);
            if (Dirty && loaded) { Dirty = false; SaveSettings(); }
        }

        // ---------- row management ----------

        void AddRow(TimerData d)
        {
            TimerRow r = new TimerRow(this, d);
            rows.Add(r);
            allData.Add(d);
            flow.Controls.Add(r);
        }

        public void RemoveRow(TimerRow r)
        {
            if (MessageBox.Show(this, "Remove timer \"" + r.D.Name + "\"?", "Tibia Timers", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (CapturingRow == r) CapturingRow = null;
            rows.Remove(r);
            allData.Remove(r.D);
            flow.Controls.Remove(r);
            r.Dispose();
            Dirty = true;
        }

        public void PickRegion(TimerRow r)
        {
            List<KeyValuePair<string, Rectangle>> others = new List<KeyValuePair<string, Rectangle>>();
            foreach (TimerRow o in rows)
                if (o != r && !o.D.Region.IsEmpty) others.Add(new KeyValuePair<string, Rectangle>(o.D.Name, o.D.Region));
            using (Overlay ov = new Overlay(false, r.D.Name, others))
            {
                if (ov.ShowDialog(this) != DialogResult.OK) return;
                r.D.Region = ov.Result;
            }
            r.RefreshLabels();
            Dirty = true;
            Status("Region for " + r.D.Name + ": " + Util.RegionText(r.D.Region));
        }

        void ShowRegions()
        {
            List<KeyValuePair<string, Rectangle>> all = new List<KeyValuePair<string, Rectangle>>();
            foreach (TimerRow o in rows)
                if (!o.D.Region.IsEmpty) all.Add(new KeyValuePair<string, Rectangle>(o.D.Name, o.D.Region));
            using (Overlay ov = new Overlay(true, "", all)) ov.ShowDialog(this);
        }

        public void StartKeyCapture(TimerRow r)
        {
            TimerRow prev = CapturingRow;
            CapturingRow = r;
            if (prev != null) prev.RefreshLabels();
            r.RefreshLabels();
            Status("Press the Tibia hotkey for " + r.D.Name + " (Esc or Backspace = no key).");
        }

        void Status(string s) { lblStatus.Text = s; }

        // ---------- settings ----------

        void LoadSettings()
        {
            if (!File.Exists(iniPath))
            {
                AddDefault("Bullseye potion", 600, 30, Retrigger.Restart);
                AddDefault("Ring of plasma", 1800, 60, Retrigger.PauseResume);
                AddDefault("Amulet of plasma", 1800, 60, Retrigger.PauseResume);
                return;
            }
            List<TimerData> list = new List<TimerData>();
            TimerData cur = null;
            int wx = int.MinValue, wy = 0, ww = 0, wh = 0;
            foreach (string raw in File.ReadAllLines(iniPath, Encoding.UTF8))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (line == "[Timer]") { cur = new TimerData(); list.Add(cur); continue; }
                if (line.StartsWith("[")) { cur = null; continue; }
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                try
                {
                    if (cur != null) cur.Read(k, v);
                    else switch (k)
                    {
                        case "Detect": chkDetect.Checked = v == "1"; break;
                        case "OnlyTibia": chkOnlyTibia.Checked = v == "1"; break;
                        case "TopMost": chkTopMost.Checked = v == "1"; break;
                        case "Overlay": chkOverlay.Checked = v == "1"; break;
                        case "OverlayLock": chkLockOverlay.Checked = v == "1"; break;
                        case "OverlayPos":
                            string[] op = v.Split(',');
                            Point pt = new Point(int.Parse(op[0]), int.Parse(op[1]));
                            foreach (Screen s in Screen.AllScreens)
                                if (s.Bounds.Contains(new Point(pt.X + Util.S(20), pt.Y + Util.S(10)))) { overlay.Location = pt; break; }
                            break;
                        case "WinX": wx = int.Parse(v); break;
                        case "WinY": wy = int.Parse(v); break;
                        case "WinW": ww = int.Parse(v); break;
                        case "WinH": wh = int.Parse(v); break;
                    }
                }
                catch { }
            }
            foreach (TimerData d in list) { d.FixAfterLoad(); AddRow(d); }
            if (wx != int.MinValue && ww > 200 && wh > 150)
            {
                Rectangle b = new Rectangle(wx, wy, ww, wh);
                foreach (Screen s in Screen.AllScreens)
                    if (s.WorkingArea.IntersectsWith(new Rectangle(b.X + 50, b.Y, 100, 30)))
                    {
                        StartPosition = FormStartPosition.Manual;
                        Bounds = b;
                        break;
                    }
            }
        }

        void AddDefault(string name, int dur, int warn, Retrigger mode)
        {
            TimerData d = new TimerData();
            d.Name = name; d.DurationSec = dur; d.WarnSec = warn; d.Mode = mode;
            AddRow(d);
        }

        void SaveSettings()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[General]");
            sb.AppendLine("Detect=" + (chkDetect.Checked ? 1 : 0));
            sb.AppendLine("OnlyTibia=" + (chkOnlyTibia.Checked ? 1 : 0));
            sb.AppendLine("TopMost=" + (chkTopMost.Checked ? 1 : 0));
            sb.AppendLine("Overlay=" + (chkOverlay.Checked ? 1 : 0));
            sb.AppendLine("OverlayLock=" + (chkLockOverlay.Checked ? 1 : 0));
            sb.AppendLine("OverlayPos=" + overlay.Left + "," + overlay.Top);
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            sb.AppendLine("WinX=" + b.X); sb.AppendLine("WinY=" + b.Y);
            sb.AppendLine("WinW=" + b.Width); sb.AppendLine("WinH=" + b.Height);
            foreach (TimerRow r in rows) { sb.AppendLine(); r.D.Write(sb); }
            try
            {
                string tmp = iniPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(iniPath)) File.Replace(tmp, iniPath, null);
                else File.Move(tmp, iniPath);
            }
            catch (Exception ex) { Status("Could not save settings: " + ex.Message); }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Per-monitor DPI awareness so mouse-hook coordinates and the region overlay use the same pixels.
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { Native.SetProcessDPIAware(); } catch { } }

            bool isNew;
            using (Mutex m = new Mutex(true, "TibiaTimers_SingleInstance", out isNew))
            {
                if (!isNew)
                {
                    MessageBox.Show("Tibia Timers is already running.", "Tibia Timers");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Util.Scale = g.DpiX / 96f;
                Application.Run(new MainForm());
            }
        }
    }
}
