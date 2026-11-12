#pragma once

#include <string>
#include <vector>
#include <algorithm>
#include <filesystem>
#include <cwctype>

namespace fs = std::filesystem;

class PathSanitizer {
public:
    // D5: macOS 辅助文件判断
    static bool IsMacAuxiliary(const std::wstring& relPath) {
        std::wstring norm = relPath;
        std::replace(norm.begin(), norm.end(), L'/', L'\\');

        // 检查是否在 __MACOSX 目录下
        if (norm.rfind(L"__MACOSX\\", 0) == 0 || norm == L"__MACOSX") {
            return true;
        }

        // 获取文件名
        size_t lastSlash = norm.find_last_of(L"\\");
        std::wstring filename = (lastSlash == std::wstring::npos) ? norm : norm.substr(lastSlash + 1);

        // 检查 .DS_Store 或 AppleDouble 隐藏资源文件 ._*
        if (_wcsicmp(filename.c_str(), L".DS_Store") == 0) {
            return true;
        }
        if (filename.rfind(L"._", 0) == 0) {
            return true;
        }

        return false;
    }

    // 检查单一文件名片段（非全路径，不含路径分隔符）是否包含非法字符或保留名
    static bool HasIllegalSegmentChars(const std::wstring& seg) {
        if (seg.empty()) return false;
        // 检查是否包含 Windows 禁用字符
        const wchar_t illegalChars[] = L":*?\"<>|";
        for (wchar_t c : seg) {
            if (c < 32 || wcschr(illegalChars, c) != nullptr) {
                return true;
            }
        }
        // 检查尾部空格或点
        wchar_t last = seg.back();
        if (last == L' ' || last == L'.') {
            return true;
        }

        // 检查 Windows 保留设备名称: CON, PRN, AUX, NUL, COM1-9, LPT1-9
        std::wstring base = seg;
        size_t dotPos = base.find(L'.');
        if (dotPos != std::wstring::npos) {
            base = base.substr(0, dotPos);
        }
        std::wstring upperBase = base;
        for (auto& c : upperBase) c = towupper(c);

        static const wchar_t* reserved[] = {
            L"CON", L"PRN", L"AUX", L"NUL",
            L"COM1", L"COM2", L"COM3", L"COM4", L"COM5", L"COM6", L"COM7", L"COM8", L"COM9",
            L"LPT1", L"LPT2", L"LPT3", L"LPT4", L"LPT5", L"LPT6", L"LPT7", L"LPT8", L"LPT9"
        };
        for (const auto& res : reserved) {
            if (upperBase == res) return true;
        }

        return false;
    }

    // 检查相对路径是否合法
    static bool HasIllegalChars(const std::wstring& relPath) {
        std::wstring norm = relPath;
        std::replace(norm.begin(), norm.end(), L'/', L'\\');

        // 绝对路径或盘符或 UNC
        if (norm.find(L":") != std::wstring::npos || norm.rfind(L"\\\\", 0) == 0) {
            return true;
        }

        // 拆分各级目录段分别检查
        size_t start = 0;
        while (start < norm.size()) {
            size_t end = norm.find(L'\\', start);
            if (end == std::wstring::npos) end = norm.size();
            std::wstring seg = norm.substr(start, end - start);
            if (seg == L"..") return true; // 路径越界
            if (HasIllegalSegmentChars(seg)) return true;
            start = end + 1;
        }

        return false;
    }

    // 批量重命名清洗（D6 默认策略：非法字符替换为 '-'）
    static std::wstring SanitizeSegment(const std::wstring& seg) {
        if (seg.empty()) return seg;
        std::wstring res;
        res.reserve(seg.size());

        const wchar_t illegalChars[] = L":*?\"<>|/\\";
        for (wchar_t c : seg) {
            if (c < 32 || wcschr(illegalChars, c) != nullptr) {
                res.push_back(L'-');
            } else {
                res.push_back(c);
            }
        }

        // 去除或替换末尾空格和点（全部替换为 '-'）
        int lastNonDotOrSpace = (int)res.size() - 1;
        while (lastNonDotOrSpace >= 0 && (res[lastNonDotOrSpace] == L' ' || res[lastNonDotOrSpace] == L'.')) {
            lastNonDotOrSpace--;
        }
        for (size_t k = lastNonDotOrSpace + 1; k < res.size(); ++k) {
            res[k] = L'-';
        }

        // 保留名称处理
        std::wstring base = res;
        size_t dotPos = base.find(L'.');
        if (dotPos != std::wstring::npos) {
            base = base.substr(0, dotPos);
        }
        std::wstring upperBase = base;
        for (auto& c : upperBase) c = towupper(c);

        static const wchar_t* reserved[] = {
            L"CON", L"PRN", L"AUX", L"NUL",
            L"COM1", L"COM2", L"COM3", L"COM4", L"COM5", L"COM6", L"COM7", L"COM8", L"COM9",
            L"LPT1", L"LPT2", L"LPT3", L"LPT4", L"LPT5", L"LPT6", L"LPT7", L"LPT8", L"LPT9"
        };
        for (const auto& resName : reserved) {
            if (upperBase == resName) {
                res = L"_" + res;
                break;
            }
        }

        return res;
    }

