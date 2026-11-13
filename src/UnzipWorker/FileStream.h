#pragma once

#include "SevenZip.h"
#include "CryptoUtils.h"
#include <windows.h>
#include <atomic>
#include <memory>
#include <functional>
#include <string>

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

    // IUnknown
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

    // ISequentialInStream
    HRESULT STDMETHODCALLTYPE Read(void *data, uint32_t size, uint32_t *processedSize) override {
        if (processedSize) *processedSize = 0;
        if (m_hFile == INVALID_HANDLE_VALUE) return E_FAIL;
        DWORD bytesRead = 0;
        BOOL ok = ReadFile(m_hFile, data, size, &bytesRead, nullptr);
        if (processedSize) *processedSize = bytesRead;
        return ok ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }

    // IInStream
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

    // IStreamGetSize
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

// 输出流包装（流式轻量计算，支持取消和暂停，杜绝重解析点写入）
class COutFileStream : public ISequentialOutStream {
public:
    using PauseCheckCallback = std::function<bool()>;

    COutFileStream(HANDLE hFile, 
                   std::function<void(uint64_t)> onBytesWritten,
                   PauseCheckCallback checkPauseOrCancel)
        : m_refCount(1),
          m_hFile(hFile),
          m_totalWritten(0),
          m_onBytesWritten(onBytesWritten),
          m_checkPauseOrCancel(checkPauseOrCancel) {}

    virtual ~COutFileStream() {
        if (m_hFile != INVALID_HANDLE_VALUE && m_hFile != NULL) {
            CloseHandle(m_hFile);
            m_hFile = INVALID_HANDLE_VALUE;
        }
    }

    // IUnknown
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

    // ISequentialOutStream
    HRESULT STDMETHODCALLTYPE Write(const void *data, uint32_t size, uint32_t *processedSize) override {
        if (processedSize) *processedSize = 0;
        if (m_checkPauseOrCancel && !m_checkPauseOrCancel()) {
            return E_ABORT; // 用户取消任务
        }
        if (m_hFile == INVALID_HANDLE_VALUE) return E_FAIL;

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
    // 1. 检查父目录是否是符号链接/重解析点（拦截提权劫持）
    // 2. 默认使用 CREATE_NEW 避免静默截断同名文件；若发生同名冲突自动加 "(2)" 序号
    static COutFileStream* CreateOutputFile(const wchar_t* path, 
                                            std::function<void(uint64_t)> onBytesWritten,
                                            PauseCheckCallback checkPauseOrCancel,
                                            std::wstring* outActualPath = nullptr) {
        std::wstring p(path);
        static thread_local std::wstring s_lastCheckedReparseDir = L"";
        size_t lastSlash = p.find_last_of(L"\\/");
        if (lastSlash != std::wstring::npos) {
            std::wstring parentDir = p.substr(0, lastSlash);
            if (parentDir != s_lastCheckedReparseDir) {
                DWORD attr = GetFileAttributesW(parentDir.c_str());
                if (attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_REPARSE_POINT)) {
                    // 父级包含符号链接或重解析点，安全拦截！
                    return nullptr;
                }
                s_lastCheckedReparseDir = parentDir;
            }
        }

        // 尝试独占新建 CREATE_NEW，若存在则递增序号避免截断
        std::wstring curPath = p;
        HANDLE h = CreateFileW(curPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE && GetLastError() == ERROR_FILE_EXISTS) {
            // 分解主文件名与扩展名
            size_t dotPos = curPath.find_last_of(L'.');
            size_t slashPos = curPath.find_last_of(L"\\/");
            std::wstring baseName = (dotPos != std::wstring::npos && (slashPos == std::wstring::npos || dotPos > slashPos))
                ? curPath.substr(0, dotPos) : curPath;
            std::wstring extName = (dotPos != std::wstring::npos && (slashPos == std::wstring::npos || dotPos > slashPos))
                ? curPath.substr(dotPos) : L"";

            for (int i = 2; i <= 999; ++i) {
                curPath = baseName + L" (" + std::to_wstring(i) + L")" + extName;
                h = CreateFileW(curPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
                if (h != INVALID_HANDLE_VALUE) {
                    break;
                }
                if (GetLastError() != ERROR_FILE_EXISTS) {
                    break;
                }
            }
        }

        if (h == INVALID_HANDLE_VALUE) return nullptr;
        if (outActualPath) *outActualPath = curPath;
        return new COutFileStream(h, onBytesWritten, checkPauseOrCancel);
    }

private:
    LONG m_refCount;
    HANDLE m_hFile;
    uint64_t m_totalWritten;
    Crc32Hasher m_crc;
    std::function<void(uint64_t)> m_onBytesWritten;
    PauseCheckCallback m_checkPauseOrCancel;
};
