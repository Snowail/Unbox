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

#include "SevenZip.h"
#include "SevenZipLibrary.h"
#include "FileStream.h"
#include "PathSanitizer.h"
#include "WorkerOpenCallback.h"
#include "WorkerExtractCallback.h"

namespace fs = std::filesystem;

// UTF-8 与 std::wstring 转换辅助函数
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

// 严谨的 JSON 字符串字段提取与反转义（完整支持 Unicode 代理对与合法非 BMP 字符/表情/特殊符号）
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
                    // RFC 8259 规范：处理高低代理对 (High: 0xD800-0xDBFF, Low: 0xDC00-0xDFFF)
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

// 判断是否为切分分卷入口（.001）
bool IsSplitVolume(const std::wstring& path) {
    if (path.size() >= 4) {
        std::wstring ext = path.substr(path.size() - 4);
        for (auto& c : ext) c = towlower(c);
        if (ext == L".001") return true;
    }
    return false;
}

// 判断是否为约定的复合 TAR 归档格式（.tar.gz, .tgz, .tar.bz2 等）
bool IsCompoundTarArchive(const std::wstring& path) {
    std::wstring lower = path;
    for (auto& c : lower) c = towlower(c);
    const wchar_t* suffixes[] = {
        L".tar.gz", L".tgz", L".tar.bz2", L".tbz2", L".tbz",
        L".tar.xz", L".txz", L".tar.zst", L".tzst", L".tar.lzma"
    };
    for (const auto& suf : suffixes) {
        size_t len = wcslen(suf);
        if (lower.size() >= len && lower.compare(lower.size() - len, len, suf) == 0) {
            return true;
        }
    }
    return false;
}

// 从归档文件名安全推导单一流的缺省文件名（解决标准 RFC 1952 GZip 无原名头的规范兼容）
std::wstring DeriveSingleStreamFilename(const std::wstring& archivePath) {
    fs::path p(archivePath);
    std::wstring ext = p.extension().wstring();
    for (auto& c : ext) c = towlower(c);
    if (ext == L".gz" || ext == L".bz2" || ext == L".xz" || ext == L".zst" || ext == L".lzma" || ext == L".001") {
        std::wstring stem = p.stem().wstring();
        if (!stem.empty()) return stem;
    }
    return L"extracted_file";
}

// IPC 发送函数
std::mutex g_coutMutex;
void SendIpcMessage(const std::string& json) {
    std::lock_guard<std::mutex> lock(g_coutMutex);
    std::cout << json << "\n";
    std::cout.flush();
}

// IPC 接收与控制状态机
std::atomic<bool> g_isPaused(false);
std::atomic<bool> g_isCanceled(false);
std::mutex g_pauseMutex;
std::condition_variable g_pauseCv;

std::mutex g_inputMutex;
std::condition_variable g_inputCv;

bool g_hasPasswordResponse = false;
std::wstring g_receivedPassword;

bool g_hasInvalidCharsResponse = false;
std::string g_invalidCharsAction; // "batch_rename", "rename_individual", "skip"
std::unordered_map<uint32_t, std::wstring> g_customNames;

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
        g_isCanceled = true;
        {
            std::lock_guard<std::mutex> lock(g_pauseMutex);
            g_isPaused = false;
        }
        g_pauseCv.notify_all();
        g_inputCv.notify_all();
    } else if (line.find("\"type\":\"password_response\"") != std::string::npos) {
        if (line.find("\"canceled\":true") != std::string::npos) {
            g_isCanceled = true;
        }
        std::string pwd = ExtractJsonStringField(line, "password");
        {
            std::lock_guard<std::mutex> lock(g_inputMutex);
            g_receivedPassword = Utf8ToWide(pwd);
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
    }
}

