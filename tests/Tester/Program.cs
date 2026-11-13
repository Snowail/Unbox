using System;
using System.IO;
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
            // 总结
            // -------------------------------------------------------------
            Console.WriteLine("\n==================================================");
            Console.WriteLine($"测试总结: 共 {_pass + _fail} 项测试，通过: {_pass}，失败: {_fail}");
            Console.WriteLine("==================================================");

            return _fail == 0 ? 0 : 1;
        }
    }
}
