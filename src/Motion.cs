using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace WorkHours
{
    /// <summary>Critically-damped-ish spring (≈2% overshoot), the Apple-style "settle" curve.</summary>
    public class SpringEase : EasingFunctionBase
    {
        public SpringEase() { EasingMode = EasingMode.EaseIn; }

        protected override double EaseInCore(double t)
        {
            if (t >= 1) return 1;
            const double w = 9.5, z = 0.78;
            double wd = w * Math.Sqrt(1 - z * z);
            return 1 - Math.Exp(-z * w * t) * (Math.Cos(wd * t) + (z * w / wd) * Math.Sin(wd * t));
        }

        protected override Freezable CreateInstanceCore() { return new SpringEase(); }
    }

    /// <summary>Small helpers for slide / scale / fade / blur transitions.</summary>
    public static class Motion
    {
        public static readonly IEasingFunction Spring = Frozen(new SpringEase());
        public static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
        public static readonly IEasingFunction In = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
        public static readonly IEasingFunction InOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });

        static IEasingFunction Frozen(EasingFunctionBase e) { e.Freeze(); return e; }
        static Duration D(double ms) { return new Duration(TimeSpan.FromMilliseconds(ms)); }

        static DoubleAnimation A(double from, double to, double ms, IEasingFunction ease)
        {
            return new DoubleAnimation(from, to, D(ms)) { EasingFunction = ease };
        }

        /// <summary>Element arrives: slides/scales from an offset to rest, fades in and un-blurs.</summary>
        public static void Enter(FrameworkElement e, double fromX, double fromY, double fromScale, double ms = 440, double blur = 8)
        {
            var st = new ScaleTransform(fromScale, fromScale);
            var tt = new TranslateTransform(fromX, fromY);
            var g = new TransformGroup();
            g.Children.Add(st);
            g.Children.Add(tt);
            e.RenderTransformOrigin = new Point(0.5, 0.25);
            e.RenderTransform = g;

            if (fromX != 0) tt.BeginAnimation(TranslateTransform.XProperty, A(fromX, 0, ms, Spring));
            if (fromY != 0) tt.BeginAnimation(TranslateTransform.YProperty, A(fromY, 0, ms, Spring));
            if (fromScale != 1)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, A(fromScale, 1, ms, Spring));
                st.BeginAnimation(ScaleTransform.ScaleYProperty, A(fromScale, 1, ms, Spring));
            }
            e.BeginAnimation(UIElement.OpacityProperty, A(0, 1, ms * 0.55, Out));

            if (blur > 0)
            {
                var fx = new BlurEffect { Radius = blur, RenderingBias = RenderingBias.Performance };
                e.Effect = fx;
                var ba = A(blur, 0, ms * 0.5, Out);
                ba.Completed += (s, a) => { if (e.Effect == fx) e.Effect = null; };   // back to crisp text
                fx.BeginAnimation(BlurEffect.RadiusProperty, ba);
            }
        }

        /// <summary>Element leaves: accelerates toward an offset, fades out and blurs. Calls done at the end.</summary>
        public static void Exit(FrameworkElement e, double toX, double toY, double toScale, double ms = 260, double blur = 8, Action done = null)
        {
            e.IsHitTestVisible = false;
            var st = new ScaleTransform(1, 1);
            var tt = new TranslateTransform(0, 0);
            var g = new TransformGroup();
            g.Children.Add(st);
            g.Children.Add(tt);
            e.RenderTransformOrigin = new Point(0.5, 0.25);
            e.RenderTransform = g;

            if (toX != 0) tt.BeginAnimation(TranslateTransform.XProperty, A(0, toX, ms, In));
            if (toY != 0) tt.BeginAnimation(TranslateTransform.YProperty, A(0, toY, ms, In));
            if (toScale != 1)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, A(1, toScale, ms, In));
                st.BeginAnimation(ScaleTransform.ScaleYProperty, A(1, toScale, ms, In));
            }
            if (blur > 0)
            {
                var fx = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
                e.Effect = fx;
                fx.BeginAnimation(BlurEffect.RadiusProperty, A(0, blur, ms, In));
            }
            var fade = A(e.Opacity, 0, ms, In);
            if (done != null) fade.Completed += (s, a) => done();
            e.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        /// <summary>Animates Height, then hands control back to layout (Height = Auto) unless superseded.</summary>
        public static void Height(FrameworkElement e, double from, double to, double ms, IEasingFunction ease, Action done = null, bool autoAfter = true)
        {
            int token = (e.Tag is int ? (int)e.Tag : 0) + 1;
            e.Tag = token;
            var a = A(from, to, ms, ease);
            a.Completed += (s, x) =>
            {
                if (!(e.Tag is int) || (int)e.Tag != token) return;   // a newer animation took over
                if (autoAfter)
                {
                    e.BeginAnimation(FrameworkElement.HeightProperty, null);
                    e.Height = double.NaN;
                }
                if (done != null) done();
            };
            e.BeginAnimation(FrameworkElement.HeightProperty, a);
        }

        public static void Rotate(RotateTransform r, double to, double ms = 380)
        {
            r.BeginAnimation(RotateTransform.AngleProperty, A(r.Angle, to, ms, Spring));
        }
    }
}
