#include <windows.h>
#include <iostream>
#include "SevenZip.h"
#include "SevenZipLibrary.h"

int main() {
    SevenZipLibrary lib;
    if (!lib.Load(L"bin\\7z.dll")) {
        std::cout << "Failed to load 7z.dll\n";
        return 1;
    }
    std::cout << "Formats count: " << lib.GetFormats().size() << "\n";
    for (const auto& fmt : lib.GetFormats()) {
        std::wcout << L"Format: " << fmt.name << L", Exts: ";
        for (const auto& e : fmt.extensions) std::wcout << e << L" ";
        std::wcout << L"\n";
    }
    return 0;
}
