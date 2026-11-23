using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnzipProxy.Core;

namespace Tester;

partial class Program
{
    private const string ZsCli = @"C:\Program Files\7-Zip-Zstandard\7z.exe";
    private const string FixedZsSha256 = "D480E5F7ACF03183DC8CD9FE341A06D34E768B3C6606C0DB4AEC2A7D0EB5098E";

    private static string RunFixtureTool(string tool, string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo(tool)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动造样工具");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000))
        {
            process.Kill();
            process.WaitForExit();
            throw new TimeoutException($"造样工具超时：{Path.GetFileName(tool)}");
        }
        error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"造样工具失败：{Path.GetFileName(tool)}，操作 {arguments[0]}，退出码 {process.ExitCode}");
        return output.GetAwaiter().GetResult();
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static WorkerRun ExtractZs(string path, string label, out string staging, out string manifest,
        string? password = null, ulong? limit = null)
    {
        staging = NewStaging(label);
        manifest = Path.Combine(_testDir, Guid.NewGuid().ToString("N") + ".manifest.json");
        return RunWorker(path, staging, manifest, password: password, limit: limit);
    }

    private static bool DeliveredHash(string archive, string expectedLeaf, string expectedHash, WorkerRun run, string stage, string manifest)
    {
        if (run is not { ExitCode: 0, Status: "ok" }) return false;
        var result = CompleteViaProduction(archive, stage, manifest, run.DeliveryRoot, recycle: false);
        if (!result.Delivered || !File.Exists(archive)) return false;
        string[] files = Directory.GetFiles(result.FinalDirectory, "*", SearchOption.AllDirectories);
        return files.Length == 1 && Path.GetFileName(files[0]) == expectedLeaf && Sha256(files[0]) == expectedHash;
    }

    private static void TestInterruptedZipAndTarRoot(string directory, string payload, string expected)
    {
        string pair = Path.Combine(directory, "pair.zip");
        CreateZipStored(pair, ("first.txt", "first"), ("second.txt", "second"));
        byte[] bytes = File.ReadAllBytes(pair);
        int second = bytes.AsSpan(4).IndexOf(new byte[] { 0x50, 0x4B, 3, 4 }) + 4;
        if (second <= 4) throw new InvalidDataException("Missing second local ZIP header");
        foreach (string name in new[] { "interrupted.zip", "interrupted.zip.001" })
        {
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, bytes[..second]);
            string before = Sha256(path);
            var run = ExtractZs(path, "interrupted", out _, out string manifest);
            Assert($"R1-002: {name} 拒绝仅首条可读的 ZIP", run.ExitCode != 0 && run.Status != "ok" &&
                !File.Exists(manifest) && Sha256(path) == before, $"exit={run.ExitCode} reason={run.ErrorReason}");
        }

