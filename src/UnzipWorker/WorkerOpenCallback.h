#pragma once

#include "SevenZip.h"
#include "FileStream.h"
#include <windows.h>
#include <string>
#include <vector>
#include <functional>
#include <algorithm>
#include <filesystem>
#include <cwctype>

namespace fs = std::filesystem;

// 输入卷身份快照：把“本次任务实际读取过的对象”绑定到卷序列号 + 128 位文件 ID + 大小 + 修改时间，
// 供 Proxy 在回收前逐卷核对，避免同名替换或误回收。
struct VolumeIdentityInfo {
    std::wstring path;
    uint64_t volumeSerial = 0;
    uint64_t fileIdHigh = 0;
    uint64_t fileIdLow = 0;
    std::string fileIdHex;   // 128 位文件 ID 的原始 16 字节规范表示（Identifier[0..15] 顺序拼接的 hex）
    uint64_t size = 0;
    uint64_t lastWriteTime = 0;
    bool valid = false;
};

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
        m_readDependencies.push_back(firstVolumePath);
        AddVolumeIdentity(firstVolumePath);
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

        // 读取依赖：仅用于诊断，不参与回收决策
        m_readDependencies.push_back(fullPath);

        // 仅当属于同目录同族真实分卷时，才计入可回收源卷名单
        if (IsSameVolumeFamily(m_firstVolumePath, fullPath)) {
            if (std::find(m_openedVolumes.begin(), m_openedVolumes.end(), fullPath) == m_openedVolumes.end()) {
                m_openedVolumes.push_back(fullPath);
                AddVolumeIdentity(fullPath);
            }
        }

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

    const std::vector<VolumeIdentityInfo>& GetVolumeIdentities() const {
        return m_volumeIdentities;
    }

    const std::vector<std::wstring>& GetReadDependencies() const {
        return m_readDependencies;
    }

    // 捕获单个卷的对象身份（句柄级读取，避免路径被替换后的误判）
    static bool CaptureIdentity(const std::wstring& path, VolumeIdentityInfo& out) {
        out = VolumeIdentityInfo{};
        out.path = path;
        HANDLE h = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES,
                               FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                               OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return false;

        bool ok = true;
        FILE_ID_INFO idInfo{};
        if (GetFileInformationByHandleEx(h, FileIdInfo, &idInfo, sizeof(idInfo))) {
            out.volumeSerial = idInfo.VolumeSerialNumber;
            out.fileIdLow = ((uint64_t)idInfo.FileId.Identifier[8]) |
                            ((uint64_t)idInfo.FileId.Identifier[9] << 8) |
                            ((uint64_t)idInfo.FileId.Identifier[10] << 16) |
                            ((uint64_t)idInfo.FileId.Identifier[11] << 24) |
                            ((uint64_t)idInfo.FileId.Identifier[12] << 32) |
                            ((uint64_t)idInfo.FileId.Identifier[13] << 40) |
                            ((uint64_t)idInfo.FileId.Identifier[14] << 48) |
                            ((uint64_t)idInfo.FileId.Identifier[15] << 56);
            out.fileIdHigh = ((uint64_t)idInfo.FileId.Identifier[0]) |
                             ((uint64_t)idInfo.FileId.Identifier[1] << 8) |
                             ((uint64_t)idInfo.FileId.Identifier[2] << 16) |
                             ((uint64_t)idInfo.FileId.Identifier[3] << 24) |
                             ((uint64_t)idInfo.FileId.Identifier[4] << 32) |
                             ((uint64_t)idInfo.FileId.Identifier[5] << 40) |
                             ((uint64_t)idInfo.FileId.Identifier[6] << 48) |
                             ((uint64_t)idInfo.FileId.Identifier[7] << 56);
            // 原始 16 字节规范表示（与 C# 端 FileIdentity 的字节序一致，避免高低 64 位定义相反）
            static const char hex[] = "0123456789abcdef";
            out.fileIdHex.reserve(32);
            for (int i = 0; i < 16; ++i) {
                uint8_t b = idInfo.FileId.Identifier[i];
                out.fileIdHex.push_back(hex[b >> 4]);
                out.fileIdHex.push_back(hex[b & 0xF]);
            }
        } else {
            ok = false;
        }

        BY_HANDLE_FILE_INFORMATION info{};
        if (GetFileInformationByHandle(h, &info)) {
            out.size = ((uint64_t)info.nFileSizeHigh << 32) | info.nFileSizeLow;
            out.lastWriteTime = ((uint64_t)info.ftLastWriteTime.dwHighDateTime << 32) | info.ftLastWriteTime.dwLowDateTime;
        } else {
            ok = false;
        }

        CloseHandle(h);
        out.valid = ok;
        return ok;
    }

    // 判断两个路径是否为同目录且符合同族分卷规则
    static bool IsSameVolumeFamily(const std::wstring& firstPath, const std::wstring& otherPath) {
        fs::path p1(firstPath);
        fs::path p2(otherPath);

        if (p1.parent_path() != p2.parent_path()) {
            return false;
        }

        std::wstring fn1 = p1.filename().wstring();
        std::wstring fn2 = p2.filename().wstring();
        for (auto& c : fn1) c = towlower(c);
        for (auto& c : fn2) c = towlower(c);

        if (fn1 == fn2) return true;

        // 模式 A: .001, .002, .003（如 archive.7z.001 -> archive.7z.002）
        size_t dot1 = fn1.find_last_of(L'.');
        size_t dot2 = fn2.find_last_of(L'.');
        if (dot1 != std::wstring::npos && dot2 != std::wstring::npos) {
            std::wstring base1 = fn1.substr(0, dot1);
            std::wstring base2 = fn2.substr(0, dot2);
            std::wstring ext1 = fn1.substr(dot1 + 1);
            std::wstring ext2 = fn2.substr(dot2 + 1);

            if (base1 == base2) {
                bool ext1AllDigits = !ext1.empty() && std::all_of(ext1.begin(), ext1.end(), iswdigit);
                bool ext2AllDigits = !ext2.empty() && std::all_of(ext2.begin(), ext2.end(), iswdigit);
                if (ext1AllDigits && ext2AllDigits) return true;

                // 模式 B: .zip, .z01, .z02
                if (ext1 == L"zip" && ext2.size() >= 2 && ext2[0] == L'z' &&
                    std::all_of(ext2.begin() + 1, ext2.end(), iswdigit)) {
                    return true;
                }
            }
        }

        // 模式 C: .part1.rar, .part2.rar（必须数字后缀 + .rar 扩展名，且前缀完全一致）
        size_t part1Pos = fn1.rfind(L".part");
        size_t part2Pos = fn2.rfind(L".part");
        if (part1Pos != std::wstring::npos && part2Pos != std::wstring::npos && part1Pos == part2Pos) {
            std::wstring rest1 = fn1.substr(part1Pos + 5);
            std::wstring rest2 = fn2.substr(part2Pos + 5);
            if (rest1.size() > 4 && rest2.size() > 4) {
                std::wstring num1 = rest1.substr(0, rest1.size() - 4);
                std::wstring num2 = rest2.substr(0, rest2.size() - 4);
                std::wstring ext1 = rest1.substr(rest1.size() - 4);
                std::wstring ext2 = rest2.substr(rest2.size() - 4);
                if (ext1 == L".rar" && ext2 == L".rar" &&
                    !num1.empty() && std::all_of(num1.begin(), num1.end(), iswdigit) &&
                    !num2.empty() && std::all_of(num2.begin(), num2.end(), iswdigit) &&
                    fn1.substr(0, part1Pos) == fn2.substr(0, part2Pos)) {
                    return true;
                }
            }
        }

        return false;
    }

private:
    void AddVolumeIdentity(const std::wstring& path) {
        VolumeIdentityInfo info;
        if (CaptureIdentity(path, info)) {
            m_volumeIdentities.push_back(info);
        }
    }

    LONG m_refCount;
    std::wstring m_firstVolumePath;
    std::function<std::wstring(bool* outCanceled)> m_passwordCallback;
    std::vector<std::wstring> m_openedVolumes;
    std::vector<std::wstring> m_readDependencies;
    std::vector<VolumeIdentityInfo> m_volumeIdentities;
};
