#pragma once

#include "SevenZip.h"
#include "CryptoUtils.h"
#include <windows.h>
#include <atomic>
#include <memory>
#include <functional>

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

// 输出流包装（流式计算 CRC32、SHA256，支持暂停和取消监控）
class COutFileStream : public ISequentialOutStream {
public:
    using PauseCheckCallback = std::function<bool()>; // 返回 false 表示取消任务

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
        if (iid == IID_IUnknown) {
            *ppvObject = static_cast<IUnknown*>(static_cast<ISequentialOutStream*>(this));
        } else if (iid == IID_ISequentialOutStream) {
            *ppvObject = static_cast<ISequentialOutStream*>(this);
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
            m_sha.Update(data, bytesWritten);
            m_totalWritten += bytesWritten;
            if (m_onBytesWritten) {
                m_onBytesWritten(bytesWritten);
            }
        }

        if (processedSize) *processedSize = bytesWritten;
        return S_OK;
    }

    uint32_t GetCRC() const { return m_crc.GetDigest(); }
    std::string GetSHA256() { return m_sha.FinishHex(); }
    uint64_t GetTotalWritten() const { return m_totalWritten; }

    static COutFileStream* CreateOutputFile(const wchar_t* path, 
                                            std::function<void(uint64_t)> onBytesWritten,
                                            PauseCheckCallback checkPauseOrCancel) {
        HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return nullptr;
        return new COutFileStream(h, onBytesWritten, checkPauseOrCancel);
    }

private:
    LONG m_refCount;
    HANDLE m_hFile;
    uint64_t m_totalWritten;
    Crc32Hasher m_crc;
    Sha256Hasher m_sha;
    std::function<void(uint64_t)> m_onBytesWritten;
    PauseCheckCallback m_checkPauseOrCancel;
};
