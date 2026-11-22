#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <io.h>
#include <fcntl.h>
#include <iostream>
#include <fstream>
#include <string>
#include <vector>
#include <unordered_map>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <atomic>
#include <sstream>
#include <filesystem>
#include <chrono>
#include <algorithm>
#include <cstdlib>
#include <cstdio>
#include <cerrno>
#include <limits>
#include "Lz4FrameSize.h"

#include "SevenZip.h"
#include "SevenZipLibrary.h"
#include "FileStream.h"
#include "PathSanitizer.h"
#include "ArchiveNames.h"
#include "NameDecoder.h"
#include "WorkerOpenCallback.h"
#include "WorkerExtractCallback.h"

namespace fs = std::filesystem;

// ---------------------------------------------------------------------------
// 基础工具
// ---------------------------------------------------------------------------

std::string WideToUtf8(const std::wstring& wstr) {
    if (wstr.empty()) return "";
    int len = WideCharToMultiByte(CP_UTF8, 0, wstr.data(), (int)wstr.size(), nullptr, 0, nullptr, nullptr);
    std::string str(len, 0);
    WideCharToMultiByte(CP_UTF8, 0, wstr.data(), (int)wstr.size(), &str[0], len, nullptr, nullptr);
    return str;
}

std::wstring Utf8ToWide(const std::string& str) {
    if (str.empty()) return L"";
    int len = MultiByteToWideChar(CP_UTF8, 0, str.data(), (int)str.size(), nullptr, 0);
    std::wstring wstr(len, 0);
    MultiByteToWideChar(CP_UTF8, 0, str.data(), (int)str.size(), &wstr[0], len);
    return wstr;
}

// 严谨的 JSON 字符串转义
std::string EscapeJsonString(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 16);
    for (char c : s) {
        if (c == '"') out += "\\\"";
        else if (c == '\\') out += "\\\\";
        else if (c == '\b') out += "\\b";
        else if (c == '\f') out += "\\f";
        else if (c == '\n') out += "\\n";
        else if (c == '\r') out += "\\r";
        else if (c == '\t') out += "\\t";
        else if (static_cast<unsigned char>(c) < 32) {
            char buf[8];
            snprintf(buf, sizeof(buf), "\\u%04x", (unsigned char)c);
            out += buf;
        } else {
            out.push_back(c);
        }
    }
    return out;
}

// 严谨的 JSON 字符串字段提取与反转义（完整支持 Unicode 代理对）
std::string ExtractJsonStringField(const std::string& json, const std::string& fieldName) {
    std::string key = "\"" + fieldName + "\":\"";
    size_t pos = json.find(key);
    if (pos == std::string::npos) return "";
    pos += key.length();

    std::string res;
    res.reserve(64);
    bool escaped = false;
    for (size_t i = pos; i < json.length(); ++i) {
        char c = json[i];
        if (escaped) {
            if (c == '"') res.push_back('"');
            else if (c == '\\') res.push_back('\\');
            else if (c == '/') res.push_back('/');
            else if (c == 'b') res.push_back('\b');
            else if (c == 'f') res.push_back('\f');
            else if (c == 'n') res.push_back('\n');
            else if (c == 'r') res.push_back('\r');
            else if (c == 't') res.push_back('\t');
            else if (c == 'u' && i + 4 < json.length()) {
                std::string hexStr = json.substr(i + 1, 4);
                try {
                    uint32_t codePoint = std::stoul(hexStr, nullptr, 16);
                    i += 4;
                    if (codePoint >= 0xD800 && codePoint <= 0xDBFF && i + 6 < json.length() && json[i + 1] == '\\' && json[i + 2] == 'u') {
                        std::string hexLow = json.substr(i + 3, 4);
                        uint32_t lowPoint = std::stoul(hexLow, nullptr, 16);
                        if (lowPoint >= 0xDC00 && lowPoint <= 0xDFFF) {
                            codePoint = 0x10000 + ((codePoint - 0xD800) << 10) + (lowPoint - 0xDC00);
                            i += 6;
                        }
                    }

                    if (codePoint < 0x80) {
                        res.push_back((char)codePoint);
                    } else if (codePoint < 0x800) {
                        res.push_back((char)(0xC0 | (codePoint >> 6)));
                        res.push_back((char)(0x80 | (codePoint & 0x3F)));
                    } else if (codePoint < 0x10000) {
                        res.push_back((char)(0xE0 | (codePoint >> 12)));
                        res.push_back((char)(0x80 | ((codePoint >> 6) & 0x3F)));
                        res.push_back((char)(0x80 | (codePoint & 0x3F)));
                    } else {
                        res.push_back((char)(0xF0 | (codePoint >> 18)));
                        res.push_back((char)(0x80 | ((codePoint >> 12) & 0x3F)));
                        res.push_back((char)(0x80 | ((codePoint >> 6) & 0x3F)));
                        res.push_back((char)(0x80 | (codePoint & 0x3F)));
                    }
                } catch (...) {}
            } else {
                res.push_back(c);
            }
            escaped = false;
        } else {
            if (c == '\\') {
                escaped = true;
            } else if (c == '"') {
                break;
            } else {
                res.push_back(c);
            }
        }
    }
    return res;
}

bool HasCompoundTarSuffix(const std::wstring& path, std::wstring* outBaseName = nullptr) {
    auto route = ClassifyArchiveName(fs::path(path).filename().wstring());
    if (route.kind != ArchiveKind::TarWrapper) return false;
    if (outBaseName) *outBaseName = std::move(route.baseName);
    return true;
}

std::wstring DeriveSingleStreamFilename(const std::wstring& archivePath) {
    const auto leaf = fs::path(archivePath).filename().wstring();
    return DefaultStreamName(ClassifyArchiveName(leaf), leaf);
}

uint64_t DefaultBudget(uint64_t knownBytes, uint64_t minimum) {
    const uint64_t slack = (std::max)(knownBytes / 4, uint64_t{64} * 1024 * 1024);
    const auto maxBytes = (std::numeric_limits<uint64_t>::max)();
    return (std::max)(minimum, slack > maxBytes - knownBytes ? maxBytes : knownBytes + slack);
}

bool HasDiskSpace(const std::wstring& directory, uint64_t minimum) {
    if (minimum == 0) return true;
    ULARGE_INTEGER available{};
    return GetDiskFreeSpaceExW(directory.c_str(), &available, nullptr, nullptr) &&
        available.QuadPart >= minimum;
}

bool IsDiskFull(HRESULT result, DWORD writeError) {
    return writeError == ERROR_DISK_FULL || writeError == ERROR_HANDLE_DISK_FULL ||
        result == HRESULT_FROM_WIN32(ERROR_DISK_FULL) || result == HRESULT_FROM_WIN32(ERROR_HANDLE_DISK_FULL);
}

