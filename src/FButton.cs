using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WorkHours
{
    /// <summary>Lightweight Fluent-style button: rounded border with rest / hover / pressed fills.</summary>
    public class FButton : Border
    {
        public event EventHandler Click;
        Brush normal;
        readonly Brush hover, pressed;
        bool down;

        public FButton(Color n, Color h, Color p, Color? stroke, UIElement child)
        {
            normal = Freeze(new SolidColorBrush(n));
            hover = Freeze(new SolidColorBrush(h));
            pressed = Freeze(new SolidColorBrush(p));
            Background = normal;
            CornerRadius = new CornerRadius(4);
            if (stroke.HasValue)
            {
                BorderBrush = Freeze(new SolidColorBrush(stroke.Value));
                BorderThickness = new Thickness(1);
            }
            Padding = new Thickness(12, 6, 12, 6);
            Child = child;
            Focusable = false;
        }

        static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        /// <summary>Change the resting fill (e.g. selected / unselected) without rebuilding.</summary>
        public void SetRest(Color c)
        {
            normal = Freeze(new SolidColorBrush(c));
            if (!down && !IsMouseOver) Background = normal;
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            if (!down) Background = hover;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (!down) Background = normal;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            down = true;
            Background = pressed;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!down) return;
            down = false;
            ReleaseMouseCapture();
            var pt = e.GetPosition(this);
            bool inside = pt.X >= 0 && pt.Y >= 0 && pt.X <= ActualWidth && pt.Y <= ActualHeight;
            Background = inside ? hover : normal;
            e.Handled = true;
            if (inside && Click != null) Click(this, EventArgs.Empty);
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (down) { down = false; Background = normal; }
        }
    }
}
