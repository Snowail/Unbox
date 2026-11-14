using System;
using System.Collections.Generic;
using System.IO;

namespace UnzipProxy.Core
{
    public sealed class CompletionRequest
    {
        public required ExtractionPlan Plan { get; set; }
        /// <summary>Worker 报告的交付根目录（相对暂存目录，空表示暂存目录本身）。</summary>
        public string DeliveryRootRelative { get; set; } = "";
        public required DeliveryManifest Manifest { get; set; }
        public bool RecycleRequested { get; set; }
        public IReadOnlyList<VolumeIdentity> Volumes { get; set; } = Array.Empty<VolumeIdentity>();
        public bool PropagateZoneIdentifier { get; set; } = true;
    }

    public sealed class CompletionResult
    {
        public bool Delivered { get; set; }
        public string FinalDirectory { get; set; } = "";
        public string FailureReason { get; set; } = "";
        public string Summary { get; set; } = "";
        public int Files { get; set; }
        public int Directories { get; set; }
        public long Bytes { get; set; }
        public int ZoneMarked { get; set; }
        /// <summary>提交失败时保留的可恢复暂存目录（内含完整待交付树与中间载荷）。</summary>
        public string KeptStagingDirectory { get; set; } = "";
        public RecycleResult? Recycle { get; set; }
        public bool Collapsed { get; set; }
    }