bool ParseBudget(std::wstring_view value, uint64_t& result) {
    if (value.empty()) return false;
    result = 0;
    for (wchar_t ch : value) {
        if (ch < L'0' || ch > L'9') return false;
        const uint64_t digit = ch - L'0';
        if (result > ((std::numeric_limits<uint64_t>::max)() - digit) / 10) return false;
        result = result * 10 + digit;
    }
    return result != 0;
}

// ---------------------------------------------------------------------------
// IPC
// ---------------------------------------------------------------------------

std::mutex g_coutMutex;
void SendIpcMessage(const std::string& json) {
    std::lock_guard<std::mutex> lock(g_coutMutex);
    std::cout << json << "\n";
    std::cout.flush();
}

std::atomic<bool> g_isPaused(false);
std::atomic<bool> g_isCanceled(false);
std::mutex g_pauseMutex;
std::condition_variable g_pauseCv;

std::mutex g_inputMutex;
std::condition_variable g_inputCv;

bool g_passwordRequestPending = false;
bool g_hasPasswordResponse = false;
std::wstring g_receivedPassword;

bool g_hasInvalidCharsResponse = false;
std::string g_invalidCharsAction; // "batch_rename", "rename_individual", "skip"
std::unordered_map<uint32_t, std::wstring> g_customNames;

bool g_hasEncodingResponse = false;
uint32_t g_chosenCodePage = 0;

void CancelExtraction() {
    {
        std::scoped_lock lock(g_pauseMutex, g_inputMutex);
        g_isCanceled = true;
        g_isPaused = false;
    }
    g_pauseCv.notify_all();
    g_inputCv.notify_all();
}

void ProcessIpcInputLine(const std::string& line) {
    if (line.find("\"type\":\"pause\"") != std::string::npos) {
        g_isPaused = true;
        SendIpcMessage("{\"type\":\"paused_ack\"}");
    } else if (line.find("\"type\":\"resume\"") != std::string::npos) {
        {
            std::lock_guard<std::mutex> lock(g_pauseMutex);
            g_isPaused = false;
        }
        g_pauseCv.notify_all();
        SendIpcMessage("{\"type\":\"resumed_ack\"}");
    } else if (line.find("\"type\":\"cancel\"") != std::string::npos) {
        CancelExtraction();
    } else if (line.find("\"type\":\"password_response\"") != std::string::npos) {
        if (line.find("\"canceled\":true") != std::string::npos) {
            CancelExtraction();
            return;
        }
        std::string pwd = ExtractJsonStringField(line, "password");
        {
            std::lock_guard<std::mutex> lock(g_inputMutex);
            if (!g_passwordRequestPending || g_hasPasswordResponse) return;
            g_receivedPassword = Utf8ToWide(pwd);
            SecureZeroMemory(pwd.data(), pwd.size());
            g_hasPasswordResponse = true;
        }
        g_inputCv.notify_all();
    } else if (line.find("\"type\":\"invalid_chars_response\"") != std::string::npos) {
        std::lock_guard<std::mutex> lock(g_inputMutex);
        if (line.find("\"action\":\"batch_rename\"") != std::string::npos) {
            g_invalidCharsAction = "batch_rename";
        } else if (line.find("\"action\":\"skip\"") != std::string::npos) {
            g_invalidCharsAction = "skip";
        } else {
            g_invalidCharsAction = "rename_individual";
        }
        g_hasInvalidCharsResponse = true;
        g_inputCv.notify_all();
    } else if (line.find("\"type\":\"encoding_response\"") != std::string::npos) {
        uint32_t cp = 0;
        size_t pos = line.find("\"cp\":");
        if (pos != std::string::npos) {
            cp = (uint32_t)_wtoi(Utf8ToWide(line.substr(pos + 5, 12)).c_str());
        }
        std::lock_guard<std::mutex> lock(g_inputMutex);
        g_chosenCodePage = cp;
        g_hasEncodingResponse = true;
        g_inputCv.notify_all();
    }
}

void StartIpcListenerThread(bool managed) {
    std::thread([managed]() {
        std::string line;
        while (std::getline(std::cin, line)) {
            if (!line.empty()) {
                ProcessIpcInputLine(line);
                SecureZeroMemory(line.data(), line.size());
            }
        }
        if (managed) CancelExtraction();
    }).detach();
}

bool CheckPauseOrCancel() {
    if (g_isCanceled) return false;
    if (g_isPaused) {
        std::unique_lock<std::mutex> lock(g_pauseMutex);
        g_pauseCv.wait(lock, []() { return !g_isPaused || g_isCanceled; });
        if (g_isCanceled) return false;
    }
    return true;
}

// One credential per archive attempt, shared by the open and extract callbacks.
class ArchivePassword {
public:
    ~ArchivePassword() { SecureZeroMemory(m_password.data(), m_password.size() * sizeof(wchar_t)); }
    bool Requested() const { return m_requested; }

    std::wstring Get(bool* outCanceled) {
        std::lock_guard<std::mutex> passwordLock(m_mutex);
        if (!g_isCanceled && !m_hasPassword) {
            std::unique_lock<std::mutex> inputLock(g_inputMutex);
            if (!g_isCanceled) {
                m_requested = true;
                g_passwordRequestPending = true;
                g_hasPasswordResponse = false;
                SendIpcMessage("{\"type\":\"password_request\"}");
                g_inputCv.wait(inputLock, []() { return g_hasPasswordResponse || g_isCanceled; });
                g_passwordRequestPending = false;
                if (!g_isCanceled) {
                    m_password.swap(g_receivedPassword);
                    m_hasPassword = true;
                }
                SecureZeroMemory(g_receivedPassword.data(), g_receivedPassword.size() * sizeof(wchar_t));
                g_receivedPassword.clear();
            }
        }
        *outCanceled = g_isCanceled;
        return *outCanceled ? L"" : m_password;
    }

private:
    std::mutex m_mutex;
    bool m_hasPassword = false;
    std::atomic<bool> m_requested{false};
    std::wstring m_password;
};

// ---------------------------------------------------------------------------
// 条目计划（解码后的名称、过滤与跳过决策）
// ---------------------------------------------------------------------------

struct ItemPlan {
    std::vector<std::wstring> targetPaths;
    std::vector<bool> toExtract;
    std::vector<uint32_t> illegalItems;
    uint32_t policyFiltered = 0;   // 按产品规则过滤（macOS 辅助文件）
    uint32_t declaredUnicode = 0;  // 格式已声明 Unicode 的名称数量
    uint32_t legacyNames = 0;      // 缺少编码声明的非 ASCII 名称数量
    uint32_t undecodableNames = 0; // 无法可靠解码的名称数量
    bool allSizesKnown = true;
    uint64_t declaredKnownBytes = 0;
    uint32_t unsafeItems = 0;
};

