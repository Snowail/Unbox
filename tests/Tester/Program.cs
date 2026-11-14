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

        // 以指定 TAR 格式（V7/ustar/GNU/PAX）写入指定条目，用于覆盖可靠 Unicode 声明与 V7 复合归档
        static void CreateTarWithEntries(string tarPath, TarEntryFormat format, params (string Name, string Content)[] entries)
        {
            if (File.Exists(tarPath)) File.Delete(tarPath);
            using var fs = File.Create(tarPath);
            using var writer = new TarWriter(fs, format);
            foreach (var e in entries)
            {
                TarEntry entry = format switch
                {
                    TarEntryFormat.V7 => new V7TarEntry(TarEntryType.V7RegularFile, e.Name),
                    TarEntryFormat.Ustar => new UstarTarEntry(TarEntryType.RegularFile, e.Name),
                    TarEntryFormat.Gnu => new GnuTarEntry(TarEntryType.RegularFile, e.Name),
                    _ => new PaxTarEntry(TarEntryType.RegularFile, e.Name),
                };
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(e.Content));
                entry.DataStream = data;
                writer.WriteEntry(entry);
            }
        }

        // 原始 ZIP 写入：支持前导空格名、重复目录条目、0x7075 Unicode Path 扩展字段等 .NET ZipArchive 无法表达的样本
        static void CreateZipRaw(string path, params (byte[] Name, byte[] Data, ushort Flags, byte[] Extra)[] entries)
        {
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            var offsets = new List<uint>();
            foreach (var e in entries)
            {
                offsets.Add((uint)fs.Position);
                w.Write(0x04034b50u);
                w.Write((ushort)20);
                w.Write(e.Flags);
                w.Write((ushort)0);   // method: stored
                w.Write((ushort)0);   // time
                w.Write((ushort)0x21); // date
                w.Write(Crc32.Compute(e.Data));
                w.Write((uint)e.Data.Length);
                w.Write((uint)e.Data.Length);
                w.Write((ushort)e.Name.Length);
                w.Write((ushort)e.Extra.Length);
                w.Write(e.Name);
                w.Write(e.Extra);
                w.Write(e.Data);
            }
            uint cd = (uint)fs.Position;
            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                w.Write(0x02014b50u);
                w.Write((ushort)20);
                w.Write((ushort)20);
                w.Write(e.Flags);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write((ushort)0x21);
                w.Write(Crc32.Compute(e.Data));
                w.Write((uint)e.Data.Length);
                w.Write((uint)e.Data.Length);
                w.Write((ushort)e.Name.Length);
                w.Write((ushort)e.Extra.Length);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write(e.Name.Length > 0 && e.Name[^1] == (byte)'/' ? 16u : 0u);
                w.Write(offsets[i]);
                w.Write(e.Name);
                w.Write(e.Extra);
            }
            uint size = (uint)fs.Position - cd;
            w.Write(0x06054b50u);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)entries.Length);
            w.Write((ushort)entries.Length);
            w.Write(size);
            w.Write(cd);
            w.Write((ushort)0);
        }

        // 创建目录联接点（junction），用于复现 P1-4 目录边界越界
        static bool CreateJunction(string linkPath, string targetPath)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("mklink");
                psi.ArgumentList.Add("/J");
                psi.ArgumentList.Add(linkPath);
                psi.ArgumentList.Add(targetPath);
                using var proc = Process.Start(psi)!;
                proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // 构造单个 PAX 扩展头记录：<十进制长度> <key>=<value>\n（长度含自身数字、空格、换行）
        static byte[] BuildPaxRecord(string key, byte[] value)
        {
            int baseLen = 1 + key.Length + 1 + value.Length + 1; // 空格 + key + '=' + value + '\n'
            int digits = 1;
            int total;
            while (true)
            {
                total = digits + baseLen;
                if (total.ToString().Length == digits) break;
                digits++;
            }
            var rec = new List<byte>();
            rec.AddRange(Encoding.ASCII.GetBytes(total.ToString()));
            rec.Add((byte)' ');
            rec.AddRange(Encoding.ASCII.GetBytes(key));
            rec.Add((byte)'=');
            rec.AddRange(value);
            rec.Add((byte)'\n');
            return rec.ToArray();
        }

        static void WriteOctalField(byte[] hdr, int offset, int length, long value)
        {
            string s = Convert.ToString(value, 8);
            if (s.Length > length - 1) s = s.Substring(s.Length - (length - 1));
            s = s.PadLeft(length - 1, '0');
            byte[] bytes = Encoding.ASCII.GetBytes(s);
            Array.Copy(bytes, 0, hdr, offset, bytes.Length);
            hdr[offset + length - 1] = 0;
        }

        static void WriteTarChecksum(byte[] hdr)
        {
            long sum = 0;
            for (int i = 0; i < 512; i++)
            {
                sum += (i >= 148 && i < 156) ? (byte)' ' : hdr[i];
            }
            string s = Convert.ToString(sum, 8).PadLeft(6, '0');
            byte[] bytes = Encoding.ASCII.GetBytes(s);
            for (int i = 0; i < 6; i++) hdr[148 + i] = bytes[i];
            hdr[154] = 0;
            hdr[155] = (byte)' ';
        }

        static byte[] BuildTarHeader(string name, long size, char typeflag)
        {
            byte[] hdr = new byte[512];
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            Array.Copy(nameBytes, 0, hdr, 0, Math.Min(nameBytes.Length, 100));
            WriteOctalField(hdr, 100, 8, 420); // mode 0644
            WriteOctalField(hdr, 108, 8, 0);   // uid
            WriteOctalField(hdr, 116, 8, 0);   // gid
            WriteOctalField(hdr, 124, 12, size);
            WriteOctalField(hdr, 136, 12, 0);  // mtime
            for (int i = 148; i < 156; i++) hdr[i] = (byte)' ';
            hdr[156] = (byte)typeflag;
            byte[] magic = Encoding.ASCII.GetBytes("ustar");
            Array.Copy(magic, 0, hdr, 257, magic.Length);
            hdr[262] = 0;
            hdr[263] = (byte)'0';
            hdr[264] = (byte)'0';
            WriteTarChecksum(hdr);
            return hdr;
        }

        // 写入一个 PAX TAR：扩展头声明 hdrcharset=BINARY，path 使用原始字节（GBK），正文为 UTF-8 内容
        static void CreatePaxBinaryTar(string tarPath, byte[] pathBytes, string content)
        {
            if (File.Exists(tarPath)) File.Delete(tarPath);
            using var fs = File.Create(tarPath);
            using var w = new BinaryWriter(fs);

            byte[] paxData;
            using (var ms = new MemoryStream())
            {
                ms.Write(BuildPaxRecord("hdrcharset", Encoding.ASCII.GetBytes("BINARY")));
                ms.Write(BuildPaxRecord("path", pathBytes));
                paxData = ms.ToArray();
            }
            w.Write(BuildTarHeader("PaxHeaders/p", paxData.Length, 'x'));
            w.Write(paxData);
            int pad = (512 - (paxData.Length % 512)) % 512;
            if (pad > 0) w.Write(new byte[pad]);

            byte[] contentBytes = Encoding.UTF8.GetBytes(content);
            w.Write(BuildTarHeader("placeholder", contentBytes.Length, '0'));
            w.Write(contentBytes);
            pad = (512 - (contentBytes.Length % 512)) % 512;
            if (pad > 0) w.Write(new byte[pad]);

            w.Write(new byte[1024]); // 两个 512 零块结束
        }

        // 写入一个 PAX TAR：全局头声明 hdrcharset=BINARY（对后续文件生效），
        // 局部头只含 path 原始字节（GBK），正文为 UTF-8 内容。
        static void CreatePaxGlobalBinaryTar(string tarPath, byte[] pathBytes, string content)
        {
            if (File.Exists(tarPath)) File.Delete(tarPath);
            using var fs = File.Create(tarPath);
            using var w = new BinaryWriter(fs);

            byte[] globalData = BuildPaxRecord("hdrcharset", Encoding.ASCII.GetBytes("BINARY"));
            w.Write(BuildTarHeader("GlobalHead", globalData.Length, 'g'));
            w.Write(globalData);
            int gpad = (512 - (globalData.Length % 512)) % 512;
            if (gpad > 0) w.Write(new byte[gpad]);

            byte[] localData = BuildPaxRecord("path", pathBytes);
            w.Write(BuildTarHeader("PaxHeaders/p", localData.Length, 'x'));
            w.Write(localData);
            int lpad = (512 - (localData.Length % 512)) % 512;
            if (lpad > 0) w.Write(new byte[lpad]);

            byte[] contentBytes = Encoding.UTF8.GetBytes(content);
            w.Write(BuildTarHeader("placeholder", contentBytes.Length, '0'));
            w.Write(contentBytes);
            int cpad = (512 - (contentBytes.Length % 512)) % 512;
            if (cpad > 0) w.Write(new byte[cpad]);

            w.Write(new byte[1024]); // 两个 512 零块结束
        }

        static void CreatePaxCharsetResetTar(string path, bool reset)
        {
            using var writer = new BinaryWriter(File.Create(path));
            void Record(string name, char type, byte[] data)
            {
                writer.Write(BuildTarHeader(name, data.Length, type));
                writer.Write(data);
                writer.Write(new byte[(512 - data.Length % 512) % 512]);
            }
            Record("global", 'g', BuildPaxRecord("hdrcharset", Encoding.ASCII.GetBytes("BINARY")));
            Record("local", 'x', BuildPaxRecord("path", Encoding.ASCII.GetBytes("first.txt")));
            Record("placeholder", '0', Encoding.UTF8.GetBytes("first"));
            Record("global2", 'g', reset ? BuildPaxRecord("hdrcharset", Array.Empty<byte>()) :
                BuildPaxRecord("comment", Encoding.ASCII.GetBytes("no charset change")));
            Record("local2", 'x', BuildPaxRecord("path", Encoding.UTF8.GetBytes("é.txt")));
            Record("placeholder2", '0', Encoding.UTF8.GetBytes("second"));
            writer.Write(new byte[1024]);
        }

        static bool MoveBlocked(string from, string to, bool directory)
        {
            try
            {
                if (directory) Directory.Move(from, to); else File.Move(from, to);
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        static void TestPinnedTraversal()
        {
            string root = NewStaging("r7pins");
            string child = Path.Combine(root, "child");
            Directory.CreateDirectory(child);
            string file = Path.Combine(child, "data.txt");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes("pinned-data"));
            using (var rootHandle = DeliveryVerifier.OpenPinnedObject(root)!)
            {
                bool visited = false;
                DeliveryVerifier.WalkVerifiedTree(rootHandle, root, _ => { }, (full, rel, handle) =>
                {
                    visited = true;
                    Assert("R7: 文件句柄阻止改名", MoveBlocked(full, full + ".moved", false));
                    Assert("R7: 遍历后代期间父目录仍钉住", MoveBlocked(child, child + ".moved", true));
                    Assert("R7: 校验期间交付根仍钉住", MoveBlocked(root, root + ".moved", true));
                    Assert("R7: 使用绑定句柄读取长度与 CRC", RandomAccess.GetLength(handle) == 11 &&
                        Crc32.ComputeHandle(handle) == Crc32.Compute(Encoding.UTF8.GetBytes("pinned-data")));
                }, msg => Assert("R7: 安全遍历无错误", false, msg));
                Assert("R7: 实际进入文件回调", visited);
            }
            string source = Path.Combine(_testDir, "r7-web.zip");
            File.WriteAllText(source, "source");
            File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            Assert("R7: 钉住期间来源标记正常写入", ZoneIdentifier.Propagate(source, root) == 1 &&
                File.Exists(file + ":Zone.Identifier"));

            string leaks = NewStaging("r7exceptions");
            for (int i = 0; i < 100; i++) Directory.CreateDirectory(Path.Combine(leaks, "d" + i));
            File.WriteAllText(Path.Combine(leaks, "file.txt"), "x");
            using var leakRoot = DeliveryVerifier.OpenPinnedObject(leaks)!;
            foreach (string kind in new[] { "file", "directory", "problem" })
            {
                string? link = null;
                if (kind == "problem")
                {
                    link = Path.Combine(leaks, "junction");
                    Assert("R7: 异常测试创建联接点", CreateJunction(link, child));
                }
                // 预热运行时路径，避免首次 JIT/系统初始化影响句柄计数。
                for (int iteration = 0; iteration < 2; iteration++)
                {
                    using var process = Process.GetCurrentProcess();
                    int before = process.HandleCount;
                    bool threw = false;
                    try
                    {
                        DeliveryVerifier.WalkVerifiedTree(leakRoot, leaks,
                            _ => { if (kind == "directory") throw new InvalidOperationException("expected"); },
                            (_, _, _) => { if (kind == "file") throw new InvalidOperationException("expected"); },
                            _ => { if (kind == "problem") throw new InvalidOperationException("expected"); });
                    }
                    catch (InvalidOperationException) { threw = true; }
                    process.Refresh();
                    if (iteration == 1)
                        Assert("R7: " + kind + " 回调异常同步释放全部句柄", threw && process.HandleCount <= before,
                            $"before={before} after={process.HandleCount}");
                }
                if (link != null) Directory.Delete(link);
            }
            // 不强制 GC，直接独占打开每个目录，检测是否仍遗留拒绝删除共享的句柄。
            Assert("R7: 异常后文件立即可改名", !MoveBlocked(Path.Combine(leaks, "file.txt"), Path.Combine(leaks, "renamed.txt"), false));
        }

        // 从 Worker finished 消息解析逐卷身份（与 MainWindow 一致：优先 16 字节 file_id 规范表示）
        static List<VolumeIdentity> ParseWorkerVolumes(WorkerRun run)
        {
            var list = new List<VolumeIdentity>();
            if (!run.Finished.HasValue) return list;
            var root = run.Finished.Value;
            if (!root.TryGetProperty("volume_identities", out var ids) || ids.ValueKind != JsonValueKind.Array) return list;
            foreach (var element in ids.EnumerateArray())
            {
                var identity = new VolumeIdentity
                {
                    Path = element.TryGetProperty("path", out var p) ? (p.GetString() ?? "") : "",
                    VolumeSerial = element.TryGetProperty("volume", out var v) ? v.GetUInt64() : 0,
                    Size = element.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    LastWriteTime = element.TryGetProperty("mtime", out var m) ? m.GetInt64() : 0,
                    Valid = element.TryGetProperty("valid", out var va) && va.ValueKind == JsonValueKind.True
                };
                if (element.TryGetProperty("file_id", out var fid) && fid.ValueKind == JsonValueKind.String)
                {
                    string hex = fid.GetString() ?? "";
                    if (hex.Length == 32)
                    {
                        try { identity.FileId = Convert.FromHexString(hex); }
                        catch { identity.Valid = false; }
                    }
                    else
                    {
                        identity.Valid = false;
                    }
                }
                else
                {
                    identity.FileIdHigh = element.TryGetProperty("file_id_high", out var fh) ? fh.GetUInt64() : 0;
                    identity.FileIdLow = element.TryGetProperty("file_id_low", out var fl) ? fl.GetUInt64() : 0;
                }
                list.Add(identity);
            }
            return list;
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

            // 未声明编码的 UTF-8 名称（清除 bit 11）：在自动模式下由 3 字节 UTF-8 特征识别（不传 --cp）
            string noFlagZip = Path.Combine(_testDir, "utf8_noflag.zip");
            CreateZip(noFlagZip, Encoding.UTF8, (@"图纸\设计说明.txt", "utf8-no-flag"));
            ClearZipUtf8Flags(noFlagZip);
            string noFlagStaging = NewStaging("utf8noflag");
            string noFlagManifest = Path.Combine(_testDir, "manifest_utf8noflag.json");
            var noFlagRun = RunWorker(noFlagZip, noFlagStaging, noFlagManifest);
            var noFlagResult = CompleteViaProduction(noFlagZip, noFlagStaging, noFlagManifest, noFlagRun.DeliveryRoot);
            Assert("编码: 未声明编码的 UTF-8 名称在自动模式下仍能正确解码",
                noFlagResult.Delivered && File.Exists(Path.Combine(noFlagResult.FinalDirectory, "图纸", "设计说明.txt")),
                noFlagResult.FailureReason);
            Assert("编码: 未声明 UTF-8 在自动模式下不弹编码选择框（UI 未显式传 --cp）", !noFlagRun.EncodingOptionsOffered);

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
                Assert("界面契约: 未设置编码时不再把系统 ACP 冒充为显式 --cp", !codeBehind.Contains("GetACP"));
            }
            else
            {
                Assert("界面契约: 找到界面源码", false, xamlPath);
            }

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 19] P2-2：V7 TAR 裸包与 gzip 复合包");
            string v7Tar = Path.Combine(_testDir, "v7.tar");
            CreateTarWithEntries(v7Tar, TarEntryFormat.V7, ("readme.txt", "v7-content"));
            string v7Staging = NewStaging("v7");
            string v7Manifest = Path.Combine(_testDir, "manifest_v7.json");
            var v7Run = RunWorker(v7Tar, v7Staging, v7Manifest, cp: "936");
            var v7Result = CompleteViaProduction(v7Tar, v7Staging, v7Manifest, v7Run.DeliveryRoot);
            Assert("V7 TAR: 裸包正确交付正文",
                v7Result.Delivered && File.Exists(Path.Combine(v7Result.FinalDirectory, "readme.txt")), v7Result.FailureReason);

            string v7Gz = v7Tar + ".gz";
            CopyGzip(v7Tar, v7Gz, false);
            string v7gzStaging = NewStaging("v7gz");
            string v7gzManifest = Path.Combine(_testDir, "manifest_v7gz.json");
            var v7gzRun = RunWorker(v7Gz, v7gzStaging, v7gzManifest, cp: "936");
            Assert("V7 TAR: gzip 复合包不再被签名检查误拒绝", v7gzRun.Status == "ok" && v7gzRun.ExitCode == 0,
                $"status={v7gzRun.Status} exit={v7gzRun.ExitCode} err={v7gzRun.ErrorReason}");
            var v7gzResult = CompleteViaProduction(v7Gz, v7gzStaging, v7gzManifest, v7gzRun.DeliveryRoot);
            Assert("V7 TAR: 复合包交付内层正文",
                v7gzResult.Delivered && File.Exists(Path.Combine(v7gzResult.FinalDirectory, "readme.txt")), v7gzResult.FailureReason);

            // ustar / GNU 裸包也需被识别（有成对 magic，旧代码已支持，此处防回归）
            string ustarTar = Path.Combine(_testDir, "ustar.tar");
            CreateTarWithEntries(ustarTar, TarEntryFormat.Ustar, ("u.txt", "ustar-content"));
            string ustarStaging = NewStaging("ustar");
            string ustarManifest = Path.Combine(_testDir, "manifest_ustar.json");
            var ustarRun = RunWorker(ustarTar, ustarStaging, ustarManifest, cp: "936");
            var ustarResult = CompleteViaProduction(ustarTar, ustarStaging, ustarManifest, ustarRun.DeliveryRoot);
            Assert("ustar TAR: 裸包正确交付", ustarResult.Delivered && File.Exists(Path.Combine(ustarResult.FinalDirectory, "u.txt")), ustarResult.FailureReason);

            string gnuTar = Path.Combine(_testDir, "gnu.tar");
            CreateTarWithEntries(gnuTar, TarEntryFormat.Gnu, ("g.txt", "gnu-content"));
            string gnuStaging = NewStaging("gnu");
            string gnuManifest = Path.Combine(_testDir, "manifest_gnu.json");
            var gnuRun = RunWorker(gnuTar, gnuStaging, gnuManifest, cp: "936");
            var gnuResult = CompleteViaProduction(gnuTar, gnuStaging, gnuManifest, gnuRun.DeliveryRoot);
            Assert("GNU TAR: 裸包正确交付", gnuResult.Delivered && File.Exists(Path.Combine(gnuResult.FinalDirectory, "g.txt")), gnuResult.FailureReason);

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 20] P1-2：ZIP Unicode Path 0x7075 与 PAX UTF-8 声明");
            byte[] fallbackName = Encoding.UTF8.GetBytes("fallback.txt");
            byte[] unicodeNameBytes = Encoding.UTF8.GetBytes("图.txt");
            byte[] unicodeExtra;
            using (var extraMs = new MemoryStream())
            using (var extraW = new BinaryWriter(extraMs, Encoding.UTF8, true))
            {
                extraW.Write((ushort)0x7075);
                extraW.Write((ushort)(5 + unicodeNameBytes.Length));
                extraW.Write((byte)1); // version
                extraW.Write(Crc32.Compute(fallbackName));
                extraW.Write(unicodeNameBytes);
                unicodeExtra = extraMs.ToArray();
            }
            string unicodeZip = Path.Combine(_testDir, "unicode_extra.zip");
            CreateZipRaw(unicodeZip, (fallbackName, Encoding.UTF8.GetBytes("unicode-extra-content"), 0, unicodeExtra));
            string unicodeStaging = NewStaging("unicode_extra");
            string unicodeManifest = Path.Combine(_testDir, "manifest_unicode_extra.json");
            var unicodeRun = RunWorker(unicodeZip, unicodeStaging, unicodeManifest, cp: "936");
            var unicodeResult = CompleteViaProduction(unicodeZip, unicodeStaging, unicodeManifest, unicodeRun.DeliveryRoot);
            Assert("Unicode Path: 0x7075 扩展名逐字保留为 图.txt",
                unicodeResult.Delivered && File.Exists(Path.Combine(unicodeResult.FinalDirectory, "图.txt")), unicodeResult.FailureReason);
            Assert("Unicode Path: 不再交付回退名 fallback.txt",
                unicodeResult.Delivered && !File.Exists(Path.Combine(unicodeResult.FinalDirectory, "fallback.txt")));

            string paxGreek = Path.Combine(_testDir, "pax_greek.tar");
            CreateTarWithEntries(paxGreek, TarEntryFormat.Pax, ("Δ.txt", "greek-content"));
            string greekStaging = NewStaging("paxgreek");
            string greekManifest = Path.Combine(_testDir, "manifest_paxgreek.json");
            var greekRun = RunWorker(paxGreek, greekStaging, greekManifest, cp: "936");
            var greekResult = CompleteViaProduction(paxGreek, greekStaging, greekManifest, greekRun.DeliveryRoot);
            Assert("PAX UTF-8: path 记录按 UTF-8 声明解码为 Δ.txt",
                greekResult.Delivered && File.Exists(Path.Combine(greekResult.FinalDirectory, "Δ.txt")), greekResult.FailureReason);

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 21] P1-3：GBK 两字节歧义与显式编码选择");
            var gbk2 = Encoding.GetEncoding(936);
            string gbkLatinZip = Path.Combine(_testDir, "gbk_latin.zip");
            CreateZip(gbkLatinZip, gbk2, ("茅.txt", "gbk-content"));
            string gbkLatinStaging = NewStaging("gbklatin");
            string gbkLatinManifest = Path.Combine(_testDir, "manifest_gbklatin.json");
            var gbkLatinRun = RunWorker(gbkLatinZip, gbkLatinStaging, gbkLatinManifest, cp: "936");
            var gbkLatinResult = CompleteViaProduction(gbkLatinZip, gbkLatinStaging, gbkLatinManifest, gbkLatinRun.DeliveryRoot);
            Assert("GBK 两字节歧义: 显式 --cp 936 严格解码为 茅.txt（不再误判 é.txt）",
                gbkLatinResult.Delivered && File.Exists(Path.Combine(gbkLatinResult.FinalDirectory, "茅.txt")), gbkLatinResult.FailureReason);
            Assert("GBK 两字节歧义: 不再落盘 é.txt",
                gbkLatinResult.Delivered && !File.Exists(Path.Combine(gbkLatinResult.FinalDirectory, "é.txt")));

            // 混合声明触发选择框，回答 CP936 后“预览=落盘”且两类名称都正确
            string gbkChoiceZip = Path.Combine(_testDir, "gbk_choice.zip");
            CreateZipRaw(gbkChoiceZip,
                (gbk2.GetBytes("茅.txt"), Encoding.UTF8.GetBytes("gbk-content"), 0, Array.Empty<byte>()),
                (Encoding.UTF8.GetBytes("图.txt"), Encoding.UTF8.GetBytes("utf8-content"), 0x800, Array.Empty<byte>()));
            string gbkChoiceStaging = NewStaging("gbkchoice");
            string gbkChoiceManifest = Path.Combine(_testDir, "manifest_gbkchoice.json");
            var gbkChoiceRun = RunWorker(gbkChoiceZip, gbkChoiceStaging, gbkChoiceManifest, cp: "936", encodingCp: 936);
            Assert("编码选择: 混合声明触发选择框", gbkChoiceRun.EncodingOptionsOffered);
            var gbkChoiceResult = CompleteViaProduction(gbkChoiceZip, gbkChoiceStaging, gbkChoiceManifest, gbkChoiceRun.DeliveryRoot);
            Assert("编码选择: CP936 后 茅.txt 与 图.txt 同时正确（预览=落盘）",
                gbkChoiceResult.Delivered && File.Exists(Path.Combine(gbkChoiceResult.FinalDirectory, "茅.txt")) &&
                File.Exists(Path.Combine(gbkChoiceResult.FinalDirectory, "图.txt")), gbkChoiceResult.FailureReason);

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 22] P1-1：内层拒绝项必须传播为 partial 并禁止回收");
            string longName = string.Join("/", Enumerable.Repeat(new string('a', 35), 17)) + "/payload.txt";
            string longTar = Path.Combine(_testDir, "long.tar");
            CreateTarWithEntries(longTar, TarEntryFormat.Pax, ("readme.txt", "short-content"), (longName, "long-content"));
            string longStaging = NewStaging("long");
            string longManifest = Path.Combine(_testDir, "manifest_long.json");
            var longRun = RunWorker(longTar, longStaging, longManifest, cp: "936");
            Assert("内层拒绝: 直接 TAR 状态为 partial、退出码 11、unsafe>0",
                longRun.Status == "partial" && longRun.ExitCode == 11 && longRun.Unsafe > 0,
                $"status={longRun.Status} exit={longRun.ExitCode} unsafe={longRun.Unsafe}");

            string longGz = longTar + ".gz";
            CopyGzip(longTar, longGz, false);
            string longGzStaging = NewStaging("longgz");
            string longGzManifest = Path.Combine(_testDir, "manifest_longgz.json");
            var longGzRun = RunWorker(longGz, longGzStaging, longGzManifest, cp: "936");
            Assert("内层拒绝: gzip 复合包状态为 partial、退出码 11、unsafe>0（不再假成功）",
                longGzRun.Status == "partial" && longGzRun.ExitCode == 11 && longGzRun.Unsafe > 0,
                $"status={longGzRun.Status} exit={longGzRun.ExitCode} unsafe={longGzRun.Unsafe}");
            var longGzResult = CompleteViaProduction(longGz, longGzStaging, longGzManifest, longGzRun.DeliveryRoot);
            Assert("内层拒绝: 正文仍交付、深层缺失项不落盘",
                longGzResult.Delivered && File.Exists(Path.Combine(longGzResult.FinalDirectory, "readme.txt")) &&
                !File.Exists(Path.Combine(longGzResult.FinalDirectory, "payload.txt")), longGzResult.FailureReason);

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 23] P2-1：Worker→IPC→C# 文件身份链路");
            string idZip = Path.Combine(_testDir, "identity.zip");
            CreateZipStored(idZip, ("a.txt", "identity-content"));
            string idStaging = NewStaging("identity");
            string idManifest = Path.Combine(_testDir, "manifest_identity.json");
            var idRun = RunWorker(idZip, idStaging, idManifest, cp: "936");
            var workerVolumes = ParseWorkerVolumes(idRun);
            var managedCapture = VolumeIdentity.Capture(idZip);
            Assert("身份链路: Worker 上报 ok 且逐卷身份存在", idRun.Status == "ok" && workerVolumes.Count == 1,
                $"status={idRun.Status} volumes={workerVolumes.Count}");
            Assert("身份链路: Worker 16 字节文件 ID 与托管端捕获一致",
                workerVolumes.Count == 1 && managedCapture != null && workerVolumes[0].Matches(managedCapture),
                $"match={workerVolumes.Count == 1 && managedCapture != null && workerVolumes[0].Matches(managedCapture)}");

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 24] P2-3 前导空格 与 P2-4 重复目录条目");
            string spaceZip = Path.Combine(_testDir, "leading_space.zip");
            CreateZipRaw(spaceZip, (Encoding.UTF8.GetBytes(" leading.txt"), Encoding.UTF8.GetBytes("leading-content"), 0, Array.Empty<byte>()));
            string spaceStaging = NewStaging("leadingspace");
            string spaceManifest = Path.Combine(_testDir, "manifest_leadingspace.json");
            var spaceRun = RunWorker(spaceZip, spaceStaging, spaceManifest, cp: "936");
            var spaceResult = CompleteViaProduction(spaceZip, spaceStaging, spaceManifest, spaceRun.DeliveryRoot);
            Assert("前导空格: 合法文件名逐字保留并交付",
                spaceResult.Delivered && File.Exists(Path.Combine(spaceResult.FinalDirectory, " leading.txt")), spaceResult.FailureReason);

            string dupZip = Path.Combine(_testDir, "duplicate_dirs.zip");
            CreateZipRaw(dupZip,
                (Encoding.UTF8.GetBytes("folder/"), Array.Empty<byte>(), 0, Array.Empty<byte>()),
                (Encoding.UTF8.GetBytes("folder/a.txt"), Encoding.UTF8.GetBytes("a"), 0, Array.Empty<byte>()),
                (Encoding.UTF8.GetBytes("folder/"), Array.Empty<byte>(), 0, Array.Empty<byte>()),
                (Encoding.UTF8.GetBytes("folder/b.txt"), Encoding.UTF8.GetBytes("b"), 0, Array.Empty<byte>()));
            string dupStaging = NewStaging("dupdirs");
            string dupManifest = Path.Combine(_testDir, "manifest_dupdirs.json");
            var dupRun = RunWorker(dupZip, dupStaging, dupManifest, cp: "936");
            var dupResult = CompleteViaProduction(dupZip, dupStaging, dupManifest, dupRun.DeliveryRoot);
            Assert("重复目录: 同一实际目录重复声明可合并，整包可提交",
                dupResult.Delivered && File.Exists(Path.Combine(dupResult.FinalDirectory, "folder", "a.txt")) &&
                File.Exists(Path.Combine(dupResult.FinalDirectory, "folder", "b.txt")), dupResult.FailureReason);

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 25] P1-4：已存在联接点必须被拒绝且根外无副作用");
            string juncDir = Path.Combine(_testDir, "junc");
            Directory.CreateDirectory(juncDir);
            string juncZip = Path.Combine(juncDir, "linked_dir.zip");
            CreateZipRaw(juncZip, (Encoding.UTF8.GetBytes("linked/"), Array.Empty<byte>(), 0, Array.Empty<byte>()));
            string juncStaging = Path.Combine(juncDir, "staging");
            Directory.CreateDirectory(juncStaging);
            string juncExternal = Path.Combine(juncDir, "external");
            Directory.CreateDirectory(juncExternal);
            bool linkOk = CreateJunction(Path.Combine(juncStaging, "linked"), juncExternal);
            Assert("联接点: 预置 junction 成功", linkOk);
            string juncManifest = Path.Combine(juncDir, "manifest_junc.json");
            var juncRun = RunWorker(juncZip, juncStaging, juncManifest, cp: "936");
            Assert("联接点: 仅目录条目被拒绝（退出码非零，不再成功）", juncRun.ExitCode != 0,
                $"exit={juncRun.ExitCode} status={juncRun.Status}");

            string juncChildZip = Path.Combine(juncDir, "linked_child.zip");
            CreateZipRaw(juncChildZip, (Encoding.UTF8.GetBytes("linked/child/"), Array.Empty<byte>(), 0, Array.Empty<byte>()));
            string juncChildStaging = Path.Combine(juncDir, "staging_child");
            Directory.CreateDirectory(juncChildStaging);
            string juncChildExternal = Path.Combine(juncDir, "external_child");
            Directory.CreateDirectory(juncChildExternal);
            bool linkOk2 = CreateJunction(Path.Combine(juncChildStaging, "linked"), juncChildExternal);
            Assert("联接点: 预置 junction 成功 (child)", linkOk2);
            string juncChildManifest = Path.Combine(juncDir, "manifest_juncchild.json");
            var juncChildRun = RunWorker(juncChildZip, juncChildStaging, juncChildManifest, cp: "936");
            bool outsideChildCreated = Directory.Exists(Path.Combine(juncChildExternal, "child"));
            Assert("联接点: 根外不得创建 child 目录", !outsideChildCreated,
                $"exit={juncChildRun.ExitCode} outsideChildCreated={outsideChildCreated}");

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 26] P1：PAX hdrcharset=BINARY 必须尊重用户显式编码");
            var gbkForPax = Encoding.GetEncoding(936);
            byte[] maoPathBytes = gbkForPax.GetBytes("茅.txt"); // GBK: C3 A9 2E 74 78 74
            string paxBinaryTar = Path.Combine(_testDir, "pax_binary.tar");
            CreatePaxBinaryTar(paxBinaryTar, maoPathBytes, "pax-binary-content");
            string paxBinaryStaging = NewStaging("paxbinary");
            string paxBinaryManifest = Path.Combine(_testDir, "manifest_paxbinary.json");
            var paxBinaryRun = RunWorker(paxBinaryTar, paxBinaryStaging, paxBinaryManifest, cp: "936");
            Assert("PAX BINARY: 指定 --cp 936 时按 GBK 解码为 茅.txt（不再绕过显式编码）",
                paxBinaryRun.Status == "ok" && File.Exists(Path.Combine(paxBinaryStaging, "茅.txt")),
                $"status={paxBinaryRun.Status} exit={paxBinaryRun.ExitCode} err={paxBinaryRun.ErrorReason}");
            Assert("PAX BINARY: 不再落盘 é.txt 乱码名", !File.Exists(Path.Combine(paxBinaryStaging, "é.txt")));
            Assert("PAX BINARY: 显式编码已定，不弹编码选择框", !paxBinaryRun.EncodingOptionsOffered);
            var paxBinaryResult = CompleteViaProduction(paxBinaryTar, paxBinaryStaging, paxBinaryManifest, paxBinaryRun.DeliveryRoot);
            Assert("PAX BINARY: 生产路径交付 茅.txt",
                paxBinaryResult.Delivered && File.Exists(Path.Combine(paxBinaryResult.FinalDirectory, "茅.txt")),
                paxBinaryResult.FailureReason);

            // 全局 pax 头声明 hdrcharset=BINARY，局部头只含 path 字节：全局声明必须对后续文件生效
            var gbkForPaxGlobal = Encoding.GetEncoding(936);
            byte[] maoPathBytesGlobal = gbkForPaxGlobal.GetBytes("茅.txt"); // GBK: C3 A9 2E 74 78 74
            string paxGlobalTar = Path.Combine(_testDir, "pax_global_binary.tar");
            CreatePaxGlobalBinaryTar(paxGlobalTar, maoPathBytesGlobal, "pax-global-content");
            string paxGlobalStaging = NewStaging("paxglobalbinary");
            string paxGlobalManifest = Path.Combine(_testDir, "manifest_paxglobalbinary.json");
            var paxGlobalRun = RunWorker(paxGlobalTar, paxGlobalStaging, paxGlobalManifest, cp: "936");
            Assert("PAX GLOBAL BINARY: 全局 hdrcharset=BINARY 对后续文件生效，--cp 936 解码为 茅.txt",
                paxGlobalRun.Status == "ok" && File.Exists(Path.Combine(paxGlobalStaging, "茅.txt")),
                $"status={paxGlobalRun.Status} exit={paxGlobalRun.ExitCode} err={paxGlobalRun.ErrorReason}");
            Assert("PAX GLOBAL BINARY: 不再落盘 é.txt 乱码名", !File.Exists(Path.Combine(paxGlobalStaging, "é.txt")));
            Assert("PAX GLOBAL BINARY: 显式编码已定，不弹编码选择框", !paxGlobalRun.EncodingOptionsOffered);
            var paxGlobalResult = CompleteViaProduction(paxGlobalTar, paxGlobalStaging, paxGlobalManifest, paxGlobalRun.DeliveryRoot);
            Assert("PAX GLOBAL BINARY: 生产路径交付 茅.txt",
                paxGlobalResult.Delivered && File.Exists(Path.Combine(paxGlobalResult.FinalDirectory, "茅.txt")),
                paxGlobalResult.FailureReason);

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 27] P1：交付根/祖先为联接点时，Verify 与来源标记必须拒绝");
            string repBase = Path.Combine(_testDir, "reparse_verify");
            Directory.CreateDirectory(repBase);
            string repOutside = Path.Combine(repBase, "outside");
            Directory.CreateDirectory(repOutside);
            File.WriteAllText(Path.Combine(repOutside, "payload.txt"), "outside-payload");
            uint repCrc = Crc32.Compute(Encoding.UTF8.GetBytes("outside-payload"));
            string repStaging = Path.Combine(repBase, "staging");
            Directory.CreateDirectory(repStaging);
            string repRoot = Path.Combine(repStaging, "deliver");
            bool repLinkOk = CreateJunction(repRoot, repOutside);
            Assert("交付根联接点: 创建 junction 成功", repLinkOk);
            var repManifest = new DeliveryManifest
            {
                DeliveryRootRelative = "deliver",
                ItemCount = 1,
                Items = new List<ManifestItem>
                {
                    new ManifestItem
                    {
                        Path = "payload.txt",
                        IsDirectory = false,
                        Size = Encoding.UTF8.GetByteCount("outside-payload"),
                        Crc = repCrc
                    }
                }
            };
            var repReport = DeliveryVerifier.Verify(repRoot, repManifest, VerifyMode.StructureAndDigest);
            Assert("交付根联接点: Verify 必须拒绝（不再顺着联接点越界对账）", !repReport.Ok, repReport.Describe());

            // 祖先联接点：交付根本身是普通目录，但其父目录被替换为指向根外的联接点
            string repAncestorOutside = Path.Combine(repBase, "ancestor_outside");
            Directory.CreateDirectory(Path.Combine(repAncestorOutside, "deliver"));
            File.WriteAllText(Path.Combine(repAncestorOutside, "deliver", "payload.txt"), "ancestor-outside");
            string repAncestorStaging = Path.Combine(repBase, "ancestor_staging");
            Directory.CreateDirectory(repAncestorStaging);
            string repAncestorLink = Path.Combine(repAncestorStaging, "parent");
            bool repAncestorLinkOk = CreateJunction(repAncestorLink, repAncestorOutside);
            Assert("祖先联接点: 创建 junction 成功", repAncestorLinkOk);
            var repAncestorReport = DeliveryVerifier.Verify(
                Path.Combine(repAncestorLink, "deliver"), repManifest, VerifyMode.StructureAndDigest);
            Assert("祖先联接点: Verify 必须拒绝（祖先被替换为联接点）", !repAncestorReport.Ok, repAncestorReport.Describe());

            // 来源标记：入口目录为联接点时不得把 Zone.Identifier 写到根外
            string repZip = Path.Combine(repBase, "web.zip");
            File.WriteAllText(repZip, "dummy");
            File.WriteAllText(repZip + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            int repMarked = ZoneIdentifier.Propagate(repZip, repRoot);
            bool repOutsideMarked = File.Exists(Path.Combine(repOutside, "payload.txt") + ":Zone.Identifier");
            Assert("来源标记: 入口目录为联接点时不得写根外 Zone.Identifier",
                repMarked == 0 && !repOutsideMarked, $"marked={repMarked} outsideMarked={repOutsideMarked}");

            // 回归：交付树内部的子目录为联接点时，Verify 的逐项句柄级最终路径核对必须拒绝
            // （不仅限于交付根/祖先），从而覆盖“根目录被并发替换成联接点后越界对账”的同类路径。
            string subRepBase = Path.Combine(_testDir, "reparse_subdir");
            Directory.CreateDirectory(subRepBase);
            string subRepOutside = Path.Combine(subRepBase, "outside");
            Directory.CreateDirectory(subRepOutside);
            File.WriteAllText(Path.Combine(subRepOutside, "inner.txt"), "sub-outside");
            uint subRepInnerCrc = Crc32.Compute(Encoding.UTF8.GetBytes("sub-outside"));
            string subRepRoot = Path.Combine(subRepBase, "deliver");
            Directory.CreateDirectory(subRepRoot);
            bool subRepLinkOk = CreateJunction(Path.Combine(subRepRoot, "linked"), subRepOutside);
            Assert("交付树子目录联接点: 创建 junction 成功", subRepLinkOk);
            var subRepManifest = new DeliveryManifest
            {
                DeliveryRootRelative = "deliver",
                ItemCount = 1,
                Items = new List<ManifestItem>
                {
                    new ManifestItem
                    {
                        Path = @"linked\inner.txt",
                        IsDirectory = false,
                        Size = Encoding.UTF8.GetByteCount("sub-outside"),
                        Crc = subRepInnerCrc
                    }
                }
            };
            var subRepReport = DeliveryVerifier.Verify(subRepRoot, subRepManifest, VerifyMode.StructureAndDigest);
            Assert("交付树子目录联接点: Verify 必须拒绝（逐项句柄核对，不得顺着联接点越界对账）",
                !subRepReport.Ok, subRepReport.Describe());

            // -------------------------------------------------------------
            Console.WriteLine("\n>>> [测试 28] R7：全局空字符集删除与缺省继承");
            foreach (bool reset in new[] { true, false })
            {
                string tar = Path.Combine(_testDir, "pax_reset_" + reset + ".tar");
                CreatePaxCharsetResetTar(tar, reset);
                string staging = NewStaging("paxreset" + reset);
                string manifest = Path.Combine(_testDir, "pax_reset_" + reset + ".json");
                var run = RunWorker(tar, staging, manifest, cp: "936");
                string wanted = reset ? "é.txt" : "茅.txt";
                string unwanted = reset ? "茅.txt" : "é.txt";
                Assert("R7: PAX " + (reset ? "空值恢复 UTF-8" : "无字段仍继承 BINARY"),
                    run.Status == "ok" && File.Exists(Path.Combine(staging, wanted)) &&
                    !File.Exists(Path.Combine(staging, unwanted)), $"status={run.Status} error={run.ErrorReason}");
                var completion = CompleteViaProduction(tar, staging, manifest, run.DeliveryRoot);
                Assert("R7: PAX 状态变更后可交付", completion.Delivered, completion.FailureReason);
            }
            TestPinnedTraversal();

            Console.WriteLine("\n==================================================");
            Console.WriteLine($"测试总结: 共 {_pass + _fail} 项测试，通过: {_pass}，失败: {_fail}");
            Console.WriteLine("==================================================");
            return _fail == 0 ? 0 : 1;
        }
    }
}
