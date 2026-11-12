#pragma once

#include "SevenZip.h"
#include <windows.h>
#include <string>
#include <vector>
#include <memory>
#include <algorithm>

struct FormatInfo {
    uint32_t index;
    std::wstring name;
    GUID classId;
    std::vector<std::wstring> extensions;
    std::vector<std::wstring> addExtensions;
    std::vector<uint8_t> signature;
    uint32_t signatureOffset;
};

class SevenZipLibrary {
public:
    SevenZipLibrary() : m_hModule(nullptr), m_pfnCreateObject(nullptr), m_pfnGetNumberOfFormats(nullptr), m_pfnGetHandlerProperty2(nullptr) {}

    ~SevenZipLibrary() {
        Unload();
    }

    bool Load(const std::wstring& dllPath) {
        Unload();
        m_hModule = LoadLibraryW(dllPath.c_str());
        if (!m_hModule) return false;

        m_pfnCreateObject = (Func_CreateObject)GetProcAddress(m_hModule, "CreateObject");
        m_pfnGetNumberOfFormats = (Func_GetNumberOfFormats)GetProcAddress(m_hModule, "GetNumberOfFormats");
        m_pfnGetHandlerProperty2 = (Func_GetHandlerProperty2)GetProcAddress(m_hModule, "GetHandlerProperty2");

        if (!m_pfnCreateObject) {
            Unload();
            return false;
        }

        if (m_pfnGetNumberOfFormats && m_pfnGetHandlerProperty2) {
            uint32_t numFormats = 0;
            if (SUCCEEDED(m_pfnGetNumberOfFormats(&numFormats))) {
                for (uint32_t i = 0; i < numFormats; ++i) {
                    FormatInfo info{};
                    info.index = i;

                    PROPVARIANT prop;
                    PropVariantInit(&prop);

                    // Name
                    if (SUCCEEDED(m_pfnGetHandlerProperty2(i, NArchive::NHandlerPropID::kName, &prop)) && prop.vt == VT_BSTR && prop.bstrVal) {
                        info.name = prop.bstrVal;
                    }
                    PropVariantClear(&prop);

                    // ClassID
                    if (SUCCEEDED(m_pfnGetHandlerProperty2(i, NArchive::NHandlerPropID::kClassID, &prop)) && prop.vt == VT_BSTR && prop.bstrVal) {
                        if (SysStringByteLen(prop.bstrVal) == sizeof(GUID)) {
                            memcpy(&info.classId, prop.bstrVal, sizeof(GUID));
                        }
                    }
                    PropVariantClear(&prop);

                    // Extension
                    if (SUCCEEDED(m_pfnGetHandlerProperty2(i, NArchive::NHandlerPropID::kExtension, &prop)) && prop.vt == VT_BSTR && prop.bstrVal) {
                        std::wstring exts = prop.bstrVal;
                        // 分割空格
                        size_t start = 0;
                        while (start < exts.size()) {
                            size_t space = exts.find(L' ', start);
                            if (space == std::wstring::npos) space = exts.size();
                            std::wstring ext = exts.substr(start, space - start);
                            if (!ext.empty()) {
                                std::wstring lowerExt = ext;
                                for (auto& c : lowerExt) c = towlower(c);
                                info.extensions.push_back(lowerExt);
                            }
                            start = space + 1;
                        }
                    }
                    PropVariantClear(&prop);

                    // Signature
                    if (SUCCEEDED(m_pfnGetHandlerProperty2(i, NArchive::NHandlerPropID::kSignature, &prop)) && prop.vt == VT_BSTR && prop.bstrVal) {
                        UINT len = SysStringByteLen(prop.bstrVal);
                        const uint8_t* bytes = reinterpret_cast<const uint8_t*>(prop.bstrVal);
                        info.signature.assign(bytes, bytes + len);
                    }
                    PropVariantClear(&prop);

                    // SignatureOffset
                    info.signatureOffset = 0;
                    if (SUCCEEDED(m_pfnGetHandlerProperty2(i, NArchive::NHandlerPropID::kSignatureOffset, &prop))) {
                        if (prop.vt == VT_UI4) info.signatureOffset = prop.ulVal;
                        else if (prop.vt == VT_UI8) info.signatureOffset = static_cast<uint32_t>(prop.uhVal.QuadPart);
                    }
                    PropVariantClear(&prop);

                    m_formats.push_back(info);
                }
            }
        }

        return true;
    }

    void Unload() {
        if (m_hModule) {
            FreeLibrary(m_hModule);
            m_hModule = nullptr;
        }
        m_formats.clear();
        m_pfnCreateObject = nullptr;
        m_pfnGetNumberOfFormats = nullptr;
        m_pfnGetHandlerProperty2 = nullptr;
    }

    const std::vector<FormatInfo>& GetFormats() const { return m_formats; }

    HRESULT CreateArchiveReader(const GUID& classId, IInArchive** archive) {
        if (!m_pfnCreateObject || !archive) return E_FAIL;
        return m_pfnCreateObject(&classId, &IID_IInArchive, reinterpret_cast<void**>(archive));
    }

    // 根据扩展名查找候选格式
    std::vector<FormatInfo> FindFormatsByExtension(const std::wstring& ext) const {
        std::wstring lowerExt = ext;
        if (!lowerExt.empty() && lowerExt[0] == L'.') lowerExt = lowerExt.substr(1);
        for (auto& c : lowerExt) c = towlower(c);

        std::vector<FormatInfo> res;
        for (const auto& fmt : m_formats) {
            for (const auto& e : fmt.extensions) {
                if (e == lowerExt) {
                    res.push_back(fmt);
                    break;
                }
            }
        }
        return res;
    }

