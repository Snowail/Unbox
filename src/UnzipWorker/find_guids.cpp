#include <windows.h>
#include <iostream>
#include <vector>

int main() {
    HMODULE hMod = LoadLibraryW(L"bin\\7z.dll");
    BYTE* p = (BYTE*)hMod;
    // 获取 PE headers
    IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)p;
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(p + dos->e_lfanew);
    DWORD size = nt->OptionalHeader.SizeOfImage;

    std::cout << "Searching in 7z.dll memory of size: " << size << "\n";
    for (DWORD i = 0; i + 16 <= size; ++i) {
        if (p[i] == 0x69 && p[i+1] == 0x0F && p[i+2] == 0x17 && p[i+3] == 0x23) {
            GUID* g = (GUID*)(p + i);
            printf("Found GUID at offset 0x%X: {%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}\n",
                i, g->Data1, g->Data2, g->Data3,
                g->Data4[0], g->Data4[1], g->Data4[2], g->Data4[3],
                g->Data4[4], g->Data4[5], g->Data4[6], g->Data4[7]);
        }
    }
    return 0;
}
