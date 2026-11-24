using System;
using System.Windows.Media;
using Microsoft.Win32;

namespace WorkHours
{
    /// <summary>Windows 11 (Fluent) colour tokens for the three themes.</summary>
    public class Palette
    {
        public bool Dark, Glass;
        public Color Bg, Card, CardStroke, SubtleHover, SubtlePressed,
            Control, ControlHover, ControlPressed, ControlStroke,
            Text, Text2, Text3, Divider,
            Accent, OnAccent, Green, Amber, Red, Neutral;

        public static Color C(uint argb)
        {
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        public static Color Alpha(Color c, byte a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        static object Reg(string key, string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(key))
                    return k == null ? null : k.GetValue(name);
            }
            catch { return null; }
        }

        const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public static bool AppsDark()
        {
            var v = Reg(PersonalizeKey, "AppsUseLightTheme");
            return v is int && (int)v == 0;
        }

        public static bool TaskbarDark()
        {
            var v = Reg(PersonalizeKey, "SystemUsesLightTheme");
            return !(v is int) || (int)v == 0;
        }

        static Color? AccentShade(int index)
        {
            var b = Reg(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette") as byte[];
            if (b == null || b.Length < (index + 1) * 4) return null;
            return Color.FromRgb(b[index * 4], b[index * 4 + 1], b[index * 4 + 2]);
        }

        public static Palette Create(string theme)
        {
            var p = new Palette();
            p.Glass = theme == "Glass";
            p.Dark = theme == "Dark" || ((theme == "System" || theme == "Glass") && AppsDark());

            if (p.Dark)
            {
                p.Bg = p.Glass ? C(0xA6141414) : C(0xFF202020);
                p.Card = p.Glass ? C(0x14FFFFFF) : C(0x0DFFFFFF);
                p.CardStroke = p.Glass ? C(0x14FFFFFF) : C(0x30000000);
                p.SubtleHover = C(0x0FFFFFFF);
                p.SubtlePressed = C(0x0AFFFFFF);
                p.Control = C(0x0FFFFFFF);
                p.ControlHover = C(0x18FFFFFF);
                p.ControlPressed = C(0x08FFFFFF);
                p.ControlStroke = C(0x14FFFFFF);
                p.Text = C(0xFFFFFFFF);
                p.Text2 = C(0xC5FFFFFF);
                p.Text3 = C(0x87FFFFFF);
                p.Divider = C(0x18FFFFFF);
                p.Accent = AccentShade(1) ?? C(0xFF60CDFF);
                p.OnAccent = C(0xFF000000);
                p.Green = C(0xFF6CCB5F);
                p.Amber = C(0xFFFCE100);
                p.Red = C(0xFFFF99A4);
                p.Neutral = C(0x87FFFFFF);
            }
            else
            {
                p.Bg = p.Glass ? C(0x99F3F3F3) : C(0xFFF3F3F3);
                p.Card = p.Glass ? C(0x99FFFFFF) : C(0xB3FFFFFF);
                p.CardStroke = C(0x0F000000);
                p.SubtleHover = C(0x09000000);
                p.SubtlePressed = C(0x06000000);
                p.Control = C(0xB3FFFFFF);
                p.ControlHover = C(0x80F9F9F9);
                p.ControlPressed = C(0x4DF9F9F9);
                p.ControlStroke = C(0x24000000);
                p.Text = C(0xE4000000);
                p.Text2 = C(0x9E000000);
                p.Text3 = C(0x72000000);
                p.Divider = C(0x14000000);
                p.Accent = AccentShade(4) ?? C(0xFF005FB8);
                p.OnAccent = C(0xFFFFFFFF);
                p.Green = C(0xFF0F7B0F);
                p.Amber = C(0xFF9D5D00);
                p.Red = C(0xFFC42B1C);
                p.Neutral = C(0x72000000);
            }
            return p;
        }
    }
}
