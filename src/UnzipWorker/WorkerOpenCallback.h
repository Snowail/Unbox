#pragma once

#include "SevenZip.h"
#include "FileStream.h"
#include <windows.h>
#include <string>
#include <vector>
#include <functional>

// 打开阶段回调：支持打开进度、多卷查找和头加密密码索取
class WorkerOpenCallback : 
    public IArchiveOpenCallback,
    public IArchiveOpenVolumeCallback,
    public ICryptoGetTextPassword
{
public:
    WorkerOpenCallback(const std::wstring& firstVolumePath, 
                       std::function<std::wstring(bool* outCanceled)> passwordCallback)
        : m_refCount(1)
        , m_firstVolumePath(firstVolumePath)
        , m_passwordCallback(passwordCallback)
    {
        m_openedVolumes.push_back(firstVolumePath);
    }

    // IUnknown
    STDMETHOD(QueryInterface)(REFIID iid, void** outObject) override {
        if (!outObject) return E_POINTER;
        if (iid == IID_IUnknown || iid == IID_IArchiveOpenCallback) {
            *outObject = static_cast<IArchiveOpenCallback*>(this);
            AddRef();
            return S_OK;
        }
        if (iid == IID_IArchiveOpenVolumeCallback) {
            *outObject = static_cast<IArchiveOpenVolumeCallback*>(this);
            AddRef();
            return S_OK;
        }
        if (iid == IID_ICryptoGetTextPassword) {
            *outObject = static_cast<ICryptoGetTextPassword*>(this);
            AddRef();
            return S_OK;
        }
        *outObject = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHOD_(ULONG, AddRef)() override {
        return (ULONG)InterlockedIncrement(&m_refCount);
    }

    STDMETHOD_(ULONG, Release)() override {
        ULONG res = (ULONG)InterlockedDecrement(&m_refCount);
        if (res == 0) {
            delete this;
        }
        return res;
    }

    // IArchiveOpenCallback
    STDMETHOD(SetTotal)(const uint64_t* files, const uint64_t* bytes) override {
        return S_OK;
    }

    STDMETHOD(SetCompleted)(const uint64_t* files, const uint64_t* bytes) override {
        return S_OK;
    }

    // IArchiveOpenVolumeCallback
    STDMETHOD(GetProperty)(PROPID propID, PROPVARIANT* value) override {
        if (!value) return E_POINTER;
        PropVariantInit(value);
        if (propID == kpidName) {
            size_t slash = m_firstVolumePath.find_last_of(L"\\/");
            std::wstring name = (slash != std::wstring::npos) ? m_firstVolumePath.substr(slash + 1) : m_firstVolumePath;
            value->vt = VT_BSTR;
            value->bstrVal = SysAllocString(name.c_str());
            return S_OK;
        }
        return S_OK;
    }

    STDMETHOD(GetStream)(const wchar_t* name, IInStream** inStream) override {
        if (!name || !inStream) return E_POINTER;
        *inStream = nullptr;

        size_t slash = m_firstVolumePath.find_last_of(L"\\/");
        std::wstring dir = (slash != std::wstring::npos) ? m_firstVolumePath.substr(0, slash + 1) : L"";
        std::wstring fullPath = dir + name;

        DWORD attr = GetFileAttributesW(fullPath.c_str());
        if (attr == INVALID_FILE_ATTRIBUTES || (attr & FILE_ATTRIBUTE_DIRECTORY)) {
            return S_FALSE;
        }

        CInFileStream* stream = CInFileStream::OpenFile(fullPath.c_str());
        if (!stream) {
            return S_FALSE;
        }

        // 记录本次实际成功打开的分卷真实路径
        m_openedVolumes.push_back(fullPath);

        *inStream = stream;
        return S_OK;
    }

    // ICryptoGetTextPassword
    STDMETHOD(CryptoGetTextPassword)(BSTR* password) override {
        if (!password) return E_POINTER;
        *password = nullptr;

        if (m_passwordCallback) {
            bool canceled = false;
            std::wstring pwd = m_passwordCallback(&canceled);
            if (canceled) {
                return E_ABORT;
            }
            *password = SysAllocString(pwd.c_str());
            return S_OK;
        }
        return E_ABORT;
    }

    const std::vector<std::wstring>& GetOpenedVolumes() const {
        return m_openedVolumes;
    }

private:
    LONG m_refCount;
    std::wstring m_firstVolumePath;
    std::function<std::wstring(bool* outCanceled)> m_passwordCallback;
    std::vector<std::wstring> m_openedVolumes;
};
