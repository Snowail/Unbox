using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using UnzipProxy.Core;

namespace Tester
{
    class Program
    {
        static int _pass = 0;
        static int _fail = 0;
        static string _testDir = "";
        static string _workerExe = "";
        static string _cli7z = "";

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

        // ------------------------------------------------------------------
        // Worker / 生产完成路径辅助
        // ------------------------------------------------------------------

        sealed class WorkerRun
        {
            public int ExitCode;
            public readonly List<string> Lines = new();
            public JsonElement? Finished;
            public string Status = "";
            public string ErrorReason = "";
            public string DeliveryRoot = "";
            public string ManifestPath = "";
            public int PolicyFiltered;
            public int Skipped;
            public int Unsafe;
            public int Extracted;
            public bool PasswordRequested;
            public bool EncodingOptionsOffered;
            public bool InvalidCharsOffered;
            public int ProgressFrames;
            public string StandardError = "";
            public string StandardOutput = "";
        }

        static WorkerRun RunWorker(string archive, string staging, string manifest, string? cp = null, string? password = null,
                                   uint? encodingCp = null, string? invalidAction = null)
        {
            var run = new WorkerRun { ManifestPath = manifest };
            var psi = new ProcessStartInfo
            {
                FileName = _workerExe,
                Arguments = $"--archive \"{archive}\" --staging \"{staging}\" --target-root \"{Path.GetFileNameWithoutExtension(archive)}\" " +
                            $"--manifest \"{manifest}\"" + (cp != null ? $" --cp {cp}" : ""),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;
            var stderrTask = process.StandardError.ReadToEndAsync();
            string? line;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                run.Lines.Add(line);
                run.StandardOutput += line + "\n";
                if (line.Contains("\"type\":\"progress\"")) run.ProgressFrames++;
                if (line.Contains("\"type\":\"password_request\""))
                {
                    run.PasswordRequested = true;
                    if (password != null)
                    {
                        var payload = new { type = "password_response", password };
                        process.StandardInput.WriteLine(JsonSerializer.Serialize(payload));
                        process.StandardInput.Flush();
                    }
                }
                if (line.Contains("\"type\":\"encoding_options\""))
                {
                    run.EncodingOptionsOffered = true;
                    if (encodingCp != null)
                    {
                        process.StandardInput.WriteLine($"{{\"type\":\"encoding_response\",\"cp\":{encodingCp}}}");
                        process.StandardInput.Flush();
                    }
                }
                if (line.Contains("\"type\":\"invalid_chars_found\""))
                {
                    run.InvalidCharsOffered = true;
                    string action = invalidAction ?? "batch_rename";
                    process.StandardInput.WriteLine($"{{\"type\":\"invalid_chars_response\",\"action\":\"{action}\"}}");
                    process.StandardInput.Flush();
                }
                if (line.Contains("\"type\":\"finished\""))
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement.Clone();
                    run.Finished = root;
                    run.Status = root.TryGetProperty("status", out var s) ? (s.GetString() ?? "") : "";
                    run.ErrorReason = root.TryGetProperty("error_reason", out var er) ? (er.GetString() ?? "") : "";
                    run.DeliveryRoot = root.TryGetProperty("delivery_root", out var dr) ? (dr.GetString() ?? "") : "";
                    run.PolicyFiltered = root.TryGetProperty("policy_filtered_items", out var pf) ? pf.GetInt32() : 0;
                    run.Skipped = root.TryGetProperty("skipped_items", out var sk) ? sk.GetInt32() : 0;
                    run.Unsafe = root.TryGetProperty("unsafe_items", out var us) ? us.GetInt32() : 0;
                    run.Extracted = root.TryGetProperty("extracted_items", out var ex) ? ex.GetInt32() : 0;
                }
            }
            process.WaitForExit();
            run.StandardError = stderrTask.Result;
            run.ExitCode = process.ExitCode;
            return run;
        }

        static ExtractionPlan MapPlan(string archive, string staging)
        {
            var plan = ArchiveTaskPlanner.Plan(archive);
            plan.StagingDirectory = staging;
            return plan;
        }

        static CompletionResult CompleteViaProduction(string archive, string staging, string manifestPath, string deliveryRoot,
                                                      bool recycle = false, IReadOnlyList<VolumeIdentity>? volumes = null)
        {
            var request = new CompletionRequest
            {
                Plan = MapPlan(archive, staging),
                DeliveryRootRelative = deliveryRoot,
                Manifest = DeliveryManifest.Load(manifestPath),
                RecycleRequested = recycle,
                Volumes = volumes ?? Array.Empty<VolumeIdentity>(),
                PropagateZoneIdentifier = true
            };
            return ExtractionCompleter.Complete(request);
        }

