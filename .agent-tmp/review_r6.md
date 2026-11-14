## 待修复清单 (Round 6)

### Issue 1: [连续未解决] [P1] `src/UnzipProxy/Core/DeliveryManifest.cs:124` (`DeliveryVerifier.Verify`)

- **问题与风险**：R5 Issue 1 仅修复了检查前已存在的联接点，新增属性检查没有锁定目录对象，检查后仍按字符串路径枚举、校验及写入来源标记；在测试副本中仅于根目录 `File.GetAttributes` 返回后插入替换调度钩子，即可复现最终根目录为联接点但 `Verify.Ok=true / DigestsChecked=1`，同一时序下 `ZoneIdentifier.Propagate` 返回 `marked=1` 并向根外文件写入 `Zone.Identifier`，交付边界仍可被竞争绕过。
- **定位代码**：
  ```csharp
  if (PathContainsReparsePoint(deliveryRootAbsolute))
  {
      report.Problems.Add("交付根目录或其祖先包含联接点/符号链接");
      return report;
  }
  ```
  ```csharp
  // ExtractionCompleter.cs，ZoneIdentifier.Propagate
  if (DeliveryVerifier.PathContainsReparsePoint(targetRoot))
  {
      return 0;
  }

  // 遍历最终树时拒绝 reparse point，绝不通过联接点/符号链接把来源标记写到暂存区之外。
  var files = new List<string>();
  EnumerateFilesNoReparse(targetRoot, files);
  foreach (string file in files)
  {
      try
      {
          File.WriteAllBytes(file + StreamSuffix, zoneData);
          marked++;
      }
      catch
      {
          // 单个文件的非致命属性写入失败不阻断交付
      }
  }
  ```

### Issue 2: [连续未解决] [P1] `src/UnzipWorker/PathSanitizer.h:232` (`EnsureDirectory`)

- **问题与风险**：R5 Issue 2 的父目录句柄会解引用联接点，禁止 `FILE_SHARE_DELETE` 只锁住目标对象而未锁住路径中的联接点，后续创建和打开仍各自重新解析完整路径；对当前头文件的 `CreateDirectoryW` 调用插入确定性调度钩子，将原本指向根内的父联接点在创建前换向根外、创建后换回，实测两次替换均成功且 `EnsureDirectory=false / ERROR_FILE_NOT_FOUND`、根外 `child` 仍存在，异常分支按原路径执行 `RemoveDirectoryW` 无法清理实际创建的根外目录。
- **定位代码**：
  ```cpp
  // OpenVerifiedDirectoryNoDelete
  hOut = CreateFileW(dirPath.c_str(), FILE_READ_ATTRIBUTES | FILE_TRAVERSE | DELETE,
                     FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                     FILE_FLAG_BACKUP_SEMANTICS, nullptr);
  ```
  ```cpp
  for (auto it = missing.rbegin(); it != missing.rend(); ++it) {
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
                                  FILE_FLAG_BACKUP_SEMANTICS, nullptr);
      if (hChild == INVALID_HANDLE_VALUE) {
          outError = GetLastError();
          if (created) RemoveDirectoryW(it->c_str()); // 打开失败（罕见），按路径清理刚创建的空目录
          if (hParent != INVALID_HANDLE_VALUE) CloseHandle(hParent);
          return false;
      }
  ```

### Issue 3: [连续未解决] [P1] `src/UnzipWorker/ArchiveNames.h:341` (`ParsePaxRecords`)

- **问题与风险**：R5 Issue 4 仅修复同一个局部扩展头内的 `hdrcharset=BINARY`，调用方仍丢弃 `g` 全局头且解析器每次重新建立空字符集状态，违反[全局扩展头对后续文件生效的规则](https://docs.oracle.com/cd/E88353_01/html/E37839/pax-1.html)；用本次提交源码重新编译的 Worker 处理“全局头声明 `BINARY`、局部头只含 GBK `茅.txt` 路径字节 `C3 A9 2E 74 78 74`”的 TAR，即使指定 `--cp 936` 仍落盘为 `é.txt`，无编码选择请求且 `status=ok / exit=0`。
- **定位代码**：
  ```cpp
  // ReadTarNames
  if (type == 'g') { // 全局 pax 头，忽略内容
      ReadBlocks(f, size);
      continue;
  }
  ```
  ```cpp
  if (sawPath) {
      outPath = path;
      // 仅当 hdrcharset=BINARY 时 path 才是二进制原始字节；缺省/UTF-8 时 path 恒为 UTF-8。
      outPathIsUtf8 = (hdrcharset != "BINARY");
  }
  ```
