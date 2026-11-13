# Unbox 项目开发交接文档 (HANDOFF.md)

本文件记录了本轮根据《Unbox-v1.1.0-独立代码复审.md》缺陷审查报告所执行的代码还原、根因分析、重新修复与验证全过程。

---

## 1. 核心背景与乱码根因定位

### 1.1 乱码问题定位与根除
- **根因分析**：
  1. **解压落盘文件乱码根因**：前序修改在 C# WPF 传递代码页时，使用了 `Encoding.Default.CodePage`（在 .NET 10 中恒定返回 65001），向 Worker 强传 `--cp 65001` 破坏了 7-Zip 对中文 Windows 常见 ANSI/GBK (`936`) 压缩包的自动代码页回退机制，并在 `PathSanitizer::FixMojibake` 中触发误判定；
  2. **详细信息窗口“名称”一栏乱码根因（截图问题）**：C# WPF 启动 `UnzipWorker.exe` 进程的 `ProcessStartInfo` 中，**未显式指定 `StandardOutputEncoding` 和 `StandardInputEncoding`**。在 Windows 默认环境下，.NET 会采用系统的 OEM/ANSI 代码页（GBK / 936）创建读取管道的 `StreamReader`；而 Worker 核心通过二进制模式输出标准 UTF-8 编码的 JSON 字符流。WPF 端用 GBK 解码 UTF-8 字节流（例如将 `希捷专修` 的 UTF-8 字节 `E5 B8 8C E6 8D B7 E4 B8 93 E4 BF AE` 按照 GBK 解码为 `甯屾嵎涓撲慨`），导致进度条下方“名称”一栏显示出极具特征的乱码！
- **根治措施**：
  1. 在 `MainWindow.xaml.cs` 中通过 Win32 API `[DllImport("kernel32.dll")] private static extern uint GetACP();` 获取系统真实的 ANSI 代码页（如简体中文下的 936），且仅当 `acp != 0 && acp != 65001` 时才传递 `--cp {acp}`；
  2. 给 `ProcessStartInfo` 显式设置 `StandardInputEncoding = Encoding.UTF8`、`StandardOutputEncoding = Encoding.UTF8`、`StandardErrorEncoding = Encoding.UTF8`，彻底解决管道两端编码不对齐的问题；
  3. 在 `App.xaml.cs` 及 `MainWindow.xaml.cs` 中显式注册 `System.Text.CodePagesEncodingProvider.Instance`，全面激活全量代码页支持；
  4. 在 `MainWindow.xaml.cs` 的 UI 渲染层增加 `FixPotentialMojibake` 双重保险容错函数，即使收到历史残留或极端异常的 GBK/UTF-8 错码，也能逐段识别并自动无损纠偏；
  5. 新增 **【测试 16】中文详细信息UI管道与防乱码双重保障自动化测试**，断言进度汇报名称不含任何 `甯` 开头乱码，且能自动无损纠错。

### 1.2 代码基准还原
- 完整创建了基准分支 `baseline-2333`（Git Tree: `d511ea2f4e59a9a28a333e773356ee0fc1191c7b`），严格对齐上一轮审核（23点33分左右）代码状态；
- 保留 `HANDOFF.md` 和 `Unbox-v1.1.0-独立代码复审.md`，在干净基准之上重新实施全量缺陷修复。

---

## 2. 独立代码审查修复清单（已全部落实）

### 2.1 P1 级别阻断项
1. **完成消息读取已释放的 Worker 回调 (UAF 释放后读取)**：
   - 在 `main.cpp` 中调用 `callback->Release()` 触发 `delete this` 之前，提前将 `finalCompletedBytes`、`openedVolumes` 拷贝到局部栈变量，杜绝销毁后解引用。
