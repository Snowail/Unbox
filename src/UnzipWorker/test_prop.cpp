#include <windows.h>
#include <iostream>
#include "SevenZip.h"

int main() {
    HMODULE hMod = LoadLibraryW(L"bin\\7z.dll");
    Func_GetHandlerProperty2 pfnProp = (Func_GetHandlerProperty2)GetProcAddress(hMod, "GetHandlerProperty2");
    Func_GetNumberOfFormats pfnNum = (Func_GetNumberOfFormats)GetProcAddress(hMod, "GetNumberOfFormats");
    uint32_t num = 0;
    pfnNum(&num);
    for (uint32_t i = 0; i < num; ++i) {
        PROPVARIANT nameProp, idProp;
        PropVariantInit(&nameProp);
        PropVariantInit(&idProp);
        pfnProp(i, NArchive::NHandlerPropID::kName, &nameProp);
        pfnProp(i, NArchive::NHandlerPropID::kClassID, &idProp);
        std::wcout << L"[" << i << L"] Name: " << (nameProp.bstrVal ? nameProp.bstrVal : L"")
                   << L", ID vt: " << idProp.vt << L", byteLen: " << (idProp.vt == VT_BSTR ? SysStringByteLen(idProp.bstrVal) : 0);
        if (idProp.vt == VT_BSTR) {
            const unsigned char* p = (const unsigned char*)idProp.bstrVal;
            std::cout << ", bytes: ";
            for (UINT b = 0; b < SysStringByteLen(idProp.bstrVal); ++b) {
                printf("%02X ", p[b]);
            }
        }
        std::cout << "\n";
        PropVariantClear(&nameProp);
        PropVariantClear(&idProp);
    }
    return 0;
}
