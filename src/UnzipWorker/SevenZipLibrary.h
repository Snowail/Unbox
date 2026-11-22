#pragma once

#include "SevenZip.h"
#include "ArchiveFormatPolicy.h"
#include <functional>
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
    std::vector<std::vector<uint8_t>> signatures;
    uint64_t signatureOffset;
};

class SevenZipLibrary {
public:
    SevenZipLibrary() : m_hModule(nullptr), m_pfnCreateObject(nullptr), m_pfnGetNumberOfFormats(nullptr), m_pfnGetHandlerProperty2(nullptr) {}

    ~SevenZipLibrary() {
        Unload();
    }

    bool Load(const std::wstring& dllPath) {
        Unload();
        m_hModule = LoadLibraryExW(dllPath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!m_hModule) return false;

        m_pfnCreateObject = (Func_CreateObject)GetProcAddress(m_hModule, "CreateObject");
        m_pfnGetNumberOfFormats = (Func_GetNumberOfFormats)GetProcAddress(m_hModule, "GetNumberOfFormats");
        m_pfnGetHandlerProperty2 = (Func_GetHandlerProperty2)GetProcAddress(m_hModule, "GetHandlerProperty2");

        if (!m_pfnCreateObject || !m_pfnGetNumberOfFormats || !m_pfnGetHandlerProperty2) {
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
                        if (len != 0) info.signatures.emplace_back(bytes, bytes + len);
                    }
                    PropVariantClear(&prop);

                    // Each multi-signature record is a byte length followed by its bytes.
                    if (SUCCEEDED(m_pfnGetHandlerProperty2(i, NArchive::NHandlerPropID::kMultiSignature, &prop))
                        && prop.vt == VT_BSTR && prop.bstrVal) {
                        const UINT length = SysStringByteLen(prop.bstrVal);
                        const auto* bytes = reinterpret_cast<const uint8_t*>(prop.bstrVal);
                        for (UINT position = 0; position < length;) {
                            const UINT count = bytes[position++];
                            if (count == 0 || count > length - position) {
                                PropVariantClear(&prop);
                                Unload();
                                return false;
                            }
                            info.signatures.emplace_back(bytes + position, bytes + position + count);
                            position += count;
                        }
                    }
                    PropVariantClear(&prop);
                    const GUID emptyId{};
                    if (info.name.empty() || IsEqualGUID(info.classId, emptyId)) {
                        Unload();
                        return false;
                    }
                    // SignatureOffset
                    info.signatureOffset = 0;
                    if (SUCCEEDED(m_pfnGetHandlerProperty2(i, NArchive::NHandlerPropID::kSignatureOffset, &prop))) {
                        if (prop.vt == VT_UI4) info.signatureOffset = prop.ulVal;
                        else if (prop.vt == VT_UI8) info.signatureOffset = prop.uhVal.QuadPart;
                    }
                    PropVariantClear(&prop);

                    m_formats.push_back(info);
                }
            }
        }

        if (!m_formats.empty()) return true;
        Unload();
        return false;
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

    bool DetectAndCreateArchive(IInStream* stream, const std::wstring& extHint, IInArchive** archive,
        IArchiveOpenCallback* callback = nullptr, uint32_t codePage = 0) {
        const auto route = ClassifyArchiveName(extHint);
        return DetectAndCreateArchive(stream, extHint, archive, callback, codePage, route.handlerHints,
            route.kind == ArchiveKind::TarWrapper || route.kind == ArchiveKind::Split);
    }

    bool DetectAndCreateArchive(IInStream* inStream, const std::wstring& extHint, IInArchive** outArchive,
        IArchiveOpenCallback* openCallback, uint32_t codePage, const std::vector<std::wstring>& hints,
        bool requireHint, const std::function<bool()>& stop = {}, std::wstring* selected = nullptr) {
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

        std::vector<GUID> tried;
        bool stopped = false;
        auto tryFormat = [&](const FormatInfo& format) {
            if (stopped || (stop && stop())) return false;
            if (std::any_of(tried.begin(), tried.end(), [&](const GUID& id) {
                return IsEqualGUID(id, format.classId);
            })) return false;
            tried.push_back(format.classId);
            if (inStream->Seek(0, FILE_BEGIN, nullptr) != S_OK) {
                stopped = true;
                return false;
            }
            IInArchive* raw = nullptr;
            const HRESULT created = CreateArchiveReader(format.classId, &raw);
            auto release = [](IInArchive* reader) { reader->Release(); };
            std::unique_ptr<IInArchive, decltype(release)> reader(raw, release);
            if (created != S_OK || !reader) return false;
            applyCodePage(reader.get());
            const uint64_t maxStartPos = 1 << 20;
            const HRESULT hr = reader->Open(inStream, &maxStartPos, openCallback);
            if (hr != S_OK) {
                if (hr == E_ABORT || (stop && stop())) stopped = true;
                return false;
            }
            if (selected) *selected = format.name;
            *outArchive = reader.release();
            return true;
        };
        auto tryName = [&](const std::wstring& name) {
            auto format = std::find_if(m_formats.begin(), m_formats.end(), [&](const FormatInfo& item) {
                return _wcsicmp(item.name.c_str(), name.c_str()) == 0;
            });
            return format != m_formats.end() && tryFormat(*format);
        };
        for (const auto& hint : hints) if (tryName(hint)) return true;
        if (requireHint || stopped) return false;
        if (hints.empty()) {
            for (const auto& format : FindFormatsByExtension(extHint)) {
                if (_wcsicmp(format.name.c_str(), L"brotli") != 0 && tryFormat(format)) return true;
            }
            if (stopped) return false;
        }

        std::vector<uint8_t> prefix(65536);
        uint32_t count = 0;
        if (inStream->Seek(0, FILE_BEGIN, nullptr) != S_OK ||
            inStream->Read(prefix.data(), static_cast<uint32_t>(prefix.size()), &count) != S_OK)
            return false;
        prefix.resize(count);
        size_t frame = 0;
        while (prefix.size() - frame >= 4 && (prefix[frame] & 0xF0) == 0x50 &&
               prefix[frame + 1] == 0x2A && prefix[frame + 2] == 0x4D && prefix[frame + 3] == 0x18) {
            if (prefix.size() - frame < 8) return false;
            const auto* p = prefix.data() + frame + 4;
            const uint32_t size = uint32_t(p[0]) | (uint32_t(p[1]) << 8) |
                (uint32_t(p[2]) << 16) | (uint32_t(p[3]) << 24);
            if (size > prefix.size() - frame - 8) return false;
            frame += 8 + size;
        }
        if (frame && prefix.size() - frame < 4) return false;
        for (const auto& format : m_formats) {
            if (format.signatureOffset > prefix.size() - frame) continue;
            if (frame && _wcsicmp(format.name.c_str(), L"zstd") && _wcsicmp(format.name.c_str(), L"lz4") &&
                _wcsicmp(format.name.c_str(), L"lz5") && _wcsicmp(format.name.c_str(), L"lizard")) continue;
            const auto offset = frame + static_cast<size_t>(format.signatureOffset);
            for (const auto& signature : format.signatures) {
                if (signature.size() <= prefix.size() - offset &&
                    std::equal(signature.begin(), signature.end(), prefix.begin() + offset) && tryFormat(format))
                    return true;
            }
        }
        if (frame || stopped) return false;

        static const wchar_t* commonOrder[] = { L"7z", L"zip", L"Rar5", L"Rar", L"tar", L"gzip", L"bzip2", L"xz", L"wim" };
        for (const auto* name : commonOrder) if (tryName(name)) return true;
        return false;
    }

private:
    HMODULE m_hModule;
    Func_CreateObject m_pfnCreateObject;
    Func_GetNumberOfFormats m_pfnGetNumberOfFormats;
    Func_GetHandlerProperty2 m_pfnGetHandlerProperty2;
    std::vector<FormatInfo> m_formats;
};
