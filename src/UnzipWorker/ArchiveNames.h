#pragma once

// 归档原始名称读取：直接从 ZIP 中央目录 / TAR 头部读取未经解码的文件名字节，
// 从而尊重格式已声明的 Unicode 名称（ZIP 通用标志位 bit 11），并对缺少可靠编码
// 信息的名称做候选解码，而不是对引擎给出的名称做无条件启发式逆转码。

#include <windows.h>
#include <algorithm>
#include <cstdint>
#include <fstream>
#include <string>
#include <vector>

class ArchiveNames {
public:
    struct RawEntry {
        std::string raw;          // 归档内原始名称字节
        bool declaredUtf8 = false; // ZIP bit 11（或 TAR 的显式 UTF-8 约定）
        bool isDir = false;
    };

    // 按文件头判断格式（不依赖扩展名，兼容无原名/别名场景）
    static bool LooksLikeZip(const std::wstring& path) {
        unsigned char sig[4] = {0};
        if (!ReadHead(path, sig, sizeof(sig))) return false;
        return sig[0] == 0x50 && sig[1] == 0x4b && (sig[2] == 0x03 || sig[2] == 0x05 || sig[2] == 0x07);
    }

    static bool LooksLikeTar(const std::wstring& path) {
        unsigned char hdr[512] = {0};
        if (!ReadHead(path, hdr, sizeof(hdr))) return false;
        // ustar magic 位于 257 偏移；同时接受 GNU tar 的 "ustar  " 形式
        if (memcmp(hdr + 257, "ustar", 5) == 0) return true;
        // 空 TAR（仅含结束块）也视为合法 TAR
        bool allZero = true;
        for (int i = 0; i < 512; ++i) {
            if (hdr[i] != 0) { allZero = false; break; }
        }
        return allZero;
    }

    // ZIP：解析 EOCD -> 中央目录，逐条读取原始名称与 bit 11 标志
    static bool ReadZipNames(const std::wstring& path, std::vector<RawEntry>& out) {
        out.clear();
        std::ifstream f(path, std::ios::binary);
        if (!f) return false;

        f.seekg(0, std::ios::end);
        std::streamoff fileSize = (std::streamoff)f.tellg();
        if (fileSize < 22) return false;

        std::streamoff tailLen = std::min<std::streamoff>(fileSize, 66000);
        std::vector<unsigned char> tail((size_t)tailLen);
        f.seekg(fileSize - tailLen);
        f.read(reinterpret_cast<char*>(tail.data()), tailLen);
        if (!f) return false;

        std::streamoff eocd = -1;
        for (std::streamoff i = tailLen - 22; i >= 0; --i) {
            if (tail[(size_t)i] == 0x50 && tail[(size_t)i + 1] == 0x4b &&
                tail[(size_t)i + 2] == 0x05 && tail[(size_t)i + 3] == 0x06) {
                eocd = i;
                break;
            }
        }
        if (eocd < 0) return false;

        uint64_t entryCount = Le16(&tail[(size_t)eocd + 10]);
        uint64_t cdSize = Le32(&tail[(size_t)eocd + 12]);
        uint64_t cdOffset = Le32(&tail[(size_t)eocd + 16]);

        // ZIP64 扩展信息
        if (entryCount == 0xFFFF || cdOffset == 0xFFFFFFFF) {
            std::streamoff locatorPos = eocd - 20;
            if (locatorPos >= 0 && Le32(&tail[(size_t)locatorPos]) == 0x07064b50u) {
                uint64_t z64Offset = Le64(&tail[(size_t)locatorPos + 8]);
                unsigned char z64[56] = {0};
                f.clear();
                f.seekg((std::streamoff)z64Offset);
                f.read(reinterpret_cast<char*>(z64), sizeof(z64));
                if (f && Le32(z64) == 0x06064b50u) {
                    entryCount = Le64(z64 + 32);
                    cdSize = Le64(z64 + 40);
                    cdOffset = Le64(z64 + 48);
                }
            }
        }

        if (cdOffset == 0 || cdSize == 0 || cdOffset >= (uint64_t)fileSize) return false;
        if (cdSize > 64ull * 1024 * 1024) return false; // 中央目录异常过大，放弃解析

        std::vector<unsigned char> cd((size_t)cdSize);
        f.clear();
        f.seekg((std::streamoff)cdOffset);
        f.read(reinterpret_cast<char*>(cd.data()), (std::streamsize)cdSize);
        if (!f) return false;

        size_t pos = 0;
        uint64_t parsed = 0;
        while (pos + 46 <= cd.size() && parsed < entryCount) {
            if (Le32(&cd[pos]) != 0x02014b50u) break;
            uint16_t flags = Le16(&cd[pos + 8]);
            uint16_t nameLen = Le16(&cd[pos + 28]);
            uint16_t extraLen = Le16(&cd[pos + 30]);
            uint16_t commentLen = Le16(&cd[pos + 32]);
            if (pos + 46 + nameLen > cd.size()) break;

            RawEntry e;
            e.raw.assign(reinterpret_cast<const char*>(&cd[pos + 46]), nameLen);
            e.declaredUtf8 = (flags & 0x0800) != 0;
            e.isDir = !e.raw.empty() && (e.raw.back() == '/' || e.raw.back() == '\\');
            out.push_back(e);

            pos += 46ull + nameLen + extraLen + commentLen;
            ++parsed;
        }

        return !out.empty() && parsed == entryCount;
    }