    /// <summary>
    /// 生产完成路径：清单对账 → 折叠判定 → 来源标记 → 一次性目录改名提交 → 提交后复核。
    /// 供 WPF 与自动化测试共用，保证测试覆盖的就是用户实际走的那条路径。
    /// </summary>
    public static class ExtractionCompleter
    {
        public static CompletionResult Complete(CompletionRequest request, Action<string>? log = null)
        {
            var result = new CompletionResult();
            var plan = request.Plan;

            try
            {
                if (!Directory.Exists(plan.StagingDirectory))
                {
                    result.FailureReason = "暂存目录不存在，可能已被清理";
                    return result;
                }
                if (request.Manifest.Items.Count == 0 && !IsTrulyEmptyArchive(plan, request))
                {
                    result.FailureReason = "交付清单为空，无法确认任何产物";
                    return result;
                }

                // 1. 定位交付根目录（必须严格位于暂存目录之内）
                string deliveryRoot = ResolveDeliveryRoot(plan.StagingDirectory, request.DeliveryRootRelative);
                if (deliveryRoot.Length == 0 || !Directory.Exists(deliveryRoot))
                {
                    result.FailureReason = "交付根目录不存在";
                    result.KeptStagingDirectory = plan.StagingDirectory;
                    return result;
                }

                // 2. 提交前逐项对账（路径 / 类型 / 长度）
                var preReport = DeliveryVerifier.Verify(deliveryRoot, request.Manifest, VerifyMode.Structure, log);
                if (!preReport.Ok)
                {
                    result.FailureReason = "交付内容与清单不一致：" + preReport.Describe();
                    result.KeptStagingDirectory = plan.StagingDirectory;
                    return result;
                }

                // 3. 同名唯一根目录折叠：把选定的交付根一次性改名到最终位置
                string effectiveManifestPrefix = "";
                string? collapseRoot = ArchiveTaskPlanner.SelectCollapseRoot(deliveryRoot, plan.SourceArchivePath);
                if (collapseRoot != null)
                {
                    effectiveManifestPrefix = Path.GetFileName(collapseRoot);
                    deliveryRoot = collapseRoot;
                    result.Collapsed = true;
                }

                // 4. 目标目录若在规划后被占用，重新选择不冲突的新目标（绝不删除既有目录）
                if (Directory.Exists(plan.FinalTargetDirectory) || File.Exists(plan.FinalTargetDirectory))
                {
                    plan.FinalTargetDirectory = ArchiveTaskPlanner.ResolveUniqueTargetDirectory(
                        plan.DestinationParentDir, plan.TargetRootDirName);
                    plan.TargetRootDirName = Path.GetFileName(plan.FinalTargetDirectory);
                }

                // 5. 来源标记在“最终树公开之前”写入（此时树仍在暂存目录内）
                if (request.PropagateZoneIdentifier)
                {
                    result.ZoneMarked = ZoneIdentifier.Propagate(plan.SourceArchivePath, deliveryRoot);
                }

                // 6. 一次性目录改名提交（同卷 rename，不存在逐项移动的半提交状态）
                Directory.Move(deliveryRoot, plan.FinalTargetDirectory);

                string leftover = "";
                if (!PathsEqual(deliveryRoot, plan.StagingDirectory))
                {
                    // 交付根之外只剩中间载荷与隔离目录，提交成功后才清理
                    try
                    {
                        Directory.Delete(plan.StagingDirectory, true);
                    }
                    catch (Exception ex)
                    {
                        leftover = ex.Message;
                    }
                }

                // 7. 提交后复核（折叠过的树按去掉前缀后的路径核对）
                var finalManifest = string.IsNullOrEmpty(effectiveManifestPrefix)
                    ? request.Manifest
                    : RebaseManifest(request.Manifest, effectiveManifestPrefix);
                var postReport = DeliveryVerifier.Verify(plan.FinalTargetDirectory, finalManifest, VerifyMode.Structure, log);
                result.Files = postReport.Files;
                result.Directories = postReport.Directories;
                result.Bytes = postReport.TotalBytes;
                if (!postReport.Ok)
                {
                    result.FailureReason = "提交后复核失败：" + postReport.Describe();
                    result.FinalDirectory = plan.FinalTargetDirectory;
                    return result;
                }

                result.Delivered = true;
                result.FinalDirectory = plan.FinalTargetDirectory;
                result.Summary = string.IsNullOrEmpty(leftover)
                    ? $"已交付 {postReport.Files} 个文件、{postReport.Directories} 个目录（{postReport.TotalBytes} 字节）"
                    : $"已交付 {postReport.Files} 个文件；暂存目录清理延迟：{leftover}";

                // 8. 仅在调用方要求且交付成功时回收源卷；回收前做摘要级复核与身份核对
                if (request.RecycleRequested && request.Volumes.Count > 0)
                {
                    var digestReport = DeliveryVerifier.Verify(plan.FinalTargetDirectory, finalManifest,
                        VerifyMode.StructureAndDigest, log);
                    if (!digestReport.Ok)
                    {
                        result.Recycle = new RecycleResult
                        {
                            Success = false,
                            Message = "回收前摘要复核未通过，源包已保留：" + digestReport.Describe()
                        };
                    }
                    else
                    {
                        result.Recycle = ShellRecycleBin.RecycleVolumes(request.Volumes);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                result.FailureReason = "完成处理失败：" + ex.Message;
                result.KeptStagingDirectory = Directory.Exists(plan.StagingDirectory) ? plan.StagingDirectory : "";
                return result;
            }
        }

        /// <summary>空归档（合法空包 / 零条目）也走成功路径，但必须没有清单外产物。</summary>
        private static bool IsTrulyEmptyArchive(ExtractionPlan plan, CompletionRequest request)
        {
            try
            {
                string deliveryRoot = ResolveDeliveryRoot(plan.StagingDirectory, request.DeliveryRootRelative);
                if (deliveryRoot.Length == 0 || !Directory.Exists(deliveryRoot)) return false;
                if (Directory.GetFileSystemEntries(deliveryRoot).Length != 0) return false;
                return !Directory.Exists(plan.FinalTargetDirectory);
            }
            catch
            {
                return false;
            }
        }

        private static DeliveryManifest RebaseManifest(DeliveryManifest manifest, string prefix)
        {
            string normalizedPrefix = DeliveryVerifier.NormalizeRelative(prefix);
            var items = new List<ManifestItem>();
            foreach (var item in manifest.Items)
            {
                string path = DeliveryVerifier.NormalizeRelative(item.Path);
                if (string.Equals(path, normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // 折叠根自身（目录条目）不再是交付内容
                    continue;
                }
                if (path.StartsWith(normalizedPrefix + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Substring(normalizedPrefix.Length + 1);
                }
                items.Add(new ManifestItem
                {
                    Path = path,
                    IsDirectory = item.IsDirectory,
                    Size = item.Size,
                    Crc = item.Crc
                });
            }
            return new DeliveryManifest
            {
                DeliveryRootRelative = manifest.DeliveryRootRelative,
                ItemCount = items.Count,
                Items = items
            };
        }

        private static string ResolveDeliveryRoot(string stagingDirectory, string relative)
        {
            string stagingFull = Path.GetFullPath(stagingDirectory).TrimEnd('\\');
            string combined;
            if (string.IsNullOrWhiteSpace(relative))
            {
                combined = stagingFull;
            }
            else
            {
                string cleanRelative = DeliveryVerifier.NormalizeRelative(relative);
                if (cleanRelative.Length == 0 || cleanRelative.Contains(':') || cleanRelative.Contains("..") ||
                    Path.IsPathRooted(cleanRelative))
                {
                    return "";
                }
                combined = Path.GetFullPath(Path.Combine(stagingFull, cleanRelative)).TrimEnd('\\');
            }

            if (!PathsEqual(combined, stagingFull) &&
                !combined.StartsWith(stagingFull + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }
            return combined;
        }

        private static bool PathsEqual(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Windows 下载来源标记（Mark of the Web）传递。</summary>
    public static class ZoneIdentifier
    {
        private const string StreamSuffix = ":Zone.Identifier";

        public static int Propagate(string sourceArchivePath, string targetRoot)
        {
            int marked = 0;
            try
            {
                if (string.IsNullOrEmpty(sourceArchivePath) || !File.Exists(sourceArchivePath)) return 0;
                string zonePath = sourceArchivePath + StreamSuffix;
                if (!File.Exists(zonePath)) return 0;
                byte[] zoneData = File.ReadAllBytes(zonePath);
                if (zoneData.Length == 0) return 0;

                // 遍历最终树时拒绝 reparse point，绝不通过联接点/符号链接把来源标记写到暂存区之外。
                var files = new List<string>();
                EnumerateFilesNoReparse(targetRoot, files);
                foreach (string file in files)
                {
                    try
                    {
                        File.WriteAllBytes(file + StreamSuffix, zoneData);
                        marked++;
                    }
                    catch
                    {
                        // 单个文件的非致命属性写入失败不阻断交付
                    }
                }
            }
            catch
            {
                // 来源标记传递失败不影响主流程
            }
            return marked;
        }

        private static void EnumerateFilesNoReparse(string dir, List<string> files)
        {
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        EnumerateFilesNoReparse(entry, files);
                    }
                    else
                    {
                        files.Add(entry);
                    }
                }
            }
            catch
            {
                // 单个目录枚举失败不阻断来源标记
            }
        }
    }
}
