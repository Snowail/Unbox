#include <windows.h>
#include <winternl.h>
#include <string>
#include <iostream>
#include <filesystem>
static FARPROC WINAPI TestGetProcAddress(HMODULE module, LPCSTR name);
#define GetProcAddress TestGetProcAddress
#include "../src/UnzipWorker/PathSanitizer.h"
#undef GetProcAddress

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
    return failures ? 1 : 0;
}