        string tarPath = Path.Combine(directory, "dotroot.tar");
        using (var tar = new TarWriter(File.Create(tarPath), TarEntryFormat.Ustar))
        {
            tar.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "./"));
            tar.WriteEntry(payload, "./report.csv");
            tar.WriteEntry(payload, "./-/nested.csv");
        }
        RunFixtureTool(ZsCli, directory, "a", "-tgzip", "dotroot.tgz", "dotroot.tar");
        RunFixtureTool(ZsCli, directory, "a", "-tlz4", "dotroot.tar.lz4", "dotroot.tar");
        foreach (string name in new[] { "dotroot.tar", "dotroot.tgz", "dotroot.tar.lz4" })
        {
            string path = Path.Combine(directory, name);
            var run = ExtractZs(path, "dotroot", out string staging, out string manifest);
            var result = CompleteViaProduction(path, staging, manifest, run.DeliveryRoot);
            string first = Path.Combine(result.FinalDirectory, "report.csv");
            string secondFile = Path.Combine(result.FinalDirectory, "-", "nested.csv");
            Assert($"R1-003: {name} 保留真实 - 目录并去除当前目录段", run.ExitCode == 0 && !run.InvalidCharsOffered &&
                result.Delivered && File.Exists(first) && File.Exists(secondFile) &&
                Sha256(first) == expected && Sha256(secondFile) == expected &&
                Directory.GetFiles(result.FinalDirectory, "*", SearchOption.AllDirectories).Length == 2);
        }
    }

    private static void TestZsArchives()
    {
        Assert("ZS: Worker 同目录固定引擎摘要", Sha256(Path.Combine(Path.GetDirectoryName(_workerExe)!, "7z.dll")) == FixedZsSha256);
        string directory = Path.Combine(_testDir, "zs-fixtures");
        Directory.CreateDirectory(directory);
        string payload = Path.Combine(directory, "report.csv");
        File.WriteAllText(payload, "column_a,column_b\n100,200\n", new UTF8Encoding(false));
        string expected = Sha256(payload);
        TestInterruptedZipAndTarRoot(directory, payload, expected);
        string tarPath = Path.Combine(directory, "project.tar");
        using (var tar = new TarWriter(File.Create(tarPath), TarEntryFormat.Ustar))
            tar.WriteEntry(payload, "report.csv");

        var aliases = new[]
        {
            ("brotli", "br", "tbr", "brotli"),
            ("lz4", "lz4", "tlz4", ""),
            ("lz5", "lz5", "tlz5", ""),
            ("lizard", "liz", "tliz", ""),
            ("zstd", "zst", "tzst", "zstd")
        };
        foreach (var (handler, extension, shortName, longAlias) in aliases)
        {
            string plain = Path.Combine(directory, $"report.csv.{extension}");
            string tarStream = Path.Combine(directory, $"project.tar.{extension}");
            RunFixtureTool(ZsCli, directory, "a", "-t" + handler, Path.GetFileName(plain), "report.csv");
            RunFixtureTool(ZsCli, directory, "a", "-t" + handler, Path.GetFileName(tarStream), "project.tar");
            string shortTar = Path.Combine(directory, $"short.{shortName}");
            File.Copy(tarStream, shortTar);
            string plainTar = Path.Combine(directory, $"plain.{extension}");
            File.Copy(tarStream, plainTar);
            string fakeTar = Path.Combine(directory, $"fake.{shortName}");
            File.Copy(plain, fakeTar);
            foreach (var (path, leaf, digest) in new[]
                { (plain, "report.csv", expected), (tarStream, "report.csv", expected), (shortTar, "report.csv", expected),
                  (plainTar, "plain", Sha256(tarPath)) })
            {
                var run = ExtractZs(path, handler, out string staging, out string manifest);
                Assert($"ZS: {Path.GetFileName(path)} 单层/复合路由和正文 SHA-256",
                    run.PasswordRequests == 0 && DeliveredHash(path, leaf, digest, run, staging, manifest),
                    $"status={run.Status} exit={run.ExitCode} reason={run.ErrorReason}");
            }
            var rejected = ExtractZs(fakeTar, handler, out _, out string rejectedManifest);
            Assert($"ZS: {shortName} 的非 TAR 载荷失败", rejected.Status == "failed" && rejected.ErrorReason == "inner_not_tar" &&
                !File.Exists(rejectedManifest) && File.Exists(fakeTar));

            if (longAlias.Length > 0)
            {
                string alias = Path.Combine(directory, $"alias.tar.{longAlias}");
                File.Copy(tarStream, alias);
                var run = ExtractZs(alias, handler, out string staging, out string manifest);
                Assert($"ZS: .tar.{longAlias} 长别名", DeliveredHash(alias, "report.csv", expected, run, staging, manifest));
            }
            if (handler == "zstd")
            {
                string renamed = Path.Combine(directory, "renamed.dat");
                File.Copy(plain, renamed);
                var run = ExtractZs(renamed, handler, out string staging, out string manifest);
                Assert("ZS: 改名 Zstd 按多签名识别", DeliveredHash(renamed, "extracted_file", expected, run, staging, manifest));
            }
        }

        foreach (string codec in new[] { "ZSTD", "BROTLI", "LZ4", "LZ5", "LIZARD", "FLZMA2" })
        {
            string name = "codec-" + codec + ".7z";
            RunFixtureTool(ZsCli, directory, "a", "-t7z", "-m0=" + codec, "-mx=1", name, "report.csv");
            string listing = RunFixtureTool(ZsCli, directory, "l", "-slt", name);
            string reported = codec == "FLZMA2" ? "LZMA2" : codec;
            Assert($"ZS: 7z 容器方法 {codec} 核实", listing.Contains("Method = " + reported, StringComparison.OrdinalIgnoreCase));
            string archive = Path.Combine(directory, name);
            var run = ExtractZs(archive, codec, out string staging, out string manifest);
            Assert($"ZS: 7z {codec} 正文 SHA-256", DeliveredHash(archive, "report.csv", expected, run, staging, manifest));
        }

        TestLegacyStreamAliases(directory, payload, expected);
        TestNestedEncryptedRar(directory);
        TestZsPasswords(directory, expected);
        TestUnknownLz4Budget(directory);
        TestManagedWorkerLifetime(directory);
    }

    private static ProcessStartInfo CreateManagedWorker(string exe, string archive, string staging, string manifest)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "--managed", "--archive", archive, "--staging", staging, "--manifest", manifest })
            start.ArgumentList.Add(argument);
        return start;
    }

    private static int RunJobParent(string workerExe, string archive, string staging, string manifest)
    {
        using var job = new JobObjectManager();
        using var worker = Process.Start(CreateManagedWorker(workerExe, archive, staging, manifest))!;
        if (!job.AssignProcess(worker))
        {
            worker.StandardInput.Close();
            if (!worker.WaitForExit(5000)) worker.Kill();
            return 1;
        }
        worker.StandardInput.NewLine = "\n";
        worker.StandardInput.WriteLine("{\"type\":\"start\"}");
        worker.StandardInput.Flush();
        while (worker.StandardOutput.ReadLine() is { } line)
        {
            if (line != "{\"type\":\"password_request\"}") continue;
            Console.WriteLine(worker.Id);
            Console.Out.Flush();
            Console.ReadLine();
            return 0;
        }
        return 1;
    }

    private static void TestManagedWorkerLifetime(string directory)
    {
        string archive = Path.Combine(directory, "xchacha20-on.7z");
        string stage = NewStaging("job");
        string manifest = Path.Combine(_testDir, "job.manifest.json");
        using (var beforeStart = Process.Start(CreateManagedWorker(_workerExe, archive, stage, manifest))!)
        {
            beforeStart.StandardInput.Close();
            bool exited = beforeStart.WaitForExit(5000);
            if (!exited) beforeStart.Kill();
            Assert("ZS: 指派前父端关闭，Worker 不读取归档或创建清单",
                exited && beforeStart.ExitCode != 0 && beforeStart.StandardOutput.ReadToEnd().Length == 0 &&
                !File.Exists(manifest) && !Directory.EnumerateFileSystemEntries(stage).Any());
        }

        string tester = typeof(Program).Assembly.Location;
        var start = new ProcessStartInfo(Path.ChangeExtension(tester, ".exe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (string arg in new[] { "--job-parent", _workerExe, archive, stage, manifest })
            start.ArgumentList.Add(arg);
        using var parent = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            string? pid = parent.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            if (!int.TryParse(pid, out int id))
            {
                Assert("ZS: Worker 加入任务对象后进入密码等待", false);
                return;
            }
            using var worker = Process.GetProcessById(id);
            parent.Kill();
            parent.WaitForExit();
            bool exited = worker.WaitForExit(5000);
            if (!exited) worker.Kill();
            Assert("ZS: 父进程异常退出时回收正在等密码的 Worker",
                exited && !File.Exists(manifest) && File.Exists(archive));
        }
        finally
        {
            if (!parent.HasExited)
            {
                parent.Kill();
                parent.WaitForExit();
            }
        }
    }

    private static void TestLegacyStreamAliases(string directory, string payload, string expected)
    {
        string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        RunFixtureTool(tar, directory, "-caf", "lzip-report.csv.lz", "--format", "raw", Path.GetFileName(payload));
        RunFixtureTool(tar, directory, "-caf", "lzip-project.tar.lz", Path.GetFileName(payload));
        RunFixtureTool(tar, directory, "-cZf", "compress-project.taz", Path.GetFileName(payload));
        File.Copy(Path.Combine(directory, "lzip-project.tar.lz"), Path.Combine(directory, "lzip-short.tlz"));
        RunFixtureTool(ZsCli, directory, "a", "-tgzip", "gzip-project.tpz", "project.tar");

        foreach (string name in new[] { "lzip-report.csv.lz", "lzip-project.tar.lz", "lzip-short.tlz", "compress-project.taz", "gzip-project.tpz" })
        {
            string archive = Path.Combine(directory, name);
            RunFixtureTool(ZsCli, directory, "t", name);
            var run = ExtractZs(archive, "legacy-alias", out string stage, out string manifest);
            string leaf = name == "lzip-report.csv.lz" ? "lzip-report.csv" : "report.csv";
            Assert($"ZS: {name} 正文 SHA-256", DeliveredHash(archive, leaf, expected, run, stage, manifest),
                $"status={run.Status} exit={run.ExitCode} reason={run.ErrorReason}");
        }
    }

    private static void TestNestedEncryptedRar(string directory)
    {
        // libarchive v3.8.2, libarchive/test/test_read_format_rar_encryption_data.rar.uu.
        // Public fixture password is documented in the matching libarchive regression test.
        const string rar = "UmFyIRoHAM+QcwAADQAAAAAAAABd/HQkhC8AIAAAABAAAAADXIatpqVMMUMdMwcAtIEAAGZvby50eHRPA7zZ1hZM30VAGpKSiN8VK46ez3C5QOjJR+bO1r9E4rg1qJzoZTcBB0V0JIQvACAAAAAQAAAAA77ikM2mTDFDHTMHALSBAABiYXIudHh0TwO82dYWTN9wdHpIiYgPZlt95Qa5ch6oJVX4yjcBo+308RTzI2TRocQ9ewBABwA=";
        const string expected = "84BA9AFCF0673AAB0D1421D931E76A19294B12117483879C4B58598D3D71E83E";
        string inner = Path.Combine(directory, "encrypted.rar");
        File.WriteAllBytes(inner, Convert.FromBase64String(rar));
        Assert("ZS: 合法加密 RAR 夹具 SHA-256", Sha256(inner) == expected);
        RunFixtureTool(ZsCli, directory, "t", "-p12345678", "encrypted.rar");
        RunFixtureTool(ZsCli, directory, "a", "-tlz4", "encrypted.rar.lz4", "encrypted.rar");
        string outer = inner + ".lz4";
        var wrapped = ExtractZs(outer, "nested-rar", out string stage, out string manifest);
        Assert("ZS: 普通 LZ4 不展开加密 RAR 或询问密码", wrapped.PasswordRequests == 0 &&
            DeliveredHash(outer, "encrypted.rar", expected, wrapped, stage, manifest));
        var opened = ExtractZs(inner, "direct-rar", out stage, out _, "12345678");
        Assert("ZS: 单独打开 RAR 才询问密码并解密", opened is { Status: "ok", ExitCode: 0, PasswordRequests: 1 } &&
            File.Exists(Path.Combine(stage, "foo.txt")) && File.Exists(Path.Combine(stage, "bar.txt")));
    }

    private static void TestZsPasswords(string directory, string expectedHash)
    {
        const string password = "UnboxFixture42";
        var methods = new[]
        {
            ("xchacha20", "XChaCha20"),
            ("xchacha20-poly1305", "XChaCha20-Poly1305"),
            ("axp", "AES+XChaCha20-Poly1305"),
            ("axa", "AES+XChaCha20+Ascon"),
            ("xaa", "XChaCha20+AES+AEGIS"),
            ("aes256", "7zAES")
        };
        foreach (var (method, reported) in methods)
        foreach (string head in new[] { "on", "off" })
        {
            string name = $"{method}-{head}.7z";
            string archive = Path.Combine(directory, name);
            string label = $"ZS: {method} / 头部 {head}";
            RunFixtureTool(ZsCli, directory, "a", "-t7z", "-m0=Copy", "-mem=" + method, "-mhe=" + head,
                "-p" + password, name, "report.csv");
            string listing = RunFixtureTool(ZsCli, directory, "l", "-slt", "-p" + password, name);
            RunFixtureTool(ZsCli, directory, "t", "-p" + password, name);
            Assert(label + " 方法与目录", listing.Contains(reported, StringComparison.OrdinalIgnoreCase)
                && listing.Contains("Path = report.csv", StringComparison.Ordinal));

            var correct = ExtractZs(archive, method, out string stage, out string manifest, password);
            Assert(label + " 正确密码与正文 SHA-256", correct.PasswordRequests == 1 &&
                DeliveredHash(archive, "report.csv", expectedHash, correct, stage, manifest));

            var wrong = ExtractZs(archive, method, out _, out string wrongManifest, "UnboxWrong42");
            Assert(label + " 错误密码", wrong.PasswordRequests == 1 && wrong.ExitCode != 0 && wrong.Status != "ok" &&
                !File.Exists(wrongManifest) && File.Exists(archive));

            var canceled = ExtractZs(archive, method, out _, out string canceledManifest);
            Assert(label + " 取消密码", canceled.PasswordRequests == 1 &&
                canceled.Lines.Contains("{\"type\":\"canceled\"}") && !File.Exists(canceledManifest) && File.Exists(archive));

            byte[] damaged = File.ReadAllBytes(archive);
            damaged[34] ^= 0x80;
            string modified = Path.Combine(directory, $"damaged-{name}");
            File.WriteAllBytes(modified, damaged);
            var corrupt = ExtractZs(modified, method, out _, out string corruptManifest, password);
            Assert(label + " 正确密码但数据损坏", corrupt.PasswordRequests == 1 && corrupt.ExitCode != 0 &&
                corrupt.Status != "ok" && !File.Exists(corruptManifest) && File.Exists(modified));
            Assert(label + " IPC 未记录凭据", new[] { correct, wrong, canceled, corrupt }.All(run =>
                !run.StandardOutput.Contains(password, StringComparison.Ordinal) &&
                !run.StandardError.Contains(password, StringComparison.Ordinal)));
        }
    }

    private static void TestUnknownLz4Budget(string directory)
    {
        const int size = 1024 * 1024;
        string payload = Path.Combine(directory, "big.txt");
        File.WriteAllBytes(payload, new byte[size]);
        string archive = Path.Combine(directory, "big.txt.lz4");
        RunFixtureTool(ZsCli, directory, "a", "-tlz4", "big.txt.lz4", "big.txt");
        var limited = ExtractZs(archive, "small-budget", out _, out string failedManifest, limit: 65536);
        Assert("ZS: 未知 LZ4 超限失败且不得发清单", limited is { ExitCode: 10, ErrorReason: "output_budget_exceeded" } &&
            limited.Finished is { } end && end.GetProperty("phase").GetString() == "outer" &&
            !end.GetProperty("size_known").GetBoolean() && end.GetProperty("limit_bytes").GetUInt64() == 65536 &&
            end.GetProperty("written_bytes").GetUInt64() <= 65536 && !File.Exists(failedManifest) && File.Exists(archive),
            $"status={limited.Status} exit={limited.ExitCode} reason={limited.ErrorReason}");
        var increased = ExtractZs(archive, "increased-budget", out string stage, out string manifest, limit: size);
        Assert("ZS: 新尝试写到有限上限仍可成功", increased.PasswordRequests == 0 &&
            DeliveredHash(archive, "big.txt", Sha256(payload), increased, stage, manifest));
    }

    private static void TestRealLz4Sample(string source)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("找不到真实 LZ4 样本", source);
        const string expected = "F54C71D8F5DA1E73037359CF5A7C3BCBA926460D67A0BEA24BEC8E573F416CE8";
        const string name = "SLP267.rar.lz4";
        const string leaf = "SLP267.rar";
        string referenceDir = Path.Combine(_testDir, "reference");
        string inputDir = Path.Combine(_testDir, "unbox");
        Directory.CreateDirectory(referenceDir);
        Directory.CreateDirectory(inputDir);
        File.Copy(source, Path.Combine(referenceDir, name));
        RunFixtureTool(@"C:\PortableSoft\bin\lz4.exe", referenceDir, name);
        string comparison = Path.Combine(referenceDir, leaf);
        Assert("真实 LZ4 独立 CLI 长度与 SHA-256", new FileInfo(comparison).Length == 1472941998 && Sha256(comparison) == expected);
        string archive = Path.Combine(inputDir, name);
        File.Copy(source, archive);
        var run = ExtractZs(archive, "real-lz4", out string stage, out string manifest);
        Assert("真实 LZ4 默认一次交付，无预算、密码或模式提示",
            run.PasswordRequests == 0 && !run.EncodingOptionsOffered && !run.InvalidCharsOffered &&
            run.Finished is { } end && end.GetProperty("size_known").GetBoolean() &&
            end.GetProperty("limit_bytes").GetUInt64() == 1472941998 &&
            DeliveredHash(archive, leaf, expected, run, stage, manifest) &&
            File.Exists(Path.Combine(inputDir, leaf, leaf)), $"status={run.Status} exit={run.ExitCode} reason={run.ErrorReason}");
    }
}