    // 对整条相对路径执行批量重命名
    static std::wstring SanitizeRelativePath(const std::wstring& relPath) {
        std::wstring norm = relPath;
        std::replace(norm.begin(), norm.end(), L'/', L'\\');

        std::wstring result;
        size_t start = 0;
        bool first = true;
        while (start < norm.size()) {
            size_t end = norm.find(L'\\', start);
            if (end == std::wstring::npos) end = norm.size();
            std::wstring seg = norm.substr(start, end - start);
            if (seg == L"..") seg = L"__"; // 避免穿越
            if (!seg.empty()) {
                seg = SanitizeSegment(seg);
                if (!first) result += L"\\";
                result += seg;
                first = false;
            }
            start = end + 1;
        }
        return result;
    }

    // D5: 检查数据是否为合法 UTF-8 编码，并判断是否包含非 ASCII 字符
    static bool IsValidUtf8(const char* data, size_t len, bool& hasNonAscii) {
        hasNonAscii = false;
        size_t i = 0;
        while (i < len) {
            unsigned char c = (unsigned char)data[i];
            if (c < 0x80) {
                i++;
            } else if ((c & 0xE0) == 0xC0) {
                if (i + 1 >= len) return false;
                if (((unsigned char)data[i + 1] & 0xC0) != 0x80) return false;
                hasNonAscii = true;
                i += 2;
            } else if ((c & 0xF0) == 0xE0) {
                if (i + 2 >= len) return false;
                if (((unsigned char)data[i + 1] & 0xC0) != 0x80) return false;
                if (((unsigned char)data[i + 2] & 0xC0) != 0x80) return false;
                hasNonAscii = true;
                i += 3;
            } else if ((c & 0xF8) == 0xF0) {
                if (i + 3 >= len) return false;
                if (((unsigned char)data[i + 1] & 0xC0) != 0x80) return false;
                if (((unsigned char)data[i + 2] & 0xC0) != 0x80) return false;
                if (((unsigned char)data[i + 3] & 0xC0) != 0x80) return false;
                hasNonAscii = true;
                i += 4;
            } else {
                return false;
            }
        }
        return true;
    }

    // D5: 智能修复跨系统或代码页导致的文件名乱码 (7z PUA 或 未标记 bit 11 的 UTF-8 误按 GBK 解码)
    static bool FixMojibake(std::wstring& path, uint32_t fallbackCodePage = 936) {
        if (path.empty()) return false;

        // 1. 检查是否存在 7-Zip 私有区字符 (0xEF80 - 0xEFFF)
        // 当 7-Zip 试图以 UTF-8 解码非 UTF-8 字节失败时，会将 0x80..0xFF 映射到 0xEF80..0xEFFF
        bool hasPua = false;
        for (wchar_t c : path) {
            if (c >= 0xEF80 && c <= 0xEFFF) {
                hasPua = true;
                break;
            }
        }

        if (hasPua) {
            std::string rawBytes;
            for (wchar_t c : path) {
                if (c >= 0xEF80 && c <= 0xEFFF) {
                    rawBytes.push_back((char)(unsigned char)(c - 0xEF00));
                } else if (c < 0x80) {
                    rawBytes.push_back((char)(unsigned char)c);
                } else {
                    char u8Buf[8] = {0};
                    int u8Len = WideCharToMultiByte(CP_UTF8, 0, &c, 1, u8Buf, sizeof(u8Buf), nullptr, nullptr);
                    for (int k = 0; k < u8Len; ++k) rawBytes.push_back(u8Buf[k]);
                }
            }
            int fixedLen = MultiByteToWideChar(fallbackCodePage, 0, rawBytes.data(), (int)rawBytes.size(), nullptr, 0);
            if (fixedLen > 0) {
                std::wstring fixed(fixedLen, 0);
                MultiByteToWideChar(fallbackCodePage, 0, rawBytes.data(), (int)rawBytes.size(), &fixed[0], fixedLen);
                path = fixed;
                return true;
            }
        }

        // 2. 检查是否由于未标 bit 11 的 UTF-8 压缩包被 7z 误用 fallbackCodePage 解码 (如 "娴嬭瘯")
        int gbkByteLen = WideCharToMultiByte(fallbackCodePage, 0, path.c_str(), (int)path.size(), nullptr, 0, nullptr, nullptr);
        if (gbkByteLen > 0) {
            std::string gbkBytes(gbkByteLen, 0);
            WideCharToMultiByte(fallbackCodePage, 0, path.c_str(), (int)path.size(), &gbkBytes[0], gbkByteLen, nullptr, nullptr);
            bool hasNonAscii = false;
            if (IsValidUtf8(gbkBytes.data(), gbkBytes.size(), hasNonAscii) && hasNonAscii) {
                int utf8Len = MultiByteToWideChar(CP_UTF8, 0, gbkBytes.data(), (int)gbkBytes.size(), nullptr, 0);
                if (utf8Len > 0) {
                    std::wstring utf8Fixed(utf8Len, 0);
                    MultiByteToWideChar(CP_UTF8, 0, gbkBytes.data(), (int)gbkBytes.size(), &utf8Fixed[0], utf8Len);
                    path = utf8Fixed;
                    return true;
                }
            }
        }

        return false;
    }
};
