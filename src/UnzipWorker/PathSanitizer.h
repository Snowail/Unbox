#pragma once

#include <windows.h>
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

    // 创建目录（逐级），并对每一级（含已存在目录）做句柄级归属验证。
    // 关键：已有目录也必须验证真实最终路径；创建每一级之前先确认父目录真实对象在根内。
    // 为避免“验证后父目录被替换成指向根外的联接点”这一 TOCTOU 窗口：
    //   - 验证与创建都不再授予 FILE_SHARE_DELETE，持有句柄期间父目录无法被改名/替换；
    //   - 父目录句柄在整个创建过程中保持打开；
    //   - 创建后若句柄级归属验证失败，通过句柄删除刚创建的空目录，绝不留下根外副作用。
    static bool EnsureDirectory(const std::wstring& canonicalRoot, const std::wstring& fullPath, DWORD& outError) {
        outError = ERROR_SUCCESS;
        std::error_code ec;

        // 1. 最终对象已存在：直接做句柄级验证（这是本轮修复的关键，不再跳过已有目录）
        if (fs::exists(fullPath, ec)) {
            if (!fs::is_directory(fullPath, ec)) {
                outError = ERROR_DIRECTORY;
                return false;
            }
            return VerifyDirectoryUnderRoot(canonicalRoot, fullPath, outError);
        }

        // 2. 找到第一个已存在的祖先
        fs::path p(fullPath);
        std::vector<fs::path> missing;
        fs::path cur = p;
        while (!cur.empty() && !fs::exists(cur, ec)) {
            missing.push_back(cur);
            fs::path parent = cur.parent_path();
            if (parent == cur) break;
            cur = parent;
        }

        // 3. 打开首个已存在祖先的句柄（不授予 FILE_SHARE_DELETE，杜绝验证后父目录被替换），
        //    并验证其真实最终路径位于受控根内；该句柄在后续创建过程中一直保持打开。
        HANDLE hParent = INVALID_HANDLE_VALUE;
        if (!cur.empty() && fs::exists(cur, ec)) {
            if (!OpenVerifiedDirectoryNoDelete(canonicalRoot, cur.wstring(), hParent, outError)) {
                return false;
            }
        }

        // 4. 逐级创建：持有父目录句柄（父目录不可被替换），创建后立即打开子目录句柄验证归属；
        //    验证失败且本层由本次调用创建时，通过句柄删除刚创建的空目录，绝不留下根外副作用。
        for (auto it = missing.rbegin(); it != missing.rend(); ++it) {
            bool created = false;
            if (!CreateDirectoryW(it->c_str(), nullptr)) {
                DWORD err = GetLastError();
                if (err != ERROR_ALREADY_EXISTS) {
                    outError = err;
                    if (hParent != INVALID_HANDLE_VALUE) CloseHandle(hParent);
                    return false;
                }
            } else {
                created = true;
            }

            HANDLE hChild = CreateFileW(it->c_str(), FILE_READ_ATTRIBUTES | FILE_TRAVERSE | DELETE,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                                        FILE_FLAG_BACKUP_SEMANTICS, nullptr);
            if (hChild == INVALID_HANDLE_VALUE) {
                outError = GetLastError();
                if (created) RemoveDirectoryW(it->c_str()); // 打开失败（罕见），按路径清理刚创建的空目录
                if (hParent != INVALID_HANDLE_VALUE) CloseHandle(hParent);
                return false;
            }
            if (!VerifyObjectUnderRoot(hChild, canonicalRoot)) {
                outError = ERROR_ACCESS_DENIED;
                if (created) DeleteEmptyDirectoryByHandle(hChild);
                else CloseHandle(hChild);
                if (hParent != INVALID_HANDLE_VALUE) CloseHandle(hParent);
                return false;
            }

            if (hParent != INVALID_HANDLE_VALUE) CloseHandle(hParent);
            hParent = hChild;
        }

        // 5. 最后一层已打开并验证的句柄就是最终目录本身，直接关闭即可
        if (hParent == INVALID_HANDLE_VALUE) {
            return VerifyDirectoryUnderRoot(canonicalRoot, fullPath, outError);
        }
        CloseHandle(hParent);
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
    // 打开目录句柄（不授予 FILE_SHARE_DELETE），验证其真实最终路径位于受控根内；成功时通过 hOut 返回句柄。
    static bool OpenVerifiedDirectoryNoDelete(const std::wstring& canonicalRoot, const std::wstring& dirPath,
                                              HANDLE& hOut, DWORD& outError) {
        hOut = CreateFileW(dirPath.c_str(), FILE_READ_ATTRIBUTES | FILE_TRAVERSE | DELETE,
                           FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                           FILE_FLAG_BACKUP_SEMANTICS, nullptr);
        if (hOut == INVALID_HANDLE_VALUE) {
            outError = GetLastError();
            return false;
        }
        if (!VerifyObjectUnderRoot(hOut, canonicalRoot)) {
            CloseHandle(hOut);
            hOut = INVALID_HANDLE_VALUE;
            outError = ERROR_ACCESS_DENIED;
            return false;
        }
        return true;
    }

    // 通过句柄删除刚创建的空目录（不按路径重解析，避免再次被联接点劫持）
    static void DeleteEmptyDirectoryByHandle(HANDLE h) {
        if (h == INVALID_HANDLE_VALUE) return;
        FILE_DISPOSITION_INFO disp{};
        disp.DeleteFile = TRUE;
        SetFileInformationByHandle(h, FileDispositionInfo, &disp, sizeof(disp));
        CloseHandle(h);
    }

    // 打开目录句柄并验证其真实最终路径位于受控根目录之内（打开时不解引用重解析点前先取其真实去向）
    static bool VerifyDirectoryUnderRoot(const std::wstring& canonicalRoot, const std::wstring& dirPath, DWORD& outError) {
        HANDLE h = CreateFileW(dirPath.c_str(), FILE_READ_ATTRIBUTES | FILE_TRAVERSE,
                               FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                               OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
        if (h == INVALID_HANDLE_VALUE) {
            outError = GetLastError();
            return false;
        }
        bool ok = VerifyObjectUnderRoot(h, canonicalRoot);
        CloseHandle(h);
        if (!ok) {
            outError = ERROR_ACCESS_DENIED;
        }
        return ok;
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
