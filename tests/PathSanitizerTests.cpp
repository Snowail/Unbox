#include <windows.h>
#include <winternl.h>
#include <string>
#include <iostream>
#include <filesystem>
#include <cstring>
#include <thread>
static FARPROC WINAPI TestGetProcAddress(HMODULE module, LPCSTR name);
#define GetProcAddress TestGetProcAddress
#include "../src/UnzipWorker/PathSanitizer.h"
#undef GetProcAddress
#include "../src/UnzipWorker/ArchiveFormatPolicy.h"
#include "../src/UnzipWorker/FileStream.h"
#include "../src/UnzipWorker/Lz4FrameSize.h"

class ShortReadStream final : public IInStream {
public:
    std::vector<uint8_t> data{4, 0x22, 0x4D, 0x18, 0x60, 0x40, 0, 3, 0, 0, 0x80, 1, 2, 3, 0, 0, 0, 0};
    uint64_t position = 5;
    uint32_t readLimit = 1;
    bool denyRestore = false;

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void**) override { return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE Read(void* destination, uint32_t size, uint32_t* read) override {
        *read = static_cast<uint32_t>((std::min)({uint64_t(size), uint64_t(readLimit), data.size() - position}));
        if (*read) std::memcpy(destination, data.data() + position, *read);
        position += *read;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Seek(int64_t offset, uint32_t origin, uint64_t* next) override {
        if (origin == FILE_BEGIN) {
            if (offset < 0 || static_cast<uint64_t>(offset) > data.size() || (denyRestore && offset == 5))
                return E_FAIL;
            position = static_cast<uint64_t>(offset);
        } else if (origin != FILE_CURRENT || offset != 0) {
            return E_FAIL;
        }
        if (next) *next = position;
        return S_OK;
    }
};

using NtCreate = NTSTATUS (NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK,
                                   PLARGE_INTEGER, ULONG, ULONG, ULONG, ULONG, PVOID, ULONG);
static std::wstring parentToMove;
static bool hookSeen = false, renameBlocked = false;
static NTSTATUS NTAPI HookCreate(PHANDLE handle, ACCESS_MASK access, POBJECT_ATTRIBUTES attrs,
    PIO_STATUS_BLOCK io, PLARGE_INTEGER size, ULONG fileAttrs, ULONG share, ULONG disposition,
    ULONG options, PVOID ea, ULONG eaSize) {
    std::wstring name(attrs->ObjectName->Buffer, attrs->ObjectName->Length / sizeof(wchar_t));
    if (name == L"child" && !parentToMove.empty()) {
        hookSeen = true;
        std::wstring moved = parentToMove + L"-moved";
        renameBlocked = !MoveFileW(parentToMove.c_str(), moved.c_str());
        if (!renameBlocked) MoveFileW(moved.c_str(), parentToMove.c_str());
    }
    auto actual = reinterpret_cast<NtCreate>(::GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtCreateFile"));
    return actual(handle, access, attrs, io, size, fileAttrs, share, disposition, options, ea, eaSize);
}
static FARPROC WINAPI TestGetProcAddress(HMODULE module, LPCSTR name) {
    if (strcmp(name, "NtCreateFile") == 0) return reinterpret_cast<FARPROC>(&HookCreate);
    return ::GetProcAddress(module, name);
}
int wmain() {
    int failures = 0;
    auto check = [&](const char* name, bool ok) {
        std::cout << (ok ? "[PASS] " : "[FAIL] ") << name << std::endl;
        if (!ok) ++failures;
    };
    // 保留到 bin 下，避免测试清理时递归跟随联接点。
    fs::path base = fs::absolute(L"bin/path-tests-" + std::to_wstring(GetCurrentProcessId()));
    fs::path root = base / L"root", outside = base / L"outside";
    fs::create_directories(root / L"inside" / L"parent");
    fs::create_directories(outside / L"parent");
    std::wstring canonical = PathSanitizer::ResolveCanonicalDirectory(root.wstring());
    DWORD error = 0;
    check("R7: normal relative directory creation", PathSanitizer::EnsureDirectory(canonical,
        (root / L"normal" / L"parent").wstring(), error));
    parentToMove = (root / L"normal").wstring();
    check("R7: hooked child creation succeeds", PathSanitizer::EnsureDirectory(canonical,
        (root / L"normal" / L"parent" / L"child").wstring(), error));
    check("R7: ancestor rename blocked at creation boundary", hookSeen && renameBlocked);
    parentToMove.clear();
    auto junction = root / L"hop";
    std::wstring cmd = L"cmd.exe /d /c mklink /J \"" + junction.wstring() + L"\" \"" +
        (root / L"inside").wstring() + L"\" >nul";
    check("R7: create inside-root intermediate junction", _wsystem(cmd.c_str()) == 0);
    bool accepted = PathSanitizer::EnsureDirectory(canonical, (junction / L"parent" / L"child").wstring(), error);
    check("R7: reject intermediate junction even pointing inside", !accepted && error == ERROR_ACCESS_DENIED);
    check("R7: rejected path has no create side effect", !fs::exists(root / L"inside" / L"parent" / L"child") &&
        !fs::exists(outside / L"parent" / L"child"));
    check("R7: existing directory through intermediate junction rejected",
        !PathSanitizer::EnsureDirectory(canonical, (junction / L"parent").wstring(), error));
    check("R7: remove test junction", RemoveDirectoryW(junction.c_str()) != 0);
    check("R7: reject parent traversal", !PathSanitizer::EnsureDirectory(canonical,
        (root / L"normal" / L".." / L".." / L"outside" / L"child").wstring(), error));
    check("ZS: absolute and device path syntax rejected",
        !PathSanitizer::IsSafeRelativeSyntax(L"C:volume") &&
        !PathSanitizer::IsSafeRelativeSyntax(L"test\\NUL.txt") &&
        !PathSanitizer::IsSafeRelativeSyntax(L"\\archive.txt"));
    auto wrapper = ClassifyArchiveName(L"Project.TAR.BROTLI");
    auto stream = ClassifyArchiveName(L"SLP267.rar.LZ4");
    check("ZS: longest suffix and case-preserving names", wrapper.kind == ArchiveKind::TarWrapper &&
        wrapper.baseName == L"Project" && DefaultStreamName(wrapper, L"Project.TAR.BROTLI") == L"Project.tar" &&
        stream.kind == ArchiveKind::Stream && DefaultStreamName(stream, L"SLP267.rar.LZ4") == L"SLP267.rar");

    OutputBudget budget(10);
    check("ZS: reservation settles partial writes", budget.Consume(8) && budget.Used() == 0);
    budget.Settle(8, 3);
    check("ZS: rejected writes do not consume budget", !budget.Consume(8) && budget.Exceeded() &&
        budget.Used() == 3 && budget.Consume(7));
    budget.Settle(7, 7);
    check("ZS: exactly at limit", budget.Used() == 10 && !budget.Consume(1));
    OutputBudget wide(UINT64_MAX);
    check("ZS: no counter wrap at UINT64_MAX", wide.Consume(UINT64_MAX - 1) && !wide.Consume(2));
    wide.Settle(UINT64_MAX - 1, UINT64_MAX - 1);
    check("ZS: large accounting remains exact", wide.Used() == UINT64_MAX - 1 && wide.Consume(1));
    OutputBudget concurrent(10000);
    std::vector<std::thread> threads;
    for (int i = 0; i < 8; ++i) {
        threads.emplace_back([&] {
            while (concurrent.Consume(1)) concurrent.Settle(1, 1);
        });
    }
    for (auto& thread : threads) thread.join();
    check("ZS: concurrent budget remains bounded", concurrent.Used() == 10000 && concurrent.Exceeded());

    for (uint32_t length = 1; length <= 3; ++length) {
        ShortReadStream frame;
        frame.readLimit = length;
        check("ZS: LZ4 short reads restore the input offset",
            TryGetStoredLz4FrameSize(frame, frame.data.size()) == 3 && frame.position == 5);
    }
    ShortReadStream frame;
    frame.data[10] = 0;
    check("ZS: LZ4 compressed block stays unknown",
        !TryGetStoredLz4FrameSize(frame, frame.data.size()) && frame.position == 5);
    frame.data[10] = 0x80;
    frame.data.push_back(0);
    check("ZS: LZ4 trailing bytes and premature EOF stay unknown",
        !TryGetStoredLz4FrameSize(frame, frame.data.size()) && frame.position == 5);
    frame.data.resize(17);
    check("ZS: LZ4 incomplete field stays unknown",
        !TryGetStoredLz4FrameSize(frame, 18) && frame.position == 5);
    frame.data.push_back(0);
    bool restored = false;
    check("ZS: LZ4 cancellation restores offset",
        !TryGetStoredLz4FrameSize(frame, frame.data.size(), restored, [] { return false; }) && restored && frame.position == 5);
    frame.denyRestore = true;
    check("ZS: LZ4 restoration failure invalidates length",
        !TryGetStoredLz4FrameSize(frame, frame.data.size(), restored, {}) && !restored);
    const fs::path absoluteBase = fs::absolute(base).lexically_normal();
    if (absoluteBase.parent_path() == fs::absolute(L"bin").lexically_normal() &&
        absoluteBase.filename().wstring().starts_with(L"path-tests-") && !fs::exists(junction)) {
        std::error_code ignored;
        fs::remove_all(absoluteBase, ignored);
    }
    return failures ? 1 : 0;
}