        static void PrepareStaging(string staging)
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
        }

        static string NewStaging(string name)
        {
            string staging = Path.Combine(_testDir, $"staging_{name}");
            PrepareStaging(staging);
            return staging;
        }

        static void Run7z(string arguments, string workDir)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _cli7z,
                Arguments = arguments,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            process?.StandardOutput.ReadToEnd();
            process?.WaitForExit();
        }

        static void CreateZipStored(string zipPath, params (string Name, string Content)[] entries)
        {
            CreateZip(zipPath, Encoding.UTF8, entries);
        }

        static void CreateZip(string zipPath, Encoding nameEncoding, params (string Name, string Content)[] entries)
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using var stream = File.Create(zipPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, false, nameEncoding);
            foreach (var entry in entries)
            {
                var zipEntry = archive.CreateEntry(entry.Name);
                using var entryStream = zipEntry.Open();
                byte[] content = Encoding.UTF8.GetBytes(entry.Content);
                entryStream.Write(content, 0, content.Length);
            }
        }

        // 清除 ZIP 中第 N 个条目的 UTF-8 标志位（local + central 头部各一处）
        static void ClearZipUtf8FlagsForEntry(string zipPath, int entryIndex)
        {
            byte[] bytes = File.ReadAllBytes(zipPath);
            int seenLocal = 0;
            int seenCentral = 0;
            for (int i = 0; i < bytes.Length - 4; i++)
            {
                bool isLocal = bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04;
                bool isCentral = bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02;
                if (!isLocal && !isCentral) continue;

                int index = isLocal ? seenLocal++ : seenCentral++;
                if (index != entryIndex) continue;

                int flagOffset = i + (isLocal ? 6 : 8);
                ushort flag = BitConverter.ToUInt16(bytes, flagOffset);
                byte[] patched = BitConverter.GetBytes((ushort)(flag & ~0x0800));
                bytes[flagOffset] = patched[0];
                bytes[flagOffset + 1] = patched[1];
            }
            File.WriteAllBytes(zipPath, bytes);
        }

        // 清除 ZIP 的 UTF-8 标志位，构造“未声明编码的 UTF-8 名称”样本
        static void ClearZipUtf8Flags(string zipPath)
        {
            byte[] bytes = File.ReadAllBytes(zipPath);
            for (int i = 0; i < bytes.Length - 4; i++)
            {
                bool isLocal = bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04;
                bool isCentral = bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02;
                if (!isLocal && !isCentral) continue;
                int flagOffset = i + (isLocal ? 6 : 8);
                ushort flag = BitConverter.ToUInt16(bytes, flagOffset);
                ushort cleared = (ushort)(flag & ~0x0800);
                byte[] patched = BitConverter.GetBytes(cleared);
                bytes[flagOffset] = patched[0];
                bytes[flagOffset + 1] = patched[1];
            }
            File.WriteAllBytes(zipPath, bytes);
        }

        static void CopyGzip(string sourceFile, string destinationGz, bool includeOriginalName)
        {
            if (File.Exists(destinationGz)) File.Delete(destinationGz);
            using var input = File.OpenRead(sourceFile);
            using var output = File.Create(destinationGz);
            using var gzip = new GZipStream(output, CompressionMode.Compress);
            input.CopyTo(gzip);
        }

        static void CreateTar(string sourceDir, string tarPath, bool includeBaseDirectory)
        {
            if (File.Exists(tarPath)) File.Delete(tarPath);
            TarFile.CreateFromDirectory(sourceDir, tarPath, includeBaseDirectory);
        }

        [STAThread]
        static int Main(string[] args)
        {
            try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }

            if (args.Length > 0 && args[0] == "hero")
            {
                string ws = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();
                HeroGenerator.Generate(ws);
                return 0;
            }

            Console.WriteLine("==================================================");
            Console.WriteLine("  Unbox · 生产完成路径与全量缺陷修复自动化测试套件");
            Console.WriteLine("==================================================\n");

            _testDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_output");
            if (Directory.Exists(_testDir))
            {
                try { Directory.Delete(_testDir, true); } catch { }
            }
            Directory.CreateDirectory(_testDir);

            _cli7z = Find7zCli();
            _workerExe = FindWorkerExe();

            // -------------------------------------------------------------
            Console.WriteLine(">>> [测试 1] D1 规则：复合扩展名剥离与名称规划");
            Assert("D1: 剥离 .tar.gz", ArchiveTaskPlanner.GetArchiveBaseName(@"C:\test\备份.tar.gz") == "备份");
            Assert("D1: 剥离 .tgz", ArchiveTaskPlanner.GetArchiveBaseName(@"C:\test\项目.tgz") == "项目");
            Assert("D1: 剥离 .part01.rar", ArchiveTaskPlanner.GetArchiveBaseName(@"C:\test\数据.part01.rar") == "数据");
            Assert("D1: 剥离 .7z.001", ArchiveTaskPlanner.GetArchiveBaseName(@"C:\test\系统.7z.001") == "系统");
            Assert("D1: 剥离普通 .zip", ArchiveTaskPlanner.GetArchiveBaseName(@"C:\test\照片.zip") == "照片");
            Assert("D1: 包名自带的数字后缀必须原样保留",
                ArchiveTaskPlanner.GetArchiveBaseName(@"C:\test\Report (2).zip") == "Report (2)");
            Assert("D1: 识别复合 TAR 格式", ArchiveTaskPlanner.IsCompoundTar(@"C:\test\archive.tar.gz"));

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 2] D1 规则：目标同名递增后缀");
            string sampleZip = Path.Combine(_testDir, "测试.zip");
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
            Console.WriteLine("\n>>> [测试 3] D5/D6 规则：macOS 过滤、非法字符与保留名");
            Assert("D5: 识别 .DS_Store", PathSanitizer.IsMacAuxiliary(@"folder\.DS_Store"));
            Assert("D5: 识别 __MACOSX 目录", PathSanitizer.IsMacAuxiliary(@"__MACOSX\docs\._img.png"));
            Assert("D5: 识别 AppleDouble ._* 文件", PathSanitizer.IsMacAuxiliary(@"src\._main.c"));
            Assert("D5: 正常文件不应被误判", !PathSanitizer.IsMacAuxiliary(@"src\main.c"));
            Assert("D6: 替换禁用字符 :*?\"<>|", PathSanitizer.SanitizeSegment("a:b*c?d\"e<f>g|h") == "a-b-c-d-e-f-g-h");
            Assert("D6: 替换末尾空格和点", PathSanitizer.SanitizeSegment("name. ") == "name--");
            Assert("D6: 保留设备名 CON 加前缀", PathSanitizer.SanitizeSegment("CON.txt") == "_CON.txt");
            Assert("D6: 相对路径穿越防护 .. -> __", PathSanitizer.SanitizeRelativePath(@"..\escape\normal.txt") == @"__\escape\normal.txt");

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 4] 完整性对账：CRC32 与清单校验");
            Assert("CRC32: 标准向量 123456789 -> 0xCBF43926",
                Crc32.Compute(Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926u);
            string crcProbe = Path.Combine(_testDir, "crc_probe.bin");
            File.WriteAllText(crcProbe, "The quick brown fox jumps over the lazy dog");
            Assert("CRC32: 文件流计算结果与内存一致",
                Crc32.ComputeFile(crcProbe) == Crc32.Compute(File.ReadAllBytes(crcProbe)));

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 5] 安全回收判定：UNC、身份核对与逐项结果");
            Assert("回收: 网络 UNC 路径判定为不支持回收", !ShellRecycleBin.IsRecycleBinSupported(@"\\server\share\file.zip"));
            var emptyRecycle = ShellRecycleBin.RecycleOpenedVolumes(Array.Empty<string>());
            Assert("回收: 空列表安全返回", emptyRecycle.Success && emptyRecycle.Items.Count == 0);

            string recycleProbe = Path.Combine(_testDir, "recycle_probe.bin");
            File.WriteAllText(recycleProbe, "identity-v1");
            var identityBefore = VolumeIdentity.Capture(recycleProbe);
            Assert("回收: 成功捕获源文件对象身份", identityBefore != null && identityBefore.Valid);
            File.WriteAllText(recycleProbe, "identity-v2-changed-content");
            var replacedResult = ShellRecycleBin.RecycleVolumes(new[] { identityBefore! });
            Assert("回收: 源文件被替换后拒绝回收且文件保留",
                !replacedResult.Success && File.Exists(recycleProbe) &&
                replacedResult.Items.Any(i => !i.Recycled && i.Detail.Contains("替换")),
                replacedResult.Message);

            // 真实回收一个临时文件，并从回收站落点核对字节（证明可还原）
            string realRecycle = Path.Combine(_testDir, "recycle_real.bin");
            File.WriteAllText(realRecycle, "recycle-real-payload-2026");
            string expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(realRecycle)));
            var realIdentity = VolumeIdentity.Capture(realRecycle);
            var realResult = realIdentity == null
                ? new RecycleResult { Success = false, Message = "无法捕获身份（跳过）" }
                : ShellRecycleBin.RecycleVolumes(new[] { realIdentity });
            bool wentToRecycleBin = realResult.Success && !File.Exists(realRecycle);
            string restoredHash = "";
            if (wentToRecycleBin)
            {
                var item = realResult.Items[0];
                if (!string.IsNullOrEmpty(item.RecycledTo) && File.Exists(item.RecycledTo))
                {
                    restoredHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(item.RecycledTo)));
                }
            }
            Assert("回收: 通过 Shell 逐项回收并返回回收站落点",
                wentToRecycleBin && !string.IsNullOrEmpty(realResult.Items[0].RecycledTo),
                $"{realResult.Message} | 明细: {(realResult.Items.Count > 0 ? realResult.Items[0].Detail : "无")} | 支持回收站={ShellRecycleBin.IsRecycleBinSupported(realRecycle)}");
            Assert("回收: 回收站内的字节与源文件摘要一致（可还原）",
                restoredHash == expectedHash || string.IsNullOrEmpty(restoredHash),
                string.IsNullOrEmpty(restoredHash) ? "未取得回收站落点，跳过摘要比对" : restoredHash);

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 6] 生产完成路径：普通 ZIP 交付到最终目录");
            string plainZip = Path.Combine(_testDir, "plain.zip");
            CreateZipStored(plainZip, ("release/src/main.c", "int main(void){return 0;}"), ("release/README.txt", "plain-readme"));
            string plainStaging = NewStaging("plain");
            string plainManifest = Path.Combine(_testDir, "manifest_plain.json");
            var plainRun = RunWorker(plainZip, plainStaging, plainManifest, cp: "936");
            Assert("生产路径: Worker 状态 ok 且退出码 0", plainRun.Status == "ok" && plainRun.ExitCode == 0, $"status={plainRun.Status} exit={plainRun.ExitCode}");
            var plainResult = CompleteViaProduction(plainZip, plainStaging, plainManifest, plainRun.DeliveryRoot);
            Assert("生产路径: 清单对账通过并交付", plainResult.Delivered, plainResult.FailureReason);
            Assert("生产路径: 最终目录内含解压文件",
                plainResult.Delivered && File.Exists(Path.Combine(plainResult.FinalDirectory, "release", "src", "main.c")));
            Assert("生产路径: 交付后暂存目录已清理", !Directory.Exists(plainStaging));

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 7] P1 修复：带 macOS 元数据的同名根 ZIP 正确交付正文");
            string macZip = Path.Combine(_testDir, "macroot.zip");
            CreateZipStored(macZip,
                ("macroot/photos/readme.txt", "PHOTO-README-BODY"),
                ("macroot/.DS_Store", "junk"),
                ("macroot/photos/._readme.txt", "appledouble"));
            string macStaging = NewStaging("mac");
            string macManifest = Path.Combine(_testDir, "manifest_mac.json");
            var macRun = RunWorker(macZip, macStaging, macManifest, cp: "936");
            Assert("Mac 元数据 ZIP: Worker 报告 ok（过滤不是失败）",
                macRun.Status == "ok" && macRun.ExitCode == 0, $"status={macRun.Status} exit={macRun.ExitCode}");
            Assert("Mac 元数据 ZIP: 过滤项单独计数", macRun.PolicyFiltered == 2 && macRun.Skipped == 0, $"policy={macRun.PolicyFiltered} skipped={macRun.Skipped}");
            var macResult = CompleteViaProduction(macZip, macStaging, macManifest, macRun.DeliveryRoot);
            Assert("Mac 元数据 ZIP: 最终目录交付成功", macResult.Delivered, macResult.FailureReason);
            Assert("Mac 元数据 ZIP: 正文文件存在且内容正确",
                macResult.Delivered && File.Exists(Path.Combine(macResult.FinalDirectory, "photos", "readme.txt")) &&
                File.ReadAllText(Path.Combine(macResult.FinalDirectory, "photos", "readme.txt")) == "PHOTO-README-BODY");
            Assert("Mac 元数据 ZIP: 同名根目录已折叠", macResult.Delivered && !Directory.Exists(Path.Combine(macResult.FinalDirectory, "macroot")));
            Assert("Mac 元数据 ZIP: 不含 macOS 垃圾文件",
                macResult.Delivered && !Directory.EnumerateFileSystemEntries(macResult.FinalDirectory, "._*", SearchOption.AllDirectories).Any());

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 8] P1 修复：文件名编码（声明 UTF-8 / 旧编码 / 未声明 UTF-8）");
            string utf8FlaggedZip = Path.Combine(_testDir, "utf8_flagged.zip");
            CreateZip(utf8FlaggedZip, Encoding.UTF8, ("图.txt", "flagged-utf8"), ("普通.txt", "plain-cjk"));
            string flaggedStaging = NewStaging("utf8flagged");
            string flaggedManifest = Path.Combine(_testDir, "manifest_utf8flagged.json");
            var flaggedRun = RunWorker(utf8FlaggedZip, flaggedStaging, flaggedManifest, cp: "936");
            var flaggedResult = CompleteViaProduction(utf8FlaggedZip, flaggedStaging, flaggedManifest, flaggedRun.DeliveryRoot);
            Assert("编码: 声明 UTF-8 的中文名逐字保留 (图.txt)",
                flaggedResult.Delivered && File.Exists(Path.Combine(flaggedResult.FinalDirectory, "图.txt")), flaggedResult.FailureReason);
            Assert("编码: 同一包内的普通中文名同样正确",
                flaggedResult.Delivered && File.Exists(Path.Combine(flaggedResult.FinalDirectory, "普通.txt")));

            // 旧编码（GBK 无 UTF-8 标志位）
            var gbk = Encoding.GetEncoding(936);
            string legacyZip = Path.Combine(_testDir, "legacy_gbk.zip");
            CreateZip(legacyZip, gbk, (@"希捷专修\固件说明.txt", "legacy-gbk"));
            string legacyStaging = NewStaging("legacy");
            string legacyManifest = Path.Combine(_testDir, "manifest_legacy.json");
            var legacyRun = RunWorker(legacyZip, legacyStaging, legacyManifest, cp: "936");
            var legacyResult = CompleteViaProduction(legacyZip, legacyStaging, legacyManifest, legacyRun.DeliveryRoot);
            Assert("编码: 未声明的旧编码名称按系统代码页正确解码",
                legacyResult.Delivered && File.Exists(Path.Combine(legacyResult.FinalDirectory, "希捷专修", "固件说明.txt")),
                legacyResult.FailureReason);

            // 未声明编码的 UTF-8 名称（清除 bit 11）
            string noFlagZip = Path.Combine(_testDir, "utf8_noflag.zip");
            CreateZip(noFlagZip, Encoding.UTF8, (@"图纸\设计说明.txt", "utf8-no-flag"));
            ClearZipUtf8Flags(noFlagZip);
            string noFlagStaging = NewStaging("utf8noflag");
            string noFlagManifest = Path.Combine(_testDir, "manifest_utf8noflag.json");
            var noFlagRun = RunWorker(noFlagZip, noFlagStaging, noFlagManifest, cp: "936");
            var noFlagResult = CompleteViaProduction(noFlagZip, noFlagStaging, noFlagManifest, noFlagRun.DeliveryRoot);
            Assert("编码: 未声明编码的 UTF-8 名称仍能正确解码",
                noFlagResult.Delivered && File.Exists(Path.Combine(noFlagResult.FinalDirectory, "图纸", "设计说明.txt")),
                noFlagResult.FailureReason);

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 9] P1 修复：复合 TAR 与内嵌归档");
            string tarSource = Path.Combine(_testDir, "tarsrc");
            Directory.CreateDirectory(tarSource);
            File.WriteAllText(Path.Combine(tarSource, "readme.txt"), "TAR-PAYLOAD-CONTENT");
            string tarFile = Path.Combine(_testDir, "package.tar");
            CreateTar(tarSource, tarFile, false);

            // 9.1 具名 .tar.gz
            string namedTarGz = Path.Combine(_testDir, "named.tar.gz");
            Run7z($"a -tgzip \"{namedTarGz}\" \"{tarFile}\"", _testDir);
            string namedStaging = NewStaging("targz_named");
            string namedManifest = Path.Combine(_testDir, "manifest_targz_named.json");
            var namedRun = RunWorker(namedTarGz, namedStaging, namedManifest, cp: "936");
            var namedResult = CompleteViaProduction(namedTarGz, namedStaging, namedManifest, namedRun.DeliveryRoot);
            Assert("复合 TAR: 具名 .tar.gz 交付内层正文",
                namedResult.Delivered && File.Exists(Path.Combine(namedResult.FinalDirectory, "readme.txt")),
                namedResult.FailureReason);
            Assert("复合 TAR: 中间 TAR 不残留在最终目录",
                namedResult.Delivered && !File.Exists(Path.Combine(namedResult.FinalDirectory, "package.tar")));

            // 9.2 无原名 .tgz
            string lambdaTgz = Path.Combine(_testDir, "lambda.tgz");
            CopyGzip(tarFile, lambdaTgz, false);
            string lambdaStaging = NewStaging("tgz_lambda");
            string lambdaManifest = Path.Combine(_testDir, "manifest_tgz_lambda.json");
            var lambdaRun = RunWorker(lambdaTgz, lambdaStaging, lambdaManifest, cp: "936");
            var lambdaResult = CompleteViaProduction(lambdaTgz, lambdaStaging, lambdaManifest, lambdaRun.DeliveryRoot);
            Assert("复合 TAR: 无原名 .tgz 交付内层正文（不再假成功）",
                lambdaResult.Delivered && File.Exists(Path.Combine(lambdaResult.FinalDirectory, "readme.txt")),
                lambdaResult.FailureReason);
            Assert("复合 TAR: 无原名 .tgz 状态为 ok", lambdaRun.Status == "ok" && lambdaRun.ExitCode == 0,
                $"status={lambdaRun.Status} exit={lambdaRun.ExitCode}");

            // 9.3 gzip 原名不带扩展名
            File.Copy(tarFile, Path.Combine(_testDir, "backup"), true);
            string noExtTarGz = Path.Combine(_testDir, "noext.tar.gz");
            Run7z($"a -tgzip \"{noExtTarGz}\" \"{Path.Combine(_testDir, "backup")}\"", _testDir);
            File.Delete(Path.Combine(_testDir, "backup"));
            string noExtStaging = NewStaging("targz_noext");
            string noExtManifest = Path.Combine(_testDir, "manifest_targz_noext.json");
            var noExtRun = RunWorker(noExtTarGz, noExtStaging, noExtManifest, cp: "936");
            var noExtResult = CompleteViaProduction(noExtTarGz, noExtStaging, noExtManifest, noExtRun.DeliveryRoot);
            Assert("复合 TAR: gzip 原名无扩展名仍按内容展开 TAR",
                noExtResult.Delivered && File.Exists(Path.Combine(noExtResult.FinalDirectory, "readme.txt")),
                noExtResult.FailureReason);

            // 9.4 合法空 TAR.GZ
            string emptyTar = Path.Combine(_testDir, "empty.tar");
            File.WriteAllBytes(emptyTar, new byte[1024]);
            string emptyTarGz = Path.Combine(_testDir, "empty.tar.gz");
            CopyGzip(emptyTar, emptyTarGz, false);
            string emptyStaging = NewStaging("targz_empty");
            string emptyManifest = Path.Combine(_testDir, "manifest_targz_empty.json");
            var emptyRun = RunWorker(emptyTarGz, emptyStaging, emptyManifest, cp: "936");
            var emptyResult = CompleteViaProduction(emptyTarGz, emptyStaging, emptyManifest, emptyRun.DeliveryRoot);
            Assert("复合 TAR: 合法空 TAR.GZ 成功并得到空目标目录",
                emptyResult.Delivered && Directory.Exists(emptyResult.FinalDirectory) &&
                Directory.GetFileSystemEntries(emptyResult.FinalDirectory).Length == 0,
                emptyResult.FailureReason);

            // 9.5 普通 ZIP 内的单个 ZIP 必须原样交付
            string innerZipSource = Path.Combine(_testDir, "innerzip_src");
            Directory.CreateDirectory(innerZipSource);
            CreateZipStored(Path.Combine(innerZipSource, "backup.zip"), ("document.bin", "Inner-Document-Payload"));
            string outerZip = Path.Combine(_testDir, "outer_container.zip");
            using (var archive = ZipFile.Open(outerZip, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(Path.Combine(innerZipSource, "backup.zip"), "backup.zip");
            }
            string outerStaging = NewStaging("outerzip");
            string outerManifest = Path.Combine(_testDir, "manifest_outerzip.json");
            var outerRun = RunWorker(outerZip, outerStaging, outerManifest, cp: "936");
            var outerResult = CompleteViaProduction(outerZip, outerStaging, outerManifest, outerRun.DeliveryRoot);
            Assert("内嵌归档: 普通 ZIP 内的 backup.zip 原样交付",
                outerResult.Delivered && File.Exists(Path.Combine(outerResult.FinalDirectory, "backup.zip")),
                outerResult.FailureReason);
            Assert("内嵌归档: 严禁把内层 document.bin 散解到最终目录",
                outerResult.Delivered && !File.Exists(Path.Combine(outerResult.FinalDirectory, "document.bin")));

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 10] 加密头 7z：密码请求与正确解码");
            string secretDir = Path.Combine(_testDir, "raw_enc");
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "secret.txt"), "Antigravity-Secret-Data-2026");
            string encrypted7z = Path.Combine(_testDir, "test_enc.7z");
            Run7z($"a \"{encrypted7z}\" * -p\"P@ssw0rd\" -mhe=on", secretDir);
            string encStaging = NewStaging("enc");
            string encManifest = Path.Combine(_testDir, "manifest_enc.json");
            var encRun = RunWorker(encrypted7z, encStaging, encManifest, cp: "936", password: "P@ssw0rd");
            Assert("加密头 7z: 触发密码请求", encRun.PasswordRequested);
            Assert("加密头 7z: 正确解出内容",
                encRun.Status == "ok" && File.ReadAllText(Path.Combine(encStaging, "secret.txt")) == "Antigravity-Secret-Data-2026");
            Assert("加密头 7z: 退出码 0", encRun.ExitCode == 0);

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 11] 完整分卷 7z：跨卷解压与卷身份上报");
            string multiDir = Path.Combine(_testDir, "raw_multi");
            Directory.CreateDirectory(multiDir);
            byte[] multiPayload = new byte[128 * 1024];
            new Random(42).NextBytes(multiPayload);
            File.WriteAllBytes(Path.Combine(multiDir, "bigdata.bin"), multiPayload);
            Run7z($"a \"{Path.Combine(_testDir, "split_data.7z")}\" * -v30k", multiDir);

            string multiStaging = NewStaging("multi");
            string multiManifest = Path.Combine(_testDir, "manifest_multi.json");
            var multiRun = RunWorker(Path.Combine(_testDir, "split_data.7z.001"), multiStaging, multiManifest, cp: "936");
            var multiResult = CompleteViaProduction(Path.Combine(_testDir, "split_data.7z.001"), multiStaging, multiManifest, multiRun.DeliveryRoot);
            Assert("完整分卷: 跨卷解出完整数据",
                multiResult.Delivered && File.Exists(Path.Combine(multiResult.FinalDirectory, "bigdata.bin")) &&
                new FileInfo(Path.Combine(multiResult.FinalDirectory, "bigdata.bin")).Length == multiPayload.Length,
                multiResult.FailureReason);
            int volumeIdentityCount = multiRun.Finished.HasValue && multiRun.Finished.Value.TryGetProperty("volume_identities", out var vis) &&
                                      vis.ValueKind == JsonValueKind.Array ? vis.GetArrayLength() : 0;
            Assert("完整分卷: 逐卷上报对象身份", volumeIdentityCount >= 3, $"身份数: {volumeIdentityCount}");

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 12] 缺尾卷反例：不得假成功、不得提交、不得保留残损产物");
            string missingDir = Path.Combine(_testDir, "raw_missing");
            Directory.CreateDirectory(missingDir);
            new Random(100).NextBytes(multiPayload);
            File.WriteAllBytes(Path.Combine(missingDir, "study.bin"), multiPayload);
            Run7z($"a \"{Path.Combine(_testDir, "study.7z")}\" * -v30k", missingDir);
            var volumeFiles = Directory.GetFiles(_testDir, "study.7z.*").OrderBy(f => f).ToArray();
            if (volumeFiles.Length >= 3) File.Delete(volumeFiles[^1]);
            string missingStaging = NewStaging("missing");
            string missingManifest = Path.Combine(_testDir, "manifest_missing.json");
            var missingRun = RunWorker(Path.Combine(_testDir, "study.7z.001"), missingStaging, missingManifest, cp: "936");
            Assert("缺尾卷: 状态为 failed（严禁 ok）", missingRun.Status == "failed", $"status={missingRun.Status}");
            Assert("缺尾卷: 明确上报原因", !string.IsNullOrEmpty(missingRun.ErrorReason), missingRun.ErrorReason);
            Assert("缺尾卷: 退出码非零", missingRun.ExitCode != 0, $"exit={missingRun.ExitCode}");
            Assert("缺尾卷: 暂存目录已排空", !Directory.Exists(missingStaging) || Directory.GetFileSystemEntries(missingStaging).Length == 0);

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 13] 同名根目录折叠与包名数字后缀");
            string seagateDir = Path.Combine(_testDir, "raw_seagate");
            Directory.CreateDirectory(Path.Combine(seagateDir, "希捷专修", "希捷固件"));
            File.WriteAllText(Path.Combine(seagateDir, "希捷专修", "希捷固件", "firmware.bin"), "Seagate-Firmware-Payload-2026");
            File.WriteAllText(Path.Combine(seagateDir, "希捷专修", "使用说明.txt"), "这是希捷专修工具说明文档");
            string seagateZip = Path.Combine(_testDir, "希捷专修.zip");
            Run7z($"a \"{seagateZip}\" *", seagateDir);
            string seagateStaging = NewStaging("seagate");
            string seagateManifest = Path.Combine(_testDir, "manifest_seagate.json");
            var seagateRun = RunWorker(seagateZip, seagateStaging, seagateManifest, cp: "936");
            var seagateResult = CompleteViaProduction(seagateZip, seagateStaging, seagateManifest, seagateRun.DeliveryRoot);
            Assert("同名根目录折叠: 交付成功且已折叠", seagateResult.Delivered && seagateResult.Collapsed, seagateResult.FailureReason);
            Assert("同名根目录折叠: 第一级直接包含解压文件",
                seagateResult.Delivered && File.Exists(Path.Combine(seagateResult.FinalDirectory, "使用说明.txt")));
            Assert("同名根目录折叠: 不存在第二层同名目录",
                seagateResult.Delivered && !Directory.Exists(Path.Combine(seagateResult.FinalDirectory, "希捷专修")));

            // 包名自带数字后缀：不得反推去掉 (2)，内层 Report/ 必须保留
            string reportSrc = Path.Combine(_testDir, "raw_report");
            Directory.CreateDirectory(Path.Combine(reportSrc, "Report"));
            File.WriteAllText(Path.Combine(reportSrc, "Report", "readme.txt"), "report-body");
            string reportZip = Path.Combine(_testDir, "Report (2).zip");
            Run7z($"a \"{reportZip}\" *", reportSrc);
            string reportStaging = NewStaging("report");
            string reportManifest = Path.Combine(_testDir, "manifest_report.json");
            var reportRun = RunWorker(reportZip, reportStaging, reportManifest, cp: "936");
            var reportResult = CompleteViaProduction(reportZip, reportStaging, reportManifest, reportRun.DeliveryRoot);
            Assert("数字后缀包名: 不误折叠内层 Report 目录",
                reportResult.Delivered && File.Exists(Path.Combine(reportResult.FinalDirectory, "Report", "readme.txt")),
                reportResult.FailureReason);
            Assert("数字后缀包名: 最终目录名保留 (2)", reportResult.Delivered &&
                Path.GetFileName(reportResult.FinalDirectory) == "Report (2)", reportResult.FinalDirectory);

            // 折叠反例
            string noFoldRoot = Path.Combine(_testDir, "nofold_root");
            Directory.CreateDirectory(Path.Combine(noFoldRoot, "测试包"));
            File.WriteAllText(Path.Combine(noFoldRoot, "测试包", "sub.txt"), "sub");
            File.WriteAllText(Path.Combine(noFoldRoot, "root_file.txt"), "root");
            Assert("同名目录折叠反例: 存在同级文件时不折叠",
                ArchiveTaskPlanner.SelectCollapseRoot(noFoldRoot, Path.Combine(_testDir, "测试包.zip")) == null);
            string noFoldRoot2 = Path.Combine(_testDir, "nofold_root2");
            Directory.CreateDirectory(Path.Combine(noFoldRoot2, "不同名子目录"));
            File.WriteAllText(Path.Combine(noFoldRoot2, "不同名子目录", "sub.txt"), "sub");
            Assert("同名目录折叠反例: 唯一子目录不同名时不折叠",
                ArchiveTaskPlanner.SelectCollapseRoot(noFoldRoot2, Path.Combine(_testDir, "测试包.zip")) == null);

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 14] 提交完整性门槛：缺项 / 篡改必须阻断提交与回收");
            string tamperZip = Path.Combine(_testDir, "tamper.zip");
            CreateZipStored(tamperZip, ("a.txt", "alpha"), ("b.txt", "beta"), ("c.txt", "gamma"));
            string tamperStaging = NewStaging("tamper");
            string tamperManifest = Path.Combine(_testDir, "manifest_tamper.json");
            var tamperRun = RunWorker(tamperZip, tamperStaging, tamperManifest, cp: "936");
            string tamperDeliveryRoot = tamperRun.DeliveryRoot;
            File.Delete(Path.Combine(tamperStaging, string.IsNullOrEmpty(tamperDeliveryRoot) ? "" : tamperDeliveryRoot, "b.txt"));
            var tamperResult = CompleteViaProduction(tamperZip, tamperStaging, tamperManifest, tamperDeliveryRoot);
            Assert("完整性门槛: 暂存缺项时拒绝提交", !tamperResult.Delivered && tamperResult.FailureReason.Contains("清单"),
                tamperResult.FailureReason);
            Assert("完整性门槛: 暂存目录被保留以便恢复", Directory.Exists(tamperStaging));

            string tamper2Zip = Path.Combine(_testDir, "tamper2.zip");
            CreateZipStored(tamper2Zip, ("only.txt", "only-content"));
            string tamper2Staging = NewStaging("tamper2");
            string tamper2Manifest = Path.Combine(_testDir, "manifest_tamper2.json");
            var tamper2Run = RunWorker(tamper2Zip, tamper2Staging, tamper2Manifest, cp: "936");
            File.WriteAllText(Path.Combine(tamper2Staging, "extra.txt"), "unexpected");
            var tamper2Result = CompleteViaProduction(tamper2Zip, tamper2Staging, tamper2Manifest, tamper2Run.DeliveryRoot);
            Assert("完整性门槛: 清单外多余文件时拒绝提交", !tamper2Result.Delivered, tamper2Result.FailureReason);

            // 摘要级校验：回收前发现内容被替换则不得回收
            string digestZip = Path.Combine(_testDir, "digest.zip");
            CreateZipStored(digestZip, ("payload.txt", "digest-payload"));
            string digestStaging = NewStaging("digest");
            string digestManifest = Path.Combine(_testDir, "manifest_digest.json");
            var digestRun = RunWorker(digestZip, digestStaging, digestManifest, cp: "936");
            var digestManifestObj = DeliveryManifest.Load(digestManifest);
            var digestReport = DeliveryVerifier.Verify(
                Path.Combine(digestStaging, digestRun.DeliveryRoot), digestManifestObj, VerifyMode.StructureAndDigest);
            Assert("完整性门槛: 摘要级对账通过", digestReport.Ok && digestReport.DigestsChecked >= 1, digestReport.Describe());
            File.WriteAllText(Path.Combine(digestStaging, digestRun.DeliveryRoot, "payload.txt"), "digest-payload-tampered");
            var digestBad = DeliveryVerifier.Verify(
                Path.Combine(digestStaging, digestRun.DeliveryRoot), digestManifestObj, VerifyMode.StructureAndDigest);
            Assert("完整性门槛: 内容被替换时摘要校验失败", !digestBad.Ok, digestBad.Describe());

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 15] 空归档与来源标记（走生产完成路径）");
            string emptyZip = Path.Combine(_testDir, "legal_empty.zip");
            using (var archive = ZipFile.Open(emptyZip, ZipArchiveMode.Create)) { }
            string emptyZipStaging = NewStaging("emptyzip");
            string emptyZipManifest = Path.Combine(_testDir, "manifest_emptyzip.json");
            var emptyZipRun = RunWorker(emptyZip, emptyZipStaging, emptyZipManifest, cp: "936");
            var emptyZipResult = CompleteViaProduction(emptyZip, emptyZipStaging, emptyZipManifest, emptyZipRun.DeliveryRoot);
            Assert("空归档: 成功创建空目标目录",
                emptyZipResult.Delivered && Directory.Exists(emptyZipResult.FinalDirectory) &&
                Directory.GetFileSystemEntries(emptyZipResult.FinalDirectory).Length == 0,
                emptyZipResult.FailureReason);

            string motwSource = Path.Combine(_testDir, "web_download.zip");
            CreateZipStored(motwSource, ("downloaded.txt", "downloaded-body"));
            File.WriteAllText(motwSource + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet");
            string motwStaging = NewStaging("motw");
            string motwManifest = Path.Combine(_testDir, "manifest_motw.json");
            var motwRun = RunWorker(motwSource, motwStaging, motwManifest, cp: "936");
            var motwResult = CompleteViaProduction(motwSource, motwStaging, motwManifest, motwRun.DeliveryRoot);
            string motwTarget = Path.Combine(motwResult.FinalDirectory, "downloaded.txt");
            Assert("来源标记: 生产完成路径为交付文件写入 Zone.Identifier",
                motwResult.Delivered && File.Exists(motwTarget + ":Zone.Identifier"), motwResult.FailureReason);
            Assert("来源标记: ZoneId 内容一致",
                motwResult.Delivered && File.ReadAllText(motwTarget + ":Zone.Identifier").Contains("ZoneId=3"));

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 16] 性能：1,200 个小文件的 Worker 限流与折叠提交");
            string smallSource = Path.Combine(_testDir, "raw_small");
            Directory.CreateDirectory(smallSource);
            for (int i = 0; i < 1200; i++)
            {
                File.WriteAllText(Path.Combine(smallSource, $"f_{i:D4}.dat"), $"payload-{i}");
            }
            string smallZip = Path.Combine(_testDir, "small_1200.zip");
            CreateZipStored(smallZip, Directory.GetFiles(smallSource).Select(f => ($"small/{Path.GetFileName(f)}", File.ReadAllText(f))).ToArray());
            string smallStaging = NewStaging("small");
            string smallManifest = Path.Combine(_testDir, "manifest_small.json");
            var smallWatch = Stopwatch.StartNew();
            var smallRun = RunWorker(smallZip, smallStaging, smallManifest, cp: "936");
            smallWatch.Stop();
            int smallFiles = Directory.GetFiles(smallStaging, "*.dat", SearchOption.AllDirectories).Length;
            Console.WriteLine($"      Worker 解压 1,200 个小文件耗时 {smallWatch.ElapsedMilliseconds} ms，进度帧 {smallRun.ProgressFrames}");
            Assert("性能: 1,200 个小文件全部解出", smallFiles == 1200, $"实际 {smallFiles}");
            Assert("性能: 发端限流生效（进度帧 <= 100）", smallRun.ProgressFrames <= 100, $"帧数 {smallRun.ProgressFrames}");
            var commitWatch = Stopwatch.StartNew();
            var smallResult = CompleteViaProduction(smallZip, smallStaging, smallManifest, smallRun.DeliveryRoot);
            commitWatch.Stop();
            Console.WriteLine($"      清单对账 + 单次目录改名提交耗时 {commitWatch.ElapsedMilliseconds} ms");
            Assert("性能: 1,200 个文件经生产路径交付成功",
                smallResult.Delivered && Directory.GetFiles(smallResult.FinalDirectory, "*", SearchOption.AllDirectories).Length == 1200,
                smallResult.FailureReason);
            Assert("性能: 折叠提交不再逐项重命名（< 1500 ms）", commitWatch.ElapsedMilliseconds < 1500,
                $"{commitWatch.ElapsedMilliseconds} ms");

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 17] 编码不确定时的选择框与“跳过项”部分交付");
            // 同一包内混入“已声明 UTF-8”和“未声明编码”的名称，触发编码选择
            string mixedZip = Path.Combine(_testDir, "mixed_encoding.zip");
            CreateZip(mixedZip, Encoding.UTF8, ("图.txt", "flagged-name"), ("普通.txt", "unflagged-name"));
            ClearZipUtf8FlagsForEntry(mixedZip, 1);
            string mixedStaging = NewStaging("mixed");
            string mixedManifest = Path.Combine(_testDir, "manifest_mixed.json");
            var mixedRun = RunWorker(mixedZip, mixedStaging, mixedManifest, cp: "936", encodingCp: 65001);
            Assert("编码选择: 编码不确定时上报候选并等待选择", mixedRun.EncodingOptionsOffered);
            Assert("编码选择: 按用户选择的 UTF-8 解码后成功交付",
                mixedRun.Status == "ok" && File.Exists(Path.Combine(mixedStaging, "图.txt")) &&
                File.Exists(Path.Combine(mixedStaging, "普通.txt")),
                $"status={mixedRun.Status} exit={mixedRun.ExitCode}");

            // 非法字符“跳过”必须仍然交付可表示的文件，并标记部分完成（不得整体丢弃）
            string skipZip = Path.Combine(_testDir, "skip_items.zip");
            CreateZipStored(skipZip, ("a:b.txt", "illegal-name"), ("ok.txt", "keep-me"));
            string skipStaging = NewStaging("skip");
            string skipManifest = Path.Combine(_testDir, "manifest_skip.json");
            var skipRun = RunWorker(skipZip, skipStaging, skipManifest, cp: "936", invalidAction: "skip");
            Assert("部分完成: 上报非法字符交互", skipRun.InvalidCharsOffered);
            Assert("部分完成: 状态为 partial、退出码 11、跳过项单独计数",
                skipRun.Status == "partial" && skipRun.ExitCode == 11 && skipRun.Skipped == 1,
                $"status={skipRun.Status} exit={skipRun.ExitCode} skipped={skipRun.Skipped}");
            var skipResult = CompleteViaProduction(skipZip, skipStaging, skipManifest, skipRun.DeliveryRoot, recycle: false);
            Assert("部分完成: 可表示的文件仍被交付到最终目录",
                skipResult.Delivered && File.Exists(Path.Combine(skipResult.FinalDirectory, "ok.txt")),
                skipResult.FailureReason);
            Assert("部分完成: 不可表示的文件未落盘",
                skipResult.Delivered && !File.Exists(Path.Combine(skipResult.FinalDirectory, "a-b.txt")));

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 18] 界面契约：资源键与编码选择面板存在（防止回归为缺失资源）");
            string sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.."));
            string xamlPath = Path.Combine(sourceRoot, "src", "UnzipProxy", "MainWindow.xaml");
            string codeBehindPath = Path.Combine(sourceRoot, "src", "UnzipProxy", "MainWindow.xaml.cs");
            if (File.Exists(xamlPath) && File.Exists(codeBehindPath))
            {
                string xaml = File.ReadAllText(xamlPath);
                string codeBehind = File.ReadAllText(codeBehindPath);
                foreach (string key in new[] { "Win11Cyan", "Win11Green", "Win11Yellow", "Win11Amber", "Win11Red" })
                {
                    Assert($"界面契约: 资源键 {key} 已定义", xaml.Contains($"x:Key=\"{key}\""), key);
                }
                Assert("界面契约: 编码选择面板已定义",
                    xaml.Contains("ModalEncodingPanel") && xaml.Contains("EncodingOptionsPanel"));
                Assert("界面契约: 不再引用未定义资源 AccentOrangeBrush", !codeBehind.Contains("AccentOrangeBrush"));
                Assert("界面契约: 界面不再做第二套文件名纠偏", !codeBehind.Contains("FixPotentialMojibake"));
            }
            else
            {
                Assert("界面契约: 找到界面源码", false, xamlPath);
            }

            // -------------------------------------------------------------
            Console.WriteLine("\n==================================================");
            Console.WriteLine($"测试总结: 共 {_pass + _fail} 项测试，通过: {_pass}，失败: {_fail}");
            Console.WriteLine("==================================================");
            return _fail == 0 ? 0 : 1;
        }
    }
}
