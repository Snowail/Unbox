#pragma once

#include "SevenZip.h"
#include "FileStream.h"
#include "PathSanitizer.h"
#include <windows.h>
#include <string>
#include <vector>
#include <atomic>
#include <mutex>
#include <condition_variable>
#include <iostream>
#include <filesystem>

namespace fs = std::filesystem;

struct ExtractedItemInfo {
    uint32_t index;
    std::wstring relPath;
    std::wstring diskPath;
    bool isDir;
    uint64_t size;
    uint32_t crc;
    bool skipped;
    int opResult;
};

class WorkerExtractCallback : 
    public IArchiveExtractCallback,
    public ICryptoGetTextPassword,
    public ICryptoGetTextPassword2
{
public:
    WorkerExtractCallback(
        IInArchive* archive,
        const std::wstring& stagingDir,
        const std::vector<std::wstring>& itemTargetPaths,
        const std::vector<bool>& itemToExtract,
        std::function<void(const std::wstring&, uint64_t, uint64_t)> onProgress,
        std::function<std::wstring(bool* outCanceled)> onPasswordRequest,
        std::function<bool()> checkPauseOrCancel)
        : m_refCount(1),
          m_archive(archive),
          m_stagingDir(stagingDir),
          m_itemTargetPaths(itemTargetPaths),
          m_itemToExtract(itemToExtract),
          m_onProgress(onProgress),
          m_onPasswordRequest(onPasswordRequest),
          m_checkPauseOrCancel(checkPauseOrCancel),
          m_totalBytes(0),
          m_completedBytes(0),
          m_hasError(false),
          m_hasSkipped(false),
          m_hasCrcError(false),
          m_hasDataError(false),
          m_hasUnsupportedMethod(false),
          m_currentOutStream(nullptr),
          m_currentIndex(0)
    {
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

    // IArchiveExtractCallback
    HRESULT STDMETHODCALLTYPE SetTotal(uint64_t total) override {
        m_totalBytes = total;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE SetCompleted(const uint64_t *completeValue) override {
        if (m_checkPauseOrCancel && !m_checkPauseOrCancel()) {
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

        m_currentIndex = index;

        // 如果该项被过滤（如 macOS 辅助文件）或被跳过
        if (index >= m_itemToExtract.size() || !m_itemToExtract[index] || askExtractMode != NExtract::NAskMode::kExtract) {
            return S_OK;
        }

        if (index >= m_itemTargetPaths.size()) {
            return S_OK;
        }

        const std::wstring& relPath = m_itemTargetPaths[index];

        // 获取该项是否为目录
        PROPVARIANT propIsDir;
        PropVariantInit(&propIsDir);
        bool isDir = false;
        if (SUCCEEDED(m_archive->GetProperty(index, kpidIsDir, &propIsDir))) {
            if (propIsDir.vt == VT_BOOL && propIsDir.boolVal == VARIANT_TRUE) {
                isDir = true;
            }
            PropVariantClear(&propIsDir);
        }

        // 严格安全边界检验：确保解析路径落在 stagingDir 之内，拦截 Zip Slip、重解析点与流注入
        std::wstring safeFullPath;
        if (!PathSanitizer::ResolveAndValidateDestination(m_stagingDir, relPath, safeFullPath)) {
            m_hasSkipped = true;
            return S_OK; // 越界条目安全跳过，绝不写盘！
        }

        if (isDir) {
            std::error_code ec;
            fs::create_directories(safeFullPath, ec);
            if (ec) {
                m_hasError = true;
                return HRESULT_FROM_WIN32(ec.value());
            }
            return S_OK;
        }

        // 确保父目录存在（缓存上一目录状态，避免 1200 个同级文件重复 1200 次系统调用）
        fs::path targetPath(safeFullPath);
        std::wstring parentDir = targetPath.parent_path().wstring();
        if (parentDir != m_lastCreatedParentDir) {
            std::error_code ec;
            fs::create_directories(parentDir, ec);
            if (ec) {
                m_hasError = true;
                return HRESULT_FROM_WIN32(ec.value());
            }
            m_lastCreatedParentDir = parentDir;
        }

        std::wstring actualDiskPath = safeFullPath;

        // 创建输出文件流 (CREATE_NEW 独占新建，同名自动 "(2)" 序号递增，拦截父目录重解析点)
        COutFileStream* stream = COutFileStream::CreateOutputFile(
            safeFullPath.c_str(),
            [this, relPath](uint64_t bytesWritten) {
                m_completedBytes += bytesWritten;
                if (m_onProgress) {
                    m_onProgress(relPath, m_completedBytes, m_totalBytes);
                }
            },
            m_checkPauseOrCancel,
            &actualDiskPath
        );

        if (!stream) {
            m_hasError = true;
            return HRESULT_FROM_WIN32(GetLastError());
        }

        m_currentOutStream = stream;
        m_currentDiskPath = actualDiskPath;
        m_currentOutStream->AddRef();
        *outStream = stream;

        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE PrepareOperation(int32_t askExtractMode) override {
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE SetOperationResult(int32_t resultEOperationResult) override {
        if (m_currentOutStream) {
            // 记录该条目的结果与校验
            ExtractedItemInfo info{};
            info.index = m_currentIndex;
            info.opResult = resultEOperationResult;
            info.crc = m_currentOutStream->GetCRC();
            info.size = m_currentOutStream->GetTotalWritten();
            info.skipped = false;

            if (m_currentIndex < m_itemTargetPaths.size()) {
                info.relPath = m_itemTargetPaths[m_currentIndex];
                info.diskPath = m_currentDiskPath;
            }

            m_extractedItems.push_back(info);

            m_currentOutStream->Release();
            m_currentOutStream = nullptr;
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

    // ICryptoGetTextPassword2
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

    const std::vector<ExtractedItemInfo>& GetExtractedItems() const { return m_extractedItems; }
    bool HasError() const { return m_hasError; }
    bool HasSkipped() const { return m_hasSkipped; }
    bool HasCrcError() const { return m_hasCrcError; }
    bool HasDataError() const { return m_hasDataError; }
    bool HasUnsupportedMethod() const { return m_hasUnsupportedMethod; }
    uint64_t GetTotalBytes() const { return m_totalBytes; }
    uint64_t GetCompletedBytes() const { return m_completedBytes; }

private:
    LONG m_refCount;
    IInArchive* m_archive;
    std::wstring m_stagingDir;
    std::vector<std::wstring> m_itemTargetPaths;
    std::vector<bool> m_itemToExtract;
    std::function<void(const std::wstring&, uint64_t, uint64_t)> m_onProgress;
    std::function<std::wstring(bool* outCanceled)> m_onPasswordRequest;
    std::function<bool()> m_checkPauseOrCancel;

    uint64_t m_totalBytes;
    std::atomic<uint64_t> m_completedBytes;
    bool m_hasError;
    bool m_hasSkipped;
    bool m_hasCrcError;
    bool m_hasDataError;
    bool m_hasUnsupportedMethod;

    COutFileStream* m_currentOutStream;
    std::wstring m_currentDiskPath;
    std::wstring m_lastCreatedParentDir;
    uint32_t m_currentIndex;
    std::vector<ExtractedItemInfo> m_extractedItems;
};
