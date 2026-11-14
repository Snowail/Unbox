#pragma once

#include <windows.h>
#include <winternl.h>
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

        if (norm.rfind(L"__MACOSX\\", 0) == 0 || norm == L"__MACOSX") {
            return true;
        }

        size_t lastSlash = norm.find_last_of(L"\\");
        std::wstring filename = (lastSlash == std::wstring::npos) ? norm : norm.substr(lastSlash + 1);

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
        const wchar_t illegalChars[] = L":*?\"<>|";
        for (wchar_t c : seg) {
            if (c < 32 || wcschr(illegalChars, c) != nullptr) {
                return true;
            }
        }
        wchar_t last = seg.back();
        if (last == L' ' || last == L'.') {
            return true;
        }

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

        if (norm.find(L":") != std::wstring::npos || norm.rfind(L"\\\\", 0) == 0) {
            return true;
        }

        size_t start = 0;
        while (start < norm.size()) {
            size_t end = norm.find(L'\\', start);
            if (end == std::wstring::npos) end = norm.size();
            std::wstring seg = norm.substr(start, end - start);
            if (seg == L"..") return true;
            if (HasIllegalSegmentChars(seg)) return true;
            start = end + 1;
        }

        return false;
    }

    // 统一路径分隔符（把 '/' 归一为 Windows 的 '\\'）
    static std::wstring SanitizePathSeparators(const std::wstring& relPath) {
        std::wstring norm = relPath;
        std::replace(norm.begin(), norm.end(), L'/', L'\\');
        return norm;
    }

    // 生产级安全验证：确保相对路径在规范化后严格驻留在受控暂存根目录之内
    static bool ResolveAndValidateDestination(const std::wstring& stagingRoot, const std::wstring& relPath, std::wstring& outFullPath) {
        if (stagingRoot.empty() || relPath.empty()) return false;

        wchar_t canonRootBuf[MAX_PATH * 2] = {0};
        DWORD rootLen = GetFullPathNameW(stagingRoot.c_str(), _countof(canonRootBuf), canonRootBuf, nullptr);
        if (rootLen == 0 || rootLen >= _countof(canonRootBuf)) return false;
        std::wstring canonRoot(canonRootBuf, rootLen);
        if (canonRoot.back() != L'\\') canonRoot.push_back(L'\\');

        std::wstring clean = relPath;
        std::replace(clean.begin(), clean.end(), L'/', L'\\');

        if (clean.size() >= 2 && iswalpha(clean[0]) && clean[1] == L':') {
            clean = clean.substr(2);
        }
        size_t firstValid = clean.find_first_not_of(L"\\");
        if (firstValid == std::wstring::npos) return false;
        clean = clean.substr(firstValid);

        std::wstring safeRel;
        size_t start = 0;
        while (start < clean.size()) {
            size_t end = clean.find(L'\\', start);
            if (end == std::wstring::npos) end = clean.size();
            std::wstring seg = clean.substr(start, end - start);
            if (seg == L".." || seg.find(L':') != std::wstring::npos) {
                return false;
            }
            if (!seg.empty() && seg != L".") {
                if (!safeRel.empty()) safeRel.push_back(L'\\');
                safeRel += seg;
            }
            start = end + 1;
        }

        if (safeRel.empty()) return false;

        std::wstring targetComb = canonRoot + safeRel;
        wchar_t canonTargetBuf[MAX_PATH * 2] = {0};
        DWORD targetLen = GetFullPathNameW(targetComb.c_str(), _countof(canonTargetBuf), canonTargetBuf, nullptr);
        if (targetLen == 0 || targetLen >= _countof(canonTargetBuf)) return false;
        std::wstring canonTarget(canonTargetBuf, targetLen);

        if (canonTarget.size() < canonRoot.size()) return false;
        if (_wcsnicmp(canonTarget.c_str(), canonRoot.c_str(), canonRoot.size()) != 0) {
            return false;
        }

        outFullPath = canonTarget;
        return true;
    }

    // 把受控根目录解析成“真实最终路径”（打开目录句柄后由内核解析全部重解析点）
    static std::wstring ResolveCanonicalDirectory(const std::wstring& dir) {
        HANDLE h = CreateFileW(dir.c_str(), FILE_READ_ATTRIBUTES | FILE_TRAVERSE,
                               FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                               OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
        if (h == INVALID_HANDLE_VALUE) return L"";
        std::wstring final_ = FinalPathOfHandle(h);
        CloseHandle(h);
        if (final_.empty()) return L"";
        if (final_.back() != L'\\') final_.push_back(L'\\');
        return final_;
    }

    // 校验已打开（或已存在）对象的真实最终路径位于受控根目录之内
    static bool IsUnderCanonicalRoot(const std::wstring& canonicalRoot, const std::wstring& finalPath) {
        if (canonicalRoot.empty() || finalPath.empty()) return false;
        // 归一化末尾分隔符：GetFinalPathNameByHandleW 返回的目录路径通常不带尾反斜杠，
        // 而 ResolveCanonicalDirectory 会补上尾反斜杠，两者必须能在“根目录本身”场景下正确比较。
        std::wstring root = canonicalRoot;
        while (!root.empty() && root.back() == L'\\') root.pop_back();
        std::wstring fp = finalPath;
        while (!fp.empty() && fp.back() == L'\\') fp.pop_back();
        if (root.empty()) return false;
        if (fp.size() < root.size()) return false;
        if (_wcsnicmp(fp.c_str(), root.c_str(), root.size()) != 0) return false;
        // 边界必须是目录分隔符或完全相等，避免 root 与 root2 这类前缀误判
        return fp.size() == root.size() || fp[root.size()] == L'\\';
    }

    // 打开一个对象并取回其真实最终路径（句柄级绑定，杜绝祖先目录被替换/联接点劫持）
    static bool VerifyObjectUnderRoot(HANDLE hObject, const std::wstring& canonicalRoot) {
        if (hObject == INVALID_HANDLE_VALUE) return false;
        std::wstring finalPath = FinalPathOfHandle(hObject);
        if (finalPath.empty()) return false;
        return IsUnderCanonicalRoot(canonicalRoot, finalPath);
    }

    // 每次仅将单一名称交给 NtCreateFile，RootDirectory 为已验证父句柄。
    // 不解析中间路径；保留整个目录链，拒绝任何一级重解析点。
    static bool EnsureDirectory(const std::wstring& canonicalRoot, const std::wstring& fullPath, DWORD& outError) {
        outError = ERROR_SUCCESS;
        std::wstring root = StripExtendedPrefix(canonicalRoot);
        while (!root.empty() && root.back() == L'\\') root.pop_back();
        std::wstring target = StripExtendedPrefix(fullPath);
        while (!target.empty() && target.back() == L'\\') target.pop_back();
        if (!IsUnderCanonicalRoot(root, target)) {
            outError = ERROR_ACCESS_DENIED;
            return false;
        }
        struct Handles {
            std::vector<HANDLE> values;
            ~Handles() { for (HANDLE h : values) CloseHandle(h); }
        } handles;
        HANDLE hRoot = CreateFileW(root.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                   nullptr, OPEN_EXISTING,
                                   FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (hRoot == INVALID_HANDLE_VALUE) { outError = GetLastError(); return false; }
        handles.values.push_back(hRoot);
        if (HasReparsePointAttribute(hRoot) || _wcsicmp(FinalPathOfHandle(hRoot).c_str(), root.c_str()) != 0) {
            outError = ERROR_ACCESS_DENIED;
            return false;
        }
        using NtCreate = NTSTATUS (NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK,
                                           PLARGE_INTEGER, ULONG, ULONG, ULONG, ULONG, PVOID, ULONG);
        using NtError = ULONG (WINAPI*)(NTSTATUS);
        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        auto create = reinterpret_cast<NtCreate>(GetProcAddress(ntdll, "NtCreateFile"));
        auto error = reinterpret_cast<NtError>(GetProcAddress(ntdll, "RtlNtStatusToDosError"));
        if (!create || !error) { outError = ERROR_PROC_NOT_FOUND; return false; }
        size_t start = root.size() + 1;
        while (start < target.size()) {
            size_t end = target.find(L'\\', start);
            if (end == std::wstring::npos) end = target.size();
            std::wstring name = target.substr(start, end - start);
            if (name.empty() || name == L"." || name == L".." || name.find_first_of(L":/") != std::wstring::npos ||
                name.size() > 32767) {
                outError = ERROR_ACCESS_DENIED;
                return false;
            }
            UNICODE_STRING unicode{};
            unicode.Buffer = name.data();
            unicode.Length = static_cast<USHORT>(name.size() * sizeof(wchar_t));
            unicode.MaximumLength = unicode.Length;
            OBJECT_ATTRIBUTES attrs{};
            attrs.Length = sizeof(attrs);
            attrs.RootDirectory = handles.values.back();
            attrs.ObjectName = &unicode;
            attrs.Attributes = 0x40; // OBJ_CASE_INSENSITIVE
            IO_STATUS_BLOCK io{};
            HANDLE child = INVALID_HANDLE_VALUE;
            NTSTATUS status = create(&child, FILE_LIST_DIRECTORY | FILE_TRAVERSE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
                &attrs, &io, nullptr, FILE_ATTRIBUTE_DIRECTORY, FILE_SHARE_READ | FILE_SHARE_WRITE,
                3 /* FILE_OPEN_IF */, 0x1 /* FILE_DIRECTORY_FILE */ | 0x20 /* FILE_SYNCHRONOUS_IO_NONALERT */ |
                0x00200000 /* FILE_OPEN_REPARSE_POINT */, nullptr, 0);
            if (status < 0) { outError = error(status); return false; }
            handles.values.push_back(child);
            if (HasReparsePointAttribute(child) || !VerifyObjectUnderRoot(child, root)) {
                outError = ERROR_ACCESS_DENIED;
                return false;
            }
            start = end + 1;
        }
        return true;
    }

    // 批量重命名清洗（D6 默认策略：非法字符替换为 '-'）
    static std::wstring SanitizeSegment(const std::wstring& seg) {
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

        int lastNonDotOrSpace = (int)res.size() - 1;
        while (lastNonDotOrSpace >= 0 && (res[lastNonDotOrSpace] == L' ' || res[lastNonDotOrSpace] == L'.')) {
            lastNonDotOrSpace--;
        }
        for (size_t k = lastNonDotOrSpace + 1; k < res.size(); ++k) {
            res[k] = L'-';
        }

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
            if (seg == L"..") seg = L"__";
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

    // 去掉 \\?\ 前缀，便于比较
    static std::wstring StripExtendedPrefix(const std::wstring& path) {
        if (path.rfind(L"\\\\?\\UNC\\", 0) == 0) {
            return L"\\\\" + path.substr(8);
        }
        if (path.rfind(L"\\\\?\\", 0) == 0) {
            return path.substr(4);
        }
        return path;
    }

private:
    // 通过句柄读取真实属性；无法读取时按“存在重解析点”处理（fail-closed）。
    // 打开时若带 FILE_FLAG_OPEN_REPARSE_POINT，句柄绑定到联接点/符号链接对象本身，
    // 从而能在此处识别出重解析点，而不是被内核静默解引用到目标目录。
    static bool HasReparsePointAttribute(HANDLE h) {
        BY_HANDLE_FILE_INFORMATION info{};
        if (!GetFileInformationByHandle(h, &info)) return true;
        return (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    }

    static std::wstring FinalPathOfHandle(HANDLE h) {
        std::wstring buffer(MAX_PATH * 4, L'\0');
        for (int attempt = 0; attempt < 4; ++attempt) {
            DWORD len = GetFinalPathNameByHandleW(h, &buffer[0], (DWORD)buffer.size(), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
            if (len == 0) return L"";
            if (len < buffer.size()) {
                buffer.resize(len);
                return StripExtendedPrefix(buffer);
            }
            buffer.resize(len + 1);
        }
        return L"";
    }
};
