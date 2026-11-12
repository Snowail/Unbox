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

#include "SevenZip.h"
#include "SevenZipLibrary.h"
#include "FileStream.h"
#include "PathSanitizer.h"
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

// 简单的 JSON 字符串转义
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
        size_t p = line.find("\"password\":\"");
        if (p != std::string::npos) {
            size_t start = p + 12;
            size_t end = line.find("\"", start);
            if (end != std::string::npos) {
                std::string pwd = line.substr(start, end - start);
                std::lock_guard<std::mutex> lock(g_inputMutex);
                g_receivedPassword = Utf8ToWide(pwd);
                g_hasPasswordResponse = true;
            }
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

std::wstring RequestPasswordFromProxy() {
    SendIpcMessage("{\"type\":\"password_request\"}");
    std::unique_lock<std::mutex> lock(g_inputMutex);
    g_hasPasswordResponse = false;
    g_inputCv.wait(lock, []() { return g_hasPasswordResponse || g_isCanceled; });
    if (g_isCanceled) return L"";
    return g_receivedPassword;
}

int wmain(int argc, wchar_t* argv[]) {
    // 设置标准输出为二进制，防止换行转义异常
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

    // 寻找 7z.dll
    if (dllPath.empty()) {
        wchar_t exePath[MAX_PATH];
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        fs::path exeDir = fs::path(exePath).parent_path();
        if (fs::exists(exeDir / L"7z.dll")) {
            dllPath = (exeDir / L"7z.dll").wstring();
        } else if (fs::exists(exeDir / L"assets" / L"7z.dll")) {
            dllPath = (exeDir / L"assets" / L"7z.dll").wstring();
        } else if (fs::exists(L"assets\\7z.dll")) {
            dllPath = L"assets\\7z.dll";
        } else if (fs::exists(L"7z.dll")) {
            dllPath = L"7z.dll";
        } else if (fs::exists(L"C:\\PortableSoft\\bin\\7z.dll")) {
            dllPath = L"C:\\PortableSoft\\bin\\7z.dll";
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
    inStream->AddRef();

    std::wstring ext = fs::path(archivePath).extension().wstring();
    IInArchive* archive = nullptr;
    if (!lib7z.DetectAndCreateArchive(inStream, ext, &archive, userCodePage) || !archive) {
        inStream->Release();
        SendIpcMessage("{\"type\":\"error\",\"message\":\"Unsupported or unrecognized archive format\"}");
        return 4;
    }

    uint32_t numItems = 0;
    if (FAILED(archive->GetNumberOfItems(&numItems))) {
        archive->Close();
        archive->Release();
        inStream->Release();
        SendIpcMessage("{\"type\":\"error\",\"message\":\"Failed to get archive items\"}");
        return 5;
    }

    // 收集条目信息，执行 D5（Mac 辅助文件过滤）与 D6（非法字符检测）
    uint64_t totalUncompressedBytes = 0;
    std::unordered_map<uint32_t, std::wstring> itemTargetPaths;
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

        // D5: 自动修复跨系统或代码页可能带来的文件名乱码
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
        SendIpcMessage(ss.str());

        // 等待 Proxy 决定动作
        std::unique_lock<std::mutex> lock(g_inputMutex);
        g_hasInvalidCharsResponse = false;
        g_inputCv.wait(lock, []() { return g_hasInvalidCharsResponse || g_isCanceled; });

        if (g_isCanceled) {
            archive->Close();
            archive->Release();
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

    // 构造 ExtractCallback
    WorkerExtractCallback* callback = new WorkerExtractCallback(
        archive,
        stagingDir,
        itemTargetPaths,
        itemToExtract,
        [](const std::wstring& relPath, uint64_t completedBytes, uint64_t totalBytes) {
            static auto lastProgressTime = std::chrono::steady_clock::now();
            static std::wstring lastFile = L"";
            auto now = std::chrono::steady_clock::now();
            auto elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(now - lastProgressTime).count();
            if (elapsedMs >= 25 || relPath != lastFile || completedBytes >= totalBytes) {
                lastProgressTime = now;
                lastFile = relPath;
                std::stringstream ss;
                ss << "{\"type\":\"progress\",\"current_file\":\"" << EscapeJsonString(WideToUtf8(relPath))
                   << "\",\"completed_bytes\":" << completedBytes
                   << ",\"total_bytes\":" << totalBytes << "}";
                SendIpcMessage(ss.str());
            }
        },
        []() {
            return RequestPasswordFromProxy();
        },
        CheckPauseOrCancel
    );
    callback->AddRef();

    // 开始解压所有条目
    HRESULT hr = archive->Extract(nullptr, (uint32_t)-1, false, callback);

    if (FAILED(hr) || g_isCanceled) {
        callback->Release();
        archive->Close();
        archive->Release();
        inStream->Release();
        if (g_isCanceled) {
            SendIpcMessage("{\"type\":\"canceled\"}");
            return 0;
        }
        SendIpcMessage("{\"type\":\"error\",\"message\":\"Extraction failed with HRESULT: " + std::to_string(hr) + "\"}");
        return 6;
    }

    // 完成通知
    uint32_t extractedCount = (uint32_t)callback->GetExtractedItems().size();
    uint32_t skippedCount = 0;
    for (bool extract : itemToExtract) {
        if (!extract) skippedCount++;
    }

    bool isPartial = hasSkipped || callback->HasError();

    std::stringstream finishMsg;
    finishMsg << "{\"type\":\"finished\",\"status\":\"" << (isPartial ? "partial" : "ok")
              << "\",\"total_items\":" << numItems
              << ",\"extracted_items\":" << extractedCount
              << ",\"skipped_items\":" << skippedCount
              << ",\"completed_bytes\":" << callback->GetCompletedBytes() << "}";
    SendIpcMessage(finishMsg.str());

    callback->Release();
    archive->Close();
    archive->Release();
    inStream->Release();

    return 0;
}
