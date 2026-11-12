#pragma once

#include <windows.h>
#include <unknwn.h>
#include <propidl.h>
#include <cstdint>

#define DEFINE_SEVENZIP_GUID(name, l, w1, w2, b1, b2, b3, b4, b5, b6, b7, b8) \
    EXTERN_C const GUID DECLSPEC_SELECTANY name \
        = { l, w1, w2, { b1, b2, b3, b4, b5, b6, b7, b8 } }

// 23170F69-40C1-278A-0000-00xx00yy0000
DEFINE_SEVENZIP_GUID(IID_ISequentialInStream,      0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x03, 0x00, 0x01, 0x00, 0x00);
DEFINE_SEVENZIP_GUID(IID_ISequentialOutStream,     0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x03, 0x00, 0x02, 0x00, 0x00);
DEFINE_SEVENZIP_GUID(IID_IInStream,                0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x03, 0x00, 0x03, 0x00, 0x00);
DEFINE_SEVENZIP_GUID(IID_IOutStream,               0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x03, 0x00, 0x04, 0x00, 0x00);
DEFINE_SEVENZIP_GUID(IID_IStreamGetSize,           0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x03, 0x00, 0x06, 0x00, 0x00);
DEFINE_SEVENZIP_GUID(IID_IArchiveOpenCallback,     0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x10, 0x00, 0x00);
DEFINE_SEVENZIP_GUID(IID_IArchiveExtractCallback,  0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x20, 0x00, 0x00);
DEFINE_SEVENZIP_GUID(IID_IArchiveOpenVolumeCallback, 0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x30, 0x00, 0x00);
DEFINE_SEVENZIP_GUID(IID_IInArchive,               0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x60, 0x00, 0x00);
DEFINE_SEVENZIP_GUID(IID_ICryptoGetTextPassword,   0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x05, 0x00, 0x10, 0x00, 0x00);
DEFINE_SEVENZIP_GUID(IID_ICryptoGetTextPassword2,  0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x05, 0x00, 0x11, 0x00, 0x00);

namespace NArchive {
    namespace NHandlerPropID {
        enum {
            kName = 0,
            kClassID,
            kExtension,
            kAddExtension,
            kUpdate,
            kKeepName,
            kSignature,
            kMultiSignature,
            kSignatureOffset,
            kAltStreams,
            kNtSecure,
            kFlags
        };
    }
}

namespace NExtract {
    namespace NAskMode {
        enum {
            kExtract = 0,
            kTest,
            kSkip
        };
    }
    namespace NOperationResult {
        enum {
            kOK = 0,
            kUnsupportedMethod,
            kDataError,
            kCRCError,
            kUnavailable,
            kUnexpectedEnd,
            kDataAfterEnd,
            kIsNotArc,
            kHeadersError,
            kWrongPassword
        };
    }
}

enum SevenZipPropID {
    kpidNoProperty = 0,
    kpidMainSubfile = 1,
    kpidHandlerId = 2,
    kpidPath = 3,
    kpidName = 4,
    kpidExtension = 5,
    kpidIsDir = 6,
    kpidSize = 7,
    kpidPackSize = 8,
    kpidAttrib = 9,
    kpidCTime = 10,
    kpidATime = 11,
    kpidMTime = 12,
    kpidSolid = 13,
    kpidCommented = 14,
    kpidEncrypted = 15,
    kpidSplitBefore = 16,
    kpidSplitAfter = 17,
    kpidDictionarySize = 18,
    kpidCRC = 19,
    kpidType = 20,
    kpidIsAnti = 21,
    kpidMethod = 22,
    kpidHostOS = 23,
    kpidFileSystem = 24,
    kpidUser = 25,
    kpidGroup = 26,
    kpidBlock = 27,
    kpidComment = 28,
    kpidPosition = 29,
    kpidPrefix = 30,
    kpidNumSubDirs = 31,
    kpidNumSubFiles = 32,
    kpidUnpackVer = 33,
    kpidVolume = 34,
    kpidIsVolume = 35,
    kpidOffset = 36,
    kpidLinks = 37,
    kpidNumBlocks = 38,
    kpidNumVolumes = 39,
    kpidTimeType = 40,
    kpidBit64 = 41,
    kpidBigEndian = 42,
    kpidCpu = 43,
    kpidPhySize = 44,
    kpidHeadersSize = 45,
    kpidChecksum = 46,
    kpidCharacts = 47,
    kpidVa = 48,
    kpidId = 49,
    kpidShortName = 50,
    kpidCreatorApp = 51,
    kpidSectorSize = 52,
    kpidPosixAttrib = 53,
    kpidSymLink = 54,
    kpidError = 55,
    kpidTotalSize = 56,
    kpidFreeSpace = 57,
    kpidClusterSize = 58,
    kpidVolumeName = 59,
    kpidLocalName = 60,
    kpidProvider = 61,
    kpidNtSecure = 62,
    kpidIsAltStream = 63,
    kpidIsAux = 64,
    kpidIsDeleted = 65,
    kpidIsTree = 66,
    kpidSha1 = 67,
    kpidSha256 = 68,
    kpidErrorType = 69,
    kpidNumErrors = 70,
    kpidErrorFlags = 71,
    kpidWarningFlags = 72,
    kpidWarning = 73,
    kpidNumStreams = 74
};

