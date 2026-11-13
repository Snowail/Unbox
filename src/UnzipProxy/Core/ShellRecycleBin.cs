using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace UnzipProxy.Core
{
    public sealed class RecycleItemResult
    {
        public string Path { get; init; } = "";
        public bool Recycled { get; init; }
        public string Detail { get; init; } = "";
        /// <summary>回收站内的实际去向（Shell 回报的新建项），没有则为空。</summary>
        public string RecycledTo { get; init; } = "";
    }

    public sealed class RecycleResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public List<RecycleItemResult> Items { get; init; } = new();

        public int RecycledCount => Items.FindAll(i => i.Recycled).Count;
        public int FailedCount => Items.FindAll(i => !i.Recycled).Count;
    }

    /// <summary>
    /// 源卷回收：改用 Shell 的 IFileOperation + IFileOperationProgressSink，
    /// 逐项获取 Shell 的真实返回值与回收站落点，并在回收前用对象身份核对源文件。
    /// 不再使用“路径消失即成功”的事后猜测。
    /// </summary>
    public static class ShellRecycleBin
    {
        private const uint FOF_SILENT = 0x0004;
        private const uint FOF_NOCONFIRMATION = 0x0010;
        private const uint FOF_ALLOWUNDO = 0x0040;
        private const uint FOF_NOERRORUI = 0x0400;
        private const uint FOFX_RECYCLEONDELETE = 0x00080000;

        private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

        private static readonly Guid IID_IShellItem = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        private static readonly Guid CLSID_FileOperation = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09");

        // 注意：必须使用默认 8 字节对齐（x64 下 sizeof = 24），否则 cbSize 不匹配会导致查询恒失败
        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            [PreserveSig] int Advise(IFileOperationProgressSink? pfops, out uint pdwCookie);
            [PreserveSig] int Unadvise(uint dwCookie);
            [PreserveSig] int SetOperationFlags(uint dwOperationFlags);
            [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
            [PreserveSig] int SetProgressDialog(IntPtr popmpd);
            [PreserveSig] int SetProperties(IntPtr pproparray);
            [PreserveSig] int SetOwnerWindow(IntPtr hwndOwner);
            [PreserveSig] int ApplyPropertiesToItem(IShellItem psiItem);
            [PreserveSig] int ApplyPropertiesToItems(IntPtr punkItems);
            [PreserveSig] int RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IFileOperationProgressSink? pfopsItem);
            [PreserveSig] int RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
            [PreserveSig] int MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IFileOperationProgressSink? pfopsItem);
            [PreserveSig] int MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
            [PreserveSig] int CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IFileOperationProgressSink? pfopsItem);
            [PreserveSig] int CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
            [PreserveSig] int DeleteItem(IShellItem psiItem, IFileOperationProgressSink? pfopsItem);
            [PreserveSig] int DeleteItems(IntPtr punkItems);
            [PreserveSig] int NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes,
                                      [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                                      [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName,
                                      IFileOperationProgressSink? pfopsItem);
            [PreserveSig] int PerformOperations();
            [PreserveSig] int GetAnyOperationsAborted(out bool pfAnyOperationsAborted);
        }

        [ComImport, Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperationProgressSink
        {
            [PreserveSig] int StartOperations();
            [PreserveSig] int FinishOperations(int hrResult);
            [PreserveSig] int PreRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
            [PreserveSig] int PostRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrRename, IShellItem psiNewlyCreated);
            [PreserveSig] int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
            [PreserveSig] int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrMove, IShellItem psiNewlyCreated);
            [PreserveSig] int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
            [PreserveSig] int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrCopy, IShellItem psiNewlyCreated);
            [PreserveSig] int PreDeleteItem(uint dwFlags, IShellItem psiItem);
            [PreserveSig] int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem psiNewlyCreated);
            [PreserveSig] int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
            [PreserveSig] int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName,
                                          [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem psiNewItem);
            [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
            [PreserveSig] int ResetTimer();
            [PreserveSig] int PauseTimer();
            [PreserveSig] int ResumeTimer();
        }

        private sealed class RecycleProgressSink : IFileOperationProgressSink
        {
            public readonly List<(string Path, int HResult, string RecycledTo)> Deleted = new();

            public int StartOperations() => 0;
            public int FinishOperations(int hrResult) => 0;
            public int PreRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName) => 0;
            public int PostRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName, int hrRename, IShellItem psiNewlyCreated) => 0;
            public int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName) => 0;
            public int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, int hrMove, IShellItem psiNewlyCreated) => 0;
            public int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName) => 0;
            public int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, int hrCopy, IShellItem psiNewlyCreated) => 0;
            public int PreDeleteItem(uint dwFlags, IShellItem psiItem) => 0;

            public int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem psiNewlyCreated)
            {
                try
                {
                    Deleted.Add((GetParsingName(psiItem) ?? "", hrDelete, GetParsingName(psiNewlyCreated) ?? ""));
                }
                catch
                {
                    // 单条回调异常不得中断整个 Shell 操作
                }
                return 0;
            }

            public int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName) => 0;
            public int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName, string pszTemplateName,
                                   uint dwFileAttributes, int hrNew, IShellItem psiNewItem) => 0;
            public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => 0;
            public int ResetTimer() => 0;
            public int PauseTimer() => 0;
            public int ResumeTimer() => 0;
        }

        private static string? GetParsingName(IShellItem? item)
        {
            if (item == null) return null;
            IntPtr pointer = IntPtr.Zero;
            try
            {
                item.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out pointer);
                return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
            }
        }

        public static bool IsRecycleBinSupported(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false; // UNC 共享
                string root = Path.GetPathRoot(path) ?? "";
                if (string.IsNullOrEmpty(root)) return false;

                var drive = new DriveInfo(root);
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                {
                    return false;
                }

                var rbInfo = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
                return SHQueryRecycleBin(root, ref rbInfo) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static long QueryRecycleBinCount(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
                if (SHQueryRecycleBin(root, ref info) == 0) return info.i64NumItems;
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// 回收本次任务实际读取过的源卷。逐卷先做身份核对，再交给 Shell 显式回收，
        /// 最后逐卷报告真实结果（成功 / 仍在原位 / 被替换）。
        /// </summary>
        public static RecycleResult RecycleVolumes(IReadOnlyList<VolumeIdentity> volumes, IntPtr ownerWindow = default)
        {
            var items = new List<RecycleItemResult>();
            if (volumes == null || volumes.Count == 0)
            {
                return new RecycleResult { Success = true, Message = "无需回收", Items = items };
            }

            var verified = new List<(VolumeIdentity Expected, string FullPath)>();
            foreach (var volume in volumes)
            {
                string fullPath = "";
                try
                {
                    if (string.IsNullOrWhiteSpace(volume.Path))
                    {
                        items.Add(Fail(volume.Path, "源卷路径为空，已保留"));
                        continue;
                    }
                    fullPath = Path.GetFullPath(volume.Path);
                    if (!File.Exists(fullPath))
                    {
                        items.Add(Fail(fullPath, "源卷不存在或已被移除，已保留"));
                        continue;
                    }
                    if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
                    {
                        items.Add(Fail(fullPath, "网络路径不支持回收站，已保留"));
                        continue;
                    }
                    if (!IsRecycleBinSupported(fullPath))
                    {
                        items.Add(Fail(fullPath, "所在驱动器不支持回收站，已保留"));
                        continue;
                    }

                    var current = VolumeIdentity.Capture(fullPath);
                    if (current == null)
                    {
                        items.Add(Fail(fullPath, "无法读取源文件对象身份，已保留"));
                        continue;
                    }
                    if (!volume.Valid)
                    {
                        items.Add(Fail(fullPath, "缺少解压时记录的源文件身份，无法确认未被替换，已保留"));
                        continue;
                    }
                    if (!volume.Matches(current))
                    {
                        items.Add(Fail(fullPath, "源文件在解压后已被替换或修改，已保留"));
                        continue;
                    }
                    verified.Add((volume, fullPath));
                }
                catch (Exception ex)
                {
                    items.Add(Fail(fullPath, $"回收前校验失败：{ex.Message}"));
                }
            }

            if (verified.Count == 0)
            {
                return new RecycleResult
                {
                    Success = false,
                    Message = "没有可回收的源卷（全部已保留）",
                    Items = items
                };
            }

            long binCountBefore = QueryRecycleBinCount(verified[0].FullPath);
            var sink = new RecycleProgressSink();
            IFileOperation? operation = null;
            uint cookie = 0;
            bool advised = false;
            int performHr = 0;
            bool aborted = false;

            try
            {
                Type? operationType = Type.GetTypeFromCLSID(CLSID_FileOperation);
                if (operationType == null)
                {
                    throw new InvalidOperationException("无法创建 Shell 文件操作对象");
                }
                operation = (IFileOperation)Activator.CreateInstance(operationType)!;
                operation.SetOwnerWindow(ownerWindow);
                operation.SetOperationFlags(FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI);
                if (operation.Advise(sink, out cookie) != 0)
                {
                    advised = false;
                }
                else
                {
                    advised = true;
                }

                foreach (var entry in verified)
                {
                    SHCreateItemFromParsingName(entry.FullPath, IntPtr.Zero, IID_IShellItem, out var shellItem);
                    int deleteHr = operation.DeleteItem(shellItem, null);
                    if (deleteHr != 0)
                    {
                        sink.Deleted.Add((entry.FullPath, deleteHr, ""));
                    }
                    Marshal.ReleaseComObject(shellItem);
                }

                performHr = operation.PerformOperations();
                operation.GetAnyOperationsAborted(out aborted);
            }
            catch (Exception ex)
            {
                items.Add(Fail("", $"调用 Shell 回收接口失败：{ex.Message}"));
            }
            finally
            {
                if (operation != null)
                {
                    if (advised)
                    {
                        try { operation.Unadvise(cookie); } catch { }
                    }
                    try { Marshal.ReleaseComObject(operation); } catch { }
                }
            }

            long binCountAfter = QueryRecycleBinCount(verified[0].FullPath);

            foreach (var entry in verified)
            {
                string path = entry.FullPath;
                int callbackIndex = sink.Deleted.FindIndex(d =>
                    !string.IsNullOrEmpty(d.Path) &&
                    string.Equals(Path.GetFullPath(d.Path), path, StringComparison.OrdinalIgnoreCase));
                bool callbackFound = callbackIndex >= 0;
                int callbackHr = callbackFound ? sink.Deleted[callbackIndex].HResult : 0;
                // Shell 的 Copy Engine 使用 S_ 前缀的成功码（例如 S_DONT_PROCESS_CHILDREN = 0x00270008），
                // 必须按 HRESULT 的符号判断成功，不能要求恰好等于 0。
                bool callbackOk = callbackHr >= 0;

                bool stillExists = File.Exists(path);
                if (stillExists)
                {
                    string detail = !callbackOk
                        ? $"Shell 返回失败 (0x{callbackHr:X8})，源文件仍在原位"
                        : $"源文件仍在原位（Shell 未报告回收完成{(aborted ? "，操作被中止" : "")}）";
                    items.Add(Fail(path, detail));
                    continue;
                }

                if (!callbackOk)
                {
                    string located = TryLocateRecycleBinPath(path);
                    items.Add(Fail(path, string.IsNullOrEmpty(located)
                        ? $"Shell 返回失败 (0x{callbackHr:X8})，源文件已不在原位，请人工确认"
                        : $"Shell 返回失败 (0x{callbackHr:X8})，但回收站内已能找到该项：{located}"));
                    continue;
                }

                string recycledTo = callbackFound ? sink.Deleted[callbackIndex].RecycledTo : "";
                if (string.IsNullOrEmpty(recycledTo))
                {
                    recycledTo = TryLocateRecycleBinPath(path);
                }

                items.Add(new RecycleItemResult
                {
                    Path = path,
                    Recycled = true,
                    Detail = string.IsNullOrEmpty(recycledTo) ? "已移入回收站（可还原）" : $"已移入回收站：{recycledTo}",
                    RecycledTo = recycledTo
                });
            }

            bool allRecycled = items.TrueForAll(i => i.Recycled);
            string message;
            if (allRecycled)
            {
                message = $"已将 {items.Count} 个源卷移入回收站（回收站项目数 {binCountBefore} -> {binCountAfter}）";
            }
            else
            {
                message = $"{items.FindAll(i => i.Recycled).Count}/{items.Count} 个源卷已移入回收站，其余已保留在原位"
                          + (performHr != 0 ? $"（Shell 返回 0x{performHr:X8}）" : "");
            }

            return new RecycleResult { Success = allRecycled, Message = message, Items = items };
        }

        /// <summary>
        /// 在系统回收站中按“原文件名 + 原目录”定位刚回收的项，返回其实际存放路径。
        /// 这是逐卷“实际去向”的直接证据；取不到时返回空字符串。
        /// </summary>
        private static string TryLocateRecycleBinPath(string originalPath, int maxItems = 5000)
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return "";
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null) return "";
                dynamic? bin = shell.NameSpace(10); // ssfBITBUCKET
                if (bin == null) return "";

                dynamic items = bin.Items();
                int count = Math.Min((int)items.Count, maxItems);
                string targetName = Path.GetFileName(originalPath);
                string targetDir = (Path.GetDirectoryName(originalPath) ?? "").TrimEnd('\\');

                for (int i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    string name = (item.Name as string) ?? "";
                    if (!string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase)) continue;

                    string deletedFrom = "";
                    try { deletedFrom = (item.ExtendedProperty("System.Recycle.DeletedFrom") as string) ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(deletedFrom) &&
                        !string.Equals(deletedFrom.TrimEnd('\\'), targetDir, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    return (item.Path as string) ?? "";
                }
            }
            catch
            {
                // 回收站枚举失败不影响已获得的 Shell 逐项结果
            }
            return "";
        }

        /// <summary>仅按当前文件状态回收（测试与兼容入口）：身份快照在调用时捕获。</summary>
        public static RecycleResult RecycleOpenedVolumes(IEnumerable<string> openedVolumes)
        {
            var identities = new List<VolumeIdentity>();
            foreach (string path in openedVolumes)
            {
                var identity = VolumeIdentity.Capture(path);
                if (identity != null)
                {
                    identities.Add(identity);
                }
                else
                {
                    identities.Add(new VolumeIdentity { Path = path, Valid = false });
                }
            }
            return RecycleVolumes(identities);
        }

        private static RecycleItemResult Fail(string path, string detail)
        {
            return new RecycleItemResult { Path = path, Recycled = false, Detail = detail };
        }
    }
}
