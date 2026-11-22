#pragma once

#include "SevenZip.h"
#include <functional>
#include <limits>
#include <optional>

namespace Lz4FrameSize {

// The limited scan proves only length. The ZS decoder checks the frame and content.
inline std::optional<uint64_t> ScanStoredFrame(IInStream& stream, uint64_t physicalSize,
    const std::function<bool()>& continueScan) {
    uint64_t position = 0;
    auto read = [&](uint8_t* data, uint32_t size) {
        if (size > physicalSize - position) return false;
        while (size != 0) {
            uint32_t count = 0;
            if (stream.Read(data, size, &count) != S_OK || count == 0 || count > size) return false;
            data += count;
            size -= count;
            position += count;
        }
        return true;
    };
    auto skip = [&](uint64_t size) {
        if (size > physicalSize - position) return false;
        position += size;
        uint64_t actual = 0;
        return stream.Seek(static_cast<int64_t>(position), FILE_BEGIN, &actual) == S_OK && actual == position;
    };
    auto littleEndian = [](const uint8_t* bytes, unsigned length) {
        uint64_t value = 0;
        for (unsigned i = 0; i < length; ++i) value |= uint64_t(bytes[i]) << (8 * i);
        return value;
    };

    uint8_t header[6];
    if (!read(header, sizeof(header)) || littleEndian(header, 4) != 0x184D2204) return {};
    const uint8_t flags = header[4];
    const uint8_t descriptor = header[5];
    const unsigned blockCode = (descriptor >> 4) & 7;
    if ((flags & 0xC3) != 0x40 || (descriptor & 0x8F) != 0 || blockCode < 4 || blockCode > 7)
        return {};
    const uint32_t maxBlock = uint32_t{1} << (8 + 2 * blockCode);
    std::optional<uint64_t> declaredSize;
    if (flags & 0x08) {
        uint8_t contentSize[8];
        if (!read(contentSize, sizeof(contentSize))) return {};
        declaredSize = littleEndian(contentSize, sizeof(contentSize));
    }
    uint8_t headerChecksum;
    if (!read(&headerChecksum, 1)) return {};

    uint64_t total = 0;
    for (unsigned blocks = 0; blocks <= 65536; ++blocks) {
        if (continueScan && !continueScan()) return {};
        uint8_t lengthBytes[4];
        if (!read(lengthBytes, sizeof(lengthBytes))) return {};
        const uint32_t field = static_cast<uint32_t>(littleEndian(lengthBytes, 4));
        if (field == 0) {
            if ((flags & 0x04) && !skip(4)) return {};
            if (position != physicalSize || (declaredSize && *declaredSize != total)) return {};
            return total;
        }
        const uint32_t length = field & 0x7FFFFFFF;
        if ((field & 0x80000000) == 0 || length > maxBlock || blocks == 65536 ||
            length > (std::numeric_limits<uint64_t>::max)() - total ||
            !skip(uint64_t(length) + ((flags & 0x10) ? 4 : 0))) return {};
        total += length;
    }
    return {};
}
}

inline std::optional<uint64_t> TryGetStoredLz4FrameSize(IInStream& stream, uint64_t physicalSize,
    bool& restored, const std::function<bool()>& continueScan) {
    restored = false;
    const auto maxPosition = static_cast<uint64_t>((std::numeric_limits<int64_t>::max)());
    uint64_t saved = 0;
    if (stream.Seek(0, FILE_CURRENT, &saved) != S_OK || saved > maxPosition) return {};
    uint64_t position = 0;
    std::optional<uint64_t> size;
    if (physicalSize <= maxPosition && stream.Seek(0, FILE_BEGIN, &position) == S_OK && position == 0)
        size = Lz4FrameSize::ScanStoredFrame(stream, physicalSize, continueScan);
    restored = stream.Seek(static_cast<int64_t>(saved), FILE_BEGIN, &position) == S_OK && saved == position;
    return restored ? size : std::nullopt;
}

inline std::optional<uint64_t> TryGetStoredLz4FrameSize(IInStream& stream, uint64_t physicalSize) {
    bool restored = false;
    return TryGetStoredLz4FrameSize(stream, physicalSize, restored, {});
}