// 构建条目计划：名称来源优先使用归档内的原始名称字节，其次才使用引擎解码结果。
ItemPlan BuildItemPlan(IInArchive* archive,
                       uint32_t numItems,
                       const std::wstring& archivePath,
                       uint32_t codePage,
                       bool allowAutoSanitize,
                       bool strictCodePage = false) {
    ItemPlan plan;
    plan.targetPaths.assign(numItems, L"");
    plan.toExtract.assign(numItems, false);

    std::vector<ArchiveNames::RawEntry> rawNames;
    bool hasRaw = false;

    // 1. 读取原始名称字节（ZIP 中央目录 / TAR 头部）
    if (ArchiveNames::LooksLikeZip(archivePath)) {
        hasRaw = ArchiveNames::ReadZipNames(archivePath, rawNames);
    } else if (ArchiveNames::LooksLikeTar(archivePath)) {
        hasRaw = ArchiveNames::ReadTarNames(archivePath, rawNames);
    }
    if (hasRaw && rawNames.size() != numItems) {
        // 条目数与 7-Zip 报告不一致：放弃原始名称，避免索引错配
        hasRaw = false;
        rawNames.clear();
    }

    for (uint32_t i = 0; i < numItems; ++i) {
        PROPVARIANT propPath;
        PropVariantInit(&propPath);
        std::wstring engineName;
        if (SUCCEEDED(archive->GetProperty(i, kpidPath, &propPath)) && propPath.vt == VT_BSTR && propPath.bstrVal) {
            engineName.assign(propPath.bstrVal, SysStringLen(propPath.bstrVal));
        }
        PropVariantClear(&propPath);

        std::wstring itemPath;
        if (hasRaw && !rawNames[i].raw.empty()) {
            NameDecoder::Decoded decoded = NameDecoder::DecodeOne(rawNames[i].raw, rawNames[i].declaredUtf8, codePage, strictCodePage);
            itemPath = decoded.name;
            if (rawNames[i].declaredUtf8) plan.declaredUnicode++;
            else {
                bool hasNonAscii = false;
                NameDecoder::IsValidUtf8(rawNames[i].raw, hasNonAscii);
                if (hasNonAscii) plan.legacyNames++;
            }
            if (decoded.ambiguous || decoded.undecodable) plan.undecodableNames++;
        } else {
            // 无法获取原始字节：直接尊重引擎给出的名称，绝不再做启发式改写
            itemPath = engineName;
        }

        if (itemPath.empty() && numItems == 1)
            itemPath = DeriveSingleStreamFilename(archivePath);

        const auto hasProperty = [&](PROPID property) {
            PROPVARIANT value{};
            const bool present = archive->GetProperty(i, property, &value) == S_OK &&
                ((value.vt == VT_BSTR && SysStringByteLen(value.bstrVal) != 0) ||
                 (value.vt == VT_BOOL && value.boolVal == VARIANT_TRUE));
            PropVariantClear(&value);
            return present;
        };
        if (!PathSanitizer::IsSafeRelativeSyntax(itemPath) || hasProperty(kpidSymLink) ||
            hasProperty(kpidNtReparse) || hasProperty(kpidHardLink) ||
            hasProperty(kpidCopyLink) || hasProperty(kpidIsAltStream)) {
            ++plan.unsafeItems;
            continue;
        }
        itemPath = PathSanitizer::SanitizePathSeparators(itemPath);

        PROPVARIANT propSize;
        PropVariantInit(&propSize);
        uint64_t itemSize = 0;
        if (SUCCEEDED(archive->GetProperty(i, kpidSize, &propSize))) {
            if (propSize.vt == VT_UI8) itemSize = propSize.uhVal.QuadPart;
            else if (propSize.vt == VT_UI4) itemSize = propSize.ulVal;
        }
        const bool itemSizeKnown = propSize.vt == VT_UI8 || propSize.vt == VT_UI4;
        PropVariantClear(&propSize);

        PROPVARIANT propIsDir;
        PropVariantInit(&propIsDir);
        bool isDir = false;
        if (SUCCEEDED(archive->GetProperty(i, kpidIsDir, &propIsDir))) {
            if (propIsDir.vt == VT_BOOL && propIsDir.boolVal == VARIANT_TRUE) isDir = true;
        }
        PropVariantClear(&propIsDir);

        // D5：macOS 辅助文件按产品规则过滤（不是“跳过”，也不代表内容缺失）
        if (PathSanitizer::IsMacAuxiliary(itemPath)) {
            plan.policyFiltered++;
            plan.targetPaths[i] = itemPath;
            plan.toExtract[i] = false;
            continue;
        }

        if (PathSanitizer::HasIllegalChars(itemPath)) {
            plan.illegalItems.push_back(i);
        }

        plan.targetPaths[i] = itemPath;
        plan.toExtract[i] = true;
        if (!isDir) {
            const auto maxBytes = (std::numeric_limits<uint64_t>::max)();
            if (!itemSizeKnown || itemSize > maxBytes - plan.declaredKnownBytes)
                plan.allSizesKnown = false;
            else
                plan.declaredKnownBytes += itemSize;
        }
    }

    (void)allowAutoSanitize;
    return plan;
}

// 等待 Proxy 选择文件名编码
bool RequestEncodingChoice(uint32_t preferredCp, const ItemPlan& plan,
                           const std::wstring& archivePath, uint32_t& outCp) {
    std::vector<uint32_t> options = NameDecoder::CandidateCodePages(preferredCp, false);

    std::vector<ArchiveNames::RawEntry> rawNames;
    bool hasRaw = false;
    if (ArchiveNames::LooksLikeZip(archivePath)) hasRaw = ArchiveNames::ReadZipNames(archivePath, rawNames);
    else if (ArchiveNames::LooksLikeTar(archivePath)) hasRaw = ArchiveNames::ReadTarNames(archivePath, rawNames);
    (void)plan;

    std::stringstream ss;
    ss << "{\"type\":\"encoding_options\",\"count\":" << options.size() << ",\"options\":[";
    bool firstOption = true;
    for (uint32_t cp : options) {
        if (!firstOption) ss << ",";
        firstOption = false;
        ss << "{\"cp\":" << cp << ",\"label\":\"" << EscapeJsonString(NameDecoder::CodePageLabel(cp)) << "\",\"samples\":[";
        if (hasRaw) {
            int shown = 0;
            for (size_t k = 0; k < rawNames.size() && shown < 3; ++k) {
                if (rawNames[k].declaredUtf8 || rawNames[k].raw.empty()) continue;
                bool nonAscii = false;
                if (!NameDecoder::IsValidUtf8(rawNames[k].raw, nonAscii) || !nonAscii) continue;
                std::wstring sample = NameDecoder::DecodeBytes(rawNames[k].raw, cp);
                if (sample.empty()) continue;
                if (shown > 0) ss << ",";
                ss << "\"" << EscapeJsonString(WideToUtf8(sample)) << "\"";
                shown++;
            }
        }
        ss << "]}";
    }
    ss << "]}";

    {
        std::lock_guard<std::mutex> lock(g_inputMutex);
        g_hasEncodingResponse = false;
        g_chosenCodePage = 0;
    }
    SendIpcMessage(ss.str());

    std::unique_lock<std::mutex> lock(g_inputMutex);
    bool answered = g_inputCv.wait_for(lock, std::chrono::minutes(10),
        []() { return g_hasEncodingResponse || g_isCanceled; });
    if (!answered || g_isCanceled) return false;
    outCp = g_chosenCodePage;
    return true;
}

