using System;
using System.IO;
using System.Diagnostics;
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
            Console.WriteLine("  Windows 双击自动解压代理 · 核心逻辑自动化测试");
            Console.WriteLine("==================================================\n");

            string testDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_output");
            if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
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

            // -------------------------------------------------------------
            // 2. D1 规则测试：目标同名递增后缀
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 2] D1 规则：目标同名递增后缀");
            string sampleZip = Path.Combine(testDir, "测试.zip");
            File.WriteAllText(sampleZip, "dummy");

            var plan1 = ArchiveTaskPlanner.Plan(sampleZip);
            Assert("D1: 首个目录规划为原包名", plan1.TargetRootDirName == "测试");

            // 模拟目标目录已被占用
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
            // 5. D2 规则测试：分卷家族定位与安全回收
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 5] D2 规则：分卷家族定位");
            string volDir = Path.Combine(testDir, "volumes");
            Directory.CreateDirectory(volDir);
            File.WriteAllText(Path.Combine(volDir, "game.part1.rar"), "p1");
            File.WriteAllText(Path.Combine(volDir, "game.part2.rar"), "p2");
            File.WriteAllText(Path.Combine(volDir, "game.part3.rar"), "p3");

            var family = ShellRecycleBin.FindVolumeFamily(Path.Combine(volDir, "game.part1.rar"));
            Assert("D2: 正确定位包含 3 个分卷的家族", family.Count == 3);

            // 网络路径回收安全检测
            Assert("D2: 网络 UNC 路径应判定不支持回收站", !ShellRecycleBin.IsRecycleBinSupported(@"\\server\share\file.zip"));

            // -------------------------------------------------------------
            // 6. 端到端测试：调用 UnzipWorker 解压真实归档
            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 6] 端到端：Worker 真实解压与过滤验证");
            string sampleArch = Path.Combine(testDir, "sample_arch.zip");
            string rawDir = Path.Combine(testDir, "raw");
            Directory.CreateDirectory(Path.Combine(rawDir, "release", "src"));
            Directory.CreateDirectory(Path.Combine(rawDir, "__MACOSX"));
            File.WriteAllText(Path.Combine(rawDir, "release", "src", "main.c"), "code");
            File.WriteAllText(Path.Combine(rawDir, ".DS_Store"), "junk");
            File.WriteAllText(Path.Combine(rawDir, "__MACOSX", "._main.c"), "junk");

            // 打包
            using (var p7z = Process.Start(new ProcessStartInfo
            {
                FileName = @"C:\PortableSoft\bin\7z.exe",
                Arguments = $"a \"{sampleArch}\" * -r",
                WorkingDirectory = rawDir,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                p7z?.WaitForExit();
            }

            string e2eStaging = Path.Combine(testDir, "e2e_staging");
            string workerExe = Path.GetFullPath(@"bin\UnzipWorker.exe");
            if (!File.Exists(workerExe))
            {
                workerExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\bin\UnzipWorker.exe"));
            }

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

            Assert("Worker E2E: 成功解出文件且保留顶层目录", hasExtractedCode);
            Assert("Worker E2E: 成功过滤 macOS 垃圾文件", !hasMacJunk);
            Assert("Worker E2E: 正常退出码 0", worker?.ExitCode == 0);

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
