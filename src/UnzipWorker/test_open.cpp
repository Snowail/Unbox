#include <windows.h>
#include <iostream>
#include "SevenZip.h"
#include "SevenZipLibrary.h"
#include "FileStream.h"

int main() {
    SevenZipLibrary lib;
    if (!lib.Load(L"bin\\7z.dll")) return 1;
    CInFileStream* stream = CInFileStream::OpenFile(L"tests\\sample1.zip");
    if (!stream) {
        std::cout << "Cannot open file\n";
        return 2;
    }
    stream->AddRef();
    auto zipFormats = lib.FindFormatsByExtension(L"zip");
    std::cout << "Found zip formats: " << zipFormats.size() << "\n";
    for (auto& fmt : zipFormats) {
        IInArchive* arc = nullptr;
        HRESULT hr = lib.CreateArchiveReader(fmt.classId, &arc);
        std::cout << "CreateArchiveReader hr: " << std::hex << hr << "\n";
        if (SUCCEEDED(hr) && arc) {
            uint64_t maxPos = 1 << 20;
            HRESULT openHr = arc->Open(stream, &maxPos, nullptr);
            std::cout << "Open hr: " << std::hex << openHr << "\n";
            arc->Release();
        }
    }
    stream->Release();
    return 0;
}