// ---------------------------------------------------------------------------
// 内层展开（复合 TAR / 分卷拼接载荷）
// ---------------------------------------------------------------------------

struct InnerExtractResult {
    enum class Kind { NotApplicable, Success, Failed, Canceled };
    Kind kind = Kind::NotApplicable;
    std::wstring deliveryRootName;  // 相对 staging 的隔离交付根目录名
    std::string errorReason;
    std::vector<DeliveredItemInfo> items;
    uint64_t limitBytes = 0;
    uint64_t writtenBytes = 0;
    bool sizeKnown = true;
    uint32_t totalItems = 0;
    uint32_t policyFiltered = 0;
    uint32_t skipped = 0;
    uint32_t unsafeItems = 0;
    uint32_t missingItems = 0;
    uint64_t completedBytes = 0;
};

std::function<void(const std::wstring&, uint64_t, uint64_t)> MakeProgressCallback(const char* phase, bool sizeKnown) {
    return [phase, sizeKnown](const std::wstring& relPath, uint64_t completedBytes, uint64_t totalBytes) {
        static std::mutex s_mutex;
        static auto s_last = std::chrono::steady_clock::time_point{};
        std::lock_guard<std::mutex> lock(s_mutex);
        auto now = std::chrono::steady_clock::now();
        auto elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(now - s_last).count();
        if (elapsedMs >= 30 || (sizeKnown && totalBytes > 0 && completedBytes == totalBytes)) {
            s_last = now;
            std::stringstream ss;
            ss << "{\"type\":\"progress\",\"phase\":\"" << phase
               << "\",\"current_file\":\"" << EscapeJsonString(WideToUtf8(relPath))
               << "\",\"completed_bytes\":" << completedBytes
               << ",\"total_bytes\":" << totalBytes << ",\"size_known\":" << (sizeKnown ? "true" : "false") << "}";
            SendIpcMessage(ss.str());
        }
    };
}

InnerExtractResult ExpandInnerArchive(SevenZipLibrary& lib7z,
                                      const std::wstring& stagingDir,
                                      const std::wstring& payloadPath,
                                      uint32_t codePage,
                                      bool requireTar,
                                      bool strictCodePage, uint64_t explicitBudget) {
    InnerExtractResult result;
    std::error_code ec;

    bool isTar = ArchiveNames::LooksLikeTar(payloadPath);
    bool isZip = ArchiveNames::LooksLikeZip(payloadPath);
    std::wstring ext = fs::path(payloadPath).extension().wstring();
    for (auto& c : ext) c = towlower(c);

    if (requireTar && !isTar) {
        result.kind = InnerExtractResult::Kind::Failed;
        result.errorReason = "inner_not_tar";
        return result;
    }
    if (!isTar && !isZip) {
        // 按扩展名再给一次机会（例如拼接出的 .7z / .rar 载荷）
        bool knownExt = (ext == L".7z" || ext == L".rar" || ext == L".zip" || ext == L".tar" || ext == L".iso");
        if (!knownExt) {
            result.kind = InnerExtractResult::Kind::NotApplicable;
            return result;
        }
    }

    CInFileStream* innerStream = CInFileStream::OpenFile(payloadPath.c_str());
    if (!innerStream) {
        result.kind = InnerExtractResult::Kind::Failed;
        result.errorReason = "cannot_open_inner_stream";
        return result;
    }

    ArchivePassword innerPassword;
    std::function<std::wstring(bool*)> requestPassword;
    if (!requireTar) requestPassword = [&innerPassword](bool* outCanceled) { return innerPassword.Get(outCanceled); };
    WorkerOpenCallback* innerOpenCallback = new WorkerOpenCallback(payloadPath, requestPassword);

    IInArchive* innerArchive = nullptr;
    const std::vector<std::wstring> hints = requireTar ? std::vector<std::wstring>{L"tar"} : std::vector<std::wstring>{};
    if (!lib7z.DetectAndCreateArchive(innerStream, ext, &innerArchive, innerOpenCallback, codePage,
        hints, requireTar, [&] { return g_isCanceled || innerPassword.Requested(); }) || !innerArchive) {
        innerOpenCallback->Release();
        innerStream->Release();
        result.kind = g_isCanceled ? InnerExtractResult::Kind::Canceled : InnerExtractResult::Kind::Failed;
        result.errorReason = "unrecognized_or_corrupted_inner_archive";
        return result;
    }

    uint32_t innerNumItems = 0;
    if (FAILED(innerArchive->GetNumberOfItems(&innerNumItems))) {
        innerArchive->Close();
        innerArchive->Release();
        innerOpenCallback->Release();
        innerStream->Release();
        result.kind = InnerExtractResult::Kind::Failed;
        result.errorReason = "corrupted_inner_archive";
        return result;
    }

    // 隔离的同卷交付根：只有内层完全成功后才把这一级目录整体提交
    std::wstring deliverName = L".unbox_deliver_" + std::to_wstring(GetCurrentProcessId()) + L"_" +
                               std::to_wstring(std::chrono::steady_clock::now().time_since_epoch().count());
    fs::path deliverDir = fs::path(stagingDir) / deliverName;
    if (!fs::create_directory(deliverDir, ec)) {
        innerArchive->Close();
        innerArchive->Release();
        innerOpenCallback->Release();
        innerStream->Release();
        result.kind = InnerExtractResult::Kind::Failed;
        result.errorReason = "cannot_create_inner_delivery_root";
        return result;
    }

    std::wstring canonicalRoot = PathSanitizer::ResolveCanonicalDirectory(deliverDir.wstring());
    if (canonicalRoot.empty()) {
        fs::remove_all(deliverDir, ec);
        innerArchive->Close();
        innerArchive->Release();
        innerOpenCallback->Release();
        innerStream->Release();
        result.kind = InnerExtractResult::Kind::Failed;
        result.errorReason = "cannot_resolve_inner_delivery_root";
        return result;
    }

    ItemPlan plan = BuildItemPlan(innerArchive, innerNumItems, payloadPath, codePage, true, strictCodePage);
    // 内层非法字符直接批量清洗（不再打断用户），并如实计数
    for (uint32_t idx : plan.illegalItems) {
        plan.targetPaths[idx] = PathSanitizer::SanitizeRelativePath(plan.targetPaths[idx]);
    }

    // 内层独立输出预算（以实际声明大小为准，避免与外层共用一份预算造成误判）
    uint64_t innerBudgetLimit = explicitBudget ? explicitBudget :
        DefaultBudget(plan.declaredKnownBytes, 128ull * 1024 * 1024);
    if (!HasDiskSpace(deliverDir.wstring(), (std::max)(plan.declaredKnownBytes, explicitBudget))) {
        innerArchive->Close();
        innerArchive->Release();
        innerOpenCallback->Release();
        innerStream->Release();
        result.kind = InnerExtractResult::Kind::Failed;
        result.errorReason = "insufficient_disk_space";
        return result;
    }
    OutputBudget innerBudget(innerBudgetLimit);

    WorkerExtractCallback* innerCallback = new WorkerExtractCallback(
        innerArchive,
        deliverDir.wstring(),
        canonicalRoot,
        plan.targetPaths,
        plan.toExtract,
        MakeProgressCallback("inner", plan.allSizesKnown),
        requestPassword,
        CheckPauseOrCancel,
        &innerBudget
    );
    innerCallback->SetDeclaredTotal(plan.declaredKnownBytes);

    HRESULT hr = innerArchive->Extract(nullptr, (uint32_t)-1, false, innerCallback);

    const DWORD writeError = innerCallback->GetWriteError();
    if (innerBudget.Exceeded()) result.errorReason = "output_budget_exceeded";
    else if (IsDiskFull(hr, writeError)) result.errorReason = "insufficient_disk_space";
    else if (innerCallback->HasWrongPassword()) result.errorReason = "wrong_password";
    else if (innerCallback->HasCrcError()) result.errorReason = "crc_error";
    else if (innerCallback->HasDataError()) result.errorReason = "data_error";
    else if (innerCallback->HasUnsupportedMethod()) result.errorReason = "unsupported_method";
    else if (writeError != ERROR_SUCCESS || innerCallback->HasError()) result.errorReason = "extract_error";
    else if (hr != S_OK) result.errorReason = "corrupted_inner_archive";
    const bool ok = result.errorReason.empty();

    result.totalItems = innerNumItems;
    result.policyFiltered = plan.policyFiltered;
    result.unsafeItems = plan.unsafeItems + innerCallback->GetUnsafeCount();
    result.limitBytes = innerBudget.Limit();
    result.writtenBytes = innerBudget.Used();
    result.sizeKnown = plan.allSizesKnown;
    result.missingItems = innerCallback->CountMissing();
    result.completedBytes = innerCallback->GetCompletedBytes();
    if (ok) {
        result.items = innerCallback->GetDeliveredItems();
    }

    // 先释放全部 COM 实例与文件句柄，再做文件系统操作
    innerCallback->Release();
    innerArchive->Close();
    innerArchive->Release();
    innerOpenCallback->Release();
    innerStream->Release();

    if (g_isCanceled) {
        fs::remove_all(deliverDir, ec);
        result.kind = InnerExtractResult::Kind::Canceled;
        return result;
    }

    if (!ok) {
        fs::remove_all(deliverDir, ec);
        result.kind = InnerExtractResult::Kind::Failed;
        return result;
    }

    result.deliveryRootName = deliverName;
    result.kind = InnerExtractResult::Kind::Success;
    return result;
}

