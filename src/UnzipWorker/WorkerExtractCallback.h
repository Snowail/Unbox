#pragma once

#include "SevenZip.h"
#include "FileStream.h"
#include "PathSanitizer.h"
#include <windows.h>
#include <string>
#include <vector>
#include <unordered_map>
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
    std::string sha256;
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
        const std::unordered_map<uint32_t, std::wstring>& itemTargetPaths,
        const std::vector<bool>& itemToExtract,
        std::function<void(const std::wstring&, uint64_t, uint64_t)> onProgress,
        std::function<std::wstring()> onPasswordRequest,
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
        if (completeValue) {
            m_completedBytes = *completeValue;
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
            return S_OK; // 返回空流即可跳过该项提取
        }

        auto it = m_itemTargetPaths.find(index);
        if (it == m_itemTargetPaths.end()) {
            return S_OK;
        }

        std::wstring relPath = it->second;

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

        fs::path fullPath = fs::path(m_stagingDir) / relPath;

        if (isDir) {
            std::error_code ec;
            fs::create_directories(fullPath, ec);
            return S_OK;
        }

        // 确保父目录存在
        std::error_code ec;
        fs::create_directories(fullPath.parent_path(), ec);

        // 创建输出文件流
        COutFileStream* stream = COutFileStream::CreateOutputFile(
            fullPath.c_str(),
            [this, relPath](uint64_t bytesWritten) {
                m_completedBytes += bytesWritten;
                if (m_onProgress) {
                    m_onProgress(relPath, m_completedBytes, m_totalBytes);
                }
            },
            m_checkPauseOrCancel
        );

        if (!stream) {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        m_currentOutStream = stream;
        m_currentOutStream->AddRef();
        *outStream = stream;

        if (m_onProgress) {
            m_onProgress(relPath, m_completedBytes, m_totalBytes);
        }

        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE PrepareOperation(int32_t askExtractMode) override {
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE SetOperationResult(int32_t resultEOperationResult) override {
        if (m_currentOutStream) {
            // 记录该条目的结果与哈希
            ExtractedItemInfo info{};
            info.index = m_currentIndex;
            info.opResult = resultEOperationResult;
            info.crc = m_currentOutStream->GetCRC();
            info.sha256 = m_currentOutStream->GetSHA256();
            info.size = m_currentOutStream->GetTotalWritten();
            info.skipped = false;

            auto it = m_itemTargetPaths.find(m_currentIndex);
            if (it != m_itemTargetPaths.end()) {
                info.relPath = it->second;
                info.diskPath = (fs::path(m_stagingDir) / it->second).wstring();
            }

            m_extractedItems.push_back(info);

            m_currentOutStream->Release();
            m_currentOutStream = nullptr;
        }

        if (resultEOperationResult != NExtract::NOperationResult::kOK) {
            m_hasError = true;
        }

        return S_OK;
    }

    // ICryptoGetTextPassword
    HRESULT STDMETHODCALLTYPE CryptoGetTextPassword(BSTR *password) override {
        if (!password) return E_POINTER;
        if (m_onPasswordRequest) {
            std::wstring pwd = m_onPasswordRequest();
            *password = SysAllocString(pwd.c_str());
            return S_OK;
        }
        return E_ABORT;
    }

    // ICryptoGetTextPassword2
    HRESULT STDMETHODCALLTYPE CryptoGetTextPassword2(int32_t *passwordIsDefined, BSTR *password) override {
        if (!passwordIsDefined || !password) return E_POINTER;
        if (m_onPasswordRequest) {
            std::wstring pwd = m_onPasswordRequest();
            *password = SysAllocString(pwd.c_str());
            *passwordIsDefined = 1;
            return S_OK;
        }
        *passwordIsDefined = 0;
        return S_OK;
    }

    const std::vector<ExtractedItemInfo>& GetExtractedItems() const { return m_extractedItems; }
    bool HasError() const { return m_hasError; }
    uint64_t GetTotalBytes() const { return m_totalBytes; }
    uint64_t GetCompletedBytes() const { return m_completedBytes; }

private:
    LONG m_refCount;
    IInArchive* m_archive;
    std::wstring m_stagingDir;
    std::unordered_map<uint32_t, std::wstring> m_itemTargetPaths;
    std::vector<bool> m_itemToExtract;
    std::function<void(const std::wstring&, uint64_t, uint64_t)> m_onProgress;
    std::function<std::wstring()> m_onPasswordRequest;
    std::function<bool()> m_checkPauseOrCancel;

    uint64_t m_totalBytes;
    std::atomic<uint64_t> m_completedBytes;
    bool m_hasError;
    bool m_hasSkipped;

    COutFileStream* m_currentOutStream;
    uint32_t m_currentIndex;
    std::vector<ExtractedItemInfo> m_extractedItems;
};