    // TAR：逐块解析头部，支持 ustar 前缀、GNU 长名与 pax path
    static bool ReadTarNames(const std::wstring& path, std::vector<RawEntry>& out) {
        out.clear();
        std::ifstream f(path, std::ios::binary);
        if (!f) return false;

        std::string pendingLongName;
        int zeroBlocks = 0;

        for (int guard = 0; guard < 1000000; ++guard) {
            unsigned char hdr[512] = {0};
            f.read(reinterpret_cast<char*>(hdr), sizeof(hdr));
            if (f.gcount() != 512) break;

            bool allZero = true;
            for (int i = 0; i < 512; ++i) {
                if (hdr[i] != 0) { allZero = false; break; }
            }
            if (allZero) {
                if (++zeroBlocks >= 2) break;
                continue;
            }
            zeroBlocks = 0;

            char type = (char)hdr[156];
            uint64_t size = ParseOctal(hdr + 124, 12);

            if (type == 'L') { // GNU 长名：数据块即完整名称
                std::string name = ReadBlocks(f, size);
                TrimTrailingNul(name);
                pendingLongName = name;
                continue;
            }
            if (type == 'x' || type == 'X') { // pax 扩展头：寻找 path=
                std::string data = ReadBlocks(f, size);
                size_t p = data.find("path=");
                if (p != std::string::npos) {
                    size_t begin = p + 5;
                    size_t end = data.find('\n', begin);
                    if (end == std::string::npos) end = data.size();
                    pendingLongName = data.substr(begin, end - begin);
                }
                continue;
            }
            if (type == 'g') { // 全局 pax 头，忽略内容
                ReadBlocks(f, size);
                continue;
            }

            std::string name;
            if (!pendingLongName.empty()) {
                name = pendingLongName;
                pendingLongName.clear();
            } else {
                name.assign(reinterpret_cast<const char*>(hdr), 100);
                TrimTrailingNul(name);
                std::string prefix(reinterpret_cast<const char*>(hdr + 345), 155);
                TrimTrailingNul(prefix);
                if (!prefix.empty()) name = prefix + "/" + name;
            }

            // TAR 名称按约定使用 UTF-8；此处只记录原始字节，解码在 NameDecoder 中完成
            RawEntry e;
            e.raw = name;
            e.declaredUtf8 = false;
            e.isDir = (type == '5');
            if (!name.empty()) out.push_back(e);

            // 跳过数据区（512 对齐）
            uint64_t skip = ((size + 511) / 512) * 512;
            if (skip > 0) {
                f.seekg((std::streamoff)skip, std::ios::cur);
                if (!f) break;
            }
        }

        return true;
    }

private:
    static bool ReadHead(const std::wstring& path, unsigned char* buf, size_t len) {
        std::ifstream f(path, std::ios::binary);
        if (!f) return false;
        f.read(reinterpret_cast<char*>(buf), (std::streamsize)len);
        return (size_t)f.gcount() == len;
    }

    static uint16_t Le16(const unsigned char* p) {
        return (uint16_t)(p[0] | (p[1] << 8));
    }
    static uint32_t Le32(const unsigned char* p) {
        return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
    }
    static uint64_t Le64(const unsigned char* p) {
        uint64_t v = 0;
        for (int i = 7; i >= 0; --i) v = (v << 8) | p[i];
        return v;
    }

    static uint64_t ParseOctal(const unsigned char* p, size_t len) {
        if (len > 0 && (p[0] & 0x80)) {
            // base-256 编码（大文件）
            uint64_t v = p[0] & 0x7F;
            for (size_t i = 1; i < len; ++i) v = (v << 8) | p[i];
            return v;
        }
        uint64_t v = 0;
        for (size_t i = 0; i < len; ++i) {
            if (p[i] < '0' || p[i] > '7') continue;
            v = v * 8 + (uint64_t)(p[i] - '0');
        }
        return v;
    }

    static std::string ReadBlocks(std::ifstream& f, uint64_t size) {
        std::string data;
        data.resize((size_t)size);
        if (size > 0) f.read(&data[0], (std::streamsize)size);
        uint64_t pad = ((size + 511) / 512) * 512 - size;
        if (pad > 0) f.seekg((std::streamoff)pad, std::ios::cur);
        return data;
    }

    static void TrimTrailingNul(std::string& s) {
        while (!s.empty() && s.back() == '\0') s.pop_back();
    }
};
