using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UnzipProxy.Core
{
    public static class SystemEffects
    {
        // -------------------------------------------------------------
        // 1. 音效播放 (MCI API，支持任何 MP3 文件，极简低延迟)
        // -------------------------------------------------------------
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwndCallback);

        public static void PlayFinishedSound(string? searchDirectory = null)
        {
            try
            {
                string? mp3Path = null;
                // 1. 尝试当前应用程序目录
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string p1 = Path.Combine(appDir, "Finished.mp3");
                if (File.Exists(p1)) mp3Path = p1;

                // 2. 尝试传入的搜索目录或上级目录
                if (mp3Path == null && !string.IsNullOrEmpty(searchDirectory))
                {
                    string p2 = Path.Combine(searchDirectory, "Finished.mp3");
                    if (File.Exists(p2)) mp3Path = p2;
                }

                // 3. 尝试工作区根目录
                if (mp3Path == null)
                {
                    string p3 = Path.GetFullPath(Path.Combine(appDir, @"..\..\Finished.mp3"));
                    if (File.Exists(p3)) mp3Path = p3;
                }

                if (mp3Path != null && File.Exists(mp3Path))
                {
                    mciSendString("close unbox_finished_sound", null, 0, IntPtr.Zero);
                    mciSendString($"open \"{mp3Path}\" type mpegvideo alias unbox_finished_sound", null, 0, IntPtr.Zero);
                    mciSendString("play unbox_finished_sound", null, 0, IntPtr.Zero);
                }
            }
            catch
            {
                // 音频播放失败不影响核心流程
            }
        }

        // -------------------------------------------------------------
        // 2. 任务栏闪烁与置顶提示 (Windows 原生 FlashWindowEx)
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
