#pragma once

#include <algorithm>
#include <cwctype>
#include <sstream>
#include <string>
#include <string_view>
#include <vector>

enum class ArchiveKind { Archive, Stream, TarWrapper, Split };

struct ArchiveRoute {
    ArchiveKind kind = ArchiveKind::Archive;
    std::wstring suffix, baseName;
    std::vector<std::wstring> handlerHints;
};

struct ArchiveFormatEntry {
    std::wstring_view suffix;
    ArchiveKind kind;
    const wchar_t* handlerHints;
};

#include "ArchiveFormatTable.g.h"

inline ArchiveRoute ClassifyArchiveName(std::wstring_view name) {
    std::wstring lower(name);
    std::transform(lower.begin(), lower.end(), lower.begin(),
        [](wchar_t value) { return static_cast<wchar_t>(std::towlower(value)); });
    for (const auto& entry : ArchiveFormatTable) {
        if (!lower.ends_with(entry.suffix)) continue;
        ArchiveRoute route{entry.kind, std::wstring(entry.suffix),
            std::wstring(name.substr(0, name.size() - entry.suffix.size())), {}};
        std::wistringstream hints(entry.handlerHints);
        for (std::wstring hint; hints >> hint;) route.handlerHints.push_back(std::move(hint));
        return route;
    }
    return {};
}

inline std::wstring DefaultStreamName(const ArchiveRoute& route, std::wstring_view leaf) {
    if (route.kind == ArchiveKind::TarWrapper && !route.baseName.empty()) return route.baseName + L".tar";
    if (route.kind == ArchiveKind::Stream || route.kind == ArchiveKind::Split) {
        const auto dot = leaf.find_last_of(L'.');
        if (dot != std::wstring_view::npos && dot != 0) return std::wstring(leaf.substr(0, dot));
    }
    return L"extracted_file";
}
