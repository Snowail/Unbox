#pragma once

#include "SevenZip.h"
#include "FileStream.h"
#include "PathSanitizer.h"
#include <windows.h>
#include <string>
#include <vector>
#include <atomic>
#include <mutex>
#include <functional>
#include <filesystem>

namespace fs = std::filesystem;

// 单条交付记录：路径一律相对“交付根目录”，用于最终逐项对账
struct DeliveredItemInfo {
    uint32_t index = 0;
    std::wstring relPath;   // 归档内逻辑路径（解码后，用于诊断）
    std::wstring relDiskPath; // 实际落盘路径（相对交付根目录，含冲突重命名后的真实名称）
    std::wstring diskPath;  // 实际落盘绝对路径（内部使用）
    bool isDir = false;
    uint64_t size = 0;
    uint32_t crc = 0;
    int opResult = 0;
};

class WorkerExtractCallback :
    public IArchiveExtractCallback,
    public ICryptoGetTextPassword,
    public ICryptoGetTextPassword2
{
public:
    WorkerExtractCallback(
        IInArchive* archive,
        const std::wstring& extractionRoot,      // 本次写盘根目录（可能是隔离交付根）
        const std::wstring& canonicalRoot,       // 句柄级校验用的规范根目录
        const std::vector<std::wstring>& itemTargetPaths,
        const std::vector<bool>& itemToExtract,
        std::function<void(const std::wstring&, uint64_t, uint64_t)> onProgress,
        std::function<std::wstring(bool* outCanceled)> onPasswordRequest,
        std::function<bool()> checkPauseOrCancel,
        OutputBudget* budget)
        : m_refCount(1),
          m_archive(archive),
          m_extractionRoot(extractionRoot),
          m_canonicalRoot(canonicalRoot),
          m_itemTargetPaths(itemTargetPaths),
          m_itemToExtract(itemToExtract),
          m_onProgress(onProgress),
          m_onPasswordRequest(onPasswordRequest),
          m_checkPauseOrCancel(checkPauseOrCancel),
          m_budget(budget),
          m_totalBytes(0),
          m_completedBytes(0),
          m_unsafeCount(0),
          m_hasError(false),
          m_hasCrcError(false),
          m_hasDataError(false),
          m_hasUnsupportedMethod(false),
          m_currentOutStream(nullptr),
          m_currentIndex(0),
          m_currentIsDir(false)
    {
        m_disposition.assign(itemToExtract.size(), ItemDisposition::Unseen);
    }

    virtual ~WorkerExtractCallback() {
        if (m_currentOutStream) {
            m_currentOutStream->Release();
            m_currentOutStream = nullptr;
        }
    }

    // IUnknown
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void **ppvObject) override {
        if (!ppvObject) return E_POINTER;
        if (iid == IID_IUnknown) {
            *ppvObject = static_cast<IUnknown*>(static_cast<IArchiveExtractCallback*>(this));
        } else if (iid == IID_IArchiveExtractCallback) {
            *ppvObject = static_cast<IArchiveExtractCallback*>(this);
        } else if (iid == IID_ICryptoGetTextPassword) {
            *ppvObject = static_cast<ICryptoGetTextPassword*>(this);
        } else if (iid == IID_ICryptoGetTextPassword2) {
            *ppvObject = static_cast<ICryptoGetTextPassword2*>(this);
        } else {
            *ppvObject = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }

    ULONG STDMETHODCALLTYPE AddRef() override {
        return InterlockedIncrement(&m_refCount);
    }

    ULONG STDMETHODCALLTYPE Release() override {
        ULONG res = InterlockedDecrement(&m_refCount);
        if (res == 0) delete this;
        return res;
    }

    // 用本次计划内条目的未压缩总字节作为进度分母（只使用一种口径）
    void SetDeclaredTotal(uint64_t total) {
        m_totalBytes = total;
    }

    // IArchiveExtractCallback
    HRESULT STDMETHODCALLTYPE SetTotal(uint64_t total) override {
        // 引擎可能上报压缩字节或解压字节，这里不接受，统一使用计划内声明总字节
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE SetCompleted(const uint64_t *completeValue) override {
        if (m_checkPauseOrCancel && !m_checkPauseOrCancel()) {
            return E_ABORT;
        }
        if (m_budget && m_budget->Exceeded()) {
            return E_ABORT;
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetStream(uint32_t index, ISequentialOutStream **outStream, int32_t askExtractMode) override {
        if (!outStream) return E_POINTER;
        *outStream = nullptr;

        if (m_checkPauseOrCancel && !m_checkPauseOrCancel()) {
            return E_ABORT;
        }
        if (m_budget && m_budget->Exceeded()) {
            return E_ABORT;
        }

        m_currentIndex = index;
        m_currentIsDir = false;
        m_currentRelDiskPath.clear();
        m_currentDiskPath.clear();

        // 被策略过滤（macOS 辅助文件）或用户主动跳过的条目：不写盘，也不计为失败
        if (index >= m_itemToExtract.size() || !m_itemToExtract[index] || askExtractMode != NExtract::NAskMode::kExtract) {
            if (index < m_disposition.size()) m_disposition[index] = ItemDisposition::SkippedOrFiltered;
            return S_OK;
        }
        if (index >= m_itemTargetPaths.size()) {
            if (index < m_disposition.size()) m_disposition[index] = ItemDisposition::SkippedOrFiltered;
            return S_OK;
        }

        const std::wstring& relPath = m_itemTargetPaths[index];

        PROPVARIANT propIsDir;
        PropVariantInit(&propIsDir);
        bool isDir = false;
        if (SUCCEEDED(m_archive->GetProperty(index, kpidIsDir, &propIsDir))) {
            if (propIsDir.vt == VT_BOOL && propIsDir.boolVal == VARIANT_TRUE) {
                isDir = true;
            }
            PropVariantClear(&propIsDir);
        }

        // 字符串级边界校验（第一道闸门）
        std::wstring safeFullPath;
        if (!PathSanitizer::ResolveAndValidateDestination(m_extractionRoot, relPath, safeFullPath)) {
            // 越界/注入路径：安全跳过并单独计数（不得伪装成“全部已恢复”）
            m_unsafeCount++;
            if (index < m_disposition.size()) m_disposition[index] = ItemDisposition::UnsafeRejected;
            return S_OK;
        }

        if (isDir) {
            DWORD err = ERROR_SUCCESS;
            if (!PathSanitizer::EnsureDirectory(m_canonicalRoot, safeFullPath, err)) {
                m_hasError = true;
                m_unsafeCount++;
                if (index < m_disposition.size()) m_disposition[index] = ItemDisposition::UnsafeRejected;
                return HRESULT_FROM_WIN32(err != ERROR_SUCCESS ? err : ERROR_ACCESS_DENIED);
            }
            m_currentIsDir = true;
            m_currentRelPath = relPath;
            m_currentDiskPath = safeFullPath;
            return S_OK;
        }

        // 确保父目录存在（同一父目录只做一次句柄级验证）
        fs::path targetPath(safeFullPath);
        std::wstring parentDir = targetPath.parent_path().wstring();
        if (parentDir != m_lastCreatedParentDir) {
            DWORD err = ERROR_SUCCESS;
            if (!PathSanitizer::EnsureDirectory(m_canonicalRoot, parentDir, err)) {
                m_hasError = true;
                m_unsafeCount++;
                if (index < m_disposition.size()) m_disposition[index] = ItemDisposition::UnsafeRejected;
                return HRESULT_FROM_WIN32(err != ERROR_SUCCESS ? err : ERROR_ACCESS_DENIED);
            }
            m_lastCreatedParentDir = parentDir;
        }

        std::wstring actualDiskPath;
        DWORD createError = ERROR_SUCCESS;
        COutFileStream* stream = COutFileStream::CreateOutputFile(
            safeFullPath.c_str(),
            m_canonicalRoot,
            [this, relPath](uint64_t bytesWritten) {
                m_completedBytes += bytesWritten;
                if (m_onProgress) {
                    m_onProgress(relPath, m_completedBytes, m_totalBytes);
                }
            },
            m_checkPauseOrCancel,
            m_budget,
            &actualDiskPath,
            &createError
        );

        if (!stream) {
            m_hasError = true;
            if (index < m_disposition.size()) m_disposition[index] = ItemDisposition::Failed;
            return HRESULT_FROM_WIN32(createError != ERROR_SUCCESS ? createError : ERROR_ACCESS_DENIED);
        }

        m_currentOutStream = stream;
        m_currentDiskPath = actualDiskPath;
        m_currentRelPath = relPath;
        m_currentOutStream->AddRef();
        *outStream = stream;

        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE PrepareOperation(int32_t askExtractMode) override {
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE SetOperationResult(int32_t resultEOperationResult) override {
        bool hasRecord = !m_currentDiskPath.empty();

        if (m_currentOutStream) {
            DeliveredItemInfo info;
            info.index = m_currentIndex;
            info.relPath = m_currentRelPath;
            info.diskPath = m_currentDiskPath;
            info.relDiskPath = MakeRelativeToRoot(m_currentDiskPath, m_extractionRoot);
            info.isDir = false;
            info.size = m_currentOutStream->GetTotalWritten();
            info.crc = m_currentOutStream->GetCRC();
            info.opResult = resultEOperationResult;
            m_deliveredItems.push_back(info);

            m_currentOutStream->Release();
            m_currentOutStream = nullptr;
        } else if (hasRecord && m_currentIsDir) {
            DeliveredItemInfo info;
            info.index = m_currentIndex;
            info.relPath = m_currentRelPath;
            info.diskPath = m_currentDiskPath;
            info.relDiskPath = MakeRelativeToRoot(m_currentDiskPath, m_extractionRoot);
            info.isDir = true;
            info.size = 0;
            info.crc = 0;
            info.opResult = resultEOperationResult;
            m_deliveredItems.push_back(info);
        }

        m_currentDiskPath.clear();
        m_currentIsDir = false;

        // 仅当本条目确实被处理（已写出文件或已建目录）时才记录终态；
        // 跳过/过滤/安全拒绝的条目其终态已在 GetStream 中标记，不得被覆盖。
        if (hasRecord && m_currentIndex < m_disposition.size()) {
            m_disposition[m_currentIndex] = (resultEOperationResult == NExtract::NOperationResult::kOK)
                ? ItemDisposition::Delivered
                : ItemDisposition::Failed;
        }

        if (resultEOperationResult != NExtract::NOperationResult::kOK) {
            m_hasError = true;
            if (resultEOperationResult == NExtract::NOperationResult::kCRCError) {
                m_hasCrcError = true;
            } else if (resultEOperationResult == NExtract::NOperationResult::kDataError) {
                m_hasDataError = true;
            } else if (resultEOperationResult == NExtract::NOperationResult::kUnsupportedMethod) {
                m_hasUnsupportedMethod = true;
            }
        }

        return S_OK;
    }

    // ICryptoGetTextPassword
    HRESULT STDMETHODCALLTYPE CryptoGetTextPassword(BSTR *password) override {
        if (!password) return E_POINTER;
        *password = nullptr;
        if (m_onPasswordRequest) {
            bool canceled = false;
            std::wstring pwd = m_onPasswordRequest(&canceled);
            if (canceled) return E_ABORT;
            *password = SysAllocString(pwd.c_str());
            return S_OK;
        }
        return E_ABORT;
    }

    HRESULT STDMETHODCALLTYPE CryptoGetTextPassword2(int32_t *passwordIsDefined, BSTR *password) override {
        if (!passwordIsDefined || !password) return E_POINTER;
        *password = nullptr;
        if (m_onPasswordRequest) {
            bool canceled = false;
            std::wstring pwd = m_onPasswordRequest(&canceled);
            if (canceled) {
                *passwordIsDefined = 0;
                return E_ABORT;
            }
            *password = SysAllocString(pwd.c_str());
            *passwordIsDefined = 1;
            return S_OK;
        }
        *passwordIsDefined = 0;
        return S_OK;
    }

    const std::vector<DeliveredItemInfo>& GetDeliveredItems() const { return m_deliveredItems; }
    bool HasError() const { return m_hasError; }
    bool HasCrcError() const { return m_hasCrcError; }
    bool HasDataError() const { return m_hasDataError; }
    bool HasUnsupportedMethod() const { return m_hasUnsupportedMethod; }
    uint32_t GetUnsafeCount() const { return m_unsafeCount; }
    uint64_t GetTotalBytes() const { return m_totalBytes; }
    uint64_t GetCompletedBytes() const { return m_completedBytes; }

    // 计划内“应交付但既未落盘、也未计入 unsafe/failed”的条目数量。
    // 少一项也必须成为 partial/failed 并禁止回收——清单与磁盘一致不足以证明全部计划条目已交付。
    uint32_t CountMissing() const {
        uint32_t missing = 0;
        for (size_t i = 0; i < m_disposition.size(); ++i) {
            if (i < m_itemToExtract.size() && m_itemToExtract[i]) {
                ItemDisposition d = m_disposition[i];
                if (d != ItemDisposition::Delivered &&
                    d != ItemDisposition::UnsafeRejected &&
                    d != ItemDisposition::Failed) {
                    ++missing;
                }
            }
        }
        return missing;
    }

private:
    static std::wstring MakeRelativeToRoot(const std::wstring& fullPath, const std::wstring& root) {
        std::wstring normalRoot = root;
        if (!normalRoot.empty() && normalRoot.back() != L'\\') normalRoot.push_back(L'\\');
        if (fullPath.size() > normalRoot.size() &&
            _wcsnicmp(fullPath.c_str(), normalRoot.c_str(), normalRoot.size()) == 0) {
            return fullPath.substr(normalRoot.size());
        }
        return fullPath;
    }

    LONG m_refCount;
    IInArchive* m_archive;

    enum class ItemDisposition : uint8_t {
        Unseen = 0,         // 引擎从未请求该条目的落盘
        Delivered,          // 已写出（文件）或已建目录
        UnsafeRejected,     // 路径越界/联接点逃逸等安全拒绝
        SkippedOrFiltered,  // 策略过滤或用户跳过
        Failed              // 提取失败（CRC/数据/写盘等）
    };
    std::vector<ItemDisposition> m_disposition;
    std::wstring m_extractionRoot;
    std::wstring m_canonicalRoot;
    std::vector<std::wstring> m_itemTargetPaths;
    std::vector<bool> m_itemToExtract;
    std::function<void(const std::wstring&, uint64_t, uint64_t)> m_onProgress;
    std::function<std::wstring(bool* outCanceled)> m_onPasswordRequest;
    std::function<bool()> m_checkPauseOrCancel;
    OutputBudget* m_budget;

    uint64_t m_totalBytes;
    std::atomic<uint64_t> m_completedBytes;
    std::atomic<uint32_t> m_unsafeCount;
    bool m_hasError;
    bool m_hasCrcError;
    bool m_hasDataError;
    bool m_hasUnsupportedMethod;

    COutFileStream* m_currentOutStream;
    std::wstring m_currentRelPath;
    std::wstring m_currentRelDiskPath;
    std::wstring m_currentDiskPath;
    bool m_currentIsDir;
    std::wstring m_lastCreatedParentDir;
    uint32_t m_currentIndex;
    std::vector<DeliveredItemInfo> m_deliveredItems;
};
