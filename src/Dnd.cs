using System;
using System.Runtime.InteropServices;

namespace WorkHours
{
    /// <summary>
    /// Windows Do Not Disturb (the bell in the notification center). Windows has no public API for it,
    /// so this uses the shell's quiet-hours state (WNF), the same value the notification center toggles.
    /// </summary>
    static class Dnd
    {
        const ulong WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED = 0x0D83063EA3BF1C75;
        const int Off = 0, On = 1;   // 0 = everyone may notify, 1 = priority only (Do Not Disturb)

        [DllImport("ntdll.dll")]
        static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr explicitScope,
            out uint changeStamp, byte[] buffer, ref uint bufferSize);

        [DllImport("ntdll.dll")]
        static extern int NtUpdateWnfStateData(ref ulong stateName, byte[] buffer, int length, IntPtr typeId,
            IntPtr explicitScope, uint matchingChangeStamp, uint checkStamp);

        /// <summary>Current profile value, or null if it can't be read.</summary>
        public static int? Read()
        {
            try
            {
                ulong name = WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED;
                uint stamp, size = 16;
                var buf = new byte[16];
                if (NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out stamp, buf, ref size) != 0 || size < 4) return null;
                return BitConverter.ToInt32(buf, 0);
            }
            catch { return null; }
        }

        public static bool IsOn { get { var v = Read(); return v.HasValue && v.Value != Off; } }

        /// <summary>Turns DND on/off. Returns true if the state now matches.</summary>
        public static bool Set(bool on)
        {
            try
            {
                ulong name = WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED;
                var buf = BitConverter.GetBytes(on ? On : Off);
                NtUpdateWnfStateData(ref name, buf, buf.Length, IntPtr.Zero, IntPtr.Zero, 0, 0);
                return IsOn == on;
            }
            catch { return false; }
        }
    }
}