2. **缺尾卷或内层失败导致假成功与源文件误删**：
   - `TryExtractInnerArchive` 改为返回 `InnerExtractResult`（`NotApplicable`, `Success`, `Failed`, `Canceled`）；
   - 严格约束递归展开条件：仅当外层为切分分卷（`.001`）或约定复合 TAR（`.tar.gz`/`.tgz` 等）时触发；普通 ZIP 内包含的单个 `backup.zip` 严格原样交付；
   - 缺尾卷或内层解压失败时，明确断言 `isPartial = true`、`errReason = "corrupted_archive"`，清空 stagingDir 残损目录，Worker 退出非零代码（10），彻底阻断正式提交与源包回收；
   - 移除 WPF 中冗余脱管的二次展开 `UnpackInnerTarIfPresentAsync`，由 Worker 原生全权托管。
3. **输出未证明落盘即提交与回收**：
   - WPF 显式等待 Worker 彻底退出（15 秒超时带 Kill 保护），核验 `ExitCode == 0`；
   - 针对非空归档，核验暂存目录存在且包含有效产物，若失败立即清理暂存残损目录并中断；
   - `WorkerExtractCallback.h` 中创建目录时严格核验错误码，若失败立即记录错误并返回 Win32 对应 HRESULT。
4. **同族分卷隔离与回收失败友好提醒**：
   - `WorkerOpenCallback.h` 区分 `m_readDependencies` 与 `m_openedVolumes`，仅同目录同族物理分卷才进入回收名单；
   - 若回收失败，界面明确警告用户“解压已完成，但无法移入回收站：...。源压缩包已保留”，并保持窗口打开供用户确认，绝不静默吞错。
5. **写盘边界与已验证句柄**：
   - 内层解压直接消费已验证落盘的 `actualDiskPath`（来自 `info.diskPath`），杜绝二次拼路径差异。

### 2.2 P2 级别规范项
1. **无原名 GZip 流 (RFC 1952 规范)**：
   - 实现 `DeriveSingleStreamFilename`，在条目路径为空且为单流模式时，自动从输入包名剥离后缀安全推导名称（如 `.txt.gz` -> `.txt`）。
2. **Unicode 代理对与 Emoji 密码 (RFC 8259 规范)**：
   - `ExtractJsonStringField` 支持 UTF-16 高低代理对合并与 4 字节 UTF-8 编码，彻底解决非 BMP 字符和 Emoji 密码转义损坏。
3. **安全来源标记 (Mark of the Web)**：
   - 实现 `PropagateZoneIdentifier`，通过 NTFS Alternate Data Stream 将源文件的 `:Zone.Identifier` 完整复制给所有解压文件。
4. **构建、版本与文档一致性**：
   - `UnzipProxy.csproj` 显式设置版本号 `1.1.0`（AssemblyVersion `1.1.0.0`）；
   - `README.md` 明确说明采用 **.NET 10 LTS 框架依赖部署**；
   - `LICENSE` 补充 7-Zip LGPL 及 unRAR 完整官方许可声明；
   - `build_worker.ps1` 与 `build_worker.bat` 均携带 `/utf-8` 编译参数；
   - `package_release.ps1` 显式增加前置 `dotnet restore`。

---

## 3. 测试与产物验证

### 3.1 自动化测试套件
- 运行：`& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project tests\Tester\Tester.csproj -c Release`
- 结果：**57/57 项测试 100% 通过（0 失败）**。
- 覆盖率：D1-D6 规范、UNC 拒绝、加密头 7z、三卷 7z 正常解压、1200 个小文件性能与限流（耗时 336ms, 12帧）、**缺尾卷反例断言**、**普通单 ZIP 嵌套原样交付断言**、**无名 GZip 流推导断言**、**Emoji / 代理对密码往返断言**、**Zone.Identifier 来源标记传递断言**、**中文文件名解压无乱码断言**、**UI名称管道UTF-8防乱码断言**、**UI双保险自动纠偏断言**。

### 3.2 发行包产物
- 文件：`dist/Unbox-v1.1.0-win-x64.zip`（体积 2.65 MB，2,774,189 字节）
- 内部 `Unbox.exe` 文件版本：`1.1.0.0`，产品版本：`1.1.0`。
