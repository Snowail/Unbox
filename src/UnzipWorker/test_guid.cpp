#include <windows.h>
#include <iostream>
#include "SevenZip.h"
#include "SevenZipLibrary.h"

int main() {
    SevenZipLibrary lib;
    if (!lib.Load(L"bin\\7z.dll")) return 1;
    auto zipFormats = lib.FindFormatsByExtension(L"zip");
    if (zipFormats.empty()) return 1;
    GUID clsid = zipFormats[0].classId;
    std::cout << "clsid: " << std::hex << clsid.Data1 << " " << clsid.Data2 << " " << clsid.Data3 << "\n";

    void* obj = nullptr;
    Func_CreateObject pfn = (Func_CreateObject)GetProcAddress(GetModuleHandleW(L"7z.dll"), "CreateObject");
    HRESULT hr = pfn(&clsid, &IID_IUnknown, &obj);
    std::cout << "IUnknown hr: " << std::hex << hr << ", obj: " << obj << "\n";
    return 0;
}
