## 待修复清单 (Round 7)

### Issue 1: [连续未解决] [P1] `src/UnzipProxy/Core/DeliveryManifest.cs:272` (`DeliveryVerifier.OpenPinnedObject`)

- **问题与风险**：R6 Issue 1 的新增句柄未封闭检查与使用之间的竞争窗口：实测仅请求 `FILE_READ_ATTRIBUTES` 的句柄仍允许对象被改名，在测试副本的 `FileInfo` 调用前移走已打开的文件、再将腾空的交付根改名并替换为根外联接点，仍得到 `Verify.Ok=true / DigestsChecked=1`，且 `ZoneIdentifier.Propagate` 还会在遍历释放条目句柄后重新按路径写入，遍历后替换子目录的独立复现得到 `marked=1 / outsideMarked=true`，交付校验与来源标记仍可越过交付根边界。
- **定位代码**：
  ```csharp
  public static SafeFileHandle? OpenPinnedObject(string path)
  {
      SafeFileHandle handle = CreateFileW(path, FILE_READ_ATTRIBUTES,
          FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
          FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
      return handle.IsInvalid ? null : handle;
  }
  ```
  ```csharp
  // DeliveryVerifier.Verify：最终路径核对之后仍重新按路径访问文件。
  var info = new FileInfo(fullPath);
  if (info.Length != expectedItem.Size)
  {
      report.Problems.Add($"文件长度不一致：{rel}（期望 {expectedItem.Size}，实际 {info.Length}）");
      return;
  }
  if (mode == VerifyMode.StructureAndDigest)
  {
      uint actualCrc;
      try
      {
          actualCrc = Crc32.ComputeFile(fullPath);
      }
  ```
  ```csharp
  // ExtractionCompleter.cs:280，ZoneIdentifier.Propagate
  var files = new List<string>();
  DeliveryVerifier.WalkVerifiedTree(rootHandle, rootFinal,
      onDirectory: _ => { },
      onFile: (fullPath, _) => files.Add(fullPath),
      onProblem: _ => { });
  foreach (string file in files)
  {
      try
      {
          File.WriteAllBytes(file + StreamSuffix, zoneData);
          marked++;
      }
  ```

### Issue 2: [连续未解决] [P1] `src/UnzipWorker/PathSanitizer.h:375` (`OpenVerifiedDirectoryNoDelete`)

- **问题与风险**：R6 Issue 2 新增的 `FILE_FLAG_OPEN_REPARSE_POINT` 只检查末级目录对象，路径中间的联接点仍被解析而未锁定，用当前头文件对 `root\hop\parent\child` 的创建调用插入调度钩子（`hop` 最初指向根内，创建前改指根外、创建后换回），实测 `redirected=1 / created=1 / restored=1 / EnsureDirectory=false / ERROR_FILE_NOT_FOUND / outsideChild=1`，父目录验证仍可绕过且失败分支仍无法清理实际创建的根外目录。
- **定位代码**：
  ```cpp
  hOut = CreateFileW(dirPath.c_str(), FILE_READ_ATTRIBUTES | FILE_TRAVERSE | DELETE,
                     FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                     FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
  ```
  ```cpp
  // EnsureDirectory
  bool created = false;
  if (!CreateDirectoryW(it->c_str(), nullptr)) {
      DWORD err = GetLastError();
      if (err != ERROR_ALREADY_EXISTS) {
          outError = err;
          if (hParent != INVALID_HANDLE_VALUE) CloseHandle(hParent);
          return false;
      }
  } else {
      created = true;
  }

  HANDLE hChild = CreateFileW(it->c_str(), FILE_READ_ATTRIBUTES | FILE_TRAVERSE | DELETE,
                              FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                              FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
  if (hChild == INVALID_HANDLE_VALUE) {
      outError = GetLastError();
      if (created) RemoveDirectoryW(it->c_str()); // 打开失败（罕见），按路径清理刚创建的空目录
      if (hParent != INVALID_HANDLE_VALUE) CloseHandle(hParent);
      return false;
  }
  ```

### Issue 3: [连续未解决] [P1] `src/UnzipWorker/ArchiveNames.h:181` (`ReadTarNames`)

- **问题与风险**：R6 Issue 3 的全局字符集继承仅处理非空声明，新增状态无法通过后续 `g` 头中的空值 `hdrcharset=` 清除，违反[空值删除先前全局字段的规则](https://docs.oracle.com/cd/E88353_01/html/E37839/pax-1.html)，用本次提交重新编译的 Worker 处理“全局 `BINARY` → 首个文件 → 全局空字符集 → 局部 UTF-8 `é.txt`”的 TAR 并指定 `--cp 936`，实测第二个文件被错误落盘为 `茅.txt` 且 `status=ok / exit=0`，仍会静默破坏已恢复默认 UTF-8 的文件名。
- **定位代码**：
  ```cpp
  if (type == 'g') { // 全局 pax 头：解析字符集声明并持续生效（hdrcharset=BINARY 必须影响后续文件）
      std::string data = ReadBlocks(f, size);
      std::string declared = ExtractPaxHdrCharset(data);
      if (!declared.empty()) globalHdrCharset = declared;
      continue;
  }
  ```
  ```cpp
  static std::string ExtractPaxHdrCharset(const std::string& data) {
      std::string path;
      std::string hdrcharset;
      bool sawPath = false;
      ScanPaxRecords(data, path, sawPath, hdrcharset);
      return hdrcharset;
  }
  ```

### Issue 4: [新引入] [P1] `src/UnzipProxy/Core/DeliveryManifest.cs:370` (`DeliveryVerifier.WalkVerifiedTree`)

- **问题与风险**：新增遍历在回调抛异常时跳过当前 `entryHandle.Dispose()`，外层 `finally` 又只释放当前目录而不释放栈内待遍历目录，使用当前代码副本在 `FileInfo.Length` 前删除已打开文件即可触发真实 `FileNotFoundException`，100 个待遍历目录的样例在异常返回后多出 101 个句柄、强制 GC 后才恢复，失败任务会遗留随目录规模增长的原生句柄并使后续任务面临资源耗尽风险。
- **定位代码**：
  ```csharp
  if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
  {
      onDirectory(rel);
      // 保留句柄交由栈处理，子目录在其句柄仍打开（不可被替换）的情况下继续遍历。
      stack.Push((full, entryHandle, true));
  }
  else
  {
      onFile(full, rel);
      entryHandle.Dispose();
  }
  ```
  ```csharp
  finally
  {
      if (ownHandle) dirHandle.Dispose();
  }
  ```
