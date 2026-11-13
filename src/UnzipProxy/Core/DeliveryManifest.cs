using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnzipProxy.Core
{
    /// <summary>交付清单中的单条记录（路径一律相对交付根目录）。</summary>
    public sealed class ManifestItem
    {
        public string Path { get; init; } = "";
        public bool IsDirectory { get; init; }
        public long Size { get; init; }
        public uint Crc { get; init; }
    }

    /// <summary>Worker 产出的交付清单：期望的最终树逐项描述。</summary>
    public sealed class DeliveryManifest
    {
        public string DeliveryRootRelative { get; init; } = "";
        public int ItemCount { get; init; }
        public List<ManifestItem> Items { get; init; } = new();

        public static DeliveryManifest Load(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                throw new InvalidDataException("交付清单不存在");
            }
            return Parse(File.ReadAllText(manifestPath));
        }

        public static DeliveryManifest Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("交付清单格式错误");
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 1)
            {
                throw new InvalidDataException("交付清单版本不受支持");
            }
            string deliveryRoot = root.TryGetProperty("delivery_root", out var dr) ? (dr.GetString() ?? "") : "";
            if (!root.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("交付清单缺少 items");
            }

            var manifest = new DeliveryManifest
            {
                DeliveryRootRelative = deliveryRoot,
                ItemCount = root.TryGetProperty("item_count", out var ic) ? ic.GetInt32() : -1
            };

            foreach (var element in itemsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("交付清单条目格式错误");
                if (!element.TryGetProperty("p", out var p) || !element.TryGetProperty("d", out var d) ||
                    !element.TryGetProperty("s", out var s) || !element.TryGetProperty("c", out var c))
                {
                    throw new InvalidDataException("交付清单条目缺少必需字段");
                }
                manifest.Items.Add(new ManifestItem
                {
                    Path = p.GetString() ?? "",
                    IsDirectory = d.GetInt32() != 0,
                    Size = s.GetInt64(),
                    Crc = unchecked((uint)c.GetInt64())
                });
            }

            if (manifest.ItemCount < 0 || manifest.ItemCount != manifest.Items.Count)
            {
                throw new InvalidDataException("交付清单条目数与 item_count 不一致");
            }
            return manifest;
        }
    }

    public enum VerifyMode
    {
        /// <summary>逐项核对路径、类型与长度（提交前的常规门槛）。</summary>
        Structure,
        /// <summary>额外逐文件核对 CRC32 摘要（回收源包前的强门槛）。</summary>
        StructureAndDigest
    }

    public sealed class VerifyReport
    {
        public List<string> Problems { get; } = new();
        public bool Ok => Problems.Count == 0;
        public int Files { get; set; }
        public int Directories { get; set; }
        public long TotalBytes { get; set; }
        public int DigestsChecked { get; set; }

        public string Describe()
        {
            if (Ok) return $"已逐项核对 {Files} 个文件、{Directories} 个目录，共 {TotalBytes} 字节";
            return string.Join("；", Problems.Take(5)) + (Problems.Count > 5 ? $"（共 {Problems.Count} 项问题）" : "");
        }
    }

    /// <summary>
    /// 交付树对账：把 Worker 的期望清单与最终目录逐项比对。
    /// 不做“只要目录非空就算成功”的弱判定，也不允许任何清单外的多余产物。
    /// </summary>
    public static class DeliveryVerifier
    {
        private const int MaxItems = 5_000_000;

        public static VerifyReport Verify(string deliveryRootAbsolute, DeliveryManifest manifest, VerifyMode mode,
                                          Action<string>? log = null)
        {
            var report = new VerifyReport();
            if (!Directory.Exists(deliveryRootAbsolute))
            {
                report.Problems.Add("交付根目录不存在");
                return report;
            }
            if (manifest.Items.Count > MaxItems)
            {
                report.Problems.Add($"交付条目数量超出上限（{manifest.Items.Count}）");
                return report;
            }

            // 期望集合
            var expected = new Dictionary<string, ManifestItem>(StringComparer.OrdinalIgnoreCase);
            var expectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in manifest.Items)
            {
                string normalized = NormalizeRelative(item.Path);
                if (normalized.Length == 0 || IsUnsafeRelative(normalized))
                {
                    report.Problems.Add($"交付清单包含非法路径：{item.Path}");
                    continue;
                }
                if (!expected.TryAdd(normalized, item))
                {
                    report.Problems.Add($"交付清单包含重复路径：{normalized}");
                }
                // 登记隐式父目录
                int slash = normalized.LastIndexOf('\\');
                while (slash > 0)
                {
                    expectedDirs.Add(normalized.Substring(0, slash));
                    slash = normalized.LastIndexOf('\\', slash - 1);
                }
                if (item.IsDirectory) expectedDirs.Add(normalized);
            }
            if (report.Problems.Count > 0) return report;

            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var pending = new Stack<string>();
            pending.Push(deliveryRootAbsolute);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                IEnumerable<string> entries;
                try
                {
                    entries = Directory.EnumerateFileSystemEntries(dir).ToList();
                }
                catch (Exception ex)
                {
                    report.Problems.Add($"无法枚举目录 {Rel(deliveryRootAbsolute, dir)}：{ex.Message}");
                    continue;
                }

                foreach (string entry in entries)
                {
                    string rel = Rel(deliveryRootAbsolute, entry);
                    bool isDir = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
                    if (isDir)
                    {
                        if (!expectedDirs.Contains(rel))
                        {
                            report.Problems.Add($"交付树包含清单外的目录：{rel}");
                        }
                        seenDirs.Add(rel);
                        pending.Push(entry);
                        continue;
                    }

                    if (!expected.TryGetValue(rel, out var expectedItem) || expectedItem.IsDirectory)
                    {
                        report.Problems.Add($"交付树包含清单外的文件：{rel}");
                        continue;
                    }
                    var info = new FileInfo(entry);
                    if (info.Length != expectedItem.Size)
                    {
                        report.Problems.Add($"文件长度不一致：{rel}（期望 {expectedItem.Size}，实际 {info.Length}）");
                        continue;
                    }
                    if (mode == VerifyMode.StructureAndDigest)
                    {
                        uint actualCrc;
                        try
                        {
                            actualCrc = Crc32.ComputeFile(entry);
                        }
                        catch (Exception ex)
                        {
                            report.Problems.Add($"无法读取文件做摘要校验：{rel}（{ex.Message}）");
                            continue;
                        }
                        if (actualCrc != expectedItem.Crc)
                        {
                            report.Problems.Add($"文件摘要不一致：{rel}（期望 {expectedItem.Crc:X8}，实际 {actualCrc:X8}）");
                            continue;
                        }
                        report.DigestsChecked++;
                    }
                    seenFiles.Add(rel);
                    report.TotalBytes += expectedItem.Size;
                }
            }

            foreach (var item in manifest.Items)
            {
                string normalized = NormalizeRelative(item.Path);
                if (item.IsDirectory)
                {
                    if (!seenDirs.Contains(normalized)) report.Problems.Add($"缺少应保留的目录：{normalized}");
                }
                else if (!seenFiles.Contains(normalized))
                {
                    report.Problems.Add($"缺少应交付的文件：{normalized}");
                }
            }

            report.Files = seenFiles.Count;
            report.Directories = seenDirs.Count;
            if (log != null && report.Ok)
            {
                log($"校验通过：{report.Files} 个文件 / {report.Directories} 个目录 / {report.TotalBytes} 字节");
            }
            return report;
        }

        public static string NormalizeRelative(string relative)
        {
            string norm = (relative ?? "").Replace('/', '\\').Trim();
            while (norm.EndsWith("\\", StringComparison.Ordinal)) norm = norm.Substring(0, norm.Length - 1);
            return norm;
        }

        private static bool IsUnsafeRelative(string normalized)
        {
            if (Path.IsPathRooted(normalized)) return true;
            if (normalized.Contains(':')) return true;
            foreach (string segment in normalized.Split('\\'))
            {
                if (segment == ".." || segment == "." || segment.Length == 0) return true;
            }
            return false;
        }

        private static string Rel(string root, string fullPath)
        {
            string normalizedRoot = root.TrimEnd('\\') + "\\";
            if (fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(normalizedRoot.Length);
            }
            return Path.GetFileName(fullPath);
        }
    }
}
