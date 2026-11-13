using System;
using System.IO;
using System.Text.RegularExpressions;

namespace UnzipProxy.Core
{
    public class ExtractionPlan
    {
        public required string SourceArchivePath { get; set; }
        public required string DestinationParentDir { get; set; }
        public required string TargetRootDirName { get; set; }
        public required string FinalTargetDirectory { get; set; }
        public required string StagingDirectory { get; set; }
    }

    public static class ArchiveTaskPlanner
    {
        private static readonly string[] CompoundSuffixes = new[] {
            ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lzma",
            ".tgz", ".tbz2", ".tbz", ".txz", ".tzst"
        };

        public static bool IsCompoundTar(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            foreach (var suf in CompoundSuffixes)
            {
                if (fileName.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // 剥离归档与分卷后缀，得到纯净的包名（不再从目标展示名反推）
        public static string GetArchiveBaseName(string filePath)
        {
            string fileName = Path.GetFileName(filePath);

            // 1. 复合 TAR 后缀
            foreach (var suf in CompoundSuffixes)
            {
                if (fileName.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                {
                    return fileName.Substring(0, fileName.Length - suf.Length);
                }
            }

            // 2. 分卷模式：.part01.rar / .part1.rar
            Match mPart = Regex.Match(fileName, @"^(.*?)\.part\d+\.rar$", RegexOptions.IgnoreCase);
            if (mPart.Success)
            {
                return mPart.Groups[1].Value;
            }

            // 3. 分卷模式：.7z.001 / .zip.001 / .001
            Match m001 = Regex.Match(fileName, @"^(.*?)(?:\.(?:7z|zip))?\.001$", RegexOptions.IgnoreCase);
            if (m001.Success)
            {
                return m001.Groups[1].Value;
            }

            // 4. 普通后缀
            return Path.GetFileNameWithoutExtension(fileName);
        }

        // 计算不冲突的目标最终目录
        public static string ResolveUniqueTargetDirectory(string parentDir, string baseName)
        {
            string candidate = Path.Combine(parentDir, baseName);
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }

            int index = 2;
            while (true)
            {
                string uniqueName = $"{baseName} ({index})";
                string fullPath = Path.Combine(parentDir, uniqueName);
                if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
                {
                    return fullPath;
                }
                index++;
            }
        }

        // 生成完整提取计划
        public static ExtractionPlan Plan(string archivePath)
        {
            string fullArchivePath = Path.GetFullPath(archivePath);
            string? parentDir = Path.GetDirectoryName(fullArchivePath);
            if (string.IsNullOrEmpty(parentDir))
            {
                parentDir = Directory.GetCurrentDirectory();
            }

            string baseName = GetArchiveBaseName(fullArchivePath);
            string finalDir = ResolveUniqueTargetDirectory(parentDir, baseName);

            // 在同卷创建独立临时暂存目录
            string stagingDir = Path.Combine(parentDir, $".unzip_staging_{Guid.NewGuid():N}");

            return new ExtractionPlan
            {
                SourceArchivePath = fullArchivePath,
                DestinationParentDir = parentDir,
                TargetRootDirName = Path.GetFileName(finalDir),
                FinalTargetDirectory = finalDir,
                StagingDirectory = stagingDir
            };
        }

        /// <summary>
        /// 同名唯一根目录折叠：只有当交付根下“恰好只有一个子目录、没有任何同级文件、
        /// 且该子目录名与压缩包自身的基名一致”时，才把该子目录选为交付根。
        /// 不再对目标展示名做“去掉 (2) 序号”的反推，也不在提交阶段逐项搬移文件。
        /// </summary>
        public static string? SelectCollapseRoot(string deliveryRoot, string archivePath)
        {
            try
            {
                if (!Directory.Exists(deliveryRoot)) return null;

                var entries = Directory.GetFileSystemEntries(deliveryRoot);
                if (entries.Length != 1) return null;

                string candidate = entries[0];
                var attributes = File.GetAttributes(candidate);
                if ((attributes & FileAttributes.Directory) == 0) return null;
                if ((attributes & FileAttributes.ReparsePoint) != 0) return null;

                string childName = Path.GetFileName(candidate);
                string baseName = GetArchiveBaseName(archivePath);
                if (string.IsNullOrEmpty(baseName)) return null;

                return string.Equals(childName, baseName, StringComparison.OrdinalIgnoreCase) ? candidate : null;
            }
            catch
            {
                return null;
            }
        }

        // 清理暂存目录（任务取消时）
        public static void CleanupStaging(ExtractionPlan plan)
        {
            try
            {
                if (Directory.Exists(plan.StagingDirectory))
                {
                    Directory.Delete(plan.StagingDirectory, true);
                }
            }
            catch
            {
                // 忽略清理异常
            }
        }
    }
}
