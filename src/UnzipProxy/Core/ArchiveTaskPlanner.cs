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