// COM interfaces for 7-Zip
MIDL_INTERFACE("23170F69-40C1-278A-0000-000300010000")
ISequentialInStream : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Read(void *data, uint32_t size, uint32_t *processedSize) = 0;
};

MIDL_INTERFACE("23170F69-40C1-278A-0000-000300020000")
ISequentialOutStream : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Write(const void *data, uint32_t size, uint32_t *processedSize) = 0;
};

MIDL_INTERFACE("23170F69-40C1-278A-0000-000300030000")
IInStream : public ISequentialInStream {
    virtual HRESULT STDMETHODCALLTYPE Seek(int64_t offset, uint32_t seekOrigin, uint64_t *newPosition) = 0;
};

MIDL_INTERFACE("23170F69-40C1-278A-0000-000300040000")
IOutStream : public ISequentialOutStream {
    virtual HRESULT STDMETHODCALLTYPE Seek(int64_t offset, uint32_t seekOrigin, uint64_t *newPosition) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetSize(uint64_t newSize) = 0;
};

MIDL_INTERFACE("23170F69-40C1-278A-0000-000300060000")
IStreamGetSize : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE GetSize(uint64_t *size) = 0;
};

MIDL_INTERFACE("23170F69-40C1-278A-0000-000600100000")
IArchiveOpenCallback : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SetTotal(const uint64_t *files, const uint64_t *bytes) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetCompleted(const uint64_t *files, const uint64_t *bytes) = 0;
};

MIDL_INTERFACE("23170F69-40C1-278A-0000-000600300000")
IArchiveOpenVolumeCallback : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE GetProperty(PROPID propID, PROPVARIANT *value) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetStream(const wchar_t *name, IInStream **inStream) = 0;
};

MIDL_INTERFACE("23170F69-40C1-278A-0000-000600200000")
IArchiveExtractCallback : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SetTotal(uint64_t total) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetCompleted(const uint64_t *completeValue) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetStream(uint32_t index, ISequentialOutStream **outStream, int32_t askExtractMode) = 0;
    virtual HRESULT STDMETHODCALLTYPE PrepareOperation(int32_t askExtractMode) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetOperationResult(int32_t resultEOperationResult) = 0;
};

MIDL_INTERFACE("23170F69-40C1-278A-0000-000500100000")
ICryptoGetTextPassword : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE CryptoGetTextPassword(BSTR *password) = 0;
};

MIDL_INTERFACE("23170F69-40C1-278A-0000-000500110000")
ICryptoGetTextPassword2 : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE CryptoGetTextPassword2(int32_t *passwordIsDefined, BSTR *password) = 0;
};

MIDL_INTERFACE("23170F69-40C1-278A-0000-000600030000")
IArchiveSetProperties : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SetProperties(const wchar_t * const *names, const PROPVARIANT *values, uint32_t numProps) = 0;
};

MIDL_INTERFACE("23170F69-40C1-278A-0000-000600600000")
IInArchive : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Open(IInStream *stream, const uint64_t *maxCheckStartPosition, IArchiveOpenCallback *openCallback) = 0;
    virtual HRESULT STDMETHODCALLTYPE Close() = 0;
    virtual HRESULT STDMETHODCALLTYPE GetNumberOfItems(uint32_t *numItems) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetProperty(uint32_t index, PROPID propID, PROPVARIANT *value) = 0;
    virtual HRESULT STDMETHODCALLTYPE Extract(const uint32_t *indices, uint32_t numItems, int32_t testMode, IArchiveExtractCallback *extractCallback) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetArchiveProperty(PROPID propID, PROPVARIANT *value) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetNumberOfProperties(uint32_t *numProperties) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetPropertyInfo(uint32_t index, BSTR *name, PROPID *propID, VARTYPE *varType) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetNumberOfArchiveProperties(uint32_t *numProperties) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetArchivePropertyInfo(uint32_t index, BSTR *name, PROPID *propID, VARTYPE *varType) = 0;
};

typedef HRESULT (WINAPI *Func_CreateObject)(const GUID *clsID, const GUID *interfaceID, void **outObject);
typedef HRESULT (WINAPI *Func_GetNumberOfFormats)(uint32_t *numFormats);
typedef HRESULT (WINAPI *Func_GetHandlerProperty2)(uint32_t formatIndex, PROPID propID, PROPVARIANT *value);
