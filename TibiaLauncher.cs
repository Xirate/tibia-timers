// Tibia Launcher - starts Tibia with or without Tibia Timers.
// Command line: --timers / --no-timers start directly without showing the window.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TibiaLauncher
{
    enum LaunchResult { Done, Watching, Error }

    class LauncherForm : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        const string TimersTitle = "Tibia Timers";   // main window title of TibiaTimers.exe
        static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        static readonly string IniPath = Path.Combine(Dir, "TibiaLauncher.ini");
        static readonly string TimersExe = Path.Combine(Dir, "TibiaTimers.exe");

        string tibiaPath;
        readonly Label lblPath, lblStatus;
        readonly CheckBox chkCloseTimers;
        readonly Button btnWith, btnWithout;

        // watcher state (closing the timers when Tibia exits)
        Timer watch;
        bool sawClient;
        DateTime watchStart;

        public LauncherForm()
        {
            float s;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiX / 96f;
            Func<int, int> S = v => (int)Math.Round(v * s);

            Text = "Tibia Launcher";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(380), S(222));
            try { Icon = Icon.ExtractAssociatedIcon(DefaultTibiaPath()); } catch { }

            Label cap = new Label();
            cap.Text = "Tibia:";
            cap.Bounds = new Rectangle(S(12), S(14), S(40), S(20));
            lblPath = new Label();
            lblPath.AutoEllipsis = true;
            lblPath.Bounds = new Rectangle(S(52), S(14), S(240), S(20));
            Button btnBrowse = new Button();
            btnBrowse.Text = "Change...";
            btnBrowse.Bounds = new Rectangle(S(296), S(9), S(72), S(27));
            btnBrowse.Click += delegate { Browse(); };

            btnWith = new Button();
            btnWith.Text = "Start Tibia + Timers";
            btnWith.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            btnWith.Bounds = new Rectangle(S(12), S(46), S(356), S(48));
            btnWith.Click += delegate { OnButton(true); };

            btnWithout = new Button();
            btnWithout.Text = "Start Tibia only";
            btnWithout.Bounds = new Rectangle(S(12), S(102), S(356), S(34));
            btnWithout.Click += delegate { OnButton(false); };

            chkCloseTimers = new CheckBox();
            chkCloseTimers.Text = "Close Tibia Timers when Tibia is closed";
            chkCloseTimers.Bounds = new Rectangle(S(14), S(146), S(350), S(24));
            chkCloseTimers.Checked = true;

            lblStatus = new Label();
            lblStatus.ForeColor = Color.DimGray;
            lblStatus.Bounds = new Rectangle(S(12), S(178), S(356), S(36));

            Controls.AddRange(new Control[] { cap, lblPath, btnBrowse, btnWith, btnWithout, chkCloseTimers, lblStatus });
            AcceptButton = btnWith;

            LoadSettings();
            RefreshPath();
            if (!File.Exists(TimersExe))
            {
                btnWith.Enabled = false;
                lblStatus.Text = "TibiaTimers.exe not found next to the launcher.";
            }
        }

        static string DefaultTibiaPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Tibia\Tibia.exe");
        }

        void RefreshPath()
        {
            bool ok = File.Exists(tibiaPath);
            lblPath.Text = ok ? tibiaPath : "not found - click Change...";
            lblPath.ForeColor = ok ? SystemColors.ControlText : Color.Red;
            new ToolTip().SetToolTip(lblPath, tibiaPath);
        }

        void Browse()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Select Tibia.exe";
                dlg.Filter = "Programs (*.exe)|*.exe";
                if (File.Exists(tibiaPath)) dlg.FileName = tibiaPath;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                tibiaPath = dlg.FileName;
                RefreshPath();
                SaveSettings();
            }
        }

        void OnButton(bool withTimers)
        {
            LaunchResult r = Launch(withTimers);
            if (r == LaunchResult.Done) Close();
            else if (r == LaunchResult.Watching) StartWatching();
        }

        // Done = nothing left to do, Watching = caller should StartWatching(), Error = message is in the status label.
        public LaunchResult Launch(bool withTimers)
        {
            if (!File.Exists(tibiaPath))
            {
                lblStatus.Text = "Tibia.exe not found. Click Change... to select it.";
                return LaunchResult.Error;
            }
            SaveSettings();

            bool startedTimers = false;
            if (withTimers)
            {
                if (TimersRunning()) lblStatus.Text = "Tibia Timers is already running.";
                else if (File.Exists(TimersExe))
                {
                    ProcessStartInfo tp = new ProcessStartInfo(TimersExe);
                    tp.WorkingDirectory = Dir;
                    Process.Start(tp);
                    startedTimers = true;
                }
            }

            ProcessStartInfo psi = new ProcessStartInfo(tibiaPath);
            psi.WorkingDirectory = Path.GetDirectoryName(tibiaPath);
            try { Process.Start(psi); }
            catch (Exception ex)
            {
                lblStatus.Text = "Could not start Tibia: " + ex.Message;
                return LaunchResult.Error;
            }
            return startedTimers && chkCloseTimers.Checked ? LaunchResult.Watching : LaunchResult.Done;
        }

        static bool TimersRunning() { return FindWindow(null, TimersTitle) != IntPtr.Zero; }

        // Tibia.exe (the updater/launcher) starts bin\client.exe and may exit; we wait for the client
        // to appear and then to disappear, and close the timers after that.
        public void StartWatching()
        {
            if (Visible) Hide();
            ShowInTaskbar = false;
            sawClient = false;
            watchStart = DateTime.UtcNow;
            watch = new Timer();
            watch.Interval = 3000;
            watch.Tick += delegate { WatchTick(); };
            watch.Start();
        }

        void WatchTick()
        {
            if (!TimersRunning()) { Finish(); return; }   // user closed the timers themselves
            bool clientNow = TibiaClientRunning();
            if (clientNow) sawClient = true;
            else if (sawClient)
            {
                PostMessage(FindWindow(null, TimersTitle), 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
                Finish();
            }
            else if ((DateTime.UtcNow - watchStart).TotalMinutes > 60) Finish();   // client never started
        }

        void Finish()
        {
            watch.Stop();
            Application.ExitThread();
        }

        bool TibiaClientRunning()
        {
            string tibiaDir = Path.GetDirectoryName(tibiaPath) ?? "";
            foreach (Process p in Process.GetProcessesByName("client"))
            {
                try
                {
                    if (p.MainModule.FileName.StartsWith(tibiaDir, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { return true; }   // can't read the path (e.g. elevated): assume it is Tibia
            }
            return false;
        }

        void LoadSettings()
        {
            tibiaPath = DefaultTibiaPath();
            if (!File.Exists(IniPath)) return;
            foreach (string line in File.ReadAllLines(IniPath, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                if (k == "TibiaPath" && v.Length > 0) tibiaPath = v;
                else if (k == "CloseTimers") chkCloseTimers.Checked = v == "1";
            }
        }

        void SaveSettings()
        {
            try
            {
                File.WriteAllText(IniPath, "TibiaPath=" + tibiaPath + "\r\nCloseTimers=" + (chkCloseTimers.Checked ? 1 : 0) + "\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            LauncherForm f = new LauncherForm();
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (mode == "--timers" || mode == "--no-timers")
            {
                // start straight away without showing the window (it only appears on errors)
                LaunchResult r = f.Launch(mode == "--timers");
                if (r == LaunchResult.Done) return;
                if (r == LaunchResult.Watching) { f.StartWatching(); Application.Run(); return; }
            }
            Application.Run(f);
        }

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    }
}