    // 获取最合理的默认代码页（处理 Windows 11 全局 UTF-8 实验选项陷阱）
    static uint32_t GetDefaultArchiveCodePage() {
        uint32_t acp = GetACP();
        if (acp != 0 && acp != CP_UTF8) {
            return acp;
        }

        // 若系统开启了全局 UTF-8 模式 (GetACP() == 65001)，查询用户当前区域设置的 ANSI 代码页
        wchar_t buf[16] = {0};
        if (GetLocaleInfoW(LOCALE_USER_DEFAULT, LOCALE_IDEFAULTANSICODEPAGE, buf, 16) > 0) {
            uint32_t locCp = (uint32_t)_wtoi(buf);
            if (locCp != 0 && locCp != CP_UTF8) {
                return locCp;
            }
        }

        if (GetLocaleInfoW(LOCALE_SYSTEM_DEFAULT, LOCALE_IDEFAULTANSICODEPAGE, buf, 16) > 0) {
            uint32_t locCp = (uint32_t)_wtoi(buf);
            if (locCp != 0 && locCp != CP_UTF8) {
                return locCp;
            }
        }

        // 默认兜底为 936 (GBK / 简体中文)
        return 936;
    }

    // 智能探测归档格式：先按扩展名，再按签名
    bool DetectAndCreateArchive(IInStream* inStream, const std::wstring& extHint, IInArchive** outArchive, uint32_t codePage = 0) {
        if (!inStream || !outArchive) return false;
        *outArchive = nullptr;

        if (codePage == 0) {
            codePage = GetDefaultArchiveCodePage();
        }

        auto applyCodePage = [codePage](IInArchive* arc) {
            if (!arc) return;
            IArchiveSetProperties* setProps = nullptr;
            if (SUCCEEDED(arc->QueryInterface(__uuidof(IArchiveSetProperties), (void**)&setProps)) && setProps) {
                const wchar_t* names[] = { L"cp" };
                PROPVARIANT values[1];
                values[0].vt = VT_UI4;
                values[0].ulVal = codePage;
                setProps->SetProperties(names, values, 1);
                setProps->Release();
            }
        };

        // 1. 尝试按扩展名候选格式
        auto extFormats = FindFormatsByExtension(extHint);
        for (const auto& fmt : extFormats) {
            IInArchive* archive = nullptr;
            if (SUCCEEDED(CreateArchiveReader(fmt.classId, &archive)) && archive) {
                applyCodePage(archive);
                uint64_t maxStartPos = 1 << 20; // 1MB 探测
                uint64_t newPos = 0;
                inStream->Seek(0, 0, &newPos);
                if (SUCCEEDED(archive->Open(inStream, &maxStartPos, nullptr))) {
                    *outArchive = archive;
                    return true;
                }
                archive->Release();
            }
        }

        // 2. 尝试根据已知头部签名探测
        uint8_t buffer[65536];
        uint32_t readBytes = 0;
        uint64_t newPos = 0;
        inStream->Seek(0, 0, &newPos);
        if (SUCCEEDED(inStream->Read(buffer, sizeof(buffer), &readBytes)) && readBytes > 0) {
            for (const auto& fmt : m_formats) {
                if (!fmt.signature.empty() && fmt.signatureOffset + fmt.signature.size() <= readBytes) {
                    if (memcmp(buffer + fmt.signatureOffset, fmt.signature.data(), fmt.signature.size()) == 0) {
                        IInArchive* archive = nullptr;
                        if (SUCCEEDED(CreateArchiveReader(fmt.classId, &archive)) && archive) {
                            applyCodePage(archive);
                            inStream->Seek(0, 0, &newPos);
                            uint64_t maxStartPos = 1 << 20;
                            if (SUCCEEDED(archive->Open(inStream, &maxStartPos, nullptr))) {
                                *outArchive = archive;
                                return true;
                            }
                            archive->Release();
                        }
                    }
                }
            }
        }

        // 3. 轮询常用格式：7z, Zip, Rar5, Rar, Tar, GZip, BZip2, Xz, Wim
        static const wchar_t* commonOrder[] = { L"7z", L"zip", L"Rar5", L"Rar", L"tar", L"gzip", L"bzip2", L"xz", L"wim" };
        for (const auto& name : commonOrder) {
            for (const auto& fmt : m_formats) {
                if (_wcsicmp(fmt.name.c_str(), name) == 0) {
                    IInArchive* archive = nullptr;
                    if (SUCCEEDED(CreateArchiveReader(fmt.classId, &archive)) && archive) {
                        applyCodePage(archive);
                        inStream->Seek(0, 0, &newPos);
                        uint64_t maxStartPos = 1 << 20;
                        if (SUCCEEDED(archive->Open(inStream, &maxStartPos, nullptr))) {
                            *outArchive = archive;
                            return true;
                        }
                        archive->Release();
                    }
                }
            }
        }

        return false;
    }

private:
    HMODULE m_hModule;
    Func_CreateObject m_pfnCreateObject;
    Func_GetNumberOfFormats m_pfnGetNumberOfFormats;
    Func_GetHandlerProperty2 m_pfnGetHandlerProperty2;
    std::vector<FormatInfo> m_formats;
};
