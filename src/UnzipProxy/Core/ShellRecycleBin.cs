using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace UnzipProxy.Core
{
    public sealed class RecycleItemResult
    {
        public string Path { get; init; } = "";
        public bool Recycled { get; init; }
        public bool RecycleBinUnavailable { get; init; }
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
        private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x80;
        private const int E_ABORT = unchecked((int)0x80004004);
        private const long MiB = 1024 * 1024;
        internal const string BinDisabledMessage = "回收站没有启用，源文件未删除";
        internal const string CapacityUnknownMessage = "无法确认目标卷的回收站容量，已保留源文件。请检查回收站属性或联系管理员";
        internal const string CapacityInsufficientMessage = "回收站剩余容量不足以容纳本次源文件，已保留源文件";

        private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

        private static readonly Guid IID_IShellItem = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        private static readonly Guid CLSID_FileOperation = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09");
        private sealed record RecycleVolume(Guid Id, long? CapacityBytes, long OccupiedBytes);

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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetVolumePathName(string path, StringBuilder mount, int capacity);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder name, int capacity);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern DriveType GetDriveType(string rootPath);

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

        private sealed class RecycleProgressSink(VolumeIdentity expected, Guid volumeId, long pendingBytes)
            : IFileOperationProgressSink
        {
            public int? DeleteResult;
            public string Destination = "";
            public string? BlockedReason;

            public int StartOperations() => 0;
            public int FinishOperations(int hrResult) => 0;
            public int PreRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName) => 0;
            public int PostRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName, int hrRename, IShellItem psiNewlyCreated) => 0;
            public int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName) => 0;
            public int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, int hrMove, IShellItem psiNewlyCreated) => 0;
            public int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName) => 0;
            public int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, int hrCopy, IShellItem psiNewlyCreated) => 0;
            public int PreDeleteItem(uint dwFlags, IShellItem psiItem)
            {
                if ((dwFlags & TSF_DELETE_RECYCLE_IF_POSSIBLE) == 0 ||
                    QueryRecycleVolume(expected.Path) is not { } bin)
                    BlockedReason = BinDisabledMessage;
                else if (bin.Id != volumeId)
                    BlockedReason = "源文件所在卷已改变，已保留源文件";
                else
                    BlockedReason = CheckCapacity(bin, pendingBytes);

                if (BlockedReason != null) return E_ABORT;
                if (VolumeIdentity.Capture(expected.Path) is { } current && expected.Matches(current))
                    return 0;
                BlockedReason = "源文件身份已改变或无法核对，已保留源文件";
                return E_ABORT;
            }

            public int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem psiNewlyCreated)
            {
                try
                {
                    DeleteResult = hrDelete;
                    Destination = GetParsingName(psiNewlyCreated) ?? "";
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

        public static bool IsRecycleBinSupported(string path) => QueryRecycleVolume(path) != null;

        private static RecycleVolume? QueryRecycleVolume(string path)
        {
            try
            {
                if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                    return null;

                var mount = new StringBuilder(32768);
                var volume = new StringBuilder(64);
                if (!GetVolumePathName(path, mount, mount.Capacity)) return null;
                string mountPath = mount.ToString();
                if (GetDriveType(mountPath) is not (DriveType.Fixed or DriveType.Removable) ||
                    !GetVolumeNameForVolumeMountPoint(mountPath, volume, volume.Capacity))
                    return null;
                string guid = volume.ToString().TrimEnd('\\');
                if (!guid.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParse(guid[10..], out var volumeId)) return null;

                const string policy = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
                using var userPolicy = Registry.CurrentUser.OpenSubKey(policy);
                using var machinePolicy = Registry.LocalMachine.OpenSubKey(policy);
                using var bin = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{volumeId:B}");
                object? configuredCapacity = bin?.GetValue("MaxCapacity");
                if (Convert.ToInt32(userPolicy?.GetValue("NoRecycleFiles")) != 0 ||
                    Convert.ToInt32(machinePolicy?.GetValue("NoRecycleFiles")) != 0 ||
                    Convert.ToInt32(bin?.GetValue("NukeOnDelete")) != 0 ||
                    configuredCapacity is 0) return null;

                var rbInfo = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                if (SHQueryRecycleBin(mountPath, ref rbInfo) != 0 || rbInfo.i64Size < 0) return null;

                // SHQueryRecycleBin reports usage, not the effective quota. Do not guess a
                // default or use the per-volume setting when policy can override it.
                long? capacity = configuredCapacity is int capMiB && capMiB > 0 &&
                    userPolicy?.GetValue("RecycleBinSize") == null &&
                    machinePolicy?.GetValue("RecycleBinSize") == null ? capMiB * MiB : null;
                return new RecycleVolume(volumeId, capacity, rbInfo.i64Size);
            }
            catch
            {
                return null;
            }
        }

        private static string? CheckCapacity(RecycleVolume bin, long pendingBytes)
        {
            if (bin.CapacityBytes is not { } capacity) return CapacityUnknownMessage;
            return pendingBytes > capacity - bin.OccupiedBytes ? CapacityInsufficientMessage : null;
        }

        private static Mutex? TryAcquireRecycleLock()
        {
            Mutex? mutex = null;
            try
            {
                using var user = WindowsIdentity.GetCurrent();
                if (user.User is not { } sid) return null;
                mutex = new Mutex(false, $@"Global\Unbox.Recycle.{sid.Value}");
                if (mutex.WaitOne(TimeSpan.FromSeconds(5))) return mutex;
            }
            catch (AbandonedMutexException) { return mutex; }
            catch { }
            mutex?.Dispose();
            return null;
        }

        /// <summary>
        /// 回收本次任务实际读取过的源卷。逐卷先做身份核对，再交给 Shell 显式回收，
        /// 最后逐卷报告真实结果（成功 / 仍在原位 / 被替换）。
        /// </summary>
        public static RecycleResult RecycleVolumes(IReadOnlyList<VolumeIdentity> volumes, IntPtr ownerWindow = default)
        {
            if (volumes == null || volumes.Count == 0)
                return new RecycleResult { Success = true, Message = "无需回收" };

            using var mutex = TryAcquireRecycleLock();
            if (mutex == null)
            {
                const string reason = "无法独占本次 Unbox 回收操作，已保留源文件";
                var retained = new List<RecycleItemResult>(volumes.Count);
                foreach (var volume in volumes) retained.Add(Fail(volume.Path, reason));
                return new RecycleResult { Message = reason, Items = retained };
            }

            try { return RecycleVolumesLocked(volumes, ownerWindow); }
            finally { mutex.ReleaseMutex(); }
        }

        private static RecycleResult RecycleVolumesLocked(IReadOnlyList<VolumeIdentity> volumes, IntPtr ownerWindow)
        {
            var items = new List<RecycleItemResult>(volumes.Count);
            var verified = new List<(VolumeIdentity Expected, string FullPath, RecycleVolume Bin)>();
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
                    const string missingIdentity = "缺少解压时记录的源文件身份，无法确认未被替换，已保留";
                    if (QueryRecycleVolume(fullPath) is not { } bin)
                    {
                        items.Add(Fail(fullPath, volume.Valid ? BinDisabledMessage : BinDisabledMessage + "；" + missingIdentity,
                            binUnavailable: true));
                        continue;
                    }
                    if (!volume.Valid)
                    {
                        items.Add(Fail(fullPath, missingIdentity));
                        continue;
                    }

                    var current = VolumeIdentity.Capture(fullPath);
                    if (current == null || current.Size < 0)
                    {
                        items.Add(Fail(fullPath, "无法读取源文件对象身份，已保留"));
                        continue;
                    }
                    if (!volume.Matches(current))
                    {
                        items.Add(Fail(fullPath, "源文件在解压后已被替换或修改，已保留"));
                        continue;
                    }
                    verified.Add((volume, fullPath, bin));
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

            var pendingByVolume = new Dictionary<Guid, long>();
            string? preflightFailure = items.Exists(item => item.RecycleBinUnavailable)
                ? "本次源卷包含无法确认可回收的卷，已保留其余源文件" : null;
            foreach (var entry in verified)
            {
                if (preflightFailure != null) break;
                long pending = pendingByVolume.GetValueOrDefault(entry.Bin.Id);
                if (entry.Expected.Size > long.MaxValue - pending)
                {
                    preflightFailure = CapacityInsufficientMessage;
                    break;
                }
                pending += entry.Expected.Size;
                preflightFailure = CheckCapacity(entry.Bin, pending);
                if (preflightFailure != null) break;
                pendingByVolume[entry.Bin.Id] = pending;
            }
            if (preflightFailure != null)
            {
                foreach (var entry in verified) items.Add(Fail(entry.FullPath, preflightFailure));
                return new RecycleResult { Message = preflightFailure, Items = items };
            }

            int firstAttempt = items.Count;
            foreach (var entry in verified)
            {
                long pending = pendingByVolume[entry.Bin.Id];
                var item = RecycleOne(entry.Expected, entry.FullPath, entry.Bin.Id, pending, ownerWindow);
                items.Add(item);
                if (item.Recycled)
                    pendingByVolume[entry.Bin.Id] -= entry.Expected.Size;
            }

            // A later operation can change the bin again. Count only items that are
            // still visible in the Shell recycle namespace with the expected identity.
            for (int i = 0; i < verified.Count; i++)
            {
                int index = firstAttempt + i;
                if (!items[index].Recycled) continue;

                var entry = verified[i];
                if (File.Exists(entry.FullPath))
                {
                    items[index] = Fail(entry.FullPath, "任务结束时源路径仍有文件，请核对原路径和回收站");
                    continue;
                }
                string destination = TryLocateRecycleBinPath(entry.Expected);
                if (destination.Length == 0)
                    items[index] = Fail(entry.FullPath, "源文件已不在原位，任务结束时未能核实可还原的回收站落点，请检查回收站");
                else if (!string.Equals(destination, items[index].RecycledTo, StringComparison.OrdinalIgnoreCase))
                    items[index] = new RecycleItemResult { Path = entry.FullPath, Recycled = true, RecycledTo = destination,
                        Detail = $"已移入回收站：{destination}" };
            }

            bool allRecycled = items.TrueForAll(i => i.Recycled);
            string message;
            if (allRecycled)
            {
                message = $"已确认 {items.Count} 个源卷可从回收站还原";
            }
            else
            {
                message = $"{items.FindAll(i => i.Recycled).Count}/{items.Count} 个源卷已确认移入回收站";
            }

            return new RecycleResult { Success = allRecycled, Message = message, Items = items };
        }

        private static RecycleItemResult RecycleOne(VolumeIdentity expected, string path, Guid volumeId,
            long pendingBytes, IntPtr ownerWindow)
        {
            IFileOperation? operation = null;
            IShellItem? item = null;
            try
            {
                var type = Type.GetTypeFromCLSID(CLSID_FileOperation, throwOnError: true)!;
                operation = (IFileOperation)Activator.CreateInstance(type)!;
                Marshal.ThrowExceptionForHR(operation.SetOwnerWindow(ownerWindow));
                Marshal.ThrowExceptionForHR(operation.SetOperationFlags(
                    FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI));
                var sink = new RecycleProgressSink(expected, volumeId, pendingBytes);
                SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItem, out item);
                Marshal.ThrowExceptionForHR(operation.DeleteItem(item, sink));
                int hr = operation.PerformOperations();
                if (File.Exists(path))
                    return Fail(path, sink.BlockedReason ?? $"Shell 未完成回收（0x{hr:X8}），源文件仍在原位",
                        binUnavailable: sink.BlockedReason == BinDisabledMessage);

                if (hr < 0 || operation.GetAnyOperationsAborted(out bool aborted) < 0 || aborted ||
                    sink.DeleteResult is null or < 0)
                    return Fail(path, "源文件已不在原位，未能确认 Shell 回收结果，请检查回收站");

                string destination = sink.Destination;
                if (!MatchesDestination(expected, destination))
                    destination = TryLocateRecycleBinPath(expected);
                if (destination.Length == 0)
                    return Fail(path, "源文件已不在原位，未能核实可还原的回收站落点，请检查回收站");
                return new RecycleItemResult { Path = path, Recycled = true, RecycledTo = destination,
                    Detail = $"已移入回收站：{destination}" };
            }
            catch (Exception ex)
            {
                return Fail(path, $"调用 Shell 回收接口失败：{ex.Message}");
            }
            finally
            {
                if (item != null) Marshal.ReleaseComObject(item);
                if (operation != null) Marshal.ReleaseComObject(operation);
            }
        }

        private static bool MatchesDestination(VolumeIdentity expected, string path) =>
            Path.IsPathFullyQualified(path) && VolumeIdentity.Capture(path) is { } actual && expected.Matches(actual);

        private static string TryLocateRecycleBinPath(VolumeIdentity expected, int maxItems = 5000)
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
                string targetDir = (Path.GetDirectoryName(expected.Path) ?? "").TrimEnd('\\');

                for (int i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    string deletedFrom = "";
                    try { deletedFrom = (item.ExtendedProperty("System.Recycle.DeletedFrom") as string) ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(deletedFrom) &&
                        !string.Equals(deletedFrom.TrimEnd('\\'), targetDir, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string path = (item.Path as string) ?? "";
                    if (MatchesDestination(expected, path)) return path;
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

        private static RecycleItemResult Fail(string path, string detail, bool binUnavailable = false)
        {
            return new RecycleItemResult { Path = path, Recycled = false, Detail = detail, RecycleBinUnavailable = binUnavailable };
        }
    }
}
