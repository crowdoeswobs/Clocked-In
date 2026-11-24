using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace WorkHours
{
    /// <summary>The Windows 11-style flyout that pops up above the tray icon.</summary>
    public class Flyout : Window
    {
        readonly Store store;
        readonly AppController ctl;
        Palette pal;
        string appliedTheme;
        readonly Border root;
        readonly TranslateTransform slide = new TranslateTransform();

        DateTime weekStart;
        DateTime monthStart;
        bool monthMode;
        DateTime? expanded;
        bool settings;
        bool jobsView, creatingJob;
        long renamingId = -1, confirmDeleteId = -1;
        bool quitting;
        bool userSizing;
        StackPanel heroStatusRow;
        Job job { get { return store.Current; } }
        const string GWork = "", GEdit = "";
        public DateTime LastHidden = DateTime.MinValue;

        // live-updated elements
        TextBlock heroTime, heroStatus, totalText, totalMoney;
        Ellipse heroDot;
        readonly Dictionary<DateTime, TextBlock> dayTotals = new Dictionary<DateTime, TextBlock>();

        // Apple-style type: SF Pro if installed, then Inter (Google Fonts), then Windows' own
        readonly FontFamily fText = new FontFamily("SF Pro Text, Inter, Segoe UI Variable Text, Segoe UI");
        readonly FontFamily fDisplay = new FontFamily("SF Pro Display, Inter, Segoe UI Variable Display, Segoe UI");
        readonly FontFamily fIcon = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        static CultureInfo En { get { return Store.En; } }

        const string GPlay = "", GPause = "", GStop = "", GSettings = "", GBack = "",
            GLeft = "", GRight = "", GDown = "", GUp = "", GDelete = "", GAdd = "",
            GMinus = "", GClock = "", GFolder = "", GPower = "", GTable = "", GCopy = "";

        public Flyout(Store store, AppController ctl)
        {
            this.store = store;
            this.ctl = ctl;
            Title = "Clocked In";
            Width = 360;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 320;
            MinHeight = 320;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Transparent;
            UseLayoutRounding = true;
            Left = -10000; Top = -10000;
            FontFamily = fText;
            FontSize = 14;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(-1),
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            root = new Border { RenderTransform = slide };
            Content = root;

            weekStart = Fmt2.MondayOf(DateTime.Today);
            monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
                // no system menu => DWM draws no caption (close) button over the acrylic backdrop
                Native.SetWindowLong(h, Native.GWL_STYLE, Native.GetWindowLong(h, Native.GWL_STYLE)
                    & ~(Native.WS_SYSMENU | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX));
                ApplyDwm();
                // remember the size when the user finishes dragging an edge or corner
                HwndSource.FromHwnd(h).AddHook((IntPtr hw, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (msg == 0x0312 && wp.ToInt32() == HotkeyId)     // WM_HOTKEY
                    {
                        handled = true;
                        if (IsVisible) HideFlyout(); else ShowFlyout();
                        return IntPtr.Zero;
                    }
                    if (msg == 0x0046 && IsVisible && !userSizing)    // WM_WINDOWPOSCHANGING
                        AnchorInPlace(lp);
                    if (msg == 0x0231) userSizing = true;              // WM_ENTERSIZEMOVE
                    else if (msg == 0x0232 && userSizing)              // WM_EXITSIZEMOVE
                    {
                        userSizing = false;
                        store.SetWindowSize(Math.Round(ActualWidth), Math.Round(ActualHeight));
                        Reposition();
                        // Settings shows "Automatic" vs "Reset": refresh it right away
                        if (settings) Dispatcher.BeginInvoke(new Action(Rebuild));
                    }
                    return IntPtr.Zero;
                });
            };
            Deactivated += (s, e) => HideFlyout();
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) HideFlyout(); };
            // clicking anywhere outside a text field takes focus away from it
            root.Focusable = true;
            root.FocusVisualStyle = null;
            PreviewMouseDown += (s, e) =>
            {
                if (!(Keyboard.FocusedElement is TextBox)) return;
                var d = e.OriginalSource as DependencyObject;
                while (d != null && !(d is TextBox)) d = (d is Visual) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
                if (d == null) root.Focus();
            };
            Closing += (s, e) => { if (!quitting) { e.Cancel = true; HideFlyout(); } };
            // (size changes are re-anchored atomically in WM_WINDOWPOSCHANGING, see AnchorInPlace)
            store.Changed += (s, e) =>
            {
                if (appliedTheme != store.Theme) ApplyTheme();
                else if (IsVisible) Rebuild();
            };

            ApplyTheme();
        }

        // =============================================================== window plumbing

        public void ApplyTheme()
        {
            appliedTheme = store.Theme;
            pal = Palette.Create(store.Theme);
            root.Background = B(pal.Bg);
            Foreground = B(pal.Text);
            ApplyDwm();
            Rebuild();
        }

        void ApplyDwm()
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero || pal == null) return;
            Native.SetDwm(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, pal.Dark ? 1 : 0);
            Native.SetDwm(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 2);              // rounded
            Native.SetDwm(h, Native.DWMWA_SYSTEMBACKDROP_TYPE, pal.Glass ? 3 : 1);    // 3 = acrylic
        }

        public void ShowFlyout()
        {
            jobsView = false; creatingJob = false; renamingId = -1; confirmDeleteId = -1;
            if (appliedTheme != store.Theme) ApplyTheme(); else Rebuild();
            var wa = WorkArea();
            MaxHeight = wa.Height - 24;
            MaxWidth = wa.Width - 24;
            if (store.WinWidth > 0 && store.WinHeight > 0)
            {
                // user-chosen size
                SizeToContent = SizeToContent.Manual;
                Width = Math.Max(MinWidth, Math.Min(store.WinWidth, MaxWidth));
                Height = Math.Max(MinHeight, Math.Min(store.WinHeight, MaxHeight));
                PlaceAt(Height);
            }
            else
            {
                // automatic: fit the content
                Width = 360;
                SizeToContent = SizeToContent.Height;
                root.Measure(new Size(Width, double.PositiveInfinity));
                PlaceAt(Math.Min(root.DesiredSize.Height, MaxHeight));
            }
            Show();
            Activate();
            Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        public void HideFlyout()
        {
            if (!IsVisible) return;
            LastHidden = DateTime.Now;
            Hide();
        }

        public void ForceClose()
        {
            quitting = true;
            if (HotkeyRegistered) Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
            Close();
        }

        // ---- global shortcut: Ctrl + Shift + Win + C opens / closes the panel
        const int HotkeyId = 0xC10C;
        public const string HotkeyText = "Ctrl + Shift + Win + C";
        public bool HotkeyRegistered { get; private set; }

        /// <summary>Creates the window handle (without showing) and registers the shortcut on it.</summary>
        public void RegisterHotkey()
        {
            var h = new WindowInteropHelper(this).EnsureHandle();
            const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
            HotkeyRegistered = Native.RegisterHotKey(h, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_WIN | MOD_NOREPEAT, 0x43 /* C */);
        }

        void PlaceAt(double h)
        {
            var wa = WorkArea();
            Left = wa.Right - Width - 12;
            Top = Math.Max(wa.Top + 12, wa.Bottom - h - 12);
        }

        void Reposition() { if (IsVisible) PlaceAt(ActualHeight); }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

        /// <summary>
        /// When the window changes size (content grows/shrinks), move it in the same step so it stays pinned
        /// bottom-right. Doing size and position separately shows one frame at the wrong spot = jitter.
        /// </summary>
        void AnchorInPlace(IntPtr lp)
        {
            const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2;
            var pos = (WINDOWPOS)System.Runtime.InteropServices.Marshal.PtrToStructure(lp, typeof(WINDOWPOS));
            if ((pos.flags & SWP_NOSIZE) != 0) return;
            var wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;   // device pixels
            double scale = 1;
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null) scale = src.CompositionTarget.TransformToDevice.M22;
            int margin = (int)Math.Round(12 * scale);
            pos.x = wa.Right - margin - pos.cx;
            pos.y = Math.Max(wa.Top + margin, wa.Bottom - margin - pos.cy);
            pos.flags &= ~SWP_NOMOVE;
            System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lp, false);
        }

        Rect WorkArea()
        {
            var wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            double sx = 1, sy = 1;
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null)
            {
                sx = src.CompositionTarget.TransformToDevice.M11;
                sy = src.CompositionTarget.TransformToDevice.M22;
            }
            else
            {
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) { sx = g.DpiX / 96.0; sy = g.DpiY / 96.0; }
            }
            return new Rect(wa.Left / sx, wa.Top / sy, wa.Width / sx, wa.Height / sy);
        }

        // =============================================================== small UI helpers

        static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        TextBlock T(string text, double size, Color color, FontWeight weight)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = B(color), FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };
        }

        TextBlock Glyph(string g, double size, Color color)
        {
            return new TextBlock { Text = g, FontFamily = fIcon, FontSize = size, Foreground = B(color), VerticalAlignment = VerticalAlignment.Center };
        }

        static void Tabular(TextBlock t) { Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular); }

        /// <summary>The "CL" clock logo as crisp vector art (same geometry as the tray icon).</summary>
        FrameworkElement Logo(double size, Color color)
        {
            double S = size, c = S / 2, w = S * 0.135, r = S * 0.5 - w / 2 - S * 0.03;
            double a1 = 42 * Math.PI / 180, a2 = 318 * Math.PI / 180;
            var ci = CultureInfo.InvariantCulture;
            Func<double, string> n = v => v.ToString("0.###", ci);
            string data =
                "M" + n(c + r * Math.Cos(a1)) + "," + n(c + r * Math.Sin(a1)) +
                " A" + n(r) + "," + n(r) + " 0 1 1 " + n(c + r * Math.Cos(a2)) + "," + n(c + r * Math.Sin(a2)) +
                " M" + n(c - S * 0.03) + "," + n(c - S * 0.235) +
                " L" + n(c - S * 0.03) + "," + n(c + S * 0.015) +
                " L" + n(c + S * 0.215) + "," + n(c + S * 0.015);
            return new Path
            {
                Data = Geometry.Parse(data),
                Stroke = B(color),
                StrokeThickness = w,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = S,
                Height = S,
                Stretch = Stretch.None,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = false
            };
        }

        UIElement IconLabel(string glyph, string label, Color color, double size)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            if (glyph != null)
            {
                var i = Glyph(glyph, size, color);
                i.Margin = new Thickness(0, 0, label == null ? 0 : 8, 0);
                sp.Children.Add(i);
            }
            if (label != null) sp.Children.Add(T(label, size, color, FontWeights.Normal));
            return sp;
        }

        FButton AccentButton(string glyph, string label)
        {
            var b = new FButton(pal.Accent, Palette.Alpha(pal.Accent, 0xE6), Palette.Alpha(pal.Accent, 0xCC), null, IconLabel(glyph, label, pal.OnAccent, 14));
            b.Padding = new Thickness(12, 8, 12, 8);
            return b;
        }

        FButton StdButton(string glyph, string label, double size = 14)
        {
            var b = new FButton(pal.Control, pal.ControlHover, pal.ControlPressed, pal.ControlStroke, IconLabel(glyph, label, pal.Text, size));
            b.Padding = size < 14 ? new Thickness(10, 6, 10, 6) : new Thickness(12, 8, 12, 8);
            return b;
        }

        FButton SubtleButton(UIElement child, bool selected = false)
        {
            return new FButton(selected ? pal.SubtleHover : Colors.Transparent, selected ? Palette.Alpha(pal.SubtleHover, (byte)Math.Min(255, pal.SubtleHover.A * 2)) : pal.SubtleHover, pal.SubtlePressed, null, child);
        }

        FButton IconButton(string glyph, double size, string tip)
        {
            var g = Glyph(glyph, size, pal.Text2);
            g.HorizontalAlignment = HorizontalAlignment.Center;
            var b = SubtleButton(g);
            b.Width = 32; b.Height = 32; b.Padding = new Thickness(0);
            if (tip != null) b.ToolTip = tip;
            return b;
        }

        Border Card()
        {
            return new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = B(pal.Card),
                BorderBrush = B(pal.CardStroke),
                BorderThickness = new Thickness(1)
            };
        }

        TextBlock Label(string text, double top)
        {
            var t = T(text, 12, pal.Text2, FontWeights.SemiBold);
            t.Margin = new Thickness(0, top, 0, 4);
            return t;
        }

        Grid Cols(params GridLength[] widths)
        {
            var g = new Grid();
            foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            return g;
        }

        static GridLength Star { get { return new GridLength(1, GridUnitType.Star); } }
        static GridLength Auto { get { return GridLength.Auto; } }
        static GridLength Px(double v) { return new GridLength(v); }

        static void Put(Grid g, UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }

        /// <summary>A Fluent-ish text box with a rounded frame and placeholder.</summary>
        Border Input(string placeholder, double width, out TextBox box)
        {
            var tb = new TextBox
            {
                FontSize = 13,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = B(pal.Text),
                CaretBrush = B(pal.Text),
                SelectionBrush = B(pal.Accent),
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 4, 4, 4)
            };
            var ph = T(placeholder, 13, pal.Text3, FontWeights.Normal);
            ph.Margin = new Thickness(10, 0, 0, 0);
            ph.IsHitTestVisible = false;
            tb.TextChanged += (s, e) => ph.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var grid = new Grid();
            grid.Children.Add(ph);
            grid.Children.Add(tb);
            var frame = new Border
            {
                Width = width,
                CornerRadius = new CornerRadius(4),
                Background = B(pal.Control),
                BorderBrush = B(pal.ControlStroke),
                BorderThickness = new Thickness(1),
                Child = grid
            };
            tb.GotKeyboardFocus += (s, e) => frame.BorderBrush = B(pal.Accent);
            tb.LostKeyboardFocus += (s, e) => frame.BorderBrush = B(pal.ControlStroke);
            box = tb;
            return frame;
        }

        void Flag(Border frame) { frame.BorderBrush = B(pal.Red); }

        // =============================================================== build

        public void Rebuild()
        {
            if (pal == null) return;
            dayTotals.Clear();
            dayCells.Clear();
            mainStack = null;
            totalText = null; totalMoney = null; heroTime = null;
            var stack = new StackPanel();
            if (job == null)
            {
                // no jobs yet: the only thing to do is create one
                settings = false; jobsView = false;
                stack.Children.Add(BuildHeader());
                stack.Children.Add(BuildCreateJob());
            }
            else if (jobsView)
            {
                stack.Children.Add(BuildHeader());
                stack.Children.Add(creatingJob ? BuildCreateJob() : BuildJobs());
            }
            else if (settings)
            {
                stack.Children.Add(BuildHeader());
                stack.Children.Add(BuildSettings());
            }
            else
            {
                stack.Children.Add(BuildHeader());
                stack.Children.Add(BuildHero());
                selectorEl = BuildSelector();
                stack.Children.Add(selectorEl);
                stack.Children.Add(TimeHost(BuildTimeContent()));
                exportEl = BuildExport();
                stack.Children.Add(exportEl);
                footerEl = BuildFooter();
                stack.Children.Add(footerEl);
                mainStack = stack;
            }
            var sv = new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            };
            sv.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), PillScrollBarStyle());

            // keep the scroll position when the panel rebuilds (e.g. expanding a day)
            double keep = currentSv != null ? (scrollAnimating ? scrollTarget : currentSv.VerticalOffset) : 0;
            StopScrollAnim();
            if (keep > 0) sv.Loaded += (s, e) => sv.ScrollToVerticalOffset(keep);
            sv.PreviewMouseWheel += (s, e) =>
            {
                e.Handled = true;
                if (!scrollAnimating) scrollTarget = sv.VerticalOffset;
                scrollTarget = Math.Max(0, Math.Min(sv.ScrollableHeight, scrollTarget - e.Delta * 0.75));
                StartScrollAnim();
            };
            currentSv = sv;
            root.Child = sv;
            UpdateLive();

            // switching between main / settings / jobs: everything under the header glides in
            if (pendingView && IsVisible)
            {
                for (int i = 1; i < stack.Children.Count; i++)
                {
                    var fe = stack.Children[i] as FrameworkElement;
                    if (fe != null) Motion.Enter(fe, 0, 14 + i * 4, 1, 420 + i * 30, 5);
                }
            }
            pendingView = false;
        }

        // =============================================================== transitions in the time area

        enum TimeAnim { None, Next, Prev, ToMonth, ToWeek, ZoomIn }
        TimeAnim pendingTime = TimeAnim.None;
        bool pendingView;
        Grid timeHost;
        bool slideIndicator;

        class DayCell { public FButton Row; public Border Host; public RotateTransform Chev; }
        readonly Dictionary<DateTime, DayCell> dayCells = new Dictionary<DateTime, DayCell>();

        StackPanel mainStack;
        UIElement selectorEl, exportEl, footerEl;

        FrameworkElement BuildTimeContent()
        {
            var time = new StackPanel();
            time.Children.Add(BuildPeriodHeader());
            time.Children.Add(monthMode ? BuildMonth() : BuildWeek());
            return time;
        }

        /// <summary>
        /// Week/month navigation: swap only the selector, the list and the totals in the live panel,
        /// instead of rebuilding everything (which re-creates the scroll view and can flash a frame).
        /// </summary>
        void RefreshTimeArea()
        {
            if (mainStack == null || timeHost == null || timeHost.Parent != mainStack || job == null || settings || jobsView)
            {
                Rebuild();
                return;
            }
            dayTotals.Clear();
            dayCells.Clear();
            totalText = null; totalMoney = null;
            selectorEl = Replace(selectorEl, BuildSelector());
            TimeHost(BuildTimeContent());
            exportEl = Replace(exportEl, BuildExport());
            footerEl = Replace(footerEl, BuildFooter());
            UpdateLive();
        }

        UIElement Replace(UIElement oldEl, UIElement newEl)
        {
            int i = mainStack.Children.IndexOf(oldEl);
            if (i < 0) return oldEl;
            mainStack.Children.RemoveAt(i);
            mainStack.Children.Insert(i, newEl);
            return newEl;
        }

        /// <summary>A persistent container for the week/month list, so old and new content can cross-animate.</summary>
        UIElement TimeHost(FrameworkElement content)
        {
            if (timeHost == null) timeHost = new Grid { ClipToBounds = true };
            var parent = timeHost.Parent as Panel;
            if (parent != null && parent != mainStack) parent.Children.Remove(timeHost);

            var anim = pendingTime;
            pendingTime = TimeAnim.None;
            var old = timeHost.Children.Count > 0 ? timeHost.Children[timeHost.Children.Count - 1] as FrameworkElement : null;

            if (anim == TimeAnim.None || old == null || !IsVisible)
            {
                timeHost.Tag = null;
                timeHost.BeginAnimation(HeightProperty, null);
                timeHost.Height = double.NaN;
                timeHost.Children.Clear();
                timeHost.Children.Add(content);
                return timeHost;
            }

            while (timeHost.Children.Count > 1) timeHost.Children.RemoveAt(0);   // leftovers of an interrupted swap
            double oldH = timeHost.ActualHeight;
            double w = timeHost.ActualWidth > 0 ? timeHost.ActualWidth : ActualWidth;
            old.VerticalAlignment = VerticalAlignment.Top;
            content.VerticalAlignment = VerticalAlignment.Top;
            timeHost.Children.Add(content);
            content.Measure(new Size(w, double.PositiveInfinity));
            double newH = content.DesiredSize.Height;
            Motion.Height(timeHost, oldH, newH, 480, Motion.Spring);

            double dx = 0, sOut = 1, sIn = 1, dist = 64;
            switch (anim)
            {
                case TimeAnim.Next: dx = 1; break;
                case TimeAnim.Prev: dx = -1; break;
                case TimeAnim.ToMonth: dx = 1; dist = 96; break;
                case TimeAnim.ToWeek: dx = -1; dist = 96; break;
                case TimeAnim.ZoomIn: sOut = 1.06; sIn = 0.93; break;
            }
            Motion.Exit(old, -dx * dist, 0, sOut, 240, 9, () => timeHost.Children.Remove(old));
            Motion.Enter(content, dx * dist, 0, sIn, 480, 9);
            return timeHost;
        }

        void ToggleDay(DateTime d)
        {
            DayCell cell;
            if (!dayCells.TryGetValue(d, out cell)) return;
            if (expanded.HasValue && expanded.Value != d)
            {
                DayCell other;
                if (dayCells.TryGetValue(expanded.Value, out other)) CollapseCell(other);
            }
            if (expanded.HasValue && expanded.Value == d)
            {
                expanded = null;
                CollapseCell(cell);
                return;
            }
            expanded = d;
            ExpandCell(cell, d);
        }

        void ExpandCell(DayCell c, DateTime d)
        {
            var detail = (FrameworkElement)BuildDayDetail(d);
            c.Host.Child = detail;
            double w = c.Host.ActualWidth > 0 ? c.Host.ActualWidth : c.Row.ActualWidth;
            detail.Measure(new Size(w, double.PositiveInfinity));
            Motion.Height(c.Host, 0, detail.DesiredSize.Height, 500, Motion.Spring);
            Motion.Enter(detail, 0, -12, 0.97, 500, 6);
            Motion.Rotate(c.Chev, 180);
            c.Row.SetRest(pal.SubtleHover);
        }

        void CollapseCell(DayCell c)
        {
            var detail = c.Host.Child as FrameworkElement;
            if (detail == null) return;
            Motion.Height(c.Host, c.Host.ActualHeight, 0, 320, Motion.InOut,
                () => { if (c.Host.Child == detail) c.Host.Child = null; });
            Motion.Exit(detail, 0, -8, 0.97, 220, 6);
            Motion.Rotate(c.Chev, 0);
            c.Row.SetRest(Colors.Transparent);
        }

        // ---- smooth wheel scrolling: glide toward the target offset each frame
        ScrollViewer currentSv;
        double scrollTarget;
        bool scrollAnimating;
        TimeSpan lastFrame;

        void StartScrollAnim()
        {
            if (scrollAnimating) return;
            scrollAnimating = true;
            lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnScrollFrame;
        }

        void StopScrollAnim()
        {
            if (!scrollAnimating) return;
            scrollAnimating = false;
            CompositionTarget.Rendering -= OnScrollFrame;
        }

        void OnScrollFrame(object sender, EventArgs e)
        {
            var sv = currentSv;
            if (sv == null) { StopScrollAnim(); return; }
            var t = ((RenderingEventArgs)e).RenderingTime;
            double dt = lastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Min(0.05, (t - lastFrame).TotalSeconds);
            if (t == lastFrame) return;
            lastFrame = t;
            double cur = sv.VerticalOffset;
            double diff = scrollTarget - cur;
            if (Math.Abs(diff) < 0.5)
            {
                sv.ScrollToVerticalOffset(scrollTarget);
                StopScrollAnim();
                return;
            }
            double k = 1 - Math.Exp(-dt * 14);   // ease-out, settles in ~0.25 s
            sv.ScrollToVerticalOffset(cur + diff * k);
        }

        Style pillStyle;
        Palette pillStylePal;

        /// <summary>Slim rounded "pill" scrollbar: no arrows, no track, brightens on hover / drag.</summary>
        Style PillScrollBarStyle()
        {
            if (pillStyle != null && pillStylePal == pal) return pillStyle;
            string rest = Hex(Palette.Alpha(pal.Text, 0x50));
            string hot = Hex(Palette.Alpha(pal.Text, 0x99));
            string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>" +
                "<Setter Property='Width' Value='14'/><Setter Property='MinWidth' Value='14'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>" +
                "<Grid Background='Transparent'>" +
                "<Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='{TemplateBinding Orientation}'>" +
                "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>" +
                "<Grid Background='Transparent'>" +
                "<Border x:Name='pill' Width='6' CornerRadius='3' Margin='0,6,2,6' HorizontalAlignment='Center' Background='" + rest + "'/>" +
                "</Grid>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='pill' Property='Background' Value='" + hot + "'/></Trigger>" +
                "<Trigger Property='IsDragging' Value='True'><Setter TargetName='pill' Property='Background' Value='" + hot + "'/></Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
                "</Track></Grid>" +
                "</ControlTemplate></Setter.Value></Setter></Style>";
            pillStyle = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
            pillStylePal = pal;
            return pillStyle;
        }

        static string Hex(Color c)
        {
            return "#" + c.A.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        UIElement BuildHeader()
        {
            var g = Cols(Star, Auto);
            g.Margin = new Thickness(16, 8, 8, 6);
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            var logo = Logo(16, pal.Accent);
            logo.Margin = new Thickness(0, 0, 8, 0);
            title.Children.Add(logo);
            title.Children.Add(T(settings ? "Settings" : "C'locked In", 14, pal.Text, FontWeights.SemiBold));
            Put(g, title, 0);
            if (job != null && !jobsView)
            {
                var gear = IconButton(settings ? GBack : GSettings, 15, settings ? "Back" : "Settings");
                gear.Click += (s, e) => { settings = !settings; pendingView = true; Rebuild(); };
                Put(g, gear, 1);
            }
            else
            {
                g.MinHeight = 32;
            }
            if (job == null || settings) return g;

            // job switcher, centered on its own line under the title
            var pc = new StackPanel { Orientation = Orientation.Horizontal };
            var name = T(job.Name, 16, pal.Accent, FontWeights.SemiBold);
            name.MaxWidth = 280;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            pc.Children.Add(name);
            var chev = Glyph(jobsView ? GUp : GDown, 10, pal.Accent);
            chev.Margin = new Thickness(8, 2, 0, 0);
            pc.Children.Add(chev);
            var pill = SubtleButton(pc, jobsView);
            pill.Padding = new Thickness(12, 5, 12, 5);
            pill.HorizontalAlignment = HorizontalAlignment.Center;
            pill.Margin = new Thickness(0, 0, 0, 8);
            pill.ToolTip = "Switch or add jobs";
            pill.Click += (s, e) =>
            {
                jobsView = !jobsView; pendingView = true;
                creatingJob = false; renamingId = -1; confirmDeleteId = -1;
                Rebuild();
            };
            var header = new StackPanel();
            g.Margin = new Thickness(16, 8, 8, 0);
            header.Children.Add(g);
            header.Children.Add(pill);
            return header;
        }

        UIElement BuildCreateJob()
        {
            bool first = job == null;
            var card = Card();
            card.Margin = new Thickness(12, 0, 12, 12);
            card.Padding = new Thickness(18, 16, 18, 18);
            var sp = new StackPanel();

            var ic = Glyph(GWork, 26, pal.Accent);
            ic.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(ic);
            var title = T(first ? "Add your first job" : "New job", 20, pal.Text, FontWeights.SemiBold);
            title.FontFamily = fDisplay;
            title.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(title);
            var sub = T(first
                    ? "Give it a name, like the company or project. You can add more jobs later and switch between them."
                    : "Each job keeps its own hours and pay.", 12, pal.Text2, FontWeights.Normal);
            sub.TextWrapping = TextWrapping.Wrap;
            sub.Margin = new Thickness(0, 4, 0, 14);
            sp.Children.Add(sub);

            sp.Children.Add(Label("Job name", 0));
            TextBox nameBox;
            var nameFrame = Input("e.g. Café Nord", double.NaN, out nameBox);
            sp.Children.Add(nameFrame);

            sp.Children.Add(Label("Hourly rate (optional)", 12));
            TextBox rateBox, curBox;
            var rateFrame = Input("0.00", 100, out rateBox);
            var curFrame = Input("kr", 60, out curBox);
            curFrame.Margin = new Thickness(6, 0, 0, 0);
            if (store.DefaultRate > 0) rateBox.Text = store.DefaultRate.ToString("0.##", CultureInfo.InvariantCulture);
            curBox.Text = store.DefaultCurrencyText;
            var rateRow = new StackPanel { Orientation = Orientation.Horizontal };
            rateRow.Children.Add(rateFrame);
            rateRow.Children.Add(curFrame);
            sp.Children.Add(rateRow);

            Action create = () =>
            {
                string name = nameBox.Text.Trim();
                double rate;
                bool okRate = Fmt2.TryParseAmount(rateBox.Text, out rate);
                if (name.Length == 0) Flag(nameFrame);
                if (!okRate) Flag(rateFrame);
                if (name.Length == 0 || !okRate) return;
                jobsView = false; creatingJob = false; settings = false; pendingView = true;
                expanded = null;
                store.AddJob(name, rate, curBox.Text.Trim());
            };
            KeyEventHandler enter = (s, e) => { if (e.Key == Key.Enter) create(); };
            nameBox.KeyDown += enter;
            rateBox.KeyDown += enter;
            curBox.KeyDown += enter;

            var btns = first ? Cols(Star) : Cols(Star, Star);
            btns.Margin = new Thickness(0, 18, 0, 0);
            var ok = AccentButton(GAdd, "Create job");
            ok.Click += (s, e) => create();
            if (first) Put(btns, ok, 0);
            else
            {
                var cancel = StdButton(null, "Cancel");
                cancel.Margin = new Thickness(0, 0, 4, 0);
                ok.Margin = new Thickness(4, 0, 0, 0);
                cancel.Click += (s, e) => { creatingJob = false; pendingView = true; Rebuild(); };
                Put(btns, cancel, 0);
                Put(btns, ok, 1);
            }
            sp.Children.Add(btns);
            card.Child = sp;

            nameFrame.Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(() => Keyboard.Focus(nameBox)),
                System.Windows.Threading.DispatcherPriority.Input);
            return card;
        }

        UIElement BuildJobs()
        {
            var sp = new StackPanel { Margin = new Thickness(8, 0, 8, 12) };
            var l = Label("Your jobs", 0);
            l.Margin = new Thickness(8, 0, 0, 4);
            sp.Children.Add(l);
            var now = DateTime.Now;
            var wk = Fmt2.MondayOf(DateTime.Today);

            foreach (var j in store.Jobs)
            {
                var jj = j;
                bool sel = j == job;

                if (renamingId == j.Id)
                {
                    TextBox box;
                    var frame = Input("Job name", double.NaN, out box);
                    frame.Margin = new Thickness(4, 4, 4, 4);
                    box.Text = j.Name;
                    bool done = false;
                    Action save = () =>
                    {
                        if (done) return;
                        done = true;
                        renamingId = -1;
                        if (box.Text.Trim().Length > 0 && box.Text.Trim() != jj.Name)
                            Dispatcher.BeginInvoke(new Action(() => store.RenameJob(jj, box.Text)));
                        else
                            Dispatcher.BeginInvoke(new Action(Rebuild));
                    };
                    box.KeyDown += (s, e) =>
                    {
                        if (e.Key == Key.Enter) save();
                        else if (e.Key == Key.Escape) { done = true; renamingId = -1; e.Handled = true; Rebuild(); }
                    };
                    box.LostKeyboardFocus += (s, e) => save();
                    frame.Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(() => { Keyboard.Focus(box); box.SelectAll(); }),
                        System.Windows.Threading.DispatcherPriority.Input);
                    sp.Children.Add(frame);
                    continue;
                }

                var g = Cols(Px(3), Star, Auto, Auto);
                Put(g, new Border
                {
                    Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5),
                    Background = sel ? B(pal.Accent) : Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Center
                }, 0);

                var info = new StackPanel { Margin = new Thickness(10, 0, 8, 0) };
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                if (j.State != ClockState.Stopped)
                {
                    nameRow.Children.Add(new Ellipse
                    {
                        Width = 6, Height = 6, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                        Fill = B(j.State == ClockState.Running ? pal.Green : pal.Amber)
                    });
                }
                var nm = T(j.Name, 14, pal.Text, sel ? FontWeights.SemiBold : FontWeights.Normal);
                nm.TextTrimming = TextTrimming.CharacterEllipsis;
                nameRow.Children.Add(nm);
                info.Children.Add(nameRow);
                string st = j.State == ClockState.Running ? "Working now · " : j.State == ClockState.Paused ? "Paused · " : "";
                info.Children.Add(T(st + "This week " + Fmt2.Dur(j.NetRange(wk, wk.AddDays(6), now)), 12, pal.Text2, FontWeights.Normal));
                Put(g, info, 1);

                var edit = IconButton(GEdit, 12, "Rename");
                edit.Width = 30; edit.Height = 30;
                edit.Click += (s, e) => { renamingId = jj.Id; confirmDeleteId = -1; Rebuild(); };
                Put(g, edit, 2);

                FButton del;
                if (confirmDeleteId == j.Id)
                {
                    del = SubtleButton(T("Delete?", 12, pal.Red, FontWeights.SemiBold));
                    del.Padding = new Thickness(8, 6, 8, 6);
                    del.ToolTip = "Deletes this job and all its hours";
                    del.Click += (s, e) => { confirmDeleteId = -1; store.DeleteJob(jj); };
                }
                else
                {
                    del = IconButton(GDelete, 12, "Delete job");
                    del.Width = 30; del.Height = 30;
                    del.Click += (s, e) => { confirmDeleteId = jj.Id; renamingId = -1; Rebuild(); };
                }
                Put(g, del, 3);

                var row = SubtleButton(g, sel);
                row.Padding = new Thickness(4, 8, 6, 8);
                row.Margin = new Thickness(0, 1, 0, 1);
                row.Click += (s, e) =>
                {
                    jobsView = false; expanded = null; confirmDeleteId = -1; pendingView = true;
                    if (jj != job) store.SelectJob(jj); else Rebuild();
                };
                sp.Children.Add(row);
            }

            var add = StdButton(GAdd, "New job");
            add.Margin = new Thickness(4, 10, 4, 0);
            add.Click += (s, e) => { creatingJob = true; renamingId = -1; confirmDeleteId = -1; pendingView = true; Rebuild(); };
            sp.Children.Add(add);
            return sp;
        }

        UIElement BuildHero()
        {
            var card = Card();
            card.Margin = new Thickness(12, 0, 12, 0);
            card.Padding = new Thickness(16, 14, 16, 16);
            var sp = new StackPanel();

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            heroDot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            heroStatus = T("", 12, pal.Text2, FontWeights.Normal);
            row.Children.Add(heroDot);
            row.Children.Add(heroStatus);
            heroStatusRow = row;
            sp.Children.Add(row);

            heroTime = new TextBlock
            {
                FontFamily = fDisplay,
                FontSize = 46,
                FontWeight = FontWeights.SemiBold,
                Foreground = B(pal.Text),
                Margin = new Thickness(0, 0, 0, -2),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            TextOptions.SetTextFormattingMode(heroTime, TextFormattingMode.Ideal);
            Tabular(heroTime);
            sp.Children.Add(heroTime);

            FButton primary, secondary = null;
            switch (job.State)
            {
                case ClockState.Running:
                    primary = StdButton(GPause, "Pause");
                    primary.Click += (s, e) => job.Pause();
                    secondary = StdButton(GStop, "Stop");
                    secondary.Click += (s, e) => job.Stop();
                    break;
                case ClockState.Paused:
                    primary = AccentButton(GPlay, "Resume");
                    primary.Click += (s, e) => job.Start();
                    secondary = StdButton(GStop, "Stop");
                    secondary.Click += (s, e) => job.Stop();
                    break;
                default:
                    primary = AccentButton(GPlay, "Start work");
                    primary.Click += (s, e) => job.Start();
                    break;
            }
            Grid btns;
            if (secondary == null)
            {
                btns = Cols(Star);
                Put(btns, primary, 0);
            }
            else
            {
                btns = Cols(Star, Star);
                primary.Margin = new Thickness(0, 0, 4, 0);
                secondary.Margin = new Thickness(4, 0, 0, 0);
                Put(btns, primary, 0);
                Put(btns, secondary, 1);
            }
            btns.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(btns);
            card.Child = sp;
            return card;
        }

        /// <summary>Win11 SelectorBar-style Week / Month switch.</summary>
        UIElement BuildSelector()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 10, 12, 0) };
            var bars = new Border[2];
            int selIdx = monthMode ? 1 : 0;
            int i = 0;
            foreach (var mode in new[] { "Week", "Month" })
            {
                bool sel = (mode == "Month") == monthMode;
                var inner = new StackPanel();
                inner.Children.Add(T(mode, 14, sel ? pal.Text : pal.Text2, sel ? FontWeights.SemiBold : FontWeights.Normal));
                var bar = new Border
                {
                    Width = 16, Height = 3, CornerRadius = new CornerRadius(1.5),
                    Margin = new Thickness(0, 4, 0, 0),
                    Background = sel ? B(pal.Accent) : Brushes.Transparent
                };
                bars[i++] = bar;
                inner.Children.Add(bar);
                var b = SubtleButton(inner);
                b.Padding = new Thickness(10, 6, 10, 2);
                bool toMonth = mode == "Month";
                b.Click += (s, e) =>
                {
                    if (monthMode == toMonth) return;
                    monthMode = toMonth;
                    if (monthMode) monthStart = new DateTime(weekStart.AddDays(3).Year, weekStart.AddDays(3).Month, 1);
                    expanded = null;
                    pendingTime = toMonth ? TimeAnim.ToMonth : TimeAnim.ToWeek;
                    slideIndicator = true;
                    RefreshTimeArea();
                };
                sp.Children.Add(b);
            }

            // the accent pill glides (and stretches) from the old tab to the new one
            if (slideIndicator && IsVisible)
            {
                slideIndicator = false;
                var target = bars[selIdx];
                var from = bars[1 - selIdx];
                sp.Loaded += (s, e) =>
                {
                    double dx = from.TranslatePoint(new Point(0, 0), target).X;
                    var tt = new TranslateTransform(dx, 0);
                    var st = new ScaleTransform(1, 1);
                    var g = new TransformGroup();
                    g.Children.Add(st);
                    g.Children.Add(tt);
                    target.RenderTransformOrigin = new Point(0.5, 0.5);
                    target.RenderTransform = g;
                    tt.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(460)) { EasingFunction = Motion.Spring });
                    var stretch = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(460) };
                    stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
                    stretch.KeyFrames.Add(new EasingDoubleKeyFrame(2.6, KeyTime.FromPercent(0.35), Motion.Out));
                    stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), Motion.Spring));
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, stretch);
                };
            }
            slideIndicator = false;
            return sp;
        }

        UIElement BuildPeriodHeader()
        {
            var g = Cols(Star, Auto);
            g.Margin = new Thickness(16, 6, 8, 2);
            var left = new StackPanel();
            bool isCurrent;
            var today = DateTime.Today;
            if (monthMode)
            {
                var thisMonth = new DateTime(today.Year, today.Month, 1);
                isCurrent = monthStart == thisMonth;
                left.Children.Add(T(monthStart.ToString("MMMM yyyy", En), 14, pal.Text, FontWeights.SemiBold));
                int days = 0;
                for (var d = monthStart; d < monthStart.AddMonths(1); d = d.AddDays(1)) if (job.HasData(d)) days++;
                string sub = isCurrent ? "This month" : monthStart == thisMonth.AddMonths(-1) ? "Last month" : "";
                sub += (sub.Length > 0 ? " · " : "") + days + (days == 1 ? " day worked" : " days worked");
                left.Children.Add(T(sub, 12, pal.Text2, FontWeights.Normal));
            }
            else
            {
                var thisWeek = Fmt2.MondayOf(today);
                isCurrent = weekStart == thisWeek;
                string name = isCurrent ? "This week" : weekStart == thisWeek.AddDays(-7) ? "Last week"
                    : weekStart == thisWeek.AddDays(7) ? "Next week" : "Week " + Fmt2.IsoWeek(weekStart);
                left.Children.Add(T(name, 14, pal.Text, FontWeights.SemiBold));
                left.Children.Add(T(Fmt2.Range(weekStart, weekStart.AddDays(6)), 12, pal.Text2, FontWeights.Normal));
            }
            Put(g, left, 0);

            var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (!isCurrent)
            {
                var t = SubtleButton(T("Today", 12, pal.Accent, FontWeights.SemiBold));
                t.Padding = new Thickness(8, 6, 8, 6);
                t.Click += (s, e) =>
                {
                    var tw = Fmt2.MondayOf(DateTime.Today);
                    var tm = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                    bool forward = monthMode ? tm > monthStart : tw > weekStart;
                    weekStart = tw;
                    monthStart = tm;
                    expanded = null;
                    pendingTime = forward ? TimeAnim.Next : TimeAnim.Prev;
                    RefreshTimeArea();
                };
                nav.Children.Add(t);
            }
            var prev = IconButton(GLeft, 12, monthMode ? "Previous month" : "Previous week");
            prev.Click += (s, e) => { Shift(-1); };
            var next = IconButton(GRight, 12, monthMode ? "Next month" : "Next week");
            next.Click += (s, e) => { Shift(1); };
            nav.Children.Add(prev);
            nav.Children.Add(next);
            Put(g, nav, 1);
            return g;
        }

        void Shift(int dir)
        {
            if (monthMode) monthStart = monthStart.AddMonths(dir);
            else weekStart = weekStart.AddDays(7 * dir);
            expanded = null;
            pendingTime = dir > 0 ? TimeAnim.Next : TimeAnim.Prev;
            RefreshTimeArea();
        }

        UIElement BuildWeek()
        {
            var sp = new StackPanel { Margin = new Thickness(8, 4, 8, 0) };
            var now = DateTime.Now;
            for (int i = 0; i < 7; i++)
            {
                var day = weekStart.AddDays(i);
                bool isToday = day == DateTime.Today;
                bool isExp = expanded.HasValue && expanded.Value == day;

                var g = Cols(Px(3), Px(52), Star, Auto, Px(22));
                var ind = new Border
                {
                    Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5),
                    Background = isToday ? B(pal.Accent) : Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Put(g, ind, 0);
                var dn = T(day.ToString("ddd", En), 14, day > DateTime.Today ? pal.Text3 : pal.Text, isToday ? FontWeights.SemiBold : FontWeights.Normal);
                dn.Margin = new Thickness(10, 0, 0, 0);
                Put(g, dn, 1);
                Put(g, T(day.ToString("d MMM", En), 12, pal.Text2, FontWeights.Normal), 2);

                var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                if (isToday && job.State != ClockState.Stopped)
                {
                    right.Children.Add(new Ellipse
                    {
                        Width = 6, Height = 6, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                        Fill = B(job.State == ClockState.Running ? pal.Green : pal.Amber)
                    });
                }
                bool has = job.HasData(day);
                var tot = T(has ? Fmt2.Dur(job.Net(day, now)) : "—", 14, has ? pal.Text : pal.Text3, has ? FontWeights.SemiBold : FontWeights.Normal);
                Tabular(tot);
                dayTotals[day] = tot;
                right.Children.Add(tot);
                Put(g, right, 3);
                var chev = Glyph(GDown, 10, pal.Text3);
                chev.HorizontalAlignment = HorizontalAlignment.Right;
                chev.RenderTransformOrigin = new Point(0.5, 0.5);
                var rot = new RotateTransform(isExp ? 180 : 0);
                chev.RenderTransform = rot;
                Put(g, chev, 4);

                var row = SubtleButton(g, isExp);
                row.Padding = new Thickness(4, 9, 10, 9);
                row.Margin = new Thickness(0, 1, 0, 1);
                var host = new Border { ClipToBounds = true };
                if (isExp) host.Child = BuildDayDetail(day);
                dayCells[day] = new DayCell { Row = row, Host = host, Chev = rot };
                var d = day;
                row.Click += (s, e) => ToggleDay(d);
                sp.Children.Add(row);
                sp.Children.Add(host);
            }
            return sp;
        }

        UIElement BuildMonth()
        {
            var sp = new StackPanel { Margin = new Thickness(8, 4, 8, 0) };
            var now = DateTime.Now;
            var first = monthStart;
            var last = monthStart.AddMonths(1).AddDays(-1);
            var thisWeek = Fmt2.MondayOf(DateTime.Today);
            for (var w = Fmt2.MondayOf(first); w <= last; w = w.AddDays(7))
            {
                var from = w < first ? first : w;
                var to = w.AddDays(6) > last ? last : w.AddDays(6);
                var net = job.NetRange(from, to, now);
                bool has = false;
                for (var d = from; d <= to; d = d.AddDays(1)) if (job.HasData(d)) { has = true; break; }
                bool current = w == thisWeek;

                var g = Cols(Px(3), Px(72), Star, Auto, Px(22));
                Put(g, new Border
                {
                    Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5),
                    Background = current ? B(pal.Accent) : Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Center
                }, 0);
                var wn = T("Week " + Fmt2.IsoWeek(w), 14, w > DateTime.Today ? pal.Text3 : pal.Text, current ? FontWeights.SemiBold : FontWeights.Normal);
                wn.Margin = new Thickness(10, 0, 0, 0);
                Put(g, wn, 1);
                Put(g, T(from.Day == to.Day ? from.ToString("d MMM", En) : from.Day + "–" + to.ToString("d MMM", En), 12, pal.Text2, FontWeights.Normal), 2);

                var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
                var tot = T(has ? Fmt2.Dur(net) : "—", 14, has ? pal.Text : pal.Text3, has ? FontWeights.SemiBold : FontWeights.Normal);
                tot.HorizontalAlignment = HorizontalAlignment.Right;
                Tabular(tot);
                right.Children.Add(tot);
                if (has && job.HourlyRate > 0)
                {
                    var m = T(job.Money(net), 11, pal.Text2, FontWeights.Normal);
                    m.HorizontalAlignment = HorizontalAlignment.Right;
                    Tabular(m);
                    right.Children.Add(m);
                }
                Put(g, right, 3);
                var chev = Glyph(GRight, 10, pal.Text3);
                chev.HorizontalAlignment = HorizontalAlignment.Right;
                Put(g, chev, 4);

                var row = SubtleButton(g);
                row.Padding = new Thickness(4, 9, 10, 9);
                row.Margin = new Thickness(0, 1, 0, 1);
                row.ToolTip = "Open this week";
                var ws = w;
                row.Click += (s, e) =>
                {
                    monthMode = false; weekStart = ws; expanded = null;
                    pendingTime = TimeAnim.ZoomIn; slideIndicator = true;
                    RefreshTimeArea();
                };
                sp.Children.Add(row);
            }
            return sp;
        }

        UIElement BuildDayDetail(DateTime day)
        {
            var card = Card();
            card.Margin = new Thickness(4, 2, 4, 8);
            card.Padding = new Thickness(14, 6, 8, 12);
            var sp = new StackPanel();
            var now = DateTime.Now;

            // --- work sessions
            sp.Children.Add(Label("Work", 4));
            var sess = job.SessionsOn(day);
            if (sess.Count == 0) sp.Children.Add(Muted("Nothing recorded"));
            foreach (var s in sess)
            {
                var s0 = s;
                string a = s.Start.Date == day ? s.Start.ToString("HH:mm") : s.Start.ToString("ddd HH:mm", En);
                string b = !s.End.HasValue ? "now" : s.End.Value.Date == day ? s.End.Value.ToString("HH:mm") : s.End.Value.ToString("ddd HH:mm", En);
                var dur = (s.End ?? now) - s.Start;
                sp.Children.Add(ItemRow(a + " – " + b, Fmt2.Dur(dur), () => job.RemoveSession(s0),
                    s.End.HasValue ? "Delete this entry" : "Discard the running session"));
            }

            // add work manually
            var addWork = Cols(Auto, Auto, Auto, Auto, Star);
            addWork.Margin = new Thickness(0, 6, 6, 0);
            TextBox fromBox, toBox;
            var fromFrame = Input("09:00", 64, out fromBox);
            var toFrame = Input("17:00", 64, out toBox);
            var dash = T("–", 13, pal.Text2, FontWeights.Normal);
            dash.Margin = new Thickness(6, 0, 6, 0);
            var addW = StdButton(GAdd, "Add time", 12);
            addW.Margin = new Thickness(8, 0, 0, 0);
            Action doAddWork = () =>
            {
                TimeSpan f, t;
                bool okF = ParseTime(fromBox.Text, out f), okT = ParseTime(toBox.Text, out t);
                if (!okF) Flag(fromFrame);
                if (!okT || (okF && t <= f)) Flag(toFrame);
                if (okF && okT && t > f) job.AddSession(day + f, day + t);
            };
            addW.Click += (s, e) => doAddWork();
            toBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) doAddWork(); };
            Put(addWork, fromFrame, 0);
            Put(addWork, dash, 1);
            Put(addWork, toFrame, 2);
            Put(addWork, addW, 3);
            sp.Children.Add(addWork);

            // --- breaks
            sp.Children.Add(Label("Breaks", 12));
            var brs = job.BreaksOn(day);
            if (brs.Count == 0) sp.Children.Add(Muted("No breaks"));
            foreach (var br in brs)
            {
                var b0 = br;
                sp.Children.Add(ItemRow("Break", "−" + Fmt2.Dur(TimeSpan.FromMinutes(br.Minutes)), () => job.RemoveBreak(b0), "Remove break"));
            }

            var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var m in new[] { 15, 30, 45, 60 })
            {
                var mm = m;
                var c = StdButton(null, m == 60 ? "+1h" : "+" + m + "m", 12);
                c.Margin = new Thickness(0, 0, 6, 6);
                c.Click += (s, e) => job.AddBreak(day, mm);
                chips.Children.Add(c);
            }
            TextBox minBox;
            var minFrame = Input("min", 56, out minBox);
            minFrame.Margin = new Thickness(0, 0, 6, 6);
            var addB = StdButton(GAdd, null, 12);
            addB.Margin = new Thickness(0, 0, 0, 6);
            addB.ToolTip = "Add a break (minutes, or h:mm)";
            Action doAddBreak = () =>
            {
                int minutes = ParseMinutes(minBox.Text);
                if (minutes <= 0) { Flag(minFrame); return; }
                job.AddBreak(day, minutes);
            };
            addB.Click += (s, e) => doAddBreak();
            minBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) doAddBreak(); };
            chips.Children.Add(minFrame);
            chips.Children.Add(addB);
            sp.Children.Add(chips);

            // --- summary
            var worked = job.Worked(day, now);
            int bm = job.BreakMinutes(day);
            var net = job.Net(day, now);
            string summary = "Worked " + Fmt2.Dur(worked) + "  ·  breaks " + Fmt2.Dur(TimeSpan.FromMinutes(bm)) + "  ·  total " + Fmt2.Dur(net);
            if (job.HourlyRate > 0) summary += "  ·  " + job.Money(net);
            var sum = T(summary, 12, pal.Text2, FontWeights.Normal);
            sum.TextWrapping = TextWrapping.Wrap;
            sum.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(sum);

            card.Child = sp;
            return card;
        }

        TextBlock Muted(string text)
        {
            var t = T(text, 13, pal.Text3, FontWeights.Normal);
            t.Margin = new Thickness(0, 2, 0, 2);
            return t;
        }

        UIElement ItemRow(string left, string right, Action onDelete, string tip)
        {
            var g = Cols(Star, Auto, Px(30));
            var l = T(left, 13, pal.Text, FontWeights.Normal);
            Tabular(l);
            Put(g, l, 0);
            var r = T(right, 13, pal.Text2, FontWeights.Normal);
            Tabular(r);
            r.Margin = new Thickness(0, 0, 4, 0);
            Put(g, r, 1);
            var del = IconButton(GDelete, 12, tip);
            del.Width = 28; del.Height = 26;
            del.Click += (s, e) => onDelete();
            Put(g, del, 2);
            return g;
        }

        UIElement BuildExport()
        {
            var g = Cols(Star, Star);
            g.Margin = new Thickness(12, 8, 12, 0);
            var ex = new FButton(pal.Control, pal.ControlHover, pal.ControlPressed, pal.ControlStroke, null);
            var exContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var exIcon = Glyph(GTable, 12, pal.Text);
            exIcon.Margin = new Thickness(0, 0, 8, 0);
            exContent.Children.Add(exIcon);
            exContent.Children.Add(T("Export", 12, pal.Text, FontWeights.Normal));
            var exChev = Glyph(GDown, 9, pal.Text2);
            exChev.Margin = new Thickness(8, 1, 0, 0);
            exContent.Children.Add(exChev);
            ex.Child = exContent;
            ex.Padding = new Thickness(10, 6, 10, 6);
            ex.Margin = new Thickness(0, 0, 4, 0);
            ex.ToolTip = "Save this " + (monthMode ? "month" : "week") + " as a file";

            // floating dropdown: overlays the panel instead of pushing content down
            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = ex,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                AllowsTransparency = true,
                StaysOpen = false,
                PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
                HorizontalOffset = -12,
                VerticalOffset = -8
            };
            var items = new StackPanel();
            items.Children.Add(ExportItem("Spreadsheet (.xlsx)", "Formatted sheet with totals · opens in Excel, Google Sheets, Numbers",
                () => { popup.IsOpen = false; ExportExcel(); }));
            items.Children.Add(ExportItem("CSV (.csv)", "Plain data · for importing into other tools",
                () => { popup.IsOpen = false; ExportCsv(); }));
            var menu = new Border
            {
                Width = 260,
                Margin = new Thickness(12),           // room for the shadow
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(8),
                Background = B(pal.Dark ? Palette.C(0xFF2C2C2C) : Palette.C(0xFFF9F9F9)),
                BorderBrush = B(pal.Dark ? Palette.C(0x33000000) : Palette.C(0x1A000000)),
                BorderThickness = new Thickness(1),
                Child = items,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = pal.Dark ? 0.5 : 0.18, Color = Colors.Black
                }
            };
            TextElement.SetFontFamily(menu, fText);
            popup.Child = menu;
            ex.Click += (s, e) => { popup.IsOpen = !popup.IsOpen; };

            var no = StdButton(GCopy, "Copy for Notion", 12);
            no.Margin = new Thickness(4, 0, 0, 0);
            no.ToolTip = "Copies a table — paste it into any Notion page";
            no.Click += (s, e) => CopyNotion();
            Put(g, ex, 0);
            Put(g, no, 1);
            g.Children.Add(popup);
            return g;
        }

        UIElement ExportItem(string title, string sub, Action click)
        {
            var sp = new StackPanel();
            sp.Children.Add(T(title, 13, pal.Text, FontWeights.SemiBold));
            var st = T(sub, 11, pal.Text2, FontWeights.Normal);
            st.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(st);
            var b = SubtleButton(sp);
            b.Padding = new Thickness(10, 7, 10, 7);
            b.Click += (s, e) => click();
            return b;
        }

        void ExportCsv()
        {
            DateTime from, to; string title, fileBase;
            PeriodInfo(out from, out to, out title, out fileBase);
            try
            {
                var path = Exporter.Csv(job, from, to, fileBase);
                HideFlyout();
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex)
            {
                ctl.Notify("Export failed", ex.Message);
            }
        }

        void PeriodInfo(out DateTime from, out DateTime to, out string title, out string fileBase)
        {
            if (monthMode)
            {
                from = monthStart; to = monthStart.AddMonths(1).AddDays(-1);
                title = "Work hours — " + monthStart.ToString("MMMM yyyy", En);
                fileBase = "Clocked In " + SafeName(job.Name) + " " + monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            }
            else
            {
                from = weekStart; to = weekStart.AddDays(6);
                int wk = Fmt2.IsoWeek(weekStart);
                int yr = weekStart.AddDays(3).Year;
                title = "Work hours — Week " + wk + " (" + Fmt2.Range(from, to) + ")";
                fileBase = "Clocked In " + SafeName(job.Name) + " " + yr + "-W" + wk.ToString("00");
            }
            title = job.Name + " · " + title;
        }

        static string SafeName(string s)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
            s = s.Trim();
            return s.Length == 0 ? "Job" : s;
        }

        void ExportExcel()
        {
            DateTime from, to; string title, fileBase;
            PeriodInfo(out from, out to, out title, out fileBase);
            try
            {
                var path = Exporter.Xlsx(job, from, to, title, fileBase);
                HideFlyout();
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ctl.Notify("Export failed", ex.Message);
            }
        }

        void CopyNotion()
        {
            DateTime from, to; string title, fileBase;
            PeriodInfo(out from, out to, out title, out fileBase);
            var md = Exporter.Markdown(job, from, to, title);
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(md); break; }
                catch { System.Threading.Thread.Sleep(60); }
            }
            ctl.Notify("Copied for Notion", "Paste into any Notion page (Ctrl+V) and it becomes a table.");
        }

        UIElement BuildSettings()
        {
            var sp = new StackPanel { Margin = new Thickness(12, 0, 12, 12) };

            // theme
            var c1 = Card();
            c1.Padding = new Thickness(14, 8, 14, 14);
            var s1 = new StackPanel();
            s1.Children.Add(Label("Theme", 4));
            var seg = Cols(Star, Star, Star, Star);
            int i = 0;
            foreach (var name in new[] { "System", "Light", "Dark", "Glass" })
            {
                bool sel = store.Theme == name;
                FButton b;
                if (sel)
                    b = new FButton(Palette.Alpha(pal.Accent, 0x30), Palette.Alpha(pal.Accent, 0x40), Palette.Alpha(pal.Accent, 0x26), pal.Accent,
                        IconLabel(null, name, pal.Text, 12));
                else
                    b = StdButton(null, name, 12);
                b.Padding = new Thickness(4, 6, 4, 6);
                b.Margin = new Thickness(i == 0 ? 0 : 3, 0, i == 3 ? 0 : 3, 0);
                var n = name;
                b.Click += (s, e) => store.SetTheme(n);
                Put(seg, b, i++);
            }
            s1.Children.Add(seg);
            c1.Child = s1;
            sp.Children.Add(c1);

            // pay
            var c2 = Card();
            c2.Margin = new Thickness(0, 8, 0, 0);
            c2.Padding = new Thickness(14, 8, 14, 12);
            var s2 = new StackPanel();
            var pj = job;
            s2.Children.Add(Label("Pay · " + pj.Name, 4));
            TextBox rateBox, curBox;
            var rateFrame = Input("0.00", 84, out rateBox);
            var curFrame = Input("kr", 52, out curBox);
            curFrame.Margin = new Thickness(6, 0, 0, 0);
            if (pj.HourlyRate > 0) rateBox.Text = pj.HourlyRate.ToString("0.##", CultureInfo.InvariantCulture);
            curBox.Text = pj.Currency;
            Action saveRate = () =>
            {
                double rate;
                if (!Fmt2.TryParseAmount(rateBox.Text, out rate))
                {
                    Flag(rateFrame);
                    return;
                }
                string cur = curBox.Text.Trim();
                if (rate != pj.HourlyRate || cur != pj.Currency)
                    Dispatcher.BeginInvoke(new Action(() => pj.SetRate(rate, cur)));
            };
            rateBox.LostKeyboardFocus += (s, e) => saveRate();
            curBox.LostKeyboardFocus += (s, e) => saveRate();
            rateBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) saveRate(); };
            curBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) saveRate(); };
            var rateRight = new StackPanel { Orientation = Orientation.Horizontal };
            rateRight.Children.Add(rateFrame);
            rateRight.Children.Add(curFrame);
            s2.Children.Add(SettingRow("Hourly rate", rateRight, true));
            var hint = T("Shows earnings for the week and month. Leave empty to hide.", 11, pal.Text3, FontWeights.Normal);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 6, 0, 0);
            s2.Children.Add(hint);
            c2.Child = s2;
            sp.Children.Add(c2);

            // reminders
            var c3 = Card();
            c3.Margin = new Thickness(0, 8, 0, 0);
            c3.Padding = new Thickness(14, 8, 14, 12);
            var s3 = new StackPanel();
            s3.Children.Add(Label("Reminders", 4));
            double rh = store.RemindHours;
            s3.Children.Add(SettingRow("Remind me to stop after",
                Stepper(rh <= 0 ? "Off" : rh.ToString("0.#", CultureInfo.CurrentCulture) + " h",
                    () => store.SetRemindHours(rh <= 1 ? 0 : rh - 0.5),
                    () => store.SetRemindHours(rh <= 0 ? 1 : Math.Min(16, rh + 0.5))), true));
            int pm = store.RemindPausedMinutes;
            s3.Children.Add(SettingRow("Remind me when paused for",
                Stepper(pm <= 0 ? "Off" : pm + " min",
                    () => store.SetRemindPaused(pm <= 5 ? 0 : pm - 5),
                    () => store.SetRemindPaused(Math.Min(240, pm + 5))), false));
            c3.Child = s3;
            sp.Children.Add(c3);

            // do not disturb
            var c5 = Card();
            c5.Margin = new Thickness(0, 8, 0, 0);
            c5.Padding = new Thickness(14, 12, 14, 12);
            var s5 = new StackPanel();
            s5.Children.Add(SettingRow("Do not disturb while working",
                Toggle(store.DndWhileWorking, v => store.SetDndWhileWorking(v)), true));
            var dndHint = T("Turns on Windows Do Not Disturb while the clock runs, and off again when you pause or stop. "
                + "If you turned it on yourself, it's left alone.", 11, pal.Text3, FontWeights.Normal);
            dndHint.TextWrapping = TextWrapping.Wrap;
            dndHint.Margin = new Thickness(0, 4, 0, 0);
            s5.Children.Add(dndHint);
            c5.Child = s5;
            sp.Children.Add(c5);

            // startup
            var c4 = Card();
            c4.Margin = new Thickness(0, 8, 0, 0);
            c4.Padding = new Thickness(14, 12, 14, 12);
            bool on = AppController.StartupEnabled;
            var s4 = new StackPanel();
            s4.Children.Add(SettingRow("Start with Windows", Toggle(on, v => { AppController.SetStartup(v); Rebuild(); }), true));
            s4.Children.Add(SettingRow("Open shortcut",
                T(HotkeyRegistered ? HotkeyText : "In use by another app", 13, HotkeyRegistered ? pal.Text2 : pal.Red, FontWeights.Normal), false));
            bool custom = store.WinWidth > 0 && store.WinHeight > 0;
            UIElement sizeRight;
            if (custom)
            {
                var reset = StdButton(null, "Reset", 12);
                reset.Click += (s, e) =>
                {
                    Width = 360;
                    SizeToContent = SizeToContent.Height;
                    store.ResetWindowSize();
                };
                sizeRight = reset;
            }
            else
            {
                sizeRight = T("Automatic", 13, pal.Text2, FontWeights.Normal);
            }
            s4.Children.Add(SettingRow("Window size", sizeRight, false));
            var sizeHint = T(custom ? "Your size is remembered. Reset to fit the content again."
                                    : "Drag any edge or corner of the window to resize it.", 11, pal.Text3, FontWeights.Normal);
            sizeHint.TextWrapping = TextWrapping.Wrap;
            sizeHint.Margin = new Thickness(0, 4, 0, 0);
            s4.Children.Add(sizeHint);
            c4.Child = s4;
            sp.Children.Add(c4);
            return sp;
        }

        UIElement SettingRow(string label, UIElement right, bool first)
        {
            var g = Cols(Star, Auto);
            g.Margin = new Thickness(0, first ? 0 : 8, 0, 0);
            var t = T(label, 13, pal.Text, FontWeights.Normal);
            t.TextWrapping = TextWrapping.Wrap;
            Put(g, t, 0);
            Put(g, right, 1);
            return g;
        }

        UIElement Stepper(string value, Action minus, Action plus)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var m = IconButton(GMinus, 11, null);
            m.Width = 28; m.Height = 28;
            m.Click += (s, e) => minus();
            var v = T(value, 13, pal.Text, FontWeights.SemiBold);
            v.Width = 58;
            v.TextAlignment = TextAlignment.Center;
            var p = IconButton(GAdd, 11, null);
            p.Width = 28; p.Height = 28;
            p.Click += (s, e) => plus();
            sp.Children.Add(m);
            sp.Children.Add(v);
            sp.Children.Add(p);
            return sp;
        }

        UIElement Toggle(bool on, Action<bool> changed)
        {
            var track = new Border
            {
                Width = 40, Height = 20, CornerRadius = new CornerRadius(10),
                Background = on ? B(pal.Accent) : B(Colors.Transparent),
                BorderBrush = on ? B(pal.Accent) : B(pal.Text2),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            track.Child = new Ellipse
            {
                Width = 12, Height = 12,
                Fill = on ? B(pal.OnAccent) : B(pal.Text2),
                HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 3, 0)
            };
            track.MouseLeftButtonUp += (s, e) => changed(!on);
            return track;
        }

        UIElement BuildFooter()
        {
            var b = new Border
            {
                BorderBrush = B(pal.Divider),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(16, 10, 16, 12)
            };
            var g = Cols(Star, Auto);
            if (settings)
            {
                Put(g, T("Data is saved in %APPDATA%\\WorkHours", 11, pal.Text3, FontWeights.Normal), 0);
            }
            else
            {
                g.RowDefinitions.Add(new RowDefinition());
                g.RowDefinitions.Add(new RowDefinition());

                Put(g, T("Total time", 13, pal.Text2, FontWeights.Normal), 0);
                totalText = T("", 16, pal.Text, FontWeights.SemiBold);
                totalText.HorizontalAlignment = HorizontalAlignment.Right;
                Tabular(totalText);
                Put(g, totalText, 1);

                // "Total pay" only when this job has an hourly rate
                totalMoney = null;
                if (job.HourlyRate > 0)
                {
                    var payLabel = T("Total pay", 13, pal.Text2, FontWeights.Normal);
                    payLabel.Margin = new Thickness(0, 4, 0, 0);
                    Grid.SetRow(payLabel, 1);
                    Put(g, payLabel, 0);
                    totalMoney = T("", 16, pal.Text, FontWeights.SemiBold);
                    Tabular(totalMoney);
                    totalMoney.Margin = new Thickness(0, 4, 0, 0);
                    totalMoney.HorizontalAlignment = HorizontalAlignment.Right;
                    Grid.SetRow(totalMoney, 1);
                    Put(g, totalMoney, 1);
                }
            }
            b.Child = g;
            return b;
        }

        // =============================================================== live refresh (1 s)

        public void UpdateLive()
        {
            if (heroTime == null) return;
            var now = DateTime.Now;
            var today = now.Date;
            var net = job.Net(today, now);
            heroTime.Text = string.Format("{0}:{1:00}:{2:00}", (int)net.TotalHours, net.Minutes, net.Seconds);

            Color dot;
            string status;
            switch (job.State)
            {
                case ClockState.Running:
                    var open = job.OpenSession;
                    bool over = store.RemindHours > 0 && net.TotalHours >= store.RemindHours;
                    dot = over ? pal.Red : pal.Green;
                    status = over
                        ? "Working · past " + store.RemindHours.ToString("0.#", CultureInfo.CurrentCulture) + " h — remember to stop"
                        : "Working · since " + (open != null ? open.Start.ToString("HH:mm") : "");
                    break;
                case ClockState.Paused:
                    dot = pal.Amber;
                    status = "Paused for " + Fmt2.Dur(now - job.PausedAt) + " · since " + job.PausedAt.ToString("HH:mm");
                    break;
                default:
                    dot = pal.Neutral;
                    var act = store.Active;
                    if (act != null && act != job && act.State != ClockState.Stopped)
                        status = act.Name + " is " + (act.State == ClockState.Running ? "running" : "paused");
                    else
                        status = "";   // idle: no status line
                    break;
            }
            heroDot.Fill = B(dot);
            heroStatus.Text = status;
            if (heroStatusRow != null)
                heroStatusRow.Visibility = status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            TextBlock tb;
            if (dayTotals.TryGetValue(today, out tb) && job.HasData(today))
            {
                tb.Text = Fmt2.Dur(net);
                tb.Foreground = B(pal.Text);
                tb.FontWeight = FontWeights.SemiBold;
            }

            if (totalText != null)
            {
                TimeSpan sum = monthMode
                    ? job.NetRange(monthStart, monthStart.AddMonths(1).AddDays(-1), now)
                    : job.NetRange(weekStart, weekStart.AddDays(6), now);
                totalText.Text = Fmt2.Dur(sum);
                if (totalMoney != null) totalMoney.Text = job.Money(sum);
            }
        }

        // =============================================================== parsing

        static bool ParseTime(string s, out TimeSpan t)
        {
            t = TimeSpan.Zero;
            s = (s ?? "").Trim().Replace('.', ':').Replace(',', ':');
            if (s.Length == 0) return false;
            int h, m = 0;
            if (s.Contains(":"))
            {
                var p = s.Split(':');
                if (!int.TryParse(p[0], out h) || !int.TryParse(p[1], out m)) return false;
            }
            else
            {
                if (!int.TryParse(s, out h)) return false;
                if (s.Length >= 3) { m = h % 100; h = h / 100; }
            }
            if (h < 0 || h > 24 || m < 0 || m > 59 || (h == 24 && m > 0)) return false;
            t = new TimeSpan(h, m, 0);
            return true;
        }

        static int ParseMinutes(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return 0;
            TimeSpan t;
            if (s.Contains(":") && ParseTime(s, out t)) return (int)t.TotalMinutes;
            if (s.EndsWith("h"))
            {
                double h;
                if (double.TryParse(s.TrimEnd('h').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out h)) return (int)Math.Round(h * 60);
                return 0;
            }
            int m;
            if (int.TryParse(s.TrimEnd('m'), out m) && m > 0 && m <= 1440) return m;
            return 0;
        }
    }
}
