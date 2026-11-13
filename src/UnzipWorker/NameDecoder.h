#pragma once

// 文件名解码器：只在“缺少可靠编码信息”时才做候选解码，
// 并且把最终选定的一份名称用于显示、写盘与对账（单一事实来源）。

#include <windows.h>
#include <algorithm>
#include <string>
#include <vector>

class NameDecoder {
public:
    static constexpr uint32_t kUtf8 = 65001;

    // 严格 UTF-8 校验（拒绝超长编码与代理区）
    static bool IsValidUtf8(const std::string& bytes, bool& hasNonAscii) {
        hasNonAscii = false;
        size_t i = 0;
        while (i < bytes.size()) {
            unsigned char c = (unsigned char)bytes[i];
            if (c < 0x80) {
                ++i;
                continue;
            }
            size_t extra = 0;
            uint32_t cp = 0;
            if ((c & 0xE0) == 0xC0) { extra = 1; cp = c & 0x1F; }
            else if ((c & 0xF0) == 0xE0) { extra = 2; cp = c & 0x0F; }
            else if ((c & 0xF8) == 0xF0) { extra = 3; cp = c & 0x07; }
            else return false;

            if (i + extra >= bytes.size()) return false;
            for (size_t k = 1; k <= extra; ++k) {
                unsigned char cc = (unsigned char)bytes[i + k];
                if ((cc & 0xC0) != 0x80) return false;
                cp = (cp << 6) | (cc & 0x3F);
            }
            if (extra == 1 && cp < 0x80) return false;
            if (extra == 2 && cp < 0x800) return false;
            if (extra == 3 && cp < 0x10000) return false;
            if (cp >= 0xD800 && cp <= 0xDFFF) return false;
            if (cp > 0x10FFFF) return false;

            hasNonAscii = true;
            i += extra + 1;
        }
        return true;
    }

    // 字节流中是否存在 3/4 字节 UTF-8 序列（真实 CJK 文本的最强特征）
    static bool HasMultiByteUtf8(const std::string& bytes) {
        size_t i = 0;
        while (i < bytes.size()) {
            unsigned char c = (unsigned char)bytes[i];
            if (c < 0x80) { ++i; continue; }
            if ((c & 0xE0) == 0xC0) { i += 2; continue; }
            if ((c & 0xF0) == 0xE0 || (c & 0xF8) == 0xF0) return true;
            return false;
        }
        return false;
    }

    // 按指定代码页解码；ok=false 表示存在无法表示的字节
    static std::wstring DecodeBytes(const std::string& bytes, uint32_t cp, bool* ok = nullptr) {
        if (bytes.empty()) {
            if (ok) *ok = true;
            return L"";
        }
        uint32_t effective = (cp == 0) ? CP_ACP : cp;
        int len = MultiByteToWideChar(effective, MB_ERR_INVALID_CHARS, bytes.data(), (int)bytes.size(), nullptr, 0);
        bool clean = (len > 0);
        DWORD flags = clean ? MB_ERR_INVALID_CHARS : 0;
        len = MultiByteToWideChar(effective, flags, bytes.data(), (int)bytes.size(), nullptr, 0);
        if (len <= 0) {
            if (ok) *ok = false;
            return L"";
        }
        std::wstring out((size_t)len, L'\0');
        MultiByteToWideChar(effective, flags, bytes.data(), (int)bytes.size(), &out[0], len);
        if (ok) *ok = clean;
        return out;
    }

    static bool ContainsReplacement(const std::wstring& s) {
        return s.find(L'\uFFFD') != std::wstring::npos;
    }

    static bool ContainsPua(const std::wstring& s) {
        for (wchar_t c : s) {
            if (c >= 0xE000 && c <= 0xF8FF) return true;
        }
        return false;
    }

    struct Decoded {
        std::wstring name;
        bool ambiguous = false;   // 编码不确定，需要在 Proxy 侧让用户确认
        bool undecodable = false; // 存在无法表示的字节
    };

    // 解码一条归档名称。
    // declaredUtf8=true 表示格式已声明这是 Unicode/UTF-8 名称，必须原样尊重。
    static Decoded DecodeOne(const std::string& bytes, bool declaredUtf8, uint32_t preferredCp) {
        Decoded result;
        if (bytes.empty()) return result;

        bool hasNonAscii = false;
        bool utf8Valid = IsValidUtf8(bytes, hasNonAscii);
        bool primaryClean = true;
        std::wstring primary = DecodeBytes(bytes, preferredCp, &primaryClean);

        if (declaredUtf8) {
            if (utf8Valid) {
                result.name = DecodeBytes(bytes, kUtf8);
                return result;
            }
            // 声明与内容冲突（极少数损坏样本）：退回首选代码页并标记待确认
            result.name = primary;
            result.ambiguous = true;
            result.undecodable = !primaryClean;
            return result;
        }

        if (utf8Valid && hasNonAscii) {
            std::wstring asUtf8 = DecodeBytes(bytes, kUtf8);
            // 规则 1：存在 3/4 字节 UTF-8 序列，说明这是真实的 UTF-8 名称
            if (HasMultiByteUtf8(bytes)) {
                result.name = asUtf8;
                return result;
            }
            // 规则 2：解出的字符全部落在拉丁扩展区（如 café），基本可判定为 UTF-8
            if (OnlyLatinSupplement(asUtf8)) {
                result.name = asUtf8;
                return result;
            }
            // 规则 3：两字节歧义（GBK 与 UTF-8 互相误判都会得到这种形态），
            // 保持系统首选代码页（与 7-Zip/WinRAR 行为一致），不做启发式改写。
            result.name = primary;
            if (!primaryClean) {
                result.name = asUtf8;
                result.ambiguous = true;
                result.undecodable = true;
            }
            return result;
        }

        result.name = primary;
        result.undecodable = !primaryClean;
        result.ambiguous = !primaryClean && !ContainsReplacement(primary);
        return result;
    }

    // 候选代码页列表（用于“编码不确定”时的选择框），第一个为默认推荐值
    static std::vector<uint32_t> CandidateCodePages(uint32_t preferredCp, bool preferUtf8First) {
        std::vector<uint32_t> list;
        auto push = [&list](uint32_t cp) {
            if (cp == 0) return;
            if (std::find(list.begin(), list.end(), cp) == list.end()) list.push_back(cp);
        };
        if (preferUtf8First) push(kUtf8);
        push(preferredCp);
        push(kUtf8);
        push(936);
        push(950);
        push(932);
        push(949);
        push(1252);
        push(437);
        return list;
    }

    static std::string CodePageLabel(uint32_t cp) {
        switch (cp) {
            case 65001: return "UTF-8";
            case 936:   return "简体中文 (GBK, CP936)";
            case 950:   return "繁体中文 (Big5, CP950)";
            case 932:   return "日文 (Shift-JIS, CP932)";
            case 949:   return "韩文 (EUC-KR, CP949)";
            case 1252:  return "西欧 (Windows-1252)";
            case 437:   return "DOS 英文 (CP437)";
            default:    return "代码页 " + std::to_string(cp);
        }
    }

private:
    static bool OnlyLatinSupplement(const std::wstring& s) {
        bool sawLatin = false;
        for (wchar_t c : s) {
            if (c < 0x80) continue;
            if (c >= 0x00C0 && c <= 0x024F) { sawLatin = true; continue; }
            return false;
        }
        return sawLatin;
    }
};
