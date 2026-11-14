## 待修复清单 (Round 8)

### Issue 1: [新引入] [P1] `src/UnzipProxy/Core/DeliveryManifest.cs:223` (`DeliveryVerifier.Verify`)

- **问题与风险**：摘要校验从使用 `FileShare.Read` 的 `ComputeFile` 改为直接读取允许 `FILE_SHARE_WRITE` 的遍历句柄，导致其他线程可在计算 CRC 期间改写已读取的区段，使校验接受并不存在于实际文件中的混合快照，错误通过源包回收前的完整性门槛。
- **定位代码**：
  ```csharp
  // DeliveryVerifier.OpenPinnedObject：该句柄现在也用于读取文件摘要。
  SafeFileHandle handle = CreateFileW(path, GENERIC_READ,
      FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
      FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
  ```
  ```csharp
  // DeliveryVerifier.Verify
  actualCrc = Crc32.ComputeHandle(handle);
  ```
  ```csharp
  // Crc32.ComputeHandle
  while ((read = RandomAccess.Read(handle, buffer, offset)) > 0)
  {
      crc = Compute(buffer.AsSpan(0, read), crc);
      offset += read;
  }
  return crc;
  ```
- **复现证据**：在当前代码的独立测试副本中，仅在首次读取 64 KiB 后插入调度钩子，由另一线程用 `FileAccess.Write / FileShare.ReadWrite` 原位改写文件；清单期望两个区段为 `A+B`，初始文件为 `A+X`，首次读取后改成 `X+B`，实测 `Ok=True / DigestsChecked=1 / writeSucceeded=True / actualMatchesExpected=False`，改写前后两种完整文件的 CRC 均不匹配清单；对原有 `ComputeFile` 使用相同调度钩子，实测 `writeBlocked=True / digestMatchesExpected=False`。