// ---------------------------------------------------------------------------
// 交付清单（供 Proxy 逐项对账）
// ---------------------------------------------------------------------------

bool WriteDeliveryManifest(const std::wstring& manifestPath,
                           const std::wstring& deliveryRootName,
                           const std::vector<DeliveredItemInfo>& items) {
    if (manifestPath.empty()) return true;
    FILE* file = nullptr;
    if (_wfopen_s(&file, manifestPath.c_str(), L"wbx") != 0) return false;
    std::unique_ptr<FILE, decltype(&std::fclose)> output(file, &std::fclose);
    const auto write = [&](const std::string& bytes) {
        return std::fwrite(bytes.data(), 1, bytes.size(), output.get()) == bytes.size();
    };

    if (!write("{\"version\":1,\"delivery_root\":\"" + EscapeJsonString(WideToUtf8(deliveryRootName)) +
        "\",\"item_count\":" + std::to_string(items.size()) + ",\"items\":[")) return false;
    bool first = true;
    for (const auto& item : items) {
        std::ostringstream entry;
        if (!first) entry << ",";
        first = false;
        entry << "{\"p\":\"" << EscapeJsonString(WideToUtf8(item.relDiskPath)) << "\""
            << ",\"d\":" << (item.isDir ? 1 : 0)
            << ",\"s\":" << item.size
            << ",\"c\":" << item.crc << "}";
        if (!write(entry.str())) return false;
    }
    if (!write("]}")) return false;
    return std::fclose(output.release()) == 0;
}

// ---------------------------------------------------------------------------
// 主流程
// ---------------------------------------------------------------------------

