#pragma once

#include "SevenZip.h"
#include "CryptoUtils.h"
#include "PathSanitizer.h"
#include <windows.h>
#include <atomic>
#include <memory>
#include <functional>
#include <string>

// 统一输出预算：外层与内层提取共享同一份“实际写出字节”上限，
// 用于兜底压缩炸弹与声明大小失真的样本。
class OutputBudget {
public:
    explicit OutputBudget(uint64_t limit) : m_limit(limit), m_used(0), m_exceeded(false) {}

    bool Consume(uint64_t n) {
        uint64_t used = m_used.fetch_add(n) + n;
        if (used > m_limit) {
            m_exceeded = true;
            return false;
        }
        return true;
    }

    bool Exceeded() const { return m_exceeded; }
    uint64_t Used() const { return m_used.load(); }
    uint64_t Limit() const { return m_limit; }

private:
    uint64_t m_limit;
    std::atomic<uint64_t> m_used;
    std::atomic<bool> m_exceeded;
};

// 输入流包装
class CInFileStream : public IInStream, public IStreamGetSize {
public:
    CInFileStream(HANDLE hFile) : m_refCount(1), m_hFile(hFile) {}
    virtual ~CInFileStream() {
        if (m_hFile != INVALID_HANDLE_VALUE && m_hFile != NULL) {
            CloseHandle(m_hFile);
            m_hFile = INVALID_HANDLE_VALUE;
        }
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void **ppvObject) override {
        if (!ppvObject) return E_POINTER;
        if (iid == IID_IUnknown) {
            *ppvObject = static_cast<IUnknown*>(static_cast<IInStream*>(this));
        } else if (iid == IID_ISequentialInStream) {
            *ppvObject = static_cast<ISequentialInStream*>(this);
        } else if (iid == IID_IInStream) {
            *ppvObject = static_cast<IInStream*>(this);
        } else if (iid == IID_IStreamGetSize) {
            *ppvObject = static_cast<IStreamGetSize*>(this);
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

    HRESULT STDMETHODCALLTYPE Read(void *data, uint32_t size, uint32_t *processedSize) override {
        if (processedSize) *processedSize = 0;
        if (m_hFile == INVALID_HANDLE_VALUE) return E_FAIL;
        DWORD bytesRead = 0;
        BOOL ok = ReadFile(m_hFile, data, size, &bytesRead, nullptr);
        if (processedSize) *processedSize = bytesRead;
        return ok ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }

    HRESULT STDMETHODCALLTYPE Seek(int64_t offset, uint32_t seekOrigin, uint64_t *newPosition) override {
        if (m_hFile == INVALID_HANDLE_VALUE) return E_FAIL;
        LARGE_INTEGER liDistanceToMove;
        liDistanceToMove.QuadPart = offset;
        LARGE_INTEGER liNewFilePointer;
        DWORD method = FILE_BEGIN;
        if (seekOrigin == 1) method = FILE_CURRENT;
        else if (seekOrigin == 2) method = FILE_END;

        BOOL ok = SetFilePointerEx(m_hFile, liDistanceToMove, &liNewFilePointer, method);
        if (newPosition) *newPosition = liNewFilePointer.QuadPart;
        return ok ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }

    HRESULT STDMETHODCALLTYPE GetSize(uint64_t *size) override {
        if (!size) return E_POINTER;
        if (m_hFile == INVALID_HANDLE_VALUE) return E_FAIL;
        LARGE_INTEGER fileSize;
        if (GetFileSizeEx(m_hFile, &fileSize)) {
            *size = fileSize.QuadPart;
            return S_OK;
        }
        return HRESULT_FROM_WIN32(GetLastError());
    }

    static CInFileStream* OpenFile(const wchar_t* path) {
        HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return nullptr;
        return new CInFileStream(h);
    }

private:
    LONG m_refCount;
    HANDLE m_hFile;
};

// 输出流包装（流式轻量计算，支持取消、暂停与统一预算，杜绝重解析点写入）
class COutFileStream : public ISequentialOutStream {
public:
    using PauseCheckCallback = std::function<bool()>;

    COutFileStream(HANDLE hFile,
                   std::function<void(uint64_t)> onBytesWritten,
                   PauseCheckCallback checkPauseOrCancel,
                   OutputBudget* budget)
        : m_refCount(1),
          m_hFile(hFile),
          m_totalWritten(0),
          m_onBytesWritten(onBytesWritten),
          m_checkPauseOrCancel(checkPauseOrCancel),
          m_budget(budget) {}

    virtual ~COutFileStream() {
        if (m_hFile != INVALID_HANDLE_VALUE && m_hFile != NULL) {
            CloseHandle(m_hFile);
            m_hFile = INVALID_HANDLE_VALUE;
        }
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void **ppvObject) override {
        if (!ppvObject) return E_POINTER;
        if (iid == IID_IUnknown || iid == IID_ISequentialOutStream) {
            *ppvObject = static_cast<ISequentialOutStream*>(this);
            AddRef();
            return S_OK;
        }
        *ppvObject = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override {
        return InterlockedIncrement(&m_refCount);
    }

    ULONG STDMETHODCALLTYPE Release() override {
        ULONG res = InterlockedDecrement(&m_refCount);
        if (res == 0) delete this;
        return res;
    }

    HRESULT STDMETHODCALLTYPE Write(const void *data, uint32_t size, uint32_t *processedSize) override {
        if (processedSize) *processedSize = 0;
        if (m_checkPauseOrCancel && !m_checkPauseOrCancel()) {
            return E_ABORT;
        }
        if (m_hFile == INVALID_HANDLE_VALUE) return E_FAIL;
        if (m_budget && !m_budget->Consume(size)) {
            return E_ABORT; // 超出统一输出预算，终止任务
        }

        DWORD bytesWritten = 0;
        BOOL ok = WriteFile(m_hFile, data, size, &bytesWritten, nullptr);
        if (!ok) return HRESULT_FROM_WIN32(GetLastError());

        if (bytesWritten > 0) {
            m_crc.Update(data, bytesWritten);
            m_totalWritten += bytesWritten;
            if (m_onBytesWritten) {
                m_onBytesWritten(bytesWritten);
            }
        }

        if (processedSize) *processedSize = bytesWritten;
        return S_OK;
    }

    uint32_t GetCRC() const { return m_crc.GetDigest(); }
    uint64_t GetTotalWritten() const { return m_totalWritten; }

    // 生产级安全创建：
    // 1. 用 CREATE_NEW 独占新建，同名冲突自动 "(2)" 递增，绝不静默截断；
    // 2. 创建后用文件句柄取回真实最终路径，验证其仍位于受控根目录之内，
    //    从而把落盘对象绑定到本次任务确认过的目录（拦截祖先联接点/符号链接逃逸）。
    static COutFileStream* CreateOutputFile(const wchar_t* path,
                                            const std::wstring& canonicalRoot,
                                            std::function<void(uint64_t)> onBytesWritten,
                                            PauseCheckCallback checkPauseOrCancel,
                                            OutputBudget* budget,
                                            std::wstring* outActualPath,
                                            DWORD* outError) {
        if (outError) *outError = ERROR_SUCCESS;
        std::wstring curPath = path;
        std::wstring baseName = path;
        std::wstring extName;
        {
            size_t dotPos = curPath.find_last_of(L'.');
            size_t slashPos = curPath.find_last_of(L"\\/");
            bool hasExt = dotPos != std::wstring::npos && (slashPos == std::wstring::npos || dotPos > slashPos);
            if (hasExt) {
                baseName = curPath.substr(0, dotPos);
                extName = curPath.substr(dotPos);
            }
        }

        HANDLE h = INVALID_HANDLE_VALUE;
        DWORD lastError = ERROR_SUCCESS;
        for (int index = 1; index <= 999; ++index) {
            if (index > 1) {
                curPath = baseName + L" (" + std::to_wstring(index) + L")" + extName;
            }
            h = CreateFileW(curPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h != INVALID_HANDLE_VALUE) break;

            lastError = GetLastError();
            if (lastError != ERROR_FILE_EXISTS && lastError != ERROR_ALREADY_EXISTS) break;
        }

        if (h == INVALID_HANDLE_VALUE) {
            if (outError) *outError = lastError;
            return nullptr;
        }

        // 句柄级归属验证：真实最终路径必须在受控根目录之内
        if (!PathSanitizer::VerifyObjectUnderRoot(h, canonicalRoot)) {
            FILE_DISPOSITION_INFO disp{};
            disp.DeleteFile = TRUE;
            SetFileInformationByHandle(h, FileDispositionInfo, &disp, sizeof(disp));
            CloseHandle(h);
            if (outError) *outError = ERROR_ACCESS_DENIED;
            return nullptr;
        }

        if (outActualPath) *outActualPath = curPath;
        return new COutFileStream(h, onBytesWritten, checkPauseOrCancel, budget);
    }

private:
    LONG m_refCount;
    HANDLE m_hFile;
    uint64_t m_totalWritten;
    Crc32Hasher m_crc;
    std::function<void(uint64_t)> m_onBytesWritten;
    PauseCheckCallback m_checkPauseOrCancel;
    OutputBudget* m_budget;
};
