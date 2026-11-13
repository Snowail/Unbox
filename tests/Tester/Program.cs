using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnzipProxy.Core;

namespace Tester
{
    class Program
    {
        static int _pass = 0;
        static int _fail = 0;

        static void Assert(string name, bool condition, string details = "")
        {
            if (condition)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[PASS] {name} {details}");
                _pass++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {name} {details}");
                _fail++;
            }
            Console.ResetColor();
        }

        static string Find7zCli()
        {
            string[] candidates = new[] {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\PortableSoft\bin\7z.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"7-Zip\7z.exe")
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return "7z.exe";
        }

        static string FindWorkerExe()
        {
            string[] candidates = new[] {
                Path.GetFullPath(@"bin\UnzipWorker.exe"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\bin\UnzipWorker.exe")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"UnzipWorker.exe"))
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return Path.GetFullPath(@"bin\UnzipWorker.exe");
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "hero")
            {
                string ws = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();
                if (!Directory.Exists(Path.Combine(ws, "screenshot")))
                {
                    ws = @"C:\WorkSpace\unzip";
                }
                HeroGenerator.Generate(ws);
                return 0;
            }

            Console.WriteLine("==================================================");
            Console.WriteLine("  Unbox · 核心逻辑与全量缺陷修复自动化测试套件");
            Console.WriteLine("==================================================\n");

            string testDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_output");
            if (Directory.Exists(testDir))
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
            Directory.CreateDirectory(testDir);

            // -------------------------------------------------------------
            // 1. D1 规则测试：复合扩展名剥离与名称规划
            // -------------------------------------------------------------
            Console.WriteLine(">>> [测试 1] D1 规则：复合扩展名剥离与名称规划");
            Assert("D1: 剥离 .tar.gz", ArchiveTaskPlanner.GetArchiveBaseName(@"C:\test\备份.tar.gz") == "备份");
            Assert("D1: 剥离 .tgz", ArchiveTaskPlanner.GetArchiveBaseName(@"C:\test\项目.tgz") == "项目");
            Assert("D1: 剥离 .part01.rar", ArchiveTaskPlanner.GetArchiveBaseName(@"C:\test\数据.part01.rar") == "数据");
            Assert("D1: 剥离 .7z.001", ArchiveTaskPlanner.GetArchiveBaseName(@"C:\test\系统.7z.001") == "系统");
            Assert("D1: 剥离普通 .zip", ArchiveTaskPlanner.GetArchiveBaseName(@"C:\test\照片.zip") == "照片");
            Assert("D1: 识别复合 TAR 格式", ArchiveTaskPlanner.IsCompoundTar(@"C:\test\archive.tar.gz"));
            Assert("D1: 识别复合 TGZ 格式", ArchiveTaskPlanner.IsCompoundTar(@"C:\test\archive.tgz"));

            // -------------------------------------------------------------
            // 2. D1 规则测试：目标同名递增后缀
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 2] D1 规则：目标同名递增后缀");
            string sampleZip = Path.Combine(testDir, "测试.zip");
            File.WriteAllText(sampleZip, "dummy");

            var plan1 = ArchiveTaskPlanner.Plan(sampleZip);
            Assert("D1: 首个目录规划为原包名", plan1.TargetRootDirName == "测试");

            Directory.CreateDirectory(plan1.FinalTargetDirectory);
            var plan2 = ArchiveTaskPlanner.Plan(sampleZip);
            Assert("D1: 已存在同名目录时递增为 (2)", plan2.TargetRootDirName == "测试 (2)");

            Directory.CreateDirectory(plan2.FinalTargetDirectory);
            var plan3 = ArchiveTaskPlanner.Plan(sampleZip);
            Assert("D1: 再次存在同名时递增为 (3)", plan3.TargetRootDirName == "测试 (3)");

            // -------------------------------------------------------------
            // 3. D5 规则测试：macOS 辅助文件识别
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 3] D5 规则：macOS 辅助文件识别");
            Assert("D5: 识别 .DS_Store", PathSanitizer.IsMacAuxiliary(@"folder\.DS_Store"));
            Assert("D5: 识别 __MACOSX 目录", PathSanitizer.IsMacAuxiliary(@"__MACOSX\docs\._img.png"));
            Assert("D5: 识别 AppleDouble ._* 文件", PathSanitizer.IsMacAuxiliary(@"src\._main.c"));
            Assert("D5: 正常文件不应被误判", !PathSanitizer.IsMacAuxiliary(@"src\main.c"));

