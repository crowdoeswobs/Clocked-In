using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WorkHours
{
    /// <summary>Wires the store, tray icon, flyout, reminders and the once-a-second tick together.</summary>
    public class AppController
    {
        readonly Application app;
        Store store;
        Tray tray;
        Flyout fly;
        DispatcherTimer timer;
        bool taskbarDark;

        DateTime lastTick = DateTime.MinValue, lastBeat = DateTime.MinValue;
        DateTime remindDay = DateTime.MinValue;
        double remindHoursRef = -1, nextOvertime;
        DateTime pausedRef = DateTime.MinValue;
        int pausedMinRef = -1;
        DateTime nextPausedReminder = DateTime.MaxValue;

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "WorkHours";

        public AppController(Application app) { this.app = app; }

        public void Init()
        {
            store = new Store();
            store.Load();
            taskbarDark = Palette.TaskbarDark();
            if (StartupEnabled) SetStartup(true);   // keep the autostart path pointing at this exe

            tray = new Tray();
            fly = new Flyout(store, this);
            fly.RegisterHotkey();

            tray.OpenRequested += (s, e) => ToggleFlyout();
            tray.BalloonClicked += (s, e) => fly.ShowFlyout();
            tray.OpenItem.Click += (s, e) => fly.ShowFlyout();
            tray.StartItem.Click += (s, e) => { if (store.Active != null) store.Active.Start(); else fly.ShowFlyout(); };
            tray.PauseItem.Click += (s, e) => { if (store.Active != null) store.Active.Pause(); };
            tray.StopItem.Click += (s, e) => { if (store.Active != null) store.Active.Stop(); };
            tray.QuitItem.Click += (s, e) => Quit();
            tray.Menu.Opening += (s, e) =>
            {
                var a = store.Active;
                var st = a == null ? ClockState.Stopped : a.State;
                string name = a == null ? "" : " " + a.Name;
                tray.StartItem.Text = (st == ClockState.Paused ? "Resume" : "Start") + name;
                tray.PauseItem.Text = "Pause" + name;
                tray.StopItem.Text = "Stop" + name;
                tray.StartItem.Enabled = a != null && st != ClockState.Running;
                tray.PauseItem.Enabled = st == ClockState.Running;
                tray.StopItem.Enabled = st != ClockState.Stopped;
            };
            store.Changed += (s, e) => { RefreshTray(); SyncDnd(); };

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => Tick();
            timer.Start();

            SystemEvents.UserPreferenceChanged += OnPrefChanged;
            SystemEvents.PowerModeChanged += OnPower;
            app.SessionEnding += (s, e) => BeatAll();
            StartSignalThread();

            RefreshTray();
            SyncDnd();
            tray.Show();

            if (store.RecoveryNote != null)
                tray.Notify("Clocked In", store.RecoveryNote);
            else if (store.FirstRun)
                tray.Notify("Clocked In is in your tray",
                    "Click the clock to add your first job. Tip: drag the icon from the ^ overflow onto the taskbar to keep it next to the volume icon.");
        }

        /// <summary>Do Not Disturb follows the clock (only if enabled, and only DND we turned on ourselves).</summary>
        void SyncDnd()
        {
            var a = store.Active;
            bool want = store.DndWhileWorking && a != null && a.State == ClockState.Running;
            if (want && !store.DndOwned)
            {
                if (!Dnd.IsOn && Dnd.Set(true))
                {
                    store.DndOwned = true;
                    store.Save();
                }
            }
            else if (!want && store.DndOwned)
            {
                ReleaseDnd();
            }
        }

        void ReleaseDnd()
        {
            if (!store.DndOwned) return;
            Dnd.Set(false);
            store.DndOwned = false;
            store.Save();
        }

        void BeatAll()
        {
            foreach (var j in store.Jobs) j.Beat();
        }

        public void Notify(string title, string text) { tray.Notify(title, text); }

        void ToggleFlyout()
        {
            if (fly.IsVisible) { fly.HideFlyout(); return; }
            // a click on the tray icon first deactivates (hides) the flyout; don't reopen it immediately
            if ((DateTime.Now - fly.LastHidden).TotalMilliseconds < 400) return;
            fly.ShowFlyout();
        }

        void Tick()
        {
            var now = DateTime.Now;
            bool slept = lastTick != DateTime.MinValue && (now - lastTick).TotalMinutes > 5;
            bool beat = (now - lastBeat).TotalSeconds >= 60;
            foreach (var j in store.Jobs.ToArray())
            {
                if (j.State == ClockState.Running)
                {
                    if (slept)
                    {
                        // the PC was asleep: don't count that time
                        var at = new DateTime(lastTick.Year, lastTick.Month, lastTick.Day, lastTick.Hour, lastTick.Minute, lastTick.Second);
                        var st = j.SuspendAt(at, now);
                        tray.Notify(st == ClockState.Paused ? "Clock paused" : "Clock stopped",
                            j.Name + ": your PC was asleep from " + at.ToString("HH:mm") + ", so that time wasn't counted."
                            + (st == ClockState.Paused ? " Resume when you're back." : ""));
                    }
                    else if (beat) j.Beat();
                }
                if (j.State == ClockState.Paused && j.PausedAt.Date < now.Date) j.Stop();
            }
            if (beat) lastBeat = now;
            lastTick = now;

            CheckReminders(now);
            RefreshTray();
            if (fly.IsVisible) fly.UpdateLive();
        }

        void CheckReminders(DateTime now)
        {
            var job = store.Active;
            if (job == null) return;
            if (job.State == ClockState.Running && store.RemindHours > 0)
            {
                if (remindDay != now.Date || remindHoursRef != store.RemindHours)
                {
                    remindDay = now.Date;
                    remindHoursRef = store.RemindHours;
                    nextOvertime = store.RemindHours;
                }
                var net = job.Net(now.Date, now);
                if (net.TotalHours >= nextOvertime)
                {
                    tray.Notify("Still on the clock · " + job.Name,
                        "You've worked " + Fmt2.Dur(net) + " today. Don't forget to stop the clock when you're done.");
                    nextOvertime = net.TotalHours + 1;
                }
            }

            if (job.State == ClockState.Paused && store.RemindPausedMinutes > 0)
            {
                if (pausedRef != job.PausedAt || pausedMinRef != store.RemindPausedMinutes)
                {
                    pausedRef = job.PausedAt;
                    pausedMinRef = store.RemindPausedMinutes;
                    nextPausedReminder = job.PausedAt.AddMinutes(store.RemindPausedMinutes);
                }
                if (now >= nextPausedReminder)
                {
                    tray.Notify("Clock is paused · " + job.Name,
                        "Paused for " + Fmt2.Dur(now - job.PausedAt) + ". Resume when you're back, or stop the clock if you're done for the day.");
                    nextPausedReminder = now.AddMinutes(store.RemindPausedMinutes);
                }
            }
        }

        void RefreshTray()
        {
            var now = DateTime.Now;
            var job = store.Active;
            if (job == null)
            {
                tray.SetLook(TrayLook.Stopped, taskbarDark);
                tray.SetText("Clocked In · click to add a job");
                return;
            }
            var net = job.Net(now.Date, now);
            TrayLook look;
            string s;
            switch (job.State)
            {
                case ClockState.Running:
                    bool over = store.RemindHours > 0 && net.TotalHours >= store.RemindHours;
                    look = over ? TrayLook.Overtime : TrayLook.Running;
                    s = over ? "Working (over " + store.RemindHours.ToString("0.#") + " h)" : "Working";
                    break;
                case ClockState.Paused:
                    look = TrayLook.Paused; s = "Paused";
                    break;
                default:
                    look = TrayLook.Stopped; s = "Not tracking";
                    break;
            }
            tray.SetLook(look, taskbarDark);
            tray.SetText(job.Name + " · " + s + "\nToday: " + Fmt2.Dur(net));
        }

        void OnPrefChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                taskbarDark = Palette.TaskbarDark();
                fly.ApplyTheme();
                RefreshTray();
            }));
        }

        void OnPower(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
                app.Dispatcher.Invoke(new Action(BeatAll));
        }

        void StartSignalThread()
        {
            var showEv = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WorkHours_Show");
            var quitEv = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WorkHours_Quit");
            var th = new Thread(() =>
            {
                var handles = new WaitHandle[] { showEv, quitEv };
                while (true)
                {
                    int i = WaitHandle.WaitAny(handles);
                    if (i == 0) app.Dispatcher.BeginInvoke(new Action(() => fly.ShowFlyout()));
                    else { app.Dispatcher.BeginInvoke(new Action(Quit)); break; }
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        public void Quit()
        {
            timer.Stop();
            BeatAll();
            SystemEvents.UserPreferenceChanged -= OnPrefChanged;
            SystemEvents.PowerModeChanged -= OnPower;
            ReleaseDnd();
            tray.Dispose();
            fly.ForceClose();
            app.Shutdown();
        }

        // ---------------- start with Windows

        public static bool StartupEnabled
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                        return k != null && k.GetValue(RunName) != null;
                }
                catch { return false; }
            }
        }

        public static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            bool created;
            var mutex = new Mutex(true, @"Local\WorkHours_SingleInstance", out created);
            if (!created)
            {
                try { EventWaitHandle.OpenExisting(@"Local\WorkHours_Show").Set(); } catch { }
                return;
            }

            System.Windows.Forms.Application.EnableVisualStyles();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log(e.Exception);
                e.Handled = true;
            };
            var ctl = new AppController(app);
            ctl.Init();
            app.Run();
            GC.KeepAlive(mutex);
        }

        static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Store.DataDir);
                File.AppendAllText(Path.Combine(Store.DataDir, "error.log"), DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
