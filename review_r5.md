## 待修复清单 (Round  5)

### Issue 1: [连续未解决] [P1] `src/UnzipProxy/Core/DeliveryManifest.cs:188` (`DeliveryVerifier.Verify`)

- **问题与风险**：第四轮 P1-4 / 必查项 11–12 仍未关闭，新增检查仅覆盖根目录下的条目，交付根本身及其祖先若被替换为联接点仍可通过校验，且 `ZoneIdentifier.EnumerateFilesNoReparse` 同样漏查入口目录；实测以指向暂存根外的联接点作为交付根，摘要校验 `Ok=true`、生产完成路径 `Delivered=true`、最终目录仍为联接点，并向根外文件写入了 `Zone.Identifier`。
- **定位代码**：
  ```csharp
  foreach (string entry in entries)
  {
      string rel = Rel(deliveryRootAbsolute, entry);
      var attributes = File.GetAttributes(entry);
      // 交付树不允许出现任何非预期 reparse point（联接点/符号链接），
      // 防止通过联接点把最终目录指向暂存区之外。
      if ((attributes & FileAttributes.ReparsePoint) != 0)
      {
          report.Problems.Add($"交付树包含非预期的联接点/符号链接：{rel}");
          continue;
      }
  ```

### Issue 2: [连续未解决] [P1] `src/UnzipWorker/PathSanitizer.h:223` (`EnsureDirectory`)

- **问题与风险**：第四轮 P1-4 / 必查项 11–12 的竞争窗口仍存在，`VerifyDirectoryUnderRoot` 允许 `FILE_SHARE_DELETE` 且验证后立即关闭句柄，后续创建仍按可被替换的完整路径执行；在验证与创建之间将父目录换为指向根外的联接点，确定性复现实测 `EnsureDirectory=false / ERROR_ACCESS_DENIED`，但根外 `child` 目录已经创建，事后拒绝没有消除根外副作用。
- **定位代码**：
  ```cpp
  // 3. 逐级创建，并在每级创建后立即验证（缩小替换窗口，失败时不留下根外副作用）
  for (auto it = missing.rbegin(); it != missing.rend(); ++it) {
      if (!CreateDirectoryW(it->c_str(), nullptr)) {
          DWORD err = GetLastError();
          if (err != ERROR_ALREADY_EXISTS) {
              outError = err;
              return false;
          }
      }
      if (!VerifyDirectoryUnderRoot(canonicalRoot, it->wstring(), outError)) {
          return false;
      }
  }
  ```

### Issue 3: [连续未解决] [P1] `src/UnzipProxy/MainWindow.xaml.cs:243` (`StartExtractionAsync`)

- **问题与风险**：第四轮 P1-3 / 必查项 1–2 的修复产生回归，用户未设置编码时 UI 仍把 `GetACP()` 当作显式 `--cp` 传入，而本轮 Worker 将任何非零 `--cp` 都切换为严格解码，导致正常 UI 入口无法使用自动识别；实测未设 bit 11 的 UTF-8 `图纸.txt` 在自动模式下正确，模拟 ACP936 的 UI 参数后静默落盘为 `鍥剧焊.txt`，没有选码请求且仍返回 `status=ok / exit=0`。
- **定位代码**：
  ```csharp
  uint codePage = _settings.ArchiveCodePage != 0
      ? (uint)_settings.ArchiveCodePage
      : GetACP();
  string cpArg = (codePage != 0) ? $" --cp {codePage}" : "";
  ```
  ```cpp
  uint32_t effectiveCp = userCodePage != 0 ? userCodePage : SevenZipLibrary::GetDefaultArchiveCodePage();
  bool cpExplicit = (userCodePage != 0);

  // 外层条目计划（尊重归档声明的 Unicode 名称；显式编码时严格按该值解码）
  ItemPlan plan = BuildItemPlan(archive, numItems, archivePath, effectiveCp, true, cpExplicit);
  ```

### Issue 4: [连续未解决] [P1] `src/UnzipWorker/ArchiveNames.h:325` (`ParsePaxRecords`)

- **问题与风险**：第四轮 P1-2/P1-3 的修复仍未正确区分名称声明，新解析器忽略 PAX 的 `hdrcharset=BINARY` 并无条件把 `path` 标成 UTF-8，使二进制名称绕过用户显式编码选择；实测扩展头声明 `hdrcharset=BINARY`、路径字节为 GBK `茅.txt`（`C3 A9 2E 74 78 74`）的合法 TAR，即使指定 `--cp 936` 仍交付 `é.txt`，且没有选码请求、返回 `status=ok / exit=0`。
- **定位代码**：
  ```cpp
  std::string key = record.substr(keyStart, eq - keyStart);
  // 值以 '\n' 结尾，去掉末尾换行
  size_t valueLen = record.size() - (eq + 1);
  if (valueLen > 0 && record.back() == '\n') valueLen -= 1;
  std::string value = record.substr(eq + 1, valueLen);
  if (key == "path") {
      outPath = value;
      outPathIsUtf8 = true; // POSIX pax：path 记录值恒为 UTF-8
  }
  ```

