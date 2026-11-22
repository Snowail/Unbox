using System;
using System.Media;
using System.Runtime.InteropServices;

namespace UnzipProxy.Core
{
    public static class SystemEffects
    {
        public static Task PlayFinishedSound() => Task.Run(() =>
        {
            try
            {
                using var wave = typeof(SystemEffects).Assembly.GetManifestResourceStream("Unbox.Finished.wav");
                if (wave == null) return;
                using var player = new SoundPlayer(wave);
                player.PlaySync();
            }
            catch
            {
                // Sound playback does not change the delivery result.
            }
        });

        // -------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        private const uint FLASHW_STOP = 0;
        private const uint FLASHW_ALL = 3;           // 闪烁标题栏和任务栏按钮
        private const uint FLASHW_TIMERNOFG = 12;     // 持续闪烁直到窗口置于前台

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        public static void FlashWindowForError(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                var fInfo = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                    hwnd = hwnd,
                    dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                    uCount = uint.MaxValue,
                    dwTimeout = 0
                };
                FlashWindowEx(ref fInfo);
            }
            catch { }
        }

        public static void StopFlashing(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                var fInfo = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                    hwnd = hwnd,
                    dwFlags = FLASHW_STOP,
                    uCount = 0,
                    dwTimeout = 0
                };
                FlashWindowEx(ref fInfo);
            }
            catch { }
        }
    }
}
