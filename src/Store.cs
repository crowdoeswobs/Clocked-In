using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WorkHours
{
    public enum ClockState { Stopped, Running, Paused }

    public class WorkSession
    {
        public DateTime Start;
        public DateTime? End;
    }

    public class BreakEntry
    {
        public long Id;
        public DateTime Date;
        public int Minutes;
    }

    /// <summary>One job: its own clock, sessions, breaks and pay rate.</summary>
    public class Job
    {
        public long Id;
        public string Name = "";
        public readonly List<WorkSession> Sessions = new List<WorkSession>();
        public readonly List<BreakEntry> Breaks = new List<BreakEntry>();
        public ClockState State = ClockState.Stopped;
        public DateTime PausedAt = DateTime.MinValue;
        public DateTime Heartbeat = DateTime.MinValue;
        public double HourlyRate;
        public string Currency = Store.DefaultCurrency();
        internal Store Owner;

        static DateTime Now() { return Store.Now(); }

        public WorkSession OpenSession
        {
            get
            {
                foreach (var s in Sessions) if (!s.End.HasValue) return s;
                return null;
            }
        }

        // ---------------- clock actions ----------------

        public void Start()
        {
            if (State == ClockState.Running) return;
            Owner.StopAllExcept(this);
            Sessions.Add(new WorkSession { Start = Now() });
            State = ClockState.Running;
            Heartbeat = Now();
            Owner.Commit();
        }

        public void Pause()
        {
            if (State != ClockState.Running) return;
            CloseOpen(Now());
            State = ClockState.Paused;
            PausedAt = Now();
            Owner.Commit();
        }

        public void Stop()
        {
            StopSilently();
            Owner.Commit();
        }

        internal void StopSilently()
        {
            if (State == ClockState.Running) CloseOpen(Now());
            State = ClockState.Stopped;
        }

        void CloseOpen(DateTime at)
        {
            var o = OpenSession;
            if (o == null) return;
            o.End = at < o.Start ? o.Start : at;
            if ((o.End.Value - o.Start).TotalSeconds < 1) Sessions.Remove(o);
        }

        /// <summary>PC slept while running: end the session at the moment it went to sleep.</summary>
        public ClockState SuspendAt(DateTime at, DateTime now)
        {
            if (State != ClockState.Running) return State;
            CloseOpen(at);
            if (at.Date == now.Date) { State = ClockState.Paused; PausedAt = at; }
            else State = ClockState.Stopped;
            Owner.Commit();
            return State;
        }

        public void Beat()
        {
            if (State != ClockState.Running) return;
            Heartbeat = Now();
            Owner.Save();
        }

        /// <summary>Fix up state after the app was closed / PC was off. Returns a note for the user, or null.</summary>
        internal string Recover(DateTime now)
        {
            string note = null;
            var open = OpenSession;
            if (State == ClockState.Running && open == null) State = ClockState.Stopped;
            if (State != ClockState.Running && open != null)
                open.End = Heartbeat > open.Start ? Heartbeat : open.Start;

            if (State == ClockState.Running && open != null)
            {
                var last = Heartbeat > open.Start ? Heartbeat : open.Start;
                if ((now - last).TotalMinutes > 15)
                {
                    open.End = last;
                    if (last.Date == now.Date)
                    {
                        State = ClockState.Paused;
                        PausedAt = last;
                        note = Name + ": the clock was running when Clocked In closed. It was paused at " + last.ToString("HH:mm") + ".";
                    }
                    else
                    {
                        State = ClockState.Stopped;
                        note = Name + ": the clock was left running on " + last.ToString("dddd d MMM", Store.En) + ". It was stopped at " + last.ToString("HH:mm") + ".";
                    }
                }
            }
            if (State == ClockState.Paused && PausedAt.Date < now.Date) State = ClockState.Stopped;
            Sessions.RemoveAll(s => s.End.HasValue && s.End.Value <= s.Start);
            return note;
        }

        // ---------------- editing ----------------

        public void AddBreak(DateTime day, int minutes)
        {
            if (minutes <= 0) return;
            Breaks.Add(new BreakEntry { Id = Owner.NextId(), Date = day.Date, Minutes = minutes });
            Owner.Commit();
        }

        public void RemoveBreak(BreakEntry b) { Breaks.Remove(b); Owner.Commit(); }

        public void AddSession(DateTime start, DateTime end)
        {
            if (end <= start) return;
            Sessions.Add(new WorkSession { Start = start, End = end });
            Sessions.Sort((a, b) => a.Start.CompareTo(b.Start));
            Owner.Commit();
        }

        public void RemoveSession(WorkSession s)
        {
            if (!s.End.HasValue) State = ClockState.Stopped;
            Sessions.Remove(s);
            Owner.Commit();
        }

        public void SetRate(double rate, string currency) { HourlyRate = rate; Currency = currency; Owner.Commit(); }

        // ---------------- queries ----------------

        public List<WorkSession> SessionsOn(DateTime day)
        {
            DateTime a = day.Date, b = a.AddDays(1), now = DateTime.Now;
            var list = new List<WorkSession>();
            foreach (var s in Sessions)
            {
                var e = s.End ?? now;
                if (s.Start < b && e > a) list.Add(s);
            }
            return list;
        }

        public List<BreakEntry> BreaksOn(DateTime day)
        {
            return Breaks.FindAll(x => x.Date == day.Date);
        }

        public TimeSpan Worked(DateTime day, DateTime now)
        {
            DateTime a = day.Date, b = a.AddDays(1);
            double secs = 0;
            foreach (var s in Sessions)
            {
                var e = s.End ?? now;
                var st = s.Start > a ? s.Start : a;
                var en = e < b ? e : b;
                if (en > st) secs += (en - st).TotalSeconds;
            }
            return TimeSpan.FromSeconds(Math.Floor(secs));
        }

        public int BreakMinutes(DateTime day)
        {
            int m = 0;
            foreach (var b in Breaks) if (b.Date == day.Date) m += b.Minutes;
            return m;
        }

        public TimeSpan Net(DateTime day, DateTime now)
        {
            var t = Worked(day, now) - TimeSpan.FromMinutes(BreakMinutes(day));
            return t < TimeSpan.Zero ? TimeSpan.Zero : t;
        }

        public TimeSpan NetRange(DateTime from, DateTime to, DateTime now)
        {
            var sum = TimeSpan.Zero;
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1)) sum += Net(d, now);
            return sum;
        }

        public bool HasData(DateTime day)
        {
            return SessionsOn(day).Count > 0 || BreaksOn(day).Count > 0;
        }

        public string Money(TimeSpan net)
        {
            return Fmt2.Money(net.TotalHours * HourlyRate, Currency);
        }
    }

    /// <summary>All jobs + app settings, persisted to %APPDATA%\WorkHours\data.txt.</summary>
    public class Store
    {
        public readonly List<Job> Jobs = new List<Job>();
        public Job Current;

        // settings
        public string Theme = "System";
        public double RemindHours = 8;
        public int RemindPausedMinutes = 30;
        public double DefaultRate = 0;
        public string DefaultCurrencyText = DefaultCurrency();
        public double WinWidth, WinHeight;   // 0 = automatic size
        public bool DndWhileWorking;          // turn on Windows Do Not Disturb while a clock runs
        public bool DndOwned;                 // true while DND is on because we turned it on

        public bool FirstRun;
        public string RecoveryNote;
        long nextId = 1;

        public event EventHandler Changed;

        public static readonly CultureInfo En = new CultureInfo("en-GB");
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const string Fmt = "yyyy-MM-dd'T'HH:mm:ss";

        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkHours"); }
        }
        static string DataPath { get { return Path.Combine(DataDir, "data.txt"); } }

        public static string DefaultCurrency()
        {
            try { return RegionInfo.CurrentRegion.CurrencySymbol; } catch { return "$"; }
        }

        public static DateTime Now()
        {
            var n = DateTime.Now;
            return new DateTime(n.Year, n.Month, n.Day, n.Hour, n.Minute, n.Second);
        }

        static string D(DateTime d) { return d.ToString(Fmt, Inv); }
        static DateTime P(string s) { return DateTime.ParseExact(s, Fmt, Inv); }
        static string Clean(string s) { return (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); }

        internal long NextId() { return nextId++; }

        public bool HasJobs { get { return Jobs.Count > 0; } }

        /// <summary>The job whose clock is running or paused, else the selected job.</summary>
        public Job Active
        {
            get
            {
                foreach (var j in Jobs) if (j.State == ClockState.Running) return j;
                foreach (var j in Jobs) if (j.State == ClockState.Paused) return j;
                return Current;
            }
        }

        internal void StopAllExcept(Job keep)
        {
            foreach (var j in Jobs) if (j != keep && j.State != ClockState.Stopped) j.StopSilently();
        }

        Job FindJob(long id)
        {
            foreach (var j in Jobs) if (j.Id == id) return j;
            return null;
        }

        // ---------------- persistence ----------------

        public void Load()
        {
            if (!File.Exists(DataPath))
            {
                FirstRun = true;
                Save();
                return;
            }

            // values from the old single-job format
            var legacy = new Job { Name = "Work", Owner = this };
            bool legacyData = false;
            long currentId = -1;

            foreach (var raw in File.ReadAllLines(DataPath, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try
                {
                    var p = line.Split('|');
                    if (p[0] == "J" && p.Length >= 8)
                    {
                        var j = new Job
                        {
                            Owner = this,
                            Id = long.Parse(p[1], Inv),
                            State = (ClockState)Enum.Parse(typeof(ClockState), p[2]),
                            PausedAt = P(p[3]),
                            Heartbeat = P(p[4]),
                            HourlyRate = double.Parse(p[5], Inv),
                            Currency = p[6],
                            Name = string.Join("|", p, 7, p.Length - 7)
                        };
                        Jobs.Add(j);
                        if (j.Id >= nextId) nextId = j.Id + 1;
                    }
                    else if (p[0] == "S" && p.Length == 4)
                    {
                        var j = FindJob(long.Parse(p[1], Inv));
                        if (j == null) continue;
                        var s = new WorkSession { Start = P(p[2]) };
                        if (p[3].Length > 0) s.End = P(p[3]);
                        j.Sessions.Add(s);
                    }
                    else if (p[0] == "B" && p.Length == 5)
                    {
                        var j = FindJob(long.Parse(p[2], Inv));
                        long id = long.Parse(p[1], Inv);
                        if (id >= nextId) nextId = id + 1;
                        if (j == null) continue;
                        j.Breaks.Add(new BreakEntry { Id = id, Date = P(p[3]).Date, Minutes = int.Parse(p[4], Inv) });
                    }
                    else if (p[0] == "S" && p.Length == 3)          // legacy session
                    {
                        var s = new WorkSession { Start = P(p[1]) };
                        if (p[2].Length > 0) s.End = P(p[2]);
                        legacy.Sessions.Add(s);
                        legacyData = true;
                    }
                    else if (p[0] == "B" && p.Length == 4)          // legacy break
                    {
                        long id = long.Parse(p[1], Inv);
                        if (id >= nextId) nextId = id + 1;
                        legacy.Breaks.Add(new BreakEntry { Id = id, Date = P(p[2]).Date, Minutes = int.Parse(p[3], Inv) });
                        legacyData = true;
                    }
                    else
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                        switch (k)
                        {
                            case "theme": Theme = v; break;
                            case "remindHours": RemindHours = double.Parse(v, Inv); break;
                            case "remindPaused": RemindPausedMinutes = int.Parse(v, Inv); break;
                            case "currentJob": currentId = long.Parse(v, Inv); break;
                            case "defaultRate": DefaultRate = double.Parse(v, Inv); break;
                            case "defaultCurrency": DefaultCurrencyText = v; break;
                            case "winW": WinWidth = double.Parse(v, Inv); break;
                            case "winH": WinHeight = double.Parse(v, Inv); break;
                            case "dnd": DndWhileWorking = v == "1"; break;
                            case "dndOwned": DndOwned = v == "1"; break;
                            // legacy single-job keys
                            case "state": legacy.State = (ClockState)Enum.Parse(typeof(ClockState), v); break;
                            case "pausedAt": legacy.PausedAt = P(v); break;
                            case "heartbeat": legacy.Heartbeat = P(v); break;
                            case "rate": legacy.HourlyRate = DefaultRate = double.Parse(v, Inv); break;
                            case "currency": legacy.Currency = DefaultCurrencyText = v; break;
                        }
                    }
                }
                catch { /* skip malformed line */ }
            }

            if (legacyData && Jobs.Count == 0)
            {
                legacy.Id = nextId++;
                Jobs.Add(legacy);
            }

            Current = FindJob(currentId) ?? (Jobs.Count > 0 ? Jobs[0] : null);

            var now = Now();
            foreach (var j in Jobs)
            {
                j.Sessions.Sort((a, b) => a.Start.CompareTo(b.Start));
                var note = j.Recover(now);
                if (note != null && RecoveryNote == null) RecoveryNote = note;
            }
            Save();
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Clocked In data file (v2)");
            sb.AppendLine("theme=" + Theme);
            sb.AppendLine("remindHours=" + RemindHours.ToString(Inv));
            sb.AppendLine("remindPaused=" + RemindPausedMinutes.ToString(Inv));
            sb.AppendLine("defaultRate=" + DefaultRate.ToString(Inv));
            sb.AppendLine("defaultCurrency=" + Clean(DefaultCurrencyText));
            sb.AppendLine("winW=" + WinWidth.ToString(Inv));
            sb.AppendLine("winH=" + WinHeight.ToString(Inv));
            sb.AppendLine("dnd=" + (DndWhileWorking ? "1" : "0"));
            sb.AppendLine("dndOwned=" + (DndOwned ? "1" : "0"));
            if (Current != null) sb.AppendLine("currentJob=" + Current.Id.ToString(Inv));
            foreach (var j in Jobs)
            {
                sb.AppendLine("J|" + j.Id.ToString(Inv) + "|" + j.State + "|" + D(j.PausedAt) + "|" + D(j.Heartbeat) + "|"
                    + j.HourlyRate.ToString(Inv) + "|" + Clean(j.Currency).Replace("|", "") + "|" + Clean(j.Name));
                foreach (var s in j.Sessions)
                    sb.AppendLine("S|" + j.Id.ToString(Inv) + "|" + D(s.Start) + "|" + (s.End.HasValue ? D(s.End.Value) : ""));
                foreach (var b in j.Breaks)
                    sb.AppendLine("B|" + b.Id.ToString(Inv) + "|" + j.Id.ToString(Inv) + "|" + D(b.Date) + "|" + b.Minutes.ToString(Inv));
            }

            try
            {
                Directory.CreateDirectory(DataDir);
                string tmp = DataPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(DataPath)) File.Replace(tmp, DataPath, DataPath + ".bak");
                else File.Move(tmp, DataPath);
            }
            catch
            {
                try { File.WriteAllText(DataPath, sb.ToString(), new UTF8Encoding(false)); } catch { }
            }
        }

        internal void Commit()
        {
            Save();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        // ---------------- jobs ----------------

        public Job AddJob(string name, double rate, string currency)
        {
            var j = new Job { Owner = this, Id = NextId(), Name = Clean(name), HourlyRate = rate, Currency = Clean(currency) };
            Jobs.Add(j);
            Current = j;
            if (rate > 0) { DefaultRate = rate; DefaultCurrencyText = j.Currency; }
            Commit();
            return j;
        }

        public void SelectJob(Job j) { Current = j; Commit(); }

        public void RenameJob(Job j, string name)
        {
            name = Clean(name);
            if (name.Length == 0 || name == j.Name) return;
            j.Name = name;
            Commit();
        }

        public void DeleteJob(Job j)
        {
            Jobs.Remove(j);
            if (Current == j) Current = Jobs.Count > 0 ? Jobs[0] : null;
            Commit();
        }

        // ---------------- settings ----------------

        public void SetTheme(string t) { Theme = t; Commit(); }
        public void SetRemindHours(double h) { RemindHours = h; Commit(); }
        public void SetRemindPaused(int m) { RemindPausedMinutes = m; Commit(); }
        public void SetWindowSize(double w, double h) { WinWidth = w; WinHeight = h; Save(); }
        public void ResetWindowSize() { WinWidth = 0; WinHeight = 0; Commit(); }
        public void SetDndWhileWorking(bool on) { DndWhileWorking = on; Commit(); }
    }

    public static class Fmt2
    {
        public static string Dur(TimeSpan t)
        {
            int total = (int)Math.Floor(t.TotalMinutes);
            if (total < 0) total = 0;
            int h = total / 60, m = total % 60;
            if (h == 0) return m + "m";
            return h + "h " + m.ToString("00") + "m";
        }

        public static string Money(double amount, string symbol)
        {
            var nf = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
            nf.CurrencySymbol = symbol ?? "";
            nf.CurrencyDecimalSeparator = ".";
            if (nf.CurrencyGroupSeparator == "." || nf.CurrencyGroupSeparator == ",") nf.CurrencyGroupSeparator = ",";
            return amount.ToString("C2", nf);
        }

        /// <summary>Accepts "22", "22.5" or "22,5".</summary>
        public static bool TryParseAmount(string text, out double value)
        {
            value = 0;
            string t = (text ?? "").Trim().Replace(" ", "").Replace(" ", "").Replace(',', '.');
            if (t.Length == 0) return true;
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        public static DateTime MondayOf(DateTime d)
        {
            int diff = ((int)d.DayOfWeek + 6) % 7;
            return d.Date.AddDays(-diff);
        }

        public static int IsoWeek(DateTime d)
        {
            var cal = CultureInfo.InvariantCulture.Calendar;
            var day = cal.GetDayOfWeek(d);
            if (day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday) d = d.AddDays(3);
            return cal.GetWeekOfYear(d, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        }

        public static string Range(DateTime a, DateTime b)
        {
            var en = Store.En;
            if (a.Month == b.Month && a.Year == b.Year) return a.Day + "–" + b.ToString("d MMM yyyy", en);
            if (a.Year == b.Year) return a.ToString("d MMM", en) + " – " + b.ToString("d MMM yyyy", en);
            return a.ToString("d MMM yyyy", en) + " – " + b.ToString("d MMM yyyy", en);
        }
    }
}
