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

        // 剥离归档与分卷后缀，得到纯净的包名
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
            string rootDirName = Path.GetFileName(finalDir);

            // 在同卷创建独立临时暂存目录
            string stagingDir = Path.Combine(parentDir, $".unzip_staging_{Guid.NewGuid():N}");

            return new ExtractionPlan
            {
                SourceArchivePath = fullArchivePath,
                DestinationParentDir = parentDir,
                TargetRootDirName = rootDirName,
                FinalTargetDirectory = finalDir,
                StagingDirectory = stagingDir
            };
        }

        // 如果压缩包解压后的根目录只有唯一一个和压缩包同名的文件夹，折叠这一级冗余同名目录
        public static bool TryCollapseRedundantSameNameFolder(string stagingDir, string archivePath, string targetRootDirName)
        {
            try
            {
                if (!Directory.Exists(stagingDir)) return false;

                string[] subDirs = Directory.GetDirectories(stagingDir);
                string[] subFiles = Directory.GetFiles(stagingDir);

                // 必须满足：根目录下仅有唯一一个子目录，且没有任何其它同级文件
                if (subDirs.Length != 1 || subFiles.Length != 0) return false;

                string nestedDirPath = subDirs[0];
                string nestedDirName = Path.GetFileName(nestedDirPath);

                string archiveBaseName = GetArchiveBaseName(archivePath);

                // 判断是否与压缩包同名（不区分大小写），或与目标规划根目录同名
                bool isSameName = string.Equals(nestedDirName, archiveBaseName, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(nestedDirName, targetRootDirName, StringComparison.OrdinalIgnoreCase);

                // 兼容处理可能带有的递增后缀，如 "希捷专修 (2)" 对应 "希捷专修"
                if (!isSameName && !string.IsNullOrEmpty(targetRootDirName))
                {
                    Match m = Regex.Match(targetRootDirName, @"^(.*?)(?: \(\d+\))?$");
                    if (m.Success && string.Equals(nestedDirName, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                    {
                        isSameName = true;
                    }
                }

                if (!isSameName) return false;

                // 执行目录折叠：将子目录内的所有项目上移至 stagingDir
                string tempNested = Path.Combine(stagingDir, $".unbox_collapse_{Guid.NewGuid():N}");
                Directory.Move(nestedDirPath, tempNested);

                foreach (var dir in Directory.GetDirectories(tempNested))
                {
                    string destDir = Path.Combine(stagingDir, Path.GetFileName(dir));
                    if (Directory.Exists(destDir))
                    {
                        Directory.Delete(destDir, true);
                    }
                    Directory.Move(dir, destDir);
                }

                foreach (var file in Directory.GetFiles(tempNested))
                {
                    string destFile = Path.Combine(stagingDir, Path.GetFileName(file));
                    if (File.Exists(destFile))
                    {
                        File.Delete(destFile);
                    }
                    File.Move(file, destFile);
                }

                Directory.Delete(tempNested, true);
                return true;
            }
            catch
            {
                // 折叠失败时降级保留原始层级，绝不影响整体解压交付
                return false;
            }
        }

        // 提交暂存目录到最终目标目录
        public static bool CommitStagingDirectory(ExtractionPlan plan, out string error)
        {
            error = "";
            try
            {
                if (!Directory.Exists(plan.StagingDirectory))
                {
                    error = "暂存目录不存在";
                    return false;
                }

                // 检查并折叠冗余的同名单根目录（如 希捷专修.zip -> 希捷专修/希捷专修/... 优化为 希捷专修/...）
                TryCollapseRedundantSameNameFolder(plan.StagingDirectory, plan.SourceArchivePath, plan.TargetRootDirName);

                // 如果目标目录碰巧刚被创建，重新规划名称
                string finalPath = plan.FinalTargetDirectory;
                if (Directory.Exists(finalPath) || File.Exists(finalPath))
                {
                    finalPath = ResolveUniqueTargetDirectory(plan.DestinationParentDir, plan.TargetRootDirName);
                    plan.FinalTargetDirectory = finalPath;
                    plan.TargetRootDirName = Path.GetFileName(finalPath);
                }

                Directory.Move(plan.StagingDirectory, finalPath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // 清理暂存目录（任务取消或失败时）
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