void StartIpcListenerThread() {
    std::thread([]() {
        std::string line;
        while (std::getline(std::cin, line)) {
            if (!line.empty()) {
                ProcessIpcInputLine(line);
            }
        }
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

std::wstring RequestPasswordFromProxy(bool* outCanceled = nullptr) {
    {
        std::lock_guard<std::mutex> lock(g_inputMutex);
        g_hasPasswordResponse = false;
        g_receivedPassword.clear();
    }
    SendIpcMessage("{\"type\":\"password_request\"}");
    std::unique_lock<std::mutex> lock(g_inputMutex);
    g_inputCv.wait(lock, []() { return g_hasPasswordResponse || g_isCanceled; });
    if (g_isCanceled) {
        if (outCanceled) *outCanceled = true;
        return L"";
    }
    if (outCanceled) *outCanceled = false;
    return g_receivedPassword;
}

enum class InnerExtractResult {
    NotApplicable,
    Success,
    Failed,
    Canceled
};

// 复合归档与分卷自动嵌套解压支持（如 split_data.7z.001 展开后为 split_data.7z，或 TAR.GZ 展开后为 TAR）
InnerExtractResult TryExtractInnerArchive(SevenZipLibrary& lib7z,
                                         const std::wstring& stagingDir,
                                         const std::wstring& actualDiskPath,
                                         uint32_t userCodePage,
                                         uint32_t& outInnerItems,
                                         uint32_t& outInnerExtracted,
                                         std::string& outErrReason) {
    outErrReason = "";
    std::error_code ec;
    if (!fs::exists(actualDiskPath, ec) || fs::is_directory(actualDiskPath, ec)) {
        return InnerExtractResult::NotApplicable;
    }

    fs::path innerPath(actualDiskPath);
    std::wstring ext = innerPath.extension().wstring();
    for (auto& c : ext) c = towlower(c);
    if (ext != L".7z" && ext != L".tar" && ext != L".zip" && ext != L".rar" && ext != L".iso") {
        return InnerExtractResult::NotApplicable;
    }

    CInFileStream* innerStream = CInFileStream::OpenFile(actualDiskPath.c_str());
    if (!innerStream) {
        outErrReason = "cannot_open_inner_stream";
        return InnerExtractResult::Failed;
    }

    WorkerOpenCallback* innerOpenCallback = new WorkerOpenCallback(actualDiskPath, [](bool* outCanceled) {
        return RequestPasswordFromProxy(outCanceled);
    });

    IInArchive* innerArchive = nullptr;
    if (!lib7z.DetectAndCreateArchive(innerStream, ext, &innerArchive, innerOpenCallback, userCodePage) || !innerArchive) {
        innerOpenCallback->Release();
        innerStream->Release();
        outErrReason = "unrecognized_or_corrupted_inner_archive";
        return InnerExtractResult::Failed;
    }

    uint32_t innerNumItems = 0;
    if (FAILED(innerArchive->GetNumberOfItems(&innerNumItems)) || innerNumItems == 0) {
        innerArchive->Close();
        innerArchive->Release();
        innerOpenCallback->Release();
        innerStream->Release();
        outErrReason = "empty_or_corrupted_inner_archive";
        return InnerExtractResult::Failed;
    }

    // 统计未压缩总字节数并做磁盘空间预算预检
    uint64_t totalInnerBytes = 0;
    for (uint32_t i = 0; i < innerNumItems; ++i) {
        PROPVARIANT propSize;
        PropVariantInit(&propSize);
        if (SUCCEEDED(innerArchive->GetProperty(i, kpidSize, &propSize))) {
            if (propSize.vt == VT_UI8) totalInnerBytes += propSize.uhVal.QuadPart;
            else if (propSize.vt == VT_UI4) totalInnerBytes += propSize.ulVal;
        }
        PropVariantClear(&propSize);
    }

    fs::space_info spaceInfo = fs::space(stagingDir, ec);
    if (!ec && spaceInfo.available < totalInnerBytes) {
        innerArchive->Close();
        innerArchive->Release();
        innerOpenCallback->Release();
        innerStream->Release();
        outErrReason = "insufficient_disk_space";
        return InnerExtractResult::Failed;
    }

    // 创建内层过渡暂存目录
    fs::path tempSubDir = fs::path(stagingDir) / L".unbox_inner_temp";
    fs::create_directories(tempSubDir, ec);

    std::vector<std::wstring> innerTargetPaths(innerNumItems);
    std::vector<bool> innerToExtract(innerNumItems, true);
    for (uint32_t i = 0; i < innerNumItems; ++i) {
        PROPVARIANT propPath;
        PropVariantInit(&propPath);
        std::wstring p;
        if (SUCCEEDED(innerArchive->GetProperty(i, kpidPath, &propPath)) && propPath.vt == VT_BSTR && propPath.bstrVal) {
            p = propPath.bstrVal;
        }
        PropVariantClear(&propPath);
        PathSanitizer::FixMojibake(p, userCodePage != 0 ? userCodePage : SevenZipLibrary::GetDefaultArchiveCodePage());
        innerTargetPaths[i] = p;
    }

    WorkerExtractCallback* innerCallback = new WorkerExtractCallback(
        innerArchive,
        tempSubDir.wstring(),
        innerTargetPaths,
        innerToExtract,
        nullptr,
        [](bool* outCanceled) { return RequestPasswordFromProxy(outCanceled); },
        CheckPauseOrCancel
    );

    HRESULT hr = innerArchive->Extract(nullptr, (uint32_t)-1, false, innerCallback);

    bool ok = SUCCEEDED(hr) && !innerCallback->HasError() && !innerCallback->HasSkipped();
    if (innerCallback->HasCrcError()) outErrReason = "crc_error";
    else if (innerCallback->HasDataError()) outErrReason = "data_error";
    else if (innerCallback->HasUnsupportedMethod()) outErrReason = "unsupported_method";
    else if (innerCallback->HasError()) outErrReason = "extract_error";

    outInnerItems = innerNumItems;
    outInnerExtracted = (uint32_t)innerCallback->GetExtractedItems().size();

    // 关键对称释放：在文件系统移动与删除前，彻底释放全部 COM 实例与文件流
    innerCallback->Release();
    innerArchive->Close();
    innerArchive->Release();
    innerOpenCallback->Release();
    innerStream->Release();

    if (g_isCanceled) {
        fs::remove_all(tempSubDir, ec);
        return InnerExtractResult::Canceled;
    }

    if (ok) {
        // 删除中间过渡归档（文件锁已完全释放）
        fs::remove(actualDiskPath, ec);

        // 将内层解出项上移至 stagingDir
        for (const auto& entry : fs::directory_iterator(tempSubDir, ec)) {
            fs::path dest = fs::path(stagingDir) / entry.path().filename();
            if (fs::exists(dest, ec)) fs::remove_all(dest, ec);
            fs::rename(entry.path(), dest, ec);
        }
        fs::remove_all(tempSubDir, ec);
        return InnerExtractResult::Success;
    } else {
        fs::remove_all(tempSubDir, ec);
        return InnerExtractResult::Failed;
    }
}

int wmain(int argc, wchar_t* argv[]) {
    // 设置标准输入输出为二进制，防止换行和特殊字符转义损坏
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stdin), _O_BINARY);

    std::wstring archivePath;
    std::wstring stagingDir;
    std::wstring targetRootDir;
    std::wstring dllPath;
    uint32_t userCodePage = 0;

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
        } else if (arg == L"--cp" && i + 1 < argc) {
            userCodePage = (uint32_t)_wtoi(argv[++i]);
        }
    }

    if (archivePath.empty() || stagingDir.empty()) {
        SendIpcMessage("{\"type\":\"error\",\"message\":\"Missing required arguments\"}");
        return 1;
    }

    StartIpcListenerThread();

    // 寻找受信安装目录下的 7z.dll（杜绝在未知工作目录中盲目探测）
    if (dllPath.empty()) {
        wchar_t exePath[MAX_PATH];
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        fs::path exeDir = fs::path(exePath).parent_path();
        if (fs::exists(exeDir / L"7z.dll")) {
            dllPath = (exeDir / L"7z.dll").wstring();
        } else if (fs::exists(exeDir / L"assets" / L"7z.dll")) {
            dllPath = (exeDir / L"assets" / L"7z.dll").wstring();
        }
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

    // 创建打开回调：支持打开进度、头加密索取密码与多卷真实物理路径追踪
    WorkerOpenCallback* openCallback = new WorkerOpenCallback(
        archivePath,
        [](bool* outCanceled) {
            return RequestPasswordFromProxy(outCanceled);
        }
    );

    std::wstring ext = fs::path(archivePath).extension().wstring();
    IInArchive* archive = nullptr;
    if (!lib7z.DetectAndCreateArchive(inStream, ext, &archive, openCallback, userCodePage) || !archive) {
        openCallback->Release();
        inStream->Release();
        SendIpcMessage("{\"type\":\"error\",\"message\":\"Unsupported or unrecognized archive format or incorrect password\"}");
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

    // 收集条目信息，执行 D5（Mac 辅助文件过滤）与 D6（非法字符检测）
    // 使用 std::vector 实现 O(1) 紧凑数组索引，消除 1,200 个小文件的散列开销
    uint64_t totalUncompressedBytes = 0;
    std::vector<std::wstring> itemTargetPaths(numItems);
    std::vector<bool> itemToExtract(numItems, false);
    std::vector<uint32_t> illegalItems;
    bool hasMacFiltered = false;

    for (uint32_t i = 0; i < numItems; ++i) {
        PROPVARIANT propPath;
        PropVariantInit(&propPath);
        std::wstring itemPath;
        if (SUCCEEDED(archive->GetProperty(i, kpidPath, &propPath)) && propPath.vt == VT_BSTR && propPath.bstrVal) {
            itemPath = propPath.bstrVal;
        }
        PropVariantClear(&propPath);

        // 标准 RFC 1952 GZip 流可能不含原始文件名头：单流模式下从源归档名智能推导有效安全名称
        if (itemPath.empty() && numItems == 1) {
            itemPath = DeriveSingleStreamFilename(archivePath);
        }

        // D5: 自动修复跨系统或代码页可能带来的文件名乱码 (纯 ASCII 路径直接快速略过)
        PathSanitizer::FixMojibake(itemPath, userCodePage != 0 ? userCodePage : SevenZipLibrary::GetDefaultArchiveCodePage());

        PROPVARIANT propSize;
        PropVariantInit(&propSize);
        uint64_t itemSize = 0;
        if (SUCCEEDED(archive->GetProperty(i, kpidSize, &propSize))) {
            if (propSize.vt == VT_UI8) itemSize = propSize.uhVal.QuadPart;
            else if (propSize.vt == VT_UI4) itemSize = propSize.ulVal;
        }
        PropVariantClear(&propSize);

        PROPVARIANT propIsDir;
        PropVariantInit(&propIsDir);
        bool isDir = false;
        if (SUCCEEDED(archive->GetProperty(i, kpidIsDir, &propIsDir))) {
            if (propIsDir.vt == VT_BOOL && propIsDir.boolVal == VARIANT_TRUE) {
                isDir = true;
            }
        }
        PropVariantClear(&propIsDir);

        // D5: 排除 macOS 辅助文件
        if (PathSanitizer::IsMacAuxiliary(itemPath)) {
            hasMacFiltered = true;
            itemToExtract[i] = false;
            continue;
        }

        // 检查非法字符 (D6)
        if (PathSanitizer::HasIllegalChars(itemPath)) {
            illegalItems.push_back(i);
        }

        itemTargetPaths[i] = itemPath;
        itemToExtract[i] = true;
        if (!isDir) {
            totalUncompressedBytes += itemSize;
        }
    }

    // 检查磁盘剩余空间预算（防止解压炸弹耗尽磁盘）
    ULARGE_INTEGER freeBytesAvailableToCaller{};
    ULARGE_INTEGER totalNumberOfBytes{};
    ULARGE_INTEGER totalNumberOfFreeBytes{};
    if (GetDiskFreeSpaceExW(stagingDir.c_str(), &freeBytesAvailableToCaller, &totalNumberOfBytes, &totalNumberOfFreeBytes)) {
        if (totalUncompressedBytes > 0 && freeBytesAvailableToCaller.QuadPart < totalUncompressedBytes) {
            archive->Close();
            archive->Release();
            openCallback->Release();
            inStream->Release();
            SendIpcMessage("{\"type\":\"error\",\"message\":\"Insufficient disk space in staging directory\"}");
            return 7;
        }
    }

    bool hasSkipped = false;

    // 如果发现非法字符或保留名，向 Proxy 上报并等待决策 (D6)
    if (!illegalItems.empty()) {
        std::stringstream ss;
        ss << "{\"type\":\"invalid_chars_found\",\"count\":" << illegalItems.size() << ",\"items\":[";
        for (size_t k = 0; k < illegalItems.size(); ++k) {
            uint32_t idx = illegalItems[k];
            std::string rawUtf8 = WideToUtf8(itemTargetPaths[idx]);
            std::string sanitizedUtf8 = WideToUtf8(PathSanitizer::SanitizeRelativePath(itemTargetPaths[idx]));
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

        // 等待 Proxy 决定动作
        std::unique_lock<std::mutex> lock(g_inputMutex);
        g_inputCv.wait(lock, []() { return g_hasInvalidCharsResponse || g_isCanceled; });

        if (g_isCanceled) {
            archive->Close();
            archive->Release();
            openCallback->Release();
            inStream->Release();
            return 0;
        }

        if (g_invalidCharsAction == "batch_rename") {
            for (uint32_t idx : illegalItems) {
                itemTargetPaths[idx] = PathSanitizer::SanitizeRelativePath(itemTargetPaths[idx]);
            }
        } else if (g_invalidCharsAction == "skip") {
            for (uint32_t idx : illegalItems) {
                itemToExtract[idx] = false;
            }
            hasSkipped = true;
        } else if (g_invalidCharsAction == "rename_individual") {
            for (uint32_t idx : illegalItems) {
                auto it = g_customNames.find(idx);
                if (it != g_customNames.end() && !it->second.empty()) {
                    itemTargetPaths[idx] = it->second;
                } else {
                    itemTargetPaths[idx] = PathSanitizer::SanitizeRelativePath(itemTargetPaths[idx]);
                }
            }
        }
    }

    // 发送初始化完成与提取开始通知
    {
        std::stringstream ss;
        ss << "{\"type\":\"init\",\"total_items\":" << numItems
           << ",\"total_bytes\":" << totalUncompressedBytes
           << ",\"mac_filtered\":" << (hasMacFiltered ? "true" : "false") << "}";
        SendIpcMessage(ss.str());
    }

    // 构造 ExtractCallback：落实严格 30ms 时间窗限流（杜绝换文件无脑刷新管道）
    WorkerExtractCallback* callback = new WorkerExtractCallback(
        archive,
        stagingDir,
        itemTargetPaths,
        itemToExtract,
        [](const std::wstring& relPath, uint64_t completedBytes, uint64_t totalBytes) {
            static auto lastProgressTime = std::chrono::steady_clock::time_point{};
            auto now = std::chrono::steady_clock::now();
            auto elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(now - lastProgressTime).count();
            // 严格限流 30ms，仅在时间到达或完成时发送快照
            if (elapsedMs >= 30 || (totalBytes > 0 && completedBytes >= totalBytes)) {
                lastProgressTime = now;
                std::stringstream ss;
                ss << "{\"type\":\"progress\",\"current_file\":\"" << EscapeJsonString(WideToUtf8(relPath))
                   << "\",\"completed_bytes\":" << completedBytes
                   << ",\"total_bytes\":" << totalBytes << "}";
                SendIpcMessage(ss.str());
            }
        },
        [](bool* outCanceled) {
            return RequestPasswordFromProxy(outCanceled);
        },
        CheckPauseOrCancel
    );

    // 开始解压所有条目
    HRESULT hr = archive->Extract(nullptr, (uint32_t)-1, false, callback);

    if (FAILED(hr) || g_isCanceled) {
        callback->Release();
        archive->Close();
        archive->Release();
        openCallback->Release();
        inStream->Release();
        if (g_isCanceled) {
            SendIpcMessage("{\"type\":\"canceled\"}");
            return 0;
        }
        SendIpcMessage("{\"type\":\"error\",\"message\":\"Extraction failed with HRESULT: " + std::to_string(hr) + "\"}");
        return 6;
    }

    // 统计条目与错误状态
    uint32_t extractedCount = (uint32_t)callback->GetExtractedItems().size();
    uint32_t skippedCount = 0;
    for (bool extract : itemToExtract) {
        if (!extract) skippedCount++;
    }

    bool isPartial = hasSkipped || callback->HasError() || callback->HasSkipped();
    std::string errReason = "";
    if (callback->HasCrcError()) errReason = "crc_error";
    else if (callback->HasDataError()) errReason = "data_error";
    else if (callback->HasUnsupportedMethod()) errReason = "unsupported_method";
    else if (callback->HasError()) errReason = "extract_error";

    // 复合归档与分卷自动嵌套解压支持：
    // 严格约束：仅当外层为分卷入口（.001）或约定复合 TAR（.tar.gz/.tgz 等）时才尝试内层展开；普通归档内的单个嵌入归档保持原样交付
    bool isSplit = IsSplitVolume(archivePath);
    bool isCompoundTar = IsCompoundTarArchive(archivePath);

    if (!isPartial && (isSplit || isCompoundTar) && numItems == 1 && extractedCount == 1 && !callback->GetExtractedItems().empty()) {
        std::wstring innerActualDiskPath = callback->GetExtractedItems()[0].diskPath;
        uint32_t innerItems = 0;
        uint32_t innerExtracted = 0;
        std::string innerErrReason = "";
        InnerExtractResult innerRes = TryExtractInnerArchive(
            lib7z,
            stagingDir,
            innerActualDiskPath,
            userCodePage,
            innerItems,
            innerExtracted,
            innerErrReason
        );

        if (innerRes == InnerExtractResult::Success) {
            numItems = innerItems;
            extractedCount = innerExtracted;
        } else if (innerRes == InnerExtractResult::Failed) {
            // 内层解压失败（如缺尾卷导致拼接不全、内层破损）：必须确立失败状态并清理残损中间文件，坚决阻断源包误删
            isPartial = true;
            errReason = innerErrReason.empty() ? "corrupted_archive" : innerErrReason;
            std::error_code ec;
            fs::remove_all(stagingDir, ec);
        } else if (innerRes == InnerExtractResult::Canceled) {
            std::error_code ec;
            fs::remove_all(stagingDir, ec);
            callback->Release();
            archive->Close();
            archive->Release();
            openCallback->Release();
            inStream->Release();
            SendIpcMessage("{\"type\":\"canceled\"}");
            return 0;
        }
    }

    // 关键安全修复：在 Release() 销毁对象前提前拷贝完指标数据，根除 Use-After-Free 释放后读取
    uint64_t finalCompletedBytes = callback->GetCompletedBytes();
    std::vector<std::wstring> openedVolumes = openCallback->GetOpenedVolumes();

    // 严格生命周期对称性：先关闭全部解压句柄、释放输入流与 COM 实例，彻底解绑源文件
    callback->Release();
    archive->Close();
    archive->Release();
    openCallback->Release();
    inStream->Release();

    // 发送最终任务完成报告（上报本次实际打开的真实分卷物理路径）
    std::stringstream finishMsg;
    finishMsg << "{\"type\":\"finished\",\"status\":\"" << (isPartial ? "partial" : "ok")
              << "\",\"total_items\":" << numItems
              << ",\"extracted_items\":" << extractedCount
              << ",\"skipped_items\":" << skippedCount
              << ",\"completed_bytes\":" << finalCompletedBytes
              << ",\"error_reason\":\"" << errReason << "\""
              << ",\"opened_volumes\":[";
    for (size_t v = 0; v < openedVolumes.size(); ++v) {
        if (v > 0) finishMsg << ",";
        finishMsg << "\"" << EscapeJsonString(WideToUtf8(openedVolumes[v])) << "\"";
    }
    finishMsg << "]}";
    SendIpcMessage(finishMsg.str());

    return isPartial ? 10 : 0;
}