int wmain(int argc, wchar_t* argv[]) {
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stdin), _O_BINARY);

    std::wstring archivePath;
    std::wstring stagingDir;
    std::wstring targetRootDir;
    std::wstring dllPath;
    std::wstring manifestPath;
    uint32_t userCodePage = 0;
    uint64_t explicitBudget = 0;
    uint64_t explicitInnerBudget = 0;
    bool managed = false;

    for (int i = 1; i < argc; ++i) {
        std::wstring arg = argv[i];
        if (arg == L"--archive" && i + 1 < argc) {
            archivePath = argv[++i];
        } else if (arg == L"--staging" && i + 1 < argc) {
            stagingDir = argv[++i];
        } else if (arg == L"--target-root" && i + 1 < argc) {
            targetRootDir = argv[++i];
        } else if (arg == L"--dll" && i + 1 < argc) {
            dllPath = argv[++i];
        } else if (arg == L"--manifest" && i + 1 < argc) {
            manifestPath = argv[++i];
        } else if (arg == L"--max-output-bytes" || arg == L"--max-inner-output-bytes") {
            auto& limit = arg == L"--max-output-bytes" ? explicitBudget : explicitInnerBudget;
            if (i + 1 >= argc || !ParseBudget(argv[++i], limit)) {
                SendIpcMessage("{\"type\":\"error\",\"message\":\"Invalid output budget\"}");
                return 1;
            }
        } else if (arg == L"--managed") {
            managed = true;
        } else if (arg == L"--cp" && i + 1 < argc) {
            userCodePage = (uint32_t)_wtoi(argv[++i]);
        }
    }
    (void)targetRootDir;

    if (archivePath.empty() || stagingDir.empty()) {
        SendIpcMessage("{\"type\":\"error\",\"message\":\"Missing required arguments\"}");
        return 1;
    }

    if (managed) {
        std::string start;
        if (!std::getline(std::cin, start) || start != "{\"type\":\"start\"}") return 1;
    }
    StartIpcListenerThread(managed);

    const auto stagingRoot = PathSanitizer::ResolveCanonicalDirectory(stagingDir);
    PathSanitizer::PinnedDirectories pinnedStaging;
    DWORD stagingError = ERROR_SUCCESS;
    if (stagingRoot.empty() || !PathSanitizer::EnsureDirectory(
        stagingRoot, stagingRoot, stagingError, &pinnedStaging)) {
        SendIpcMessage("{\"type\":\"error\",\"message\":\"Cannot verify staging directory\"}");
        return 7;
    }

    if (dllPath.empty()) {
        std::wstring exePath(32768, L'\0');
        const DWORD length = GetModuleFileNameW(nullptr, exePath.data(), static_cast<DWORD>(exePath.size()));
        if (length != 0 && length < exePath.size()) {
            exePath.resize(length);
            dllPath = (fs::path(exePath).parent_path() / L"7z.dll").wstring();
        }
    } else {
        std::error_code error;
        const auto absolute = fs::absolute(dllPath, error);
        if (!error) dllPath = absolute.wstring();
    }

    SevenZipLibrary lib7z;
    if (!lib7z.Load(dllPath)) {
        SendIpcMessage("{\"type\":\"error\",\"message\":\"Failed to load 7z.dll at: " + EscapeJsonString(WideToUtf8(dllPath)) + "\"}");
        return 2;
    }

    CInFileStream* inStream = CInFileStream::OpenFile(archivePath.c_str());
    if (!inStream) {
        SendIpcMessage("{\"type\":\"error\",\"message\":\"Cannot open archive file: " + EscapeJsonString(WideToUtf8(archivePath)) + "\"}");
        return 3;
    }

    ArchivePassword archivePassword;
    auto requestPassword = [&archivePassword](bool* outCanceled) { return archivePassword.Get(outCanceled); };
    WorkerOpenCallback* openCallback = new WorkerOpenCallback(archivePath, requestPassword);

    const auto route = ClassifyArchiveName(fs::path(archivePath).filename().wstring());
    const bool outerIsCompoundTar = HasCompoundTarSuffix(archivePath);
    const bool outerIsSplitVolume = route.kind == ArchiveKind::Split;
    std::wstring ext = fs::path(archivePath).extension().wstring();
    std::wstring handler;
    IInArchive* archive = nullptr;
    if (!lib7z.DetectAndCreateArchive(inStream, ext, &archive, openCallback, userCodePage, route.handlerHints,
        outerIsCompoundTar || outerIsSplitVolume, [&] { return g_isCanceled || archivePassword.Requested(); },
        &handler) || !archive) {
        openCallback->Release();
        inStream->Release();
        if (g_isCanceled) {
            SendIpcMessage("{\"type\":\"canceled\"}");
            return 0;
        }
        SendIpcMessage(archivePassword.Requested()
            ? "{\"type\":\"error\",\"error_reason\":\"password_or_data_error\",\"message\":\"Password incorrect or data damaged\"}"
            : "{\"type\":\"error\",\"message\":\"Unsupported or unrecognized archive format\"}");
        return 4;
    }

    uint32_t numItems = 0;
    if (FAILED(archive->GetNumberOfItems(&numItems))) {
        archive->Close();
        archive->Release();
        openCallback->Release();
        inStream->Release();
        SendIpcMessage("{\"type\":\"error\",\"message\":\"Failed to get archive items\"}");
        return 5;
    }

    uint32_t effectiveCp = userCodePage != 0 ? userCodePage : SevenZipLibrary::GetDefaultArchiveCodePage();
    bool cpExplicit = (userCodePage != 0);

    // 外层条目计划（尊重归档声明的 Unicode 名称；显式编码时严格按该值解码）
    ItemPlan plan = BuildItemPlan(archive, numItems, archivePath, effectiveCp, true, cpExplicit);

    // 名称的编码声明自相矛盾或存在无法解码的名称时，请 Proxy 让用户选择编码
    if (plan.undecodableNames > 0 || (plan.declaredUnicode > 0 && plan.legacyNames > 0)) {
        uint32_t chosen = 0;
        if (!RequestEncodingChoice(effectiveCp, plan, archivePath, chosen)) {
            archive->Close();
            archive->Release();
            openCallback->Release();
            inStream->Release();
            SendIpcMessage(g_isCanceled ? "{\"type\":\"canceled\"}"
                : "{\"type\":\"error\",\"error_reason\":\"prompt_timeout\",\"message\":\"Encoding prompt timed out\"}");
            return g_isCanceled ? 0 : 5;
        }
        // 用户一旦显式选择（即使与当前代码页相同）即切换为严格模式并重建计划，
        // 确保“选择框预览”与“落盘”走同一路径，绝不套用启发式改写覆盖用户选择。
        if (chosen != 0) {
            effectiveCp = chosen;
            cpExplicit = true;
            plan = BuildItemPlan(archive, numItems, archivePath, effectiveCp, true, true);
        }
    }

    std::optional<uint64_t> storedLz4Size;
    if (!plan.allSizesKnown && numItems == 1 && plan.toExtract[0] && _wcsicmp(handler.c_str(), L"lz4") == 0) {
        uint64_t physicalSize = 0;
        bool restored = true;
        if (inStream->GetSize(&physicalSize) == S_OK)
            storedLz4Size = TryGetStoredLz4FrameSize(*inStream, physicalSize, restored, CheckPauseOrCancel);
        if (!restored || g_isCanceled) {
            archive->Close();
            archive->Release();
            openCallback->Release();
            inStream->Release();
            SendIpcMessage(g_isCanceled ? "{\"type\":\"canceled\"}"
                : "{\"type\":\"error\",\"message\":\"Cannot restore input stream position\"}");
            return g_isCanceled ? 0 : 5;
        }
        if (storedLz4Size) {
            plan.declaredKnownBytes = *storedLz4Size;
            plan.allSizesKnown = true;
        }
    }

    if (!HasDiskSpace(stagingDir, (std::max)(plan.declaredKnownBytes, explicitBudget))) {
        archive->Close();
        archive->Release();
        openCallback->Release();
        inStream->Release();
        SendIpcMessage("{\"type\":\"error\",\"error_reason\":\"insufficient_disk_space\",\"message\":\"Insufficient disk space in staging directory\"}");
        return 7;
    }

    bool userSkipped = false;
    if (!plan.illegalItems.empty()) {
        std::stringstream ss;
        ss << "{\"type\":\"invalid_chars_found\",\"count\":" << plan.illegalItems.size() << ",\"items\":[";
        for (size_t k = 0; k < plan.illegalItems.size(); ++k) {
            uint32_t idx = plan.illegalItems[k];
            std::string rawUtf8 = WideToUtf8(plan.targetPaths[idx]);
            std::string sanitizedUtf8 = WideToUtf8(PathSanitizer::SanitizeRelativePath(plan.targetPaths[idx]));
            if (k > 0) ss << ",";
            ss << "{\"index\":" << idx << ",\"raw\":\"" << EscapeJsonString(rawUtf8)
               << "\",\"sanitized\":\"" << EscapeJsonString(sanitizedUtf8) << "\"}";
        }
        ss << "]}";

        {
            std::lock_guard<std::mutex> lock(g_inputMutex);
            g_hasInvalidCharsResponse = false;
        }
        SendIpcMessage(ss.str());

        std::unique_lock<std::mutex> lock(g_inputMutex);
        bool answered = g_inputCv.wait_for(lock, std::chrono::minutes(10),
            []() { return g_hasInvalidCharsResponse || g_isCanceled; });
        if (!answered || g_isCanceled) {
            archive->Close();
            archive->Release();
            openCallback->Release();
            inStream->Release();
            SendIpcMessage(g_isCanceled ? "{\"type\":\"canceled\"}"
                : "{\"type\":\"error\",\"error_reason\":\"prompt_timeout\",\"message\":\"Filename prompt timed out\"}");
            return g_isCanceled ? 0 : 5;
        }

        if (g_invalidCharsAction == "batch_rename") {
            for (uint32_t idx : plan.illegalItems) {
                plan.targetPaths[idx] = PathSanitizer::SanitizeRelativePath(plan.targetPaths[idx]);
            }
        } else if (g_invalidCharsAction == "skip") {
            for (uint32_t idx : plan.illegalItems) {
                plan.toExtract[idx] = false;
            }
            userSkipped = true;
        } else {
            for (uint32_t idx : plan.illegalItems) {
                auto it = g_customNames.find(idx);
                if (it != g_customNames.end() && !it->second.empty()) {
                    plan.targetPaths[idx] = it->second;
                } else {
                    plan.targetPaths[idx] = PathSanitizer::SanitizeRelativePath(plan.targetPaths[idx]);
                }
            }
        }
    }

    // 复合归档 / 分卷：外层先解到隔离目录，再按内容决定是否展开内层
    bool outerNeedsInnerCheck = outerIsCompoundTar || outerIsSplitVolume;

    std::wstring outerExtractionRoot = stagingDir;
    if (outerNeedsInnerCheck) {
        outerExtractionRoot = (fs::path(stagingDir) / L".unbox_outer_tmp").wstring();
        std::error_code ec;
        if (!fs::create_directory(outerExtractionRoot, ec)) {
            archive->Close();
            archive->Release();
            openCallback->Release();
            inStream->Release();
            SendIpcMessage("{\"type\":\"error\",\"message\":\"Cannot create staging sub-directory\"}");
            return 7;
        }
    }

    std::wstring canonicalRoot = PathSanitizer::ResolveCanonicalDirectory(outerExtractionRoot);
    if (canonicalRoot.empty()) {
        archive->Close();
        archive->Release();
        openCallback->Release();
        inStream->Release();
        SendIpcMessage("{\"type\":\"error\",\"message\":\"Cannot resolve staging directory\"}");
        return 7;
    }

    // 统一输出预算：声明大小之外再留出合理余量，杜绝声明失真导致写爆磁盘
    uint64_t budgetLimit = storedLz4Size
        ? (explicitBudget ? (std::min)(explicitBudget, *storedLz4Size) : *storedLz4Size)
        : (explicitBudget ? explicitBudget : DefaultBudget(plan.declaredKnownBytes, 256ull * 1024 * 1024));
    OutputBudget budget(budgetLimit);

    {
        std::stringstream ss;
        ss << "{\"type\":\"init\",\"total_items\":" << numItems
           << ",\"total_bytes\":" << plan.declaredKnownBytes
           << ",\"size_known\":" << (plan.allSizesKnown ? "true" : "false")
           << ",\"catalog_sha256\":\"" << ArchiveFormatCatalogSha256 << "\""
           << ",\"mac_filtered\":" << (plan.policyFiltered > 0 ? "true" : "false")
           << ",\"policy_filtered_items\":" << plan.policyFiltered
           << ",\"code_page\":" << effectiveCp
           << ",\"budget_bytes\":" << budgetLimit << "}";
        SendIpcMessage(ss.str());
    }

    WorkerExtractCallback* callback = new WorkerExtractCallback(
        archive,
        outerExtractionRoot,
        canonicalRoot,
        plan.targetPaths,
        plan.toExtract,
        MakeProgressCallback("outer", plan.allSizesKnown),
        requestPassword,
        CheckPauseOrCancel,
        &budget
    );
    callback->SetDeclaredTotal(plan.declaredKnownBytes);

    HRESULT hr = archive->Extract(nullptr, (uint32_t)-1, false, callback);

    if (g_isCanceled) {
        callback->Release();
        archive->Close();
        archive->Release();
        openCallback->Release();
        inStream->Release();
        SendIpcMessage("{\"type\":\"canceled\"}");
        return 0;
    }

    std::vector<DeliveredItemInfo> delivered = callback->GetDeliveredItems();
    uint64_t completedBytes = callback->GetCompletedBytes();
    uint32_t unsafeItems = plan.unsafeItems + callback->GetUnsafeCount();
    uint32_t missingItems = callback->CountMissing();
    bool extractError = callback->HasError();
    bool wrongPassword = callback->HasWrongPassword();
    bool crcError = callback->HasCrcError();
    bool dataError = callback->HasDataError();
    bool unsupported = callback->HasUnsupportedMethod();
    const DWORD writeError = callback->GetWriteError();
    std::vector<std::wstring> openedVolumes = openCallback->GetOpenedVolumes();
    std::vector<VolumeIdentityInfo> volumeIdentities = openCallback->GetVolumeIdentities();

    // 严格生命周期对称性：先释放全部 COM 实例与文件句柄
    callback->Release();
    archive->Close();
    archive->Release();
    openCallback->Release();
    inStream->Release();

    std::string errorReason;
    const char* failurePhase = "outer";
    uint64_t limitBytes = budget.Limit();
    uint64_t writtenBytes = budget.Used();
    uint64_t intermediateBytes = 0;
    bool sizeKnown = plan.allSizesKnown;
    if (budget.Exceeded()) errorReason = "output_budget_exceeded";
    else if (IsDiskFull(hr, writeError)) errorReason = "insufficient_disk_space";
    else if (wrongPassword) errorReason = "wrong_password";
    else if (crcError) errorReason = archivePassword.Requested() ? "password_or_data_error" : "crc_error";
    else if (dataError) errorReason = archivePassword.Requested() ? "password_or_data_error" : "data_error";
    else if (unsupported) errorReason = "unsupported_method";
    else if (writeError != ERROR_SUCCESS || extractError) errorReason = "extract_error";
    else if (hr != S_OK) errorReason = archivePassword.Requested() ? "password_or_data_error" : "extract_error";
    else if (storedLz4Size && writtenBytes != *storedLz4Size) errorReason = "data_error";

    bool hardFailure = extractError || budget.Exceeded() || !errorReason.empty();
    std::wstring deliveryRootName;  // 相对 staging 的交付根；空表示 staging 本身
    uint32_t policyFiltered = plan.policyFiltered;
    uint32_t totalItems = numItems;

    // 内层展开：仅对约定复合 TAR 与分卷入口；其余情况整体交付隔离目录
    if (!hardFailure && outerNeedsInnerCheck) {
        if (delivered.size() == 1 && !delivered[0].isDir) {
            std::wstring payload = delivered[0].diskPath;
            InnerExtractResult inner = ExpandInnerArchive(lib7z, stagingDir, payload, effectiveCp, outerIsCompoundTar, cpExplicit, explicitInnerBudget);
            if (inner.kind == InnerExtractResult::Kind::Success) {
                delivered = inner.items;
                totalItems = inner.totalItems;
                policyFiltered += inner.policyFiltered;
                completedBytes = inner.completedBytes;
                deliveryRootName = inner.deliveryRootName;
                // 内层拒绝/漏项必须累计进外层结果，绝不能被“外层成功”覆盖（P1-1）
                unsafeItems += inner.unsafeItems;
                missingItems += inner.missingItems;
                // 内层完全成功后才删除中间载荷（成功前保留可恢复的中间产物）
                std::error_code ec;
                fs::remove(fs::path(payload), ec);
                fs::remove_all(fs::path(stagingDir) / L".unbox_outer_tmp", ec);
            } else if (inner.kind == InnerExtractResult::Kind::Failed) {
                hardFailure = true;
                errorReason = inner.errorReason.empty() ? "corrupted_archive" : inner.errorReason;
                failurePhase = "inner";
                limitBytes = inner.limitBytes;
                writtenBytes = inner.writtenBytes;
                intermediateBytes = delivered[0].size;
                sizeKnown = inner.sizeKnown;

                delivered.clear();
            } else if (inner.kind == InnerExtractResult::Kind::Canceled) {

                SendIpcMessage("{\"type\":\"canceled\"}");
                return 0;
            } else {
                // NotApplicable：载荷不是归档（例如分卷拼接出的原始数据），保持原样交付
                deliveryRootName = L".unbox_outer_tmp";
            }
        } else if (outerIsCompoundTar) {
            // 约定复合 TAR 必须恰好交付一个 TAR 载荷
            hardFailure = true;
            errorReason = "inner_payload_missing";

            delivered.clear();
        } else {
            // 分卷容器直接解出了多个条目：整体交付隔离目录
            deliveryRootName = L".unbox_outer_tmp";
        }
    }

    if (hardFailure) {
        if (outerNeedsInnerCheck) {
            std::error_code cleanupError;
            fs::remove_all(outerExtractionRoot, cleanupError);
        }
        delivered.clear();
    }

    // 交付清单：路径一律相对交付根目录，供 Proxy 逐项对账（长度 + CRC32）
    const bool manifestOk = !hardFailure && WriteDeliveryManifest(manifestPath, deliveryRootName, delivered);
    if (!hardFailure && !manifestOk) {
        hardFailure = true;
        errorReason = "manifest_write_failed";
    }

    bool userSkipOrUnsafe = userSkipped || unsafeItems > 0 || missingItems > 0;
    std::string status = hardFailure ? "failed" : (userSkipOrUnsafe ? "partial" : "ok");

    uint32_t skippedCount = 0;
    if (userSkipped) {
        skippedCount = (uint32_t)plan.illegalItems.size();
    }

    std::stringstream finishMsg;
    finishMsg << "{\"type\":\"finished\",\"status\":\"" << status << "\""
              << ",\"total_items\":" << totalItems
              << ",\"extracted_items\":" << delivered.size()
              << ",\"skipped_items\":" << skippedCount
              << ",\"policy_filtered_items\":" << policyFiltered
              << ",\"unsafe_items\":" << unsafeItems
              << ",\"missing_items\":" << missingItems
              << ",\"failed_items\":" << (hardFailure ? 1 : 0)
              << ",\"completed_bytes\":" << completedBytes
              << ",\"empty_archive\":" << ((delivered.empty() && !hardFailure) ? "true" : "false")
              << ",\"code_page\":" << effectiveCp
              << ",\"error_reason\":\"" << errorReason << "\""
              << ",\"phase\":\"" << failurePhase << "\""
              << ",\"limit_bytes\":" << limitBytes
              << ",\"written_bytes\":" << writtenBytes
              << ",\"intermediate_bytes\":" << intermediateBytes
              << ",\"size_known\":" << (sizeKnown ? "true" : "false")
              << ",\"delivery_root\":\"" << EscapeJsonString(WideToUtf8(deliveryRootName)) << "\""
              << ",\"manifest\":\"" << EscapeJsonString(WideToUtf8(manifestPath)) << "\""
              << ",\"manifest_ok\":" << (manifestOk ? "true" : "false")
              << ",\"opened_volumes\":[";
    for (size_t v = 0; v < openedVolumes.size(); ++v) {
        if (v > 0) finishMsg << ",";
        finishMsg << "\"" << EscapeJsonString(WideToUtf8(openedVolumes[v])) << "\"";
    }
    finishMsg << "],\"volume_identities\":[";
    for (size_t v = 0; v < volumeIdentities.size(); ++v) {
        const auto& vi = volumeIdentities[v];
        if (v > 0) finishMsg << ",";
        finishMsg << "{\"path\":\"" << EscapeJsonString(WideToUtf8(vi.path)) << "\""
                  << ",\"volume\":" << vi.volumeSerial
                  << ",\"file_id\":\"" << vi.fileIdHex << "\""
                  << ",\"file_id_high\":" << vi.fileIdHigh
                  << ",\"file_id_low\":" << vi.fileIdLow
                  << ",\"size\":" << vi.size
                  << ",\"mtime\":" << vi.lastWriteTime
                  << ",\"valid\":" << (vi.valid ? "true" : "false") << "}";
    }
    finishMsg << "]}";
    SendIpcMessage(finishMsg.str());

    if (hardFailure) return 10;
    if (userSkipOrUnsafe) return 11;
    return 0;
}