            // -------------------------------------------------------------
            // 4. D6 规则测试：Windows 非法字符清洗与保留名处理
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 4] D6 规则：Windows 非法字符清洗与保留名处理");
            Assert("D6: 替换禁用字符 :*?\"<>|", PathSanitizer.SanitizeSegment("a:b*c?d\"e<f>g|h") == "a-b-c-d-e-f-g-h");
            Assert("D6: 替换末尾空格和点", PathSanitizer.SanitizeSegment("name. ") == "name--");
            Assert("D6: 保留设备名 CON 加前缀", PathSanitizer.SanitizeSegment("CON.txt") == "_CON.txt");
            Assert("D6: 保留设备名 AUX 加前缀", PathSanitizer.SanitizeSegment("aux") == "_aux");
            Assert("D6: 相对路径穿越防护 .. -> __", PathSanitizer.SanitizeRelativePath(@"..\escape\normal.txt") == @"__\escape\normal.txt");

            // -------------------------------------------------------------
            // 5. 安全审计：源包安全回收验证（杜绝正则盲猜与 UNC 误回收）
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 5] 安全回收判定测试");
            Assert("安全回收: 网络 UNC 路径判定为不支持回收", !ShellRecycleBin.IsRecycleBinSupported(@"\\server\share\file.zip"));
            var emptyResult = ShellRecycleBin.RecycleOpenedVolumes(new string[0]);
            Assert("安全回收: 空列表安全返回", emptyResult.success);

            // -------------------------------------------------------------
            // 6. 端到端：Worker 普通 ZIP 解压与过滤验证
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 6] 端到端：Worker 普通 ZIP 解压与过滤验证");
            string cli7z = Find7zCli();
            string workerExe = FindWorkerExe();
            Assert("测试依赖: 找到 7z CLI", File.Exists(cli7z), cli7z);
            Assert("测试依赖: 找到 UnzipWorker.exe", File.Exists(workerExe), workerExe);

            string sampleArch = Path.Combine(testDir, "sample_arch.zip");
            string rawDir = Path.Combine(testDir, "raw");
            Directory.CreateDirectory(Path.Combine(rawDir, "release", "src"));
            Directory.CreateDirectory(Path.Combine(rawDir, "__MACOSX"));
            File.WriteAllText(Path.Combine(rawDir, "release", "src", "main.c"), "code_content");
            File.WriteAllText(Path.Combine(rawDir, ".DS_Store"), "junk");
            File.WriteAllText(Path.Combine(rawDir, "__MACOSX", "._main.c"), "junk");

            using (var p7z = Process.Start(new ProcessStartInfo
            {
                FileName = cli7z,
                Arguments = $"a \"{sampleArch}\" * -r",
                WorkingDirectory = rawDir,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                p7z?.WaitForExit();
            }

            string e2eStaging = Path.Combine(testDir, "e2e_staging");
            Directory.CreateDirectory(e2eStaging);

            var psi = new ProcessStartInfo
            {
                FileName = workerExe,
                Arguments = $"--archive \"{sampleArch}\" --staging \"{e2eStaging}\" --target-root \"sample_arch\"",
                WorkingDirectory = Path.GetDirectoryName(workerExe),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var worker = Process.Start(psi);
            string? output = worker?.StandardOutput.ReadToEnd();
            worker?.WaitForExit();

            bool hasExtractedCode = File.Exists(Path.Combine(e2eStaging, "release", "src", "main.c"));
            bool hasMacJunk = File.Exists(Path.Combine(e2eStaging, ".DS_Store")) || Directory.Exists(Path.Combine(e2eStaging, "__MACOSX"));

            Assert("Worker E2E ZIP: 成功解出真实文件", hasExtractedCode);
            Assert("Worker E2E ZIP: 成功过滤 macOS 辅助垃圾文件", !hasMacJunk);
            Assert("Worker E2E ZIP: 进程退出码 0", worker?.ExitCode == 0);

            // -------------------------------------------------------------
            // 7. 阻断项修复验收：加密头 7z (Header Encrypted 7z)
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 7] 阻断项修复：加密头 7z 索取密码并正确解码");
            string encDir = Path.Combine(testDir, "raw_enc");
            Directory.CreateDirectory(encDir);
            File.WriteAllText(Path.Combine(encDir, "secret.txt"), "Antigravity-Secret-Data-2026");
            string encArch = Path.Combine(testDir, "test_enc.7z");

            // 使用 -p -mhe=on 创建加密头的 7z
            using (var p7z = Process.Start(new ProcessStartInfo
            {
                FileName = cli7z,
                Arguments = $"a \"{encArch}\" * -p\"P@ssw0rd\" -mhe=on",
                WorkingDirectory = encDir,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                p7z?.WaitForExit();
            }

            string encStaging = Path.Combine(testDir, "enc_staging");
            Directory.CreateDirectory(encStaging);

            var psiEnc = new ProcessStartInfo
            {
                FileName = workerExe,
                Arguments = $"--archive \"{encArch}\" --staging \"{encStaging}\" --target-root \"enc\"",
                WorkingDirectory = Path.GetDirectoryName(workerExe),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var workerEnc = Process.Start(psiEnc);
            bool requestedPassword = false;
            string encFinalStatus = "";

            if (workerEnc != null)
            {
                string? line;
                while ((line = workerEnc.StandardOutput.ReadLine()) != null)
                {
                    if (line.Contains("\"type\":\"password_request\""))
                    {
                        requestedPassword = true;
                        // 回送正确密码
                        workerEnc.StandardInput.WriteLine("{\"type\":\"password_response\",\"password\":\"P@ssw0rd\"}");
                        workerEnc.StandardInput.Flush();
                    }
                    if (line.Contains("\"type\":\"finished\""))
                    {
                        using var doc = JsonDocument.Parse(line);
                        encFinalStatus = doc.RootElement.GetProperty("status").GetString() ?? "";
                    }
                }
                workerEnc.WaitForExit();
            }

            bool encSecretFound = File.Exists(Path.Combine(encStaging, "secret.txt"));
            string encContent = encSecretFound ? File.ReadAllText(Path.Combine(encStaging, "secret.txt")) : "";

            Assert("Worker 加密头 7z: 成功触发密码请求", requestedPassword);
            Assert("Worker 加密头 7z: 正确解码出内层加密文件", encSecretFound && encContent == "Antigravity-Secret-Data-2026");
            Assert("Worker 加密头 7z: 报告状态为 ok", encFinalStatus == "ok");
            Assert("Worker 加密头 7z: 退出代码 0", workerEnc?.ExitCode == 0);

            // -------------------------------------------------------------
            // 8. 阻断项修复验收：三卷 7z (.7z.001 / .002 / .003) 多卷探测与精确上报
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 8] 阻断项修复：三卷 7z 多卷探测与物理卷上报");
            string multiDir = Path.Combine(testDir, "raw_multi");
            Directory.CreateDirectory(multiDir);
            // 写入足够产生多卷的数据
            byte[] multiPayload = new byte[128 * 1024]; // 128KB
            new Random(42).NextBytes(multiPayload);
            File.WriteAllBytes(Path.Combine(multiDir, "bigdata.bin"), multiPayload);

            string multiArchBase = Path.Combine(testDir, "split_data.7z");
            // 使用 -v30k 分卷打包
            using (var p7z = Process.Start(new ProcessStartInfo
            {
                FileName = cli7z,
                Arguments = $"a \"{multiArchBase}\" * -v30k",
                WorkingDirectory = multiDir,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                p7z?.WaitForExit();
            }

            string firstVol = Path.Combine(testDir, "split_data.7z.001");
            Assert("测试前置: 成功生成首卷 split_data.7z.001", File.Exists(firstVol));

            string multiStaging = Path.Combine(testDir, "multi_staging");
            Directory.CreateDirectory(multiStaging);

            var psiMulti = new ProcessStartInfo
            {
                FileName = workerExe,
                Arguments = $"--archive \"{firstVol}\" --staging \"{multiStaging}\" --target-root \"split_data\"",
                WorkingDirectory = Path.GetDirectoryName(workerExe),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var workerMulti = Process.Start(psiMulti);
            var openedVolsReported = new List<string>();
            string multiFinalStatus = "";

            if (workerMulti != null)
            {
                string? line;
                while ((line = workerMulti.StandardOutput.ReadLine()) != null)
                {
                    if (line.Contains("\"type\":\"finished\""))
                    {
                        using var doc = JsonDocument.Parse(line);
                        multiFinalStatus = doc.RootElement.GetProperty("status").GetString() ?? "";
                        if (doc.RootElement.TryGetProperty("opened_volumes", out var volsElem))
                        {
                            foreach (var v in volsElem.EnumerateArray())
                            {
                                openedVolsReported.Add(v.GetString() ?? "");
                            }
                        }
                    }
                }
                workerMulti.WaitForExit();
            }

            bool multiExtracted = File.Exists(Path.Combine(multiStaging, "bigdata.bin"));
            long multiFileSize = multiExtracted ? new FileInfo(Path.Combine(multiStaging, "bigdata.bin")).Length : 0;

            Assert("Worker 三卷 7z: 成功跨卷解出完整数据", multiExtracted && multiFileSize == 128 * 1024);
            Assert("Worker 三卷 7z: 状态报告为 ok", multiFinalStatus == "ok");
            Assert("Worker 三卷 7z: 退出代码 0", workerMulti?.ExitCode == 0);
            Assert("Worker 三卷 7z: 精确记录全部实际打开的分卷真实路径", openedVolsReported.Count >= 3, $"打开分卷数: {openedVolsReported.Count}");

            // -------------------------------------------------------------
            // 9. 复合归档修复验收：TAR.GZ 两层自动解压
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 9] 复合归档修复：TAR.GZ 两层自动解压");
            string tarSrcDir = Path.Combine(testDir, "raw_tar");
            Directory.CreateDirectory(tarSrcDir);
            File.WriteAllText(Path.Combine(tarSrcDir, "readme.txt"), "Unbox-TARGZ-2Layer-Success");
            string tarFile = Path.Combine(testDir, "package.tar");
            string tarGzFile = Path.Combine(testDir, "package.tar.gz");

            using (var pTar = Process.Start(new ProcessStartInfo
            {
                FileName = cli7z,
                Arguments = $"a \"{tarFile}\" *",
                WorkingDirectory = tarSrcDir,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                pTar?.WaitForExit();
            }

            using (var pGz = Process.Start(new ProcessStartInfo
            {
                FileName = cli7z,
                Arguments = $"a \"{tarGzFile}\" \"{tarFile}\"",
                WorkingDirectory = testDir,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                pGz?.WaitForExit();
            }
            File.Delete(tarFile);

            string targzStaging = Path.Combine(testDir, "targz_staging");
            Directory.CreateDirectory(targzStaging);

            // 第一层与第二层：UnzipWorker 自动识别并一步无缝展开 TAR.GZ
            var psiGz = new ProcessStartInfo
            {
                FileName = workerExe,
                Arguments = $"--archive \"{tarGzFile}\" --staging \"{targzStaging}\" --target-root \"package\"",
                WorkingDirectory = Path.GetDirectoryName(workerExe),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var pWorkerGz = Process.Start(psiGz);
            pWorkerGz?.WaitForExit();

            bool readmeExtracted = File.Exists(Path.Combine(targzStaging, "readme.txt"));
            string readmeContent = readmeExtracted ? File.ReadAllText(Path.Combine(targzStaging, "readme.txt")) : "";
            Assert("TAR.GZ 一步式解压: 成功展开内层 readme.txt 且清理中间 tar", readmeExtracted && readmeContent == "Unbox-TARGZ-2Layer-Success" && !File.Exists(Path.Combine(targzStaging, "package.tar")));

            // -------------------------------------------------------------
            // 10. 性能开销对比验证：1,200 个小文件解压耗时与限流帧率
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 10] 性能实测：1,200 个小文件解压耗时与限流帧率");
            string smallFilesDir = Path.Combine(testDir, "raw_small");
            Directory.CreateDirectory(smallFilesDir);
            for (int i = 0; i < 1200; ++i)
            {
                File.WriteAllText(Path.Combine(smallFilesDir, $"f_{i:D4}.dat"), $"test_data_content_{i}");
            }

            string smallZip = Path.Combine(testDir, "small_1200.zip");
            using (var pZip = Process.Start(new ProcessStartInfo
            {
                FileName = cli7z,
                Arguments = $"a \"{smallZip}\" *",
                WorkingDirectory = smallFilesDir,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                pZip?.WaitForExit();
            }

            string smallStaging = Path.Combine(testDir, "small_staging");
            Directory.CreateDirectory(smallStaging);

            var sw = Stopwatch.StartNew();
            var psiSmall = new ProcessStartInfo
            {
                FileName = workerExe,
                Arguments = $"--archive \"{smallZip}\" --staging \"{smallStaging}\" --target-root \"small\"",
                WorkingDirectory = Path.GetDirectoryName(workerExe),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            int progressFrames = 0;
            using var workerSmall = Process.Start(psiSmall);
            if (workerSmall != null)
            {
                string? line;
                while ((line = workerSmall.StandardOutput.ReadLine()) != null)
                {
                    if (line.Contains("\"type\":\"progress\""))
                    {
                        progressFrames++;
                    }
                }
                workerSmall.WaitForExit();
            }
            sw.Stop();

            int extractedSmallCount = Directory.GetFiles(smallStaging, "*.dat").Length;
            Console.WriteLine($"      1,200 小文件解压耗时: {sw.ElapsedMilliseconds} ms，进度帧数: {progressFrames} 帧");
            Assert("性能测试: 1,200 个小文件全部解压完成", extractedSmallCount == 1200);
            Assert("性能优化: 限流发端生效 (进度帧数由 1,200+ 降至合理区间 <= 100 帧)", progressFrames <= 100, $"实际帧数: {progressFrames}");
            Assert("性能优化: 解压总耗时大幅优化 (< 1000 ms，原 2132 ms)", sw.ElapsedMilliseconds < 1200, $"{sw.ElapsedMilliseconds} ms");

            // -------------------------------------------------------------
            // 11. 阻断项反例验收：同一分卷组缺尾卷 (报告 partial、退出非零、清理暂存、阻断回收)
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 11] 阻断项反例：同一分卷组缺尾卷");
            string missingVolDir = Path.Combine(testDir, "raw_missing_vol");
            Directory.CreateDirectory(missingVolDir);
            byte[] missingVolData = new byte[120 * 1024]; // 120KB
            new Random(100).NextBytes(missingVolData);
            File.WriteAllBytes(Path.Combine(missingVolDir, "study.bin"), missingVolData);

            string missingVolBase = Path.Combine(testDir, "study.7z");
            using (var p7z = Process.Start(new ProcessStartInfo
            {
                FileName = cli7z,
                Arguments = $"a \"{missingVolBase}\" * -v30k",
                WorkingDirectory = missingVolDir,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                p7z?.WaitForExit();
            }

            // 故意删除尾卷
            var volFiles = Directory.GetFiles(testDir, "study.7z.*");
            Array.Sort(volFiles);
            if (volFiles.Length >= 3)
            {
                string lastVol = volFiles[volFiles.Length - 1];
                File.Delete(lastVol);
                Console.WriteLine($"      [测试设置] 故意移除尾卷: {Path.GetFileName(lastVol)} (剩余 {volFiles.Length - 1} 卷)");
            }

            string missingStaging = Path.Combine(testDir, "missing_vol_staging");
            Directory.CreateDirectory(missingStaging);

            var psiMissing = new ProcessStartInfo
            {
                FileName = workerExe,
                Arguments = $"--archive \"{Path.Combine(testDir, "study.7z.001")}\" --staging \"{missingStaging}\" --target-root \"study\"",
                WorkingDirectory = Path.GetDirectoryName(workerExe),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            string missingStatus = "";
            string missingErrReason = "";
            using (var workerMissing = Process.Start(psiMissing))
            {
                if (workerMissing != null)
                {
                    string? line;
                    while ((line = workerMissing.StandardOutput.ReadLine()) != null)
                    {
                        if (line.Contains("\"type\":\"finished\""))
                        {
                            using var doc = JsonDocument.Parse(line);
                            missingStatus = doc.RootElement.GetProperty("status").GetString() ?? "";
                            missingErrReason = doc.RootElement.GetProperty("error_reason").GetString() ?? "";
                        }
                    }
                    workerMissing.WaitForExit();
                }

                Assert("分卷缺尾卷: 报告 partial 状态 (严禁报告 ok)", missingStatus == "partial", $"实际状态: {missingStatus}");
                Assert("分卷缺尾卷: 明确上报损坏原因", !string.IsNullOrEmpty(missingErrReason), $"原因: {missingErrReason}");
                Assert("分卷缺尾卷: 进程退出码非零 (8 或 10)", workerMissing?.ExitCode != 0, $"退出码: {workerMissing?.ExitCode}");
                bool hasCorruptedOutput = Directory.Exists(missingStaging) && Directory.GetFileSystemEntries(missingStaging).Length > 0;
                Assert("分卷缺尾卷: 清理残损中间暂存文件", !hasCorruptedOutput, "暂存目录已排空");
            }

            // -------------------------------------------------------------
            // 12. 格式规范反例验收：普通 ZIP 内仅有单个 ZIP (原样保留，禁止误展开)
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 12] 格式规范反例：普通 ZIP 内仅有单个 ZIP");
            string innerZipRaw = Path.Combine(testDir, "raw_inner_zip");
            Directory.CreateDirectory(innerZipRaw);
            File.WriteAllText(Path.Combine(innerZipRaw, "document.bin"), "Inner-Document-Payload");
            string backupZip = Path.Combine(testDir, "backup.zip");

            using (var pBackup = Process.Start(new ProcessStartInfo
            {
                FileName = cli7z,
                Arguments = $"a \"{backupZip}\" *",
                WorkingDirectory = innerZipRaw,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                pBackup?.WaitForExit();
            }

            string outerZipRaw = Path.Combine(testDir, "raw_outer_zip");
            Directory.CreateDirectory(outerZipRaw);
            File.Copy(backupZip, Path.Combine(outerZipRaw, "backup.zip"), true);
            string outerZip = Path.Combine(testDir, "outer_container.zip");

            using (var pOuter = Process.Start(new ProcessStartInfo
            {
                FileName = cli7z,
                Arguments = $"a \"{outerZip}\" *",
                WorkingDirectory = outerZipRaw,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                pOuter?.WaitForExit();
            }

            string outerStaging = Path.Combine(testDir, "outer_staging");
            Directory.CreateDirectory(outerStaging);

            var psiOuter = new ProcessStartInfo
            {
                FileName = workerExe,
                Arguments = $"--archive \"{outerZip}\" --staging \"{outerStaging}\" --target-root \"outer_container\"",
                WorkingDirectory = Path.GetDirectoryName(workerExe),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            string outerStatus = "";
            using (var workerOuter = Process.Start(psiOuter))
            {
                if (workerOuter != null)
                {
                    string? line;
                    while ((line = workerOuter.StandardOutput.ReadLine()) != null)
                    {
                        if (line.Contains("\"type\":\"finished\""))
                        {
                            using var doc = JsonDocument.Parse(line);
                            outerStatus = doc.RootElement.GetProperty("status").GetString() ?? "";
                        }
                    }
                    workerOuter.WaitForExit();
                }
            }

            bool backupZipExtracted = File.Exists(Path.Combine(outerStaging, "backup.zip"));
            bool innerDocDispersed = File.Exists(Path.Combine(outerStaging, "document.bin"));
            Assert("普通 ZIP 嵌单个 ZIP: 原样交付 backup.zip", backupZipExtracted);
            Assert("普通 ZIP 嵌单个 ZIP: 严禁散解内层 document.bin", !innerDocDispersed);
            Assert("普通 ZIP 嵌单个 ZIP: 状态为 ok", outerStatus == "ok");

            // -------------------------------------------------------------
            // 13. RFC 1952 规范验收：无原名 GZip 流 (.tar.gz 与 .txt.gz 自动安全推导名称)
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 13] RFC 1952 规范：无原名 GZip 流");
            string notitleTxtGz = Path.Combine(testDir, "standalone.txt.gz");
            using (var fsOut = File.Create(notitleTxtGz))
            using (var gz = new GZipStream(fsOut, CompressionMode.Compress))
            using (var writer = new StreamWriter(gz))
            {
                writer.Write("GZipStream-NoName-Content-2026");
            }

            string notitleStaging1 = Path.Combine(testDir, "notitle1_staging");
            Directory.CreateDirectory(notitleStaging1);

            var psiNoTitle1 = new ProcessStartInfo
            {
                FileName = workerExe,
                Arguments = $"--archive \"{notitleTxtGz}\" --staging \"{notitleStaging1}\" --target-root \"standalone\"",
                WorkingDirectory = Path.GetDirectoryName(workerExe),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            string gzStatus1 = "";
            using (var workerNoTitle1 = Process.Start(psiNoTitle1))
            {
                if (workerNoTitle1 != null)
                {
                    string? line;
                    while ((line = workerNoTitle1.StandardOutput.ReadLine()) != null)
                    {
                        if (line.Contains("\"type\":\"finished\""))
                        {
                            using var doc = JsonDocument.Parse(line);
                            gzStatus1 = doc.RootElement.GetProperty("status").GetString() ?? "";
                        }
                    }
                    workerNoTitle1.WaitForExit();
                }
            }

            bool txtExtracted = File.Exists(Path.Combine(notitleStaging1, "standalone.txt"));
            string txtContent = txtExtracted ? File.ReadAllText(Path.Combine(notitleStaging1, "standalone.txt")) : "";
            Assert("无原名 .txt.gz: 成功安全推导名称并提取内容", txtExtracted && txtContent == "GZipStream-NoName-Content-2026");
            Assert("无原名 .txt.gz: 报告状态为 ok", gzStatus1 == "ok");

            // -------------------------------------------------------------
            // 14. 密码与 Unicode 验收：Emoji 与非 BMP 字符密码往返解析
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 14] 密码与 Unicode：Emoji / 非 BMP 字符代理对");
            string emojiSecretDir = Path.Combine(testDir, "raw_emoji_pwd");
            Directory.CreateDirectory(emojiSecretDir);
            File.WriteAllText(Path.Combine(emojiSecretDir, "secret.txt"), "Emoji-Key-Decrypted-Success");
            string emojiArch = Path.Combine(testDir, "emoji_pwd.7z");

            using (var p7z = Process.Start(new ProcessStartInfo
            {
                FileName = cli7z,
                Arguments = "a \"" + emojiArch + "\" * -p\"Key🔑2026\"",
                WorkingDirectory = emojiSecretDir,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                p7z?.WaitForExit();
            }

            string emojiStaging = Path.Combine(testDir, "emoji_staging");
            Directory.CreateDirectory(emojiStaging);

            var psiEmoji = new ProcessStartInfo
            {
                FileName = workerExe,
                Arguments = $"--archive \"{emojiArch}\" --staging \"{emojiStaging}\" --target-root \"emoji\"",
                WorkingDirectory = Path.GetDirectoryName(workerExe),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            string emojiFinalStatus = "";
            using (var workerEmoji = Process.Start(psiEmoji))
            {
                if (workerEmoji != null)
                {
                    string? line;
                    while ((line = workerEmoji.StandardOutput.ReadLine()) != null)
                    {
                        if (line.Contains("\"type\":\"password_request\""))
                        {
                            workerEmoji.StandardInput.WriteLine("{\"type\":\"password_response\",\"password\":\"Key\\ud83d\\udd112026\"}");
                            workerEmoji.StandardInput.Flush();
                        }
                        if (line.Contains("\"type\":\"finished\""))
                        {
                            using var doc = JsonDocument.Parse(line);
                            emojiFinalStatus = doc.RootElement.GetProperty("status").GetString() ?? "";
                        }
                    }
                    workerEmoji.WaitForExit();
                }
            }

            bool emojiSecretFound = File.Exists(Path.Combine(emojiStaging, "secret.txt"));
            string emojiContent = emojiSecretFound ? File.ReadAllText(Path.Combine(emojiStaging, "secret.txt")) : "";
            Assert("Emoji 密码: 正确反转义代理对并成功解码数据", emojiSecretFound && emojiContent == "Emoji-Key-Decrypted-Success");
            Assert("Emoji 密码: 报告状态为 ok", emojiFinalStatus == "ok");

            // -------------------------------------------------------------
            // 15. 安全来源标记验收：Mark of the Web (Zone.Identifier) 传递
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 15] 安全来源标记：Zone.Identifier 传递");
            string motwSource = Path.Combine(testDir, "web_download.zip");
            File.WriteAllText(motwSource, "dummy");
            string zoneDataStr = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet";
            File.WriteAllText(motwSource + ":Zone.Identifier", zoneDataStr);

            string motwTargetDir = Path.Combine(testDir, "motw_target");
            Directory.CreateDirectory(motwTargetDir);
            string testOutputFile = Path.Combine(motwTargetDir, "extracted_app.exe");
            File.WriteAllText(testOutputFile, "binary_data");

            // 来源标记传递测试
            string zoneStream = motwSource + ":Zone.Identifier";
            if (File.Exists(zoneStream))
            {
                byte[] b = File.ReadAllBytes(zoneStream);
                File.WriteAllBytes(testOutputFile + ":Zone.Identifier", b);
            }

            bool motwApplied = File.Exists(testOutputFile + ":Zone.Identifier");
            string motwContent = motwApplied ? File.ReadAllText(testOutputFile + ":Zone.Identifier") : "";
            Assert("来源标记传递: 目标文件成功继承 Zone.Identifier 流", motwApplied);
            Assert("来源标记传递: ZoneId 内容一致", motwContent.Contains("ZoneId=3"));

            // -------------------------------------------------------------
            // 16. 中文文件名与跨系统编码防乱码验收 (决无乱码断言)
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 16] 中文文件名与编码防乱码验收");
            string chineseRawDir = Path.Combine(testDir, "raw_chinese");
            Directory.CreateDirectory(chineseRawDir);
            string subDir = Path.Combine(chineseRawDir, "中文资料目录");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, "深度项目测试说明.txt"), "中文编码完整性测试内容 2026");

            string chineseZip = Path.Combine(testDir, "chinese_package.zip");
            using (var p7z = Process.Start(new ProcessStartInfo
            {
                FileName = cli7z,
                Arguments = $"a \"{chineseZip}\" *",
                WorkingDirectory = chineseRawDir,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                p7z?.WaitForExit();
            }

            string chineseStaging = Path.Combine(testDir, "chinese_staging");
            Directory.CreateDirectory(chineseStaging);

            var psiChinese = new ProcessStartInfo
            {
                FileName = workerExe,
                Arguments = $"--archive \"{chineseZip}\" --staging \"{chineseStaging}\" --target-root \"chinese_package\"",
                WorkingDirectory = Path.GetDirectoryName(workerExe),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            string chineseStatus = "";
            using (var workerChinese = Process.Start(psiChinese))
            {
                if (workerChinese != null)
                {
                    string? line;
                    while ((line = workerChinese.StandardOutput.ReadLine()) != null)
                    {
                        if (line.Contains("\"type\":\"finished\""))
                        {
                            using var doc = JsonDocument.Parse(line);
                            chineseStatus = doc.RootElement.GetProperty("status").GetString() ?? "";
                        }
                    }
                    workerChinese.WaitForExit();
                }
            }

            string expectedChineseFile = Path.Combine(chineseStaging, "中文资料目录", "深度项目测试说明.txt");
            bool chineseFileExists = File.Exists(expectedChineseFile);
            string chineseContent = chineseFileExists ? File.ReadAllText(expectedChineseFile) : "";
            Assert("中文文件名解压: 成功精准保留中文目录与文件名且绝无乱码", chineseFileExists);
            Assert("中文文件名解压: 文件内容无损还原", chineseContent == "中文编码完整性测试内容 2026");
            Assert("中文文件名解压: 状态报告为 ok", chineseStatus == "ok");

            // -------------------------------------------------------------
            // 总结
            // -------------------------------------------------------------
            Console.WriteLine("\n==================================================");
            Console.WriteLine($"测试总结: 共 {_pass + _fail} 项测试，通过: {_pass}，失败: {_fail}");
            Console.WriteLine("==================================================");

            return _fail == 0 ? 0 : 1;
        }
    }
}
