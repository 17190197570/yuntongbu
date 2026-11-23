// Stage 1: a read-only local source exercises the actual Windows Cloud Files API.
// File payloads are only transferred by CfExecute; no ordinary copies create placeholders.
#include <windows.h>
#include <winternl.h>
#include <cfapi.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cwctype>
#include <iomanip>
#include <iostream>
#include <map>
#include <memory>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <tuple>
#include <utility>
#include <vector>

namespace {
constexpr wchar_t kProvider[] = L"CloudDrive Lite Local Prototype";
constexpr wchar_t kRootPrefix[] = L"CloudDriveLite.LocalPrototype.v1\n";
constexpr DWORD kChunkSize = 1024 * 1024; // Also 4 KiB aligned, as CFAPI requires.
constexpr NTSTATUS kCloudFailure = static_cast<NTSTATUS>(0xC000CF12L); // STATUS_CLOUD_FILE_UNSUCCESSFUL
constexpr NTSTATUS kCloudCancelled = static_cast<NTSTATUS>(0xC000CF1BL);
constexpr NTSTATUS kDehydrationDisallowed = static_cast<NTSTATUS>(0xC000CF20L);
constexpr GUID kProviderId = {0x03c53dcb, 0x6614, 0x4826, {0xa3, 0xda, 0xa5, 0xb7, 0x0a, 0x36, 0x51, 0x8f}};
std::mutex gLogMutex;
HANDLE gStopEvent = nullptr;
std::wstring gSmbPipe;
DWORD gAgentPid = 0;
std::uint64_t gSmbPlaceholderCount = 0;
bool IsSmb(const std::wstring& source) { return source.compare(0, 4, L"smb:") == 0; }

std::string Utf8(const std::wstring& value) {
    if (value.empty()) return {};
    int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (!count) throw std::runtime_error("Invalid UTF-16 text.");
    std::string result(count, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), result.data(), count, nullptr, nullptr);
    return result;
}

void Log(const std::string& value) {
    std::lock_guard<std::mutex> lock(gLogMutex);
    std::cout << value << std::endl;
}

std::string Hex(HRESULT value) {
    std::ostringstream out;
    out << "0x" << std::hex << std::uppercase << std::setw(8) << std::setfill('0')
        << static_cast<unsigned long>(value);
    return out.str();
}

void Check(HRESULT result, const char* operation) {
    if (FAILED(result)) throw std::runtime_error(std::string(operation) + " failed: " + Hex(result));
}

void WinCheck(BOOL value, const char* operation) {
    if (!value) Check(HRESULT_FROM_WIN32(GetLastError()), operation);
}

struct Handle {
    HANDLE value = INVALID_HANDLE_VALUE;
    Handle() = default;
    explicit Handle(HANDLE h) : value(h) {}
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value(std::exchange(other.value, INVALID_HANDLE_VALUE)) {}
    ~Handle() { if (value != INVALID_HANDLE_VALUE && value != nullptr) CloseHandle(value); }
    operator HANDLE() const { return value; }
};

struct ProtectedHandle {
    HANDLE value = INVALID_HANDLE_VALUE;
    explicit ProtectedHandle(const std::wstring& path, bool writeAccess = true) {
        auto flags = CF_OPEN_FILE_FLAG_EXCLUSIVE;
        if (writeAccess) flags |= CF_OPEN_FILE_FLAG_WRITE_ACCESS;
        Check(CfOpenFileWithOplock(path.c_str(), flags, &value), "CfOpenFileWithOplock (file may be in use)");
    }
    ~ProtectedHandle() { if (value != INVALID_HANDLE_VALUE) CfCloseHandle(value); }
};

struct ProtectedReference {
    HANDLE value;
    bool held = true;
    explicit ProtectedReference(HANDLE handle) : value(handle) {
        if (!CfReferenceProtectedHandle(value))
            throw std::runtime_error("The file was opened by another process; release cancelled.");
    }
    void Release() { if (held) { CfReleaseProtectedHandle(value); held = false; } }
    ~ProtectedReference() { Release(); }
};

std::wstring Lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t c) { return std::towlower(c); });
    return value;
}

std::wstring FullPath(const std::wstring& input) {
    if (input.size() < 3 || input[1] != L':' || (input[2] != L'\\' && input[2] != L'/') ||
        !std::iswalpha(input[0]))
        throw std::runtime_error("Use an absolute local drive path, for example C:\\CloudDriveDemo.");
    if (input.find_first_of(L"\r\n\t") != std::wstring::npos || input.find(L':', 2) != std::wstring::npos)
        throw std::runtime_error("Alternate streams and control characters are not allowed in paths.");
    DWORD needed = GetFullPathNameW(input.c_str(), 0, nullptr, nullptr);
    WinCheck(needed != 0, "GetFullPathName");
    std::wstring path(needed, L'\0');
    DWORD used = GetFullPathNameW(input.c_str(), needed, path.data(), nullptr);
    WinCheck(used != 0 && used < needed, "GetFullPathName");
    path.resize(used);
    std::replace(path.begin(), path.end(), L'/', L'\\');
    while (path.size() > 3 && path.back() == L'\\') path.pop_back();
    if (path.size() <= 3) throw std::runtime_error("A drive root cannot be a source or sync root.");
    return path;
}

std::wstring Extended(const std::wstring& path) { return L"\\\\?\\" + path; }

bool IsWithin(const std::wstring& path, const std::wstring& parent) {
    const auto p = Lower(path), base = Lower(parent);
    return p == base || (p.size() > base.size() && p.compare(0, base.size(), base) == 0 && p[base.size()] == L'\\');
}

void RelativePathCheck(const std::wstring& relative) {
    if (relative.empty() || relative.front() == L'\\' || relative.front() == L'/' ||
        relative.find_first_of(L":/\r\n\t") != std::wstring::npos)
        throw std::runtime_error("Expected a relative file path without traversal or alternate streams.");
    size_t begin = 0;
    while (begin < relative.size()) {
        size_t end = relative.find(L'\\', begin);
        if (end == std::wstring::npos) end = relative.size();
        auto part = relative.substr(begin, end - begin);
        if (part.empty() || part == L"." || part == L".." || part.back() == L'.' || part.back() == L' ')
            throw std::runtime_error("Unsafe or ambiguous relative path component.");
        begin = end + 1;
    }
    if (relative.back() == L'\\') throw std::runtime_error("A relative file path cannot end with a separator.");
}

// Source paths reject all reparse points. Root descendants permit cloud placeholders only.
void CheckPathChain(const std::wstring& path, const std::wstring& root, bool allowCloud, bool allowMissingLeaf = false) {
    size_t end = 3;
    while (end <= path.size()) {
        if (end > 3) {
            const auto component = path.substr(0, end);
            DWORD attrs = GetFileAttributesW(Extended(component).c_str());
            if (attrs == INVALID_FILE_ATTRIBUTES) {
                DWORD error = GetLastError();
                if (allowMissingLeaf && end == path.size() &&
                    (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)) return;
                Check(HRESULT_FROM_WIN32(error), "GetFileAttributes (path chain)");
            }
            if (attrs & FILE_ATTRIBUTE_REPARSE_POINT) {
                Handle file(CreateFileW(Extended(component).c_str(), FILE_READ_ATTRIBUTES,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
                WinCheck(file.value != INVALID_HANDLE_VALUE, "Open reparse metadata");
                FILE_ATTRIBUTE_TAG_INFO tag{};
                WinCheck(GetFileInformationByHandleEx(file, FileAttributeTagInfo, &tag, sizeof(tag)), "Read reparse tag");
                auto state = CfGetPlaceholderStateFromAttributeTag(tag.FileAttributes, tag.ReparseTag);
                const bool cloud = state != CF_PLACEHOLDER_STATE_INVALID &&
                    ((state & CF_PLACEHOLDER_STATE_PLACEHOLDER) || (state & CF_PLACEHOLDER_STATE_SYNC_ROOT));
                if (!allowCloud || !IsWithin(component, root) || !cloud)
                    throw std::runtime_error("Reparse points, symlinks and junctions are not allowed: " + Utf8(component));
            }
        }
        if (end == path.size()) break;
        end = path.find(L'\\', end + 1);
        if (end == std::wstring::npos) end = path.size();
    }
}

void ValidateVolume(const std::wstring& root) {
    wchar_t volume[MAX_PATH]{};
    WinCheck(GetVolumePathNameW(root.c_str(), volume, MAX_PATH), "GetVolumePathName");
    if (GetDriveTypeW(volume) != DRIVE_FIXED)
        throw std::runtime_error("The prototype requires a local fixed disk.");
    wchar_t fs[MAX_PATH]{};
    WinCheck(GetVolumeInformationW(volume, nullptr, 0, nullptr, nullptr, nullptr, fs, MAX_PATH), "GetVolumeInformation");
    if (_wcsicmp(fs, L"NTFS") != 0) throw std::runtime_error("The prototype requires an NTFS volume.");
}

Handle OpenMetadata(const std::wstring& path) {
    Handle file(CreateFileW(Extended(path).c_str(), FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    WinCheck(file.value != INVALID_HANDLE_VALUE, "Open metadata (file may be changing)");
    return file;
}

struct Metadata {
    FILE_BASIC_INFO basic{};
    FILE_STANDARD_INFO standard{};
    FILE_ID_INFO id{};
};

Metadata GetMetadata(HANDLE file, bool source) {
    Metadata data;
    WinCheck(GetFileInformationByHandleEx(file, FileBasicInfo, &data.basic, sizeof(data.basic)), "Read file basic information");
    WinCheck(GetFileInformationByHandleEx(file, FileStandardInfo, &data.standard, sizeof(data.standard)), "Read file size");
    WinCheck(GetFileInformationByHandleEx(file, FileIdInfo, &data.id, sizeof(data.id)), "Read file identity");
    if (source && (data.basic.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT))
        throw std::runtime_error("A source reparse point is not supported.");
    return data;
}

std::string IdText(const FILE_ID_INFO& id) {
    std::ostringstream out;
    out << std::hex << std::setfill('0') << std::setw(16) << id.VolumeSerialNumber;
    for (auto b : id.FileId.Identifier) out << std::setw(2) << static_cast<int>(b);
    return out.str();
}

std::wstring RootIdentity(const std::wstring& source) {
    if (IsSmb(source)) return std::wstring(kRootPrefix) + source + L"\nSQLiteStableMapping.v2";
    auto handle = OpenMetadata(source);
    auto meta = GetMetadata(handle, true);
    if (!meta.standard.Directory) throw std::runtime_error("Source must be a directory.");
    const auto id = IdText(meta.id);
    return std::wstring(kRootPrefix) + source + L"\n" + std::wstring(id.begin(), id.end());
}

struct RootInfo {
    std::wstring identity;
    std::wstring source;
    LONGLONG fileId = 0;
    CF_HYDRATION_POLICY hydration{};
    CF_POPULATION_POLICY population{};
};

bool QueryRoot(const std::wstring& root, RootInfo& result) {
    std::vector<BYTE> bytes(sizeof(CF_SYNC_ROOT_STANDARD_INFO) + 65536);
    DWORD returned = 0;
    HRESULT hr = CfGetSyncRootInfoByPath(root.c_str(), CF_SYNC_ROOT_INFO_STANDARD,
        bytes.data(), static_cast<DWORD>(bytes.size()), &returned);
    if (FAILED(hr)) {
        // CldFlt also returns INVALID_FUNCTION for an ordinary directory without a cloud
        // reparse point. The caller still requires an empty non-reparse path and uses
        // CfRegisterSyncRoot without UPDATE, so this cannot replace an existing provider.
        if (hr == HRESULT_FROM_WIN32(ERROR_NOT_A_CLOUD_FILE) ||
            hr == HRESULT_FROM_WIN32(ERROR_CLOUD_FILE_NOT_UNDER_SYNC_ROOT) ||
            hr == HRESULT_FROM_WIN32(ERROR_NOT_A_CLOUD_SYNC_ROOT) ||
            hr == HRESULT_FROM_WIN32(ERROR_INVALID_FUNCTION)) return false;
        Check(hr, "CfGetSyncRootInfoByPath");
    }
    const auto* info = reinterpret_cast<const CF_SYNC_ROOT_STANDARD_INFO*>(bytes.data());
    if (info->SyncRootIdentityLength > bytes.size() - offsetof(CF_SYNC_ROOT_STANDARD_INFO, SyncRootIdentity) ||
        info->SyncRootIdentityLength % sizeof(wchar_t))
        throw std::runtime_error("Invalid sync-root registration identity.");
    result.identity.assign(reinterpret_cast<const wchar_t*>(info->SyncRootIdentity),
        info->SyncRootIdentityLength / sizeof(wchar_t));
    if (std::wstring(info->ProviderName) != kProvider || result.identity.compare(0, wcslen(kRootPrefix), kRootPrefix) != 0)
        throw std::runtime_error("This directory belongs to another cloud provider.");
    auto handle = OpenMetadata(root);
    BY_HANDLE_FILE_INFORMATION local{};
    WinCheck(GetFileInformationByHandle(handle, &local), "Read sync root file ID");
    const ULONGLONG localId = (static_cast<ULONGLONG>(local.nFileIndexHigh) << 32) | local.nFileIndexLow;
    if (localId != static_cast<ULONGLONG>(info->SyncRootFileId.QuadPart))
        throw std::runtime_error("The requested path is beneath a sync root; provide the exact owned root.");
    const size_t start = wcslen(kRootPrefix);
    size_t end = result.identity.find(L'\n', start);
    if (end == std::wstring::npos) throw std::runtime_error("Malformed owned sync-root identity.");
    result.source = result.identity.substr(start, end - start);
    result.fileId = info->SyncRootFileId.QuadPart;
    result.hydration = info->HydrationPolicy;
    result.population = info->PopulationPolicy;
    return true;
}

struct FileIdentityHeader {
    std::uint32_t magic = 0x314C4443; // CDL1
    std::uint32_t version = 1;
    std::uint32_t pathCharacters = 0;
    std::uint32_t directory = 0;
    ULONGLONG volumeSerial = 0;
    FILE_ID_128 fileId{};
    LONGLONG size = 0;
    LONGLONG creationTime = 0;
    LONGLONG lastWriteTime = 0;
};

struct FileIdentity {
    FileIdentityHeader header;
    std::wstring relative;
};

std::vector<BYTE> MakeIdentity(const Metadata& metadata, const std::wstring& relative) {
    RelativePathCheck(relative);
    size_t length = sizeof(FileIdentityHeader) + relative.size() * sizeof(wchar_t);
    if (length > CF_PLACEHOLDER_MAX_FILE_IDENTITY_LENGTH)
        throw std::runtime_error("Relative path exceeds the prototype identity limit (approximately 2,000 UTF-16 characters).");
    FileIdentityHeader header;
    header.pathCharacters = static_cast<std::uint32_t>(relative.size());
    header.directory = metadata.standard.Directory ? 1 : 0;
    header.volumeSerial = metadata.id.VolumeSerialNumber;
    header.fileId = metadata.id.FileId;
    header.size = metadata.standard.Directory ? 0 : metadata.standard.EndOfFile.QuadPart;
    header.creationTime = metadata.basic.CreationTime.QuadPart;
    header.lastWriteTime = metadata.basic.LastWriteTime.QuadPart;
    std::vector<BYTE> bytes(length);
    std::memcpy(bytes.data(), &header, sizeof(header));
    std::memcpy(bytes.data() + sizeof(header), relative.data(), relative.size() * sizeof(wchar_t));
    return bytes;
}

FileIdentity ParseIdentity(const void* data, DWORD length) {
    if (length < sizeof(FileIdentityHeader) || length > CF_PLACEHOLDER_MAX_FILE_IDENTITY_LENGTH)
        throw std::runtime_error("The file has no supported CloudDrive Lite identity.");
    FileIdentity result;
    std::memcpy(&result.header, data, sizeof(result.header));
    if (result.header.magic != 0x314C4443 || result.header.version != 1 ||
        result.header.directory > 1 || result.header.size < 0 ||
        result.header.pathCharacters != (length - sizeof(FileIdentityHeader)) / sizeof(wchar_t) ||
        (length - sizeof(FileIdentityHeader)) % sizeof(wchar_t))
        throw std::runtime_error("Unsupported or malformed CloudDrive Lite file identity.");
    result.relative.resize(result.header.pathCharacters);
    std::memcpy(result.relative.data(), static_cast<const BYTE*>(data) + sizeof(FileIdentityHeader),
        result.relative.size() * sizeof(wchar_t));
    if (result.relative.find(L'\0') != std::wstring::npos) throw std::runtime_error("Invalid file identity path.");
    RelativePathCheck(result.relative);
    return result;
}

void VerifySource(const FileIdentity& expected, const Metadata& actual) {
    const auto& h = expected.header;
    if (h.directory != static_cast<std::uint32_t>(actual.standard.Directory ? 1 : 0) ||
        h.volumeSerial != actual.id.VolumeSerialNumber ||
        std::memcmp(&h.fileId, &actual.id.FileId, sizeof(h.fileId)) != 0 ||
        (!h.directory && (h.size != actual.standard.EndOfFile.QuadPart ||
        h.creationTime != actual.basic.CreationTime.QuadPart || h.lastWriteTime != actual.basic.LastWriteTime.QuadPart)))
        throw std::runtime_error("Source version changed. The prototype will not overwrite or release this file.");
}

std::vector<BYTE> PlaceholderInfo(HANDLE handle) {
    std::vector<BYTE> bytes(sizeof(CF_PLACEHOLDER_STANDARD_INFO) + CF_PLACEHOLDER_MAX_FILE_IDENTITY_LENGTH);
    DWORD returned = 0;
    Check(CfGetPlaceholderInfo(handle, CF_PLACEHOLDER_INFO_STANDARD,
        bytes.data(), static_cast<DWORD>(bytes.size()), &returned), "CfGetPlaceholderInfo");
    const auto* info = reinterpret_cast<const CF_PLACEHOLDER_STANDARD_INFO*>(bytes.data());
    if (info->FileIdentityLength > bytes.size() - offsetof(CF_PLACEHOLDER_STANDARD_INFO, FileIdentity))
        throw std::runtime_error("Invalid placeholder identity size.");
    return bytes;
}

std::array<BYTE, 32> HashBytes(const BYTE* input, ULONG length) {
    std::array<BYTE, 32> result{};
    if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, const_cast<BYTE*>(input), length,
        result.data(), static_cast<ULONG>(result.size())) < 0)
        throw std::runtime_error("BCryptHash failed.");
    return result;
}

std::wstring HashName(const std::wstring& input) {
    auto value = Lower(input);
    auto hash = HashBytes(reinterpret_cast<const BYTE*>(value.data()), static_cast<ULONG>(value.size() * sizeof(wchar_t)));
    std::wostringstream out;
    out << std::hex << std::setfill(L'0');
    for (BYTE b : hash) out << std::setw(2) << static_cast<int>(b);
    return out.str();
}

std::wstring VerifiedReleaseEventName(const std::wstring& root, const std::wstring& relative) {
    return L"Local\\CloudDriveLite.VerifiedRelease." + HashName(root + L"\n" + relative);
}

// This narrow gate is signalled only while source + local content are locked and
// verified. A normal Explorer dehydrate request has no signalled gate and is denied.
struct VerifiedReleaseGate {
    Handle event;
    VerifiedReleaseGate(const std::wstring& root, const std::wstring& relative)
        : event(CreateEventW(nullptr, TRUE, FALSE, VerifiedReleaseEventName(root, relative).c_str())) {
        WinCheck(event.value != nullptr, "Create verified-release gate");
        WinCheck(ResetEvent(event), "Reset verified-release gate");
        WinCheck(SetEvent(event), "Signal verified-release gate");
    }
    ~VerifiedReleaseGate() { ResetEvent(event); }
};

struct ProviderMutex {
    Handle handle;
    bool owned = false;
    explicit ProviderMutex(const std::wstring& root) : handle(CreateMutexW(nullptr, FALSE,
        (L"Local\\CloudDriveLite.Provider." + HashName(root)).c_str())) {
        WinCheck(handle.value != nullptr, "Create provider mutex");
        DWORD result = WaitForSingleObject(handle, 0);
        if (result != WAIT_OBJECT_0 && result != WAIT_ABANDONED)
            throw std::runtime_error("A provider is already running for this root. Stop it before unregistering.");
        owned = true;
    }
    ~ProviderMutex() { if (owned) ReleaseMutex(handle); }
};

bool Stopping(HANDLE event) { return event && WaitForSingleObject(event, 0) == WAIT_OBJECT_0; }

#include "SmbBridge.h"

void Populate(const std::wstring& source, const std::wstring& root, const std::wstring& relative,
    LONGLONG rootId, HANDLE stop, std::uint64_t& created) {
    if (Stopping(stop)) throw std::runtime_error("Population cancelled.");
    const auto currentSource = relative.empty() ? source : source + L"\\" + relative;
    const auto currentRoot = relative.empty() ? root : root + L"\\" + relative;
    CheckPathChain(currentSource, source, false);
    CheckPathChain(currentRoot, root, true);
    WIN32_FIND_DATAW data{};
    HANDLE find = FindFirstFileExW((Extended(currentSource) + L"\\*").c_str(), FindExInfoBasic,
        &data, FindExSearchNameMatch, nullptr, FIND_FIRST_EX_LARGE_FETCH);
    if (find == INVALID_HANDLE_VALUE) {
        if (GetLastError() == ERROR_FILE_NOT_FOUND) return;
        Check(HRESULT_FROM_WIN32(GetLastError()), "Enumerate source directory");
    }
    struct FindCloser { HANDLE value; ~FindCloser() { FindClose(value); } } closer{find};
    do {
        if (Stopping(stop)) throw std::runtime_error("Population cancelled.");
        std::wstring name = data.cFileName;
        if (name == L"." || name == L"..") continue;
        const auto itemRelative = relative.empty() ? name : relative + L"\\" + name;
        RelativePathCheck(itemRelative);
        if (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)
            throw std::runtime_error("Source contains a reparse point: " + Utf8(itemRelative));
        auto sourceFile = OpenMetadata(source + L"\\" + itemRelative);
        auto metadata = GetMetadata(sourceFile, true);
        auto identity = MakeIdentity(metadata, itemRelative);
        const auto target = root + L"\\" + itemRelative;
        DWORD attrs = GetFileAttributesW(Extended(target).c_str());
        if (attrs == INVALID_FILE_ATTRIBUTES) {
            DWORD error = GetLastError();
            if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND)
                Check(HRESULT_FROM_WIN32(error), "Inspect placeholder target");
            CF_PLACEHOLDER_CREATE_INFO info{};
            info.RelativeFileName = name.c_str();
            info.FsMetadata.BasicInfo = metadata.basic;
            // Source access time is descriptive only. Do not copy provider/control attributes.
            info.FsMetadata.BasicInfo.FileAttributes &= FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_HIDDEN |
                FILE_ATTRIBUTE_SYSTEM | FILE_ATTRIBUTE_ARCHIVE | FILE_ATTRIBUTE_READONLY;
            if (!info.FsMetadata.BasicInfo.FileAttributes) info.FsMetadata.BasicInfo.FileAttributes = FILE_ATTRIBUTE_NORMAL;
            info.FsMetadata.FileSize.QuadPart = metadata.standard.Directory ? 0 : metadata.standard.EndOfFile.QuadPart;
            info.FileIdentity = identity.data();
            info.FileIdentityLength = static_cast<DWORD>(identity.size());
            info.Flags = CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC;
            if (metadata.standard.Directory) info.Flags |= CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION;
            DWORD processed = 0;
            Check(CfCreatePlaceholders(currentRoot.c_str(), &info, 1, CF_CREATE_FLAG_STOP_ON_ERROR,
                &processed), "CfCreatePlaceholders");
            Check(info.Result, "Create placeholder result");
            if (processed != 1) throw std::runtime_error("CFAPI did not create the requested placeholder.");
            ++created;
        } else {
            try {
                CheckPathChain(target, root, true);
                auto existing = OpenMetadata(target);
                auto bytes = PlaceholderInfo(existing);
                const auto* info = reinterpret_cast<const CF_PLACEHOLDER_STANDARD_INFO*>(bytes.data());
                auto currentIdentity = ParseIdentity(info->FileIdentity, info->FileIdentityLength);
                if (info->SyncRootFileId.QuadPart != rootId || currentIdentity.relative != itemRelative)
                    throw std::runtime_error("Existing file belongs to another mapping: " + Utf8(itemRelative));
                VerifySource(currentIdentity, metadata);
            } catch (const std::exception& error) { Log("LOCAL_VERSION_RETAINED " + Utf8(itemRelative) + " " + error.what()); continue; }
        }
        if (metadata.standard.Directory)
            Populate(source, root, itemRelative, rootId, stop, created);
    } while (FindNextFileW(find, &data));
    if (GetLastError() != ERROR_NO_MORE_FILES) Check(HRESULT_FROM_WIN32(GetLastError()), "Enumerate next source file");
}

struct RequestKey {
    LONGLONG transfer;
    LONGLONG request;
    bool operator<(const RequestKey& other) const { return std::tie(transfer, request) < std::tie(other.transfer, other.request); }
};

struct LocalHint { std::string kind; std::wstring path; DWORD pid = 0; std::wstring image; };
struct ProviderContext {
    std::wstring source;
    std::wstring root;
    std::wstring rootIdentity;
    HANDLE stop;
    std::mutex requestsMutex;
    std::map<RequestKey, std::shared_ptr<std::atomic<bool>>> requests;
    std::mutex hintsMutex;
    std::vector<LocalHint> hints;
    std::atomic<bool> hintsOverflow{false};
};

// Completion notifications only append bounded in-memory hints; the Agent persists/reconciles them.
// No SMB, SQLite, stdout or blocking queue operations on CFAPI notification threads.
void QueueLocalHint(const CF_CALLBACK_INFO* callback, const char* kind) noexcept {
    auto* context = static_cast<ProviderContext*>(callback->CallbackContext);
    if (!IsSmb(context->source)) return;
    try {
        if (!callback->FileIdentityLength) return; // Ordinary new files are found by watcher + reconciliation.
        auto identity = ParseIdentity(callback->FileIdentity, callback->FileIdentityLength);
        std::unique_lock<std::mutex> lock(context->hintsMutex, std::try_to_lock);
        if (!lock.owns_lock() || context->hints.size() >= 4096) { context->hintsOverflow.store(true); return; }
        DWORD pid = callback->ProcessInfo ? callback->ProcessInfo->ProcessId : 0;
        std::wstring image;
        if (callback->ProcessInfo && callback->ProcessInfo->ImagePath)
            image.assign(callback->ProcessInfo->ImagePath, wcsnlen_s(callback->ProcessInfo->ImagePath, 4096));
        context->hints.push_back({kind, std::move(identity.relative), pid, std::move(image)});
    } catch (...) { context->hintsOverflow.store(true); }
}
void CALLBACK LocalOpened(const CF_CALLBACK_INFO* callback, const CF_CALLBACK_PARAMETERS*) noexcept { QueueLocalHint(callback, "Opened"); }
void CALLBACK LocalClosed(const CF_CALLBACK_INFO* callback, const CF_CALLBACK_PARAMETERS*) noexcept { QueueLocalHint(callback, "Closed"); }
void CALLBACK LocalRenamed(const CF_CALLBACK_INFO* callback, const CF_CALLBACK_PARAMETERS*) noexcept { QueueLocalHint(callback, "RenameCompleted"); }
void CALLBACK LocalDeleted(const CF_CALLBACK_INFO* callback, const CF_CALLBACK_PARAMETERS*) noexcept { QueueLocalHint(callback, "DeleteCompleted"); }
std::string Json(const std::wstring& value);
void FlushLocalHints(ProviderContext& context) {
    std::vector<LocalHint> pending;
    { std::lock_guard<std::mutex> lock(context.hintsMutex); pending.swap(context.hints); }
    if (context.hintsOverflow.exchange(false)) { Log("LOCAL_HINT Overflow null"); Log("CACHE_HINT {\"Kind\":\"Overflow\"}"); }
    for (const auto& hint : pending) {
        Log("LOCAL_HINT " + hint.kind + " " + Json(hint.path));
        Log("CACHE_HINT {\"Kind\":" + Json(std::wstring(hint.kind.begin(), hint.kind.end())) +
            ",\"Path\":" + Json(hint.path) + ",\"ProcessId\":" + std::to_string(hint.pid) + ",\"ProcessName\":" + Json(hint.image) + "}");
    }
}

HRESULT Transfer(const CF_CALLBACK_INFO* callback, LONGLONG offset, LONGLONG length,
    const void* buffer, NTSTATUS status) noexcept {
    CF_OPERATION_INFO operation{};
    operation.StructSize = sizeof(operation);
    operation.Type = CF_OPERATION_TYPE_TRANSFER_DATA;
    operation.ConnectionKey = callback->ConnectionKey;
    operation.TransferKey = callback->TransferKey;
    operation.RequestKey = callback->RequestKey;
    operation.CorrelationVector = callback->CorrelationVector;
    CF_OPERATION_PARAMETERS parameters{};
    parameters.ParamSize = static_cast<ULONG>(offsetof(CF_OPERATION_PARAMETERS, TransferData) + sizeof(parameters.TransferData));
    parameters.TransferData.Flags = CF_OPERATION_TRANSFER_DATA_FLAG_NONE;
    parameters.TransferData.CompletionStatus = status;
    parameters.TransferData.Buffer = buffer;
    parameters.TransferData.Offset.QuadPart = offset;
    parameters.TransferData.Length.QuadPart = length;
    return CfExecute(&operation, &parameters);
}

void CALLBACK FetchData(const CF_CALLBACK_INFO* callback, const CF_CALLBACK_PARAMETERS* parameters) noexcept {
    auto* context = static_cast<ProviderContext*>(callback->CallbackContext);
    std::shared_ptr<std::atomic<bool>> cancelled;
    const RequestKey key{callback->TransferKey.QuadPart, callback->RequestKey.QuadPart};
    try {
        QueueLocalHint(callback, "Downloading");
        cancelled = std::make_shared<std::atomic<bool>>(false);
        {
            std::lock_guard<std::mutex> lock(context->requestsMutex);
            context->requests[key] = cancelled;
        }
        if (callback->SyncRootIdentityLength != context->rootIdentity.size() * sizeof(wchar_t) ||
            std::memcmp(callback->SyncRootIdentity, context->rootIdentity.data(), callback->SyncRootIdentityLength))
            throw std::runtime_error("Hydration callback root identity mismatch.");
        auto identity = ParseIdentity(callback->FileIdentity, callback->FileIdentityLength);
        if (identity.header.directory || callback->FileSize.QuadPart != identity.header.size)
            throw std::runtime_error("Hydration callback file metadata mismatch.");
        Handle source;
        std::unique_ptr<RemoteRead> remote;
        if (IsSmb(context->source)) remote = std::make_unique<RemoteRead>(identity, context->stop, cancelled.get());
        else {
            auto path = context->source + L"\\" + identity.relative;
            CheckPathChain(path, context->source, false);
            source.value = CreateFileW(Extended(path).c_str(), GENERIC_READ, FILE_SHARE_READ,
                nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
            WinCheck(source.value != INVALID_HANDLE_VALUE, "Open source for hydration");
            VerifySource(identity, GetMetadata(source, true));
        }
        Log("FETCH_DATA " + Utf8(identity.relative) + " bytes=" + std::to_string(identity.header.size));
        std::vector<BYTE> buffer(kChunkSize);
        const LONGLONG requestedOffset = parameters->FetchData.RequiredFileOffset.QuadPart;
        LONGLONG requestedLength = parameters->FetchData.RequiredLength.QuadPart;
        if (requestedLength == CF_EOF) requestedLength = identity.header.size - requestedOffset;
        if (requestedOffset < 0 || requestedOffset > identity.header.size || requestedLength < 0 ||
            requestedLength > identity.header.size - requestedOffset) throw std::runtime_error("Invalid FETCH_DATA range.");
        LONGLONG offset = requestedOffset & ~4095LL;
        LONGLONG end = requestedOffset + requestedLength;
        if (end < identity.header.size) end = (std::min)(identity.header.size, end > MAXLONGLONG - 4095 ? identity.header.size : (end + 4095) & ~4095LL);
        const LONGLONG start = offset;
        if (!remote) { LARGE_INTEGER position{}; position.QuadPart = offset; WinCheck(SetFilePointerEx(source, position, nullptr, FILE_BEGIN), "Seek source range"); }
        while (offset < end) {
            if (cancelled->load() || Stopping(context->stop)) throw std::runtime_error("Hydration cancelled.");
            DWORD wanted = static_cast<DWORD>((std::min)(static_cast<LONGLONG>(kChunkSize), end - offset));
            DWORD read = 0;
            if (remote) { remote->Read(offset, wanted, buffer.data()); read = wanted; }
            else WinCheck(ReadFile(source, buffer.data(), wanted, &read, nullptr), "Read source data");
            if (read != wanted) throw std::runtime_error("Source ended before its recorded size.");
            Check(Transfer(callback, offset, read, buffer.data(), 0), "CfExecute TRANSFER_DATA");
            offset += read;
            LARGE_INTEGER total{}, completed{}; total.QuadPart = end - start; completed.QuadPart = offset - start;
            CfReportProviderProgress(callback->ConnectionKey, callback->TransferKey, total, completed);
        }
        if (!identity.header.size) Check(Transfer(callback, 0, 0, buffer.data(), 0), "Transfer empty file");
        Log("RANGE_COMPLETED " + Utf8(identity.relative) + " offset=" + std::to_string(start) + " length=" + std::to_string(end - start));
        QueueLocalHint(callback, "Hydrated");
    } catch (const std::exception& error) {
        QueueLocalHint(callback, "DownloadFailed");
        try { Log(std::string("HYDRATION_FAILED ") + error.what()); } catch (...) {}
        LONGLONG offset = parameters->FetchData.RequiredFileOffset.QuadPart;
        LONGLONG length = parameters->FetchData.RequiredLength.QuadPart;
        // Error completion covers the entire requested aligned range, including EOF.
        if (length <= 0) length = callback->FileSize.QuadPart > offset ? callback->FileSize.QuadPart - offset : 4096;
        const bool wasCancelled = (cancelled && cancelled->load()) || Stopping(context->stop);
        Transfer(callback, offset, length, nullptr, wasCancelled ? kCloudCancelled : kCloudFailure);
    } catch (...) {
        Transfer(callback, parameters->FetchData.RequiredFileOffset.QuadPart,
            parameters->FetchData.RequiredLength.QuadPart, nullptr, kCloudFailure);
    }
    try {
        std::lock_guard<std::mutex> lock(context->requestsMutex);
        context->requests.erase(key);
    } catch (...) {}
}

void CALLBACK CancelFetch(const CF_CALLBACK_INFO* callback, const CF_CALLBACK_PARAMETERS*) noexcept {
    try {
        auto* context = static_cast<ProviderContext*>(callback->CallbackContext);
        std::lock_guard<std::mutex> lock(context->requestsMutex);
        auto it = context->requests.find({callback->TransferKey.QuadPart, callback->RequestKey.QuadPart});
        if (it != context->requests.end()) it->second->store(true);
    } catch (...) {}
}

// Explorer's own "Free up space" must not bypass the provider's verified release path.
void CALLBACK DenyExternalDehydrate(const CF_CALLBACK_INFO* callback, const CF_CALLBACK_PARAMETERS*) noexcept {
    try { Log("DEHYDRATE_CALLBACK"); } catch (...) {}
    NTSTATUS completion = kDehydrationDisallowed;
    try {
        auto* context = static_cast<ProviderContext*>(callback->CallbackContext);
        if (callback->SyncRootIdentityLength != context->rootIdentity.size() * sizeof(wchar_t) ||
            std::memcmp(callback->SyncRootIdentity, context->rootIdentity.data(), callback->SyncRootIdentityLength))
            throw std::runtime_error("Dehydration callback root identity mismatch.");
        auto identity = ParseIdentity(callback->FileIdentity, callback->FileIdentityLength);
        if (identity.header.directory || callback->FileSize.QuadPart != identity.header.size)
            throw std::runtime_error("Dehydration callback file metadata mismatch.");
        Handle gate(OpenEventW(SYNCHRONIZE, FALSE,
            VerifiedReleaseEventName(context->root, identity.relative).c_str()));
        if (gate.value && WaitForSingleObject(gate, 0) == WAIT_OBJECT_0) completion = 0;
    } catch (...) {
        // An invalid identity, unavailable gate, or allocation failure always denies release.
    }
    CF_OPERATION_INFO operation{};
    operation.StructSize = sizeof(operation);
    operation.Type = CF_OPERATION_TYPE_ACK_DEHYDRATE;
    operation.ConnectionKey = callback->ConnectionKey;
    operation.TransferKey = callback->TransferKey;
    operation.RequestKey = callback->RequestKey;
    CF_OPERATION_PARAMETERS parameters{};
    parameters.ParamSize = static_cast<ULONG>(offsetof(CF_OPERATION_PARAMETERS, AckDehydrate) + sizeof(parameters.AckDehydrate));
    parameters.AckDehydrate.CompletionStatus = completion;
    const auto result = CfExecute(&operation, &parameters);
    try { Log("DEHYDRATE_ACK " + Hex(result) + " status=" + Hex(completion)); } catch (...) {}
}

BOOL WINAPI ConsoleControl(DWORD type) {
    if (type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT || type == CTRL_CLOSE_EVENT ||
        type == CTRL_LOGOFF_EVENT || type == CTRL_SHUTDOWN_EVENT) {
        if (gStopEvent) SetEvent(gStopEvent);
        return TRUE;
    }
    return FALSE;
}

void CALLBACK DenyRemoteDelete(const CF_CALLBACK_INFO* callback, const CF_CALLBACK_PARAMETERS*) noexcept {
    const auto* context = static_cast<ProviderContext*>(callback->CallbackContext);
    CF_OPERATION_INFO operation{}; operation.StructSize = sizeof(operation); operation.Type = CF_OPERATION_TYPE_ACK_DELETE;
    operation.ConnectionKey = callback->ConnectionKey; operation.TransferKey = callback->TransferKey; operation.RequestKey = callback->RequestKey;
    CF_OPERATION_PARAMETERS parameters{}; parameters.ParamSize = static_cast<ULONG>(offsetof(CF_OPERATION_PARAMETERS, AckDelete) + sizeof(parameters.AckDelete));
    parameters.AckDelete.CompletionStatus = IsSmb(context->source) ? static_cast<NTSTATUS>(0xC0000022L) : 0;
    CfExecute(&operation, &parameters);
}
void CALLBACK DenyRemoteRename(const CF_CALLBACK_INFO* callback, const CF_CALLBACK_PARAMETERS*) noexcept {
    const auto* context = static_cast<ProviderContext*>(callback->CallbackContext);
    CF_OPERATION_INFO operation{}; operation.StructSize = sizeof(operation); operation.Type = CF_OPERATION_TYPE_ACK_RENAME;
    operation.ConnectionKey = callback->ConnectionKey; operation.TransferKey = callback->TransferKey; operation.RequestKey = callback->RequestKey;
    CF_OPERATION_PARAMETERS parameters{}; parameters.ParamSize = static_cast<ULONG>(offsetof(CF_OPERATION_PARAMETERS, AckRename) + sizeof(parameters.AckRename));
    parameters.AckRename.CompletionStatus = IsSmb(context->source) ? static_cast<NTSTATUS>(0xC0000022L) : 0;
    CfExecute(&operation, &parameters);
}

void Run(const std::wstring& source, const std::wstring& root, const std::wstring& stopEventName, const std::wstring& refreshName = L"") {
    if (IsWithin(root, source) || IsWithin(source, root))
        throw std::runtime_error("Source and sync root must not overlap.");
    if (!IsSmb(source)) CheckPathChain(source, source, false);
    CheckPathChain(root, root, true, true);
    if (!IsSmb(source)) ValidateVolume(source);
    ProviderMutex mutex(root);
    if (!CreateDirectoryW(Extended(root).c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS)
        Check(HRESULT_FROM_WIN32(GetLastError()), "Create sync root (parent directory must exist)");
    ValidateVolume(root);
    {
        auto rootHandle = OpenMetadata(root);
        auto rootMetadata = GetMetadata(rootHandle, false);
        if (!rootMetadata.standard.Directory) throw std::runtime_error("Sync root must be a directory.");
    }
    auto identity = RootIdentity(source);
    if (identity.size() * sizeof(wchar_t) > 65536) throw std::runtime_error("Source path is too long for sync-root identity.");
    RootInfo rootInfo;
    bool registered = QueryRoot(root, rootInfo);
    if (registered) {
        if (Lower(rootInfo.identity) != Lower(identity))
            throw std::runtime_error("This owned root is registered to a different source or source directory version.");
    } else {
        CheckPathChain(root, root, false);
        WIN32_FIND_DATAW data{};
        HANDLE find = FindFirstFileW((Extended(root) + L"\\*").c_str(), &data);
        if (find != INVALID_HANDLE_VALUE) {
            bool empty = true;
            do { if (wcscmp(data.cFileName, L".") && wcscmp(data.cFileName, L"..")) { empty = false; break; } }
            while (FindNextFileW(find, &data));
            FindClose(find);
            if (!empty) throw std::runtime_error("An unregistered sync root must be empty; existing files are never converted.");
        } else if (GetLastError() != ERROR_FILE_NOT_FOUND) {
            Check(HRESULT_FROM_WIN32(GetLastError()), "Inspect new sync root");
        }
        CF_SYNC_REGISTRATION registration{};
        registration.StructSize = sizeof(registration);
        registration.ProviderName = kProvider;
        registration.ProviderVersion = L"0.1.0";
        registration.ProviderId = kProviderId;
        registration.SyncRootIdentity = identity.data();
        registration.SyncRootIdentityLength = static_cast<DWORD>(identity.size() * sizeof(wchar_t));
        CF_SYNC_POLICIES policies{};
        policies.StructSize = sizeof(policies);
        policies.Hydration.Primary = CF_HYDRATION_POLICY_FULL;
        policies.Hydration.Modifier = CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED;
        policies.Population.Primary = CF_POPULATION_POLICY_ALWAYS_FULL;
        policies.Population.Modifier = CF_POPULATION_POLICY_MODIFIER_NONE;
        policies.InSync = CF_INSYNC_POLICY_TRACK_FILE_LAST_WRITE_TIME | CF_INSYNC_POLICY_TRACK_FILE_CREATION_TIME;
        policies.HardLink = CF_HARDLINK_POLICY_NONE;
        Check(CfRegisterSyncRoot(root.c_str(), &registration, &policies,
            CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT | CF_REGISTER_FLAG_MARK_IN_SYNC_ON_ROOT), "CfRegisterSyncRoot");
        if (!QueryRoot(root, rootInfo)) throw std::runtime_error("The newly registered root cannot be queried.");
        Log("REGISTERED " + Utf8(root));
    }
    if (rootInfo.hydration.Primary != CF_HYDRATION_POLICY_FULL ||
        !(rootInfo.hydration.Modifier & CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED) ||
        rootInfo.population.Primary != CF_POPULATION_POLICY_ALWAYS_FULL) {
        // Update only an exact owned root after validating its source identity. No unregister,
        // deletion, namespace rebuild or InSync reset is involved in this migration.
        CF_SYNC_REGISTRATION registration{}; registration.StructSize = sizeof(registration);
        registration.ProviderName = kProvider; registration.ProviderVersion = L"0.1.0"; registration.ProviderId = kProviderId;
        registration.SyncRootIdentity = identity.data(); registration.SyncRootIdentityLength = static_cast<DWORD>(identity.size() * sizeof(wchar_t));
        CF_SYNC_POLICIES policies{}; policies.StructSize = sizeof(policies);
        policies.Hydration.Primary = CF_HYDRATION_POLICY_FULL;
        policies.Hydration.Modifier = CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED;
        policies.Population.Primary = CF_POPULATION_POLICY_ALWAYS_FULL;
        policies.InSync = CF_INSYNC_POLICY_TRACK_FILE_LAST_WRITE_TIME | CF_INSYNC_POLICY_TRACK_FILE_CREATION_TIME;
        policies.HardLink = CF_HARDLINK_POLICY_NONE;
        Check(CfRegisterSyncRoot(root.c_str(), &registration, &policies, CF_REGISTER_FLAG_UPDATE | CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT), "Update owned root cache policies");
        RootInfo updated; if (!QueryRoot(root, updated) || updated.fileId != rootInfo.fileId || updated.identity != rootInfo.identity)
            throw std::runtime_error("Policy update changed the root identity; refusing to connect.");
        rootInfo = std::move(updated); Log("POLICIES_UPDATED FULL ALWAYS_FULL AUTO_DEHYDRATION_ALLOWED");
    }
    Handle stop(CreateEventW(nullptr, TRUE, FALSE, stopEventName.empty() ? nullptr : stopEventName.c_str()));
    WinCheck(stop.value != nullptr, "Create stop event");
    gStopEvent = stop;
    WinCheck(SetConsoleCtrlHandler(ConsoleControl, TRUE), "Install console control handler");
    struct ConsoleGuard { ~ConsoleGuard() { SetConsoleCtrlHandler(ConsoleControl, FALSE); gStopEvent = nullptr; } } consoleGuard;
    ProviderContext context{source, root, identity, stop};
    const CF_CALLBACK_REGISTRATION callbacks[] = {
        {CF_CALLBACK_TYPE_FETCH_DATA, FetchData},
        {CF_CALLBACK_TYPE_CANCEL_FETCH_DATA, CancelFetch},
        {CF_CALLBACK_TYPE_NOTIFY_DEHYDRATE, DenyExternalDehydrate},
        {CF_CALLBACK_TYPE_NOTIFY_DELETE, DenyRemoteDelete},
        {CF_CALLBACK_TYPE_NOTIFY_RENAME, DenyRemoteRename},
        {CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION, LocalOpened},
        {CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION, LocalClosed},
        {CF_CALLBACK_TYPE_NOTIFY_RENAME_COMPLETION, LocalRenamed},
        {CF_CALLBACK_TYPE_NOTIFY_DELETE_COMPLETION, LocalDeleted},
        CF_CALLBACK_REGISTRATION_END
    };
    CF_CONNECTION_KEY connection{};
    Check(CfConnectSyncRoot(root.c_str(), callbacks, &context,
        CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH | CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION,
        &connection), "CfConnectSyncRoot");
    struct ConnectionGuard {
        CF_CONNECTION_KEY key;
        HANDLE stop;
        ~ConnectionGuard() { SetEvent(stop); CfDisconnectSyncRoot(key); }
    } connectionGuard{connection, stop};
    Check(CfUpdateSyncProviderStatus(connection, CF_PROVIDER_STATUS_POPULATE_NAMESPACE), "Set provider status");
    std::uint64_t created = 0;
    if (IsSmb(source)) PopulateIndexed(root, L"", rootInfo.fileId, stop, created);
    else Populate(source, root, L"", rootInfo.fileId, stop, created);
    Check(CfUpdateSyncProviderStatus(connection, CF_PROVIDER_STATUS_IDLE), "Set provider idle");
    Log("READY root=" + Utf8(root) + " created=" + std::to_string(created) + " placeholders=" + std::to_string(gSmbPlaceholderCount));
    Log("Keep this process running; reading a placeholder now triggers FETCH_DATA. Press Ctrl+C to stop.");
    Handle refresh(refreshName.empty() ? nullptr : CreateEventW(nullptr, FALSE, FALSE, refreshName.c_str()));
    if (!refreshName.empty()) WinCheck(refresh.value != nullptr, "Create refresh event");
    Handle agent(IsSmb(source) ? OpenProcess(SYNCHRONIZE, FALSE, gAgentPid) : nullptr);
    if (IsSmb(source)) WinCheck(agent.value != nullptr, "Monitor Agent lifetime");
    HANDLE events[] = {stop.value, refresh.value, agent.value};
    ULONGLONG nextHealth = 0;
    for (;;) {
        if (GetTickCount64() >= nextHealth) {
            CF_SYNC_PROVIDER_STATUS providerStatus{};
            Check(CfQuerySyncProviderStatus(connection, &providerStatus), "CFAPI health query");
            Log("HEALTH cfapi=connected status=" + std::to_string(static_cast<unsigned>(providerStatus)));
            nextHealth = GetTickCount64() + 10000;
        }
        DWORD wait = WaitForMultipleObjects(agent.value ? 3 : (refresh.value ? 2 : 1), events, FALSE, 250);
        FlushLocalHints(context);
        if (wait == WAIT_TIMEOUT) continue;
        if (wait == WAIT_OBJECT_0 || (agent.value && wait == WAIT_OBJECT_0 + 2)) break;
        if (wait == WAIT_OBJECT_0 + 1 && IsSmb(source)) {
            try { gSmbPlaceholderCount = 0; PopulateIndexed(root, L"", rootInfo.fileId, stop, created); Log("NAMESPACE_REFRESHED created_total=" + std::to_string(created) + " placeholders=" + std::to_string(gSmbPlaceholderCount)); }
            catch (const std::exception& error) { Log(std::string("NAMESPACE_REFRESH_FAILED ") + error.what()); }
        } else WinCheck(FALSE, "Wait for shutdown");
    }
    Log("STOPPING");
}

std::string Json(const std::wstring& value) {
    const auto input = Utf8(value);
    std::ostringstream out;
    out << '"';
    for (unsigned char c : input) {
        if (c == '\\') out << "\\\\";
        else if (c == '"') out << "\\\"";
        else if (c < 0x20) out << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(c);
        else out << c;
    }
    out << '"';
    return out.str();
}

RootInfo RequireRoot(const std::wstring& root) {
    CheckPathChain(root, root, true);
    RootInfo info;
    if (!QueryRoot(root, info)) throw std::runtime_error("This directory is not an owned CloudDrive Lite sync root.");
    return info;
}

void Status(const std::wstring& root, const std::wstring& relative) {
    RelativePathCheck(relative);
    auto rootInfo = RequireRoot(root);
    const auto target = root + L"\\" + relative;
    CheckPathChain(target, root, true);
    Handle file(CreateFileW(Extended(target).c_str(), FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, nullptr));
    WinCheck(file.value != INVALID_HANDLE_VALUE, "Open placeholder status");
    auto metadata = GetMetadata(file, false);
    auto bytes = PlaceholderInfo(file);
    const auto* info = reinterpret_cast<const CF_PLACEHOLDER_STANDARD_INFO*>(bytes.data());
    auto identity = ParseIdentity(info->FileIdentity, info->FileIdentityLength);
    if (identity.relative != relative || info->SyncRootFileId.QuadPart != rootInfo.fileId)
        throw std::runtime_error("The file does not belong to the requested root mapping.");
    FILE_ATTRIBUTE_TAG_INFO tag{};
    WinCheck(GetFileInformationByHandleEx(file, FileAttributeTagInfo, &tag, sizeof(tag)), "Read placeholder state");
    // CfGetPlaceholderInfo plus the checked provider identity is authoritative. Some
    // filter views sanitize reparse attributes returned to the provider process.
    const bool partial = !metadata.standard.Directory && info->OnDiskDataSize.QuadPart < metadata.standard.EndOfFile.QuadPart;
    std::ostringstream out;
    out << "{\"path\":" << Json(target)
        << ",\"attributes\":" << tag.FileAttributes << ",\"reparseTag\":" << tag.ReparseTag
        << ",\"isPlaceholder\":true"
        << ",\"placeholder\":true"
        << ",\"isDirectory\":" << (metadata.standard.Directory ? "true" : "false")
        << ",\"fileSize\":" << metadata.standard.EndOfFile.QuadPart
        << ",\"logicalSize\":" << metadata.standard.EndOfFile.QuadPart
        << ",\"hydrated\":" << (!partial && !metadata.standard.Directory ? "true" : "false")
        << ",\"onDiskDataSize\":" << info->OnDiskDataSize.QuadPart
        << ",\"allocatedBytes\":" << metadata.standard.AllocationSize.QuadPart
        << ",\"pinState\":" << static_cast<int>(info->PinState)
        << ",\"hydrationPolicy\":" << static_cast<int>(rootInfo.hydration.Primary)
        << ",\"hydrationModifier\":" << static_cast<int>(rootInfo.hydration.Modifier)
        << ",\"populationPolicy\":" << static_cast<int>(rootInfo.population.Primary)
        << ",\"modifiedDataSize\":" << info->ModifiedDataSize.QuadPart
        << ",\"inSync\":" << (info->InSyncState == CF_IN_SYNC_STATE_IN_SYNC ? "true" : "false")
        << ",\"pinned\":" << (info->PinState == CF_PIN_STATE_PINNED ? "true" : "false")
        << ",\"partial\":" << (partial ? "true" : "false") << "}";
    Log(out.str());
}

std::array<BYTE, 32> HashFile(HANDLE file, LONGLONG size, bool overlapped) {
    BCRYPT_HASH_HANDLE hash = nullptr;
    NTSTATUS status = BCryptCreateHash(BCRYPT_SHA256_ALG_HANDLE, &hash, nullptr, 0, nullptr, 0, 0);
    if (status < 0) throw std::runtime_error("BCryptCreateHash failed.");
    struct HashCloser { BCRYPT_HASH_HANDLE value; ~HashCloser() { BCryptDestroyHash(value); } } closer{hash};
    std::vector<BYTE> buffer(kChunkSize);
    Handle event(overlapped ? CreateEventW(nullptr, TRUE, FALSE, nullptr) : nullptr);
    if (overlapped) WinCheck(event.value != nullptr, "Create hash read event");
    LONGLONG position = 0;
    while (position < size) {
        DWORD wanted = static_cast<DWORD>((std::min)(static_cast<LONGLONG>(kChunkSize), size - position));
        DWORD read = 0;
        OVERLAPPED operation{};
        operation.Offset = static_cast<DWORD>(position);
        operation.OffsetHigh = static_cast<DWORD>(static_cast<ULONGLONG>(position) >> 32);
        operation.hEvent = event;
        if (overlapped) ResetEvent(event);
        BOOL result = ReadFile(file, buffer.data(), wanted, &read, overlapped ? &operation : nullptr);
        if (!result && overlapped && GetLastError() == ERROR_IO_PENDING)
            result = GetOverlappedResult(file, &operation, &read, TRUE);
        WinCheck(result, "Read hash input");
        if (read != wanted) throw std::runtime_error("Unexpected EOF while verifying file content.");
        if (BCryptHashData(hash, buffer.data(), read, 0) < 0) throw std::runtime_error("BCryptHashData failed.");
        position += read;
    }
    std::array<BYTE, 32> result{};
    if (BCryptFinishHash(hash, result.data(), static_cast<ULONG>(result.size()), 0) < 0)
        throw std::runtime_error("BCryptFinishHash failed.");
    return result;
}

std::array<BYTE, 32> HashRemote(RemoteRead& remote, LONGLONG size) {
    BCRYPT_HASH_HANDLE hash = nullptr;
    if (BCryptCreateHash(BCRYPT_SHA256_ALG_HANDLE, &hash, nullptr, 0, nullptr, 0, 0) < 0) throw std::runtime_error("Create remote hash failed.");
    struct Guard { BCRYPT_HASH_HANDLE value; ~Guard() { BCryptDestroyHash(value); } } guard{hash};
    std::vector<BYTE> buffer(kChunkSize);
    for (LONGLONG offset = 0; offset < size;) {
        DWORD count = static_cast<DWORD>((std::min)(static_cast<LONGLONG>(kChunkSize), size - offset));
        remote.Read(offset, count, buffer.data());
        if (BCryptHashData(hash, buffer.data(), count, 0) < 0) throw std::runtime_error("Hash remote block failed.");
        offset += count;
    }
    std::array<BYTE, 32> result{};
    if (BCryptFinishHash(hash, result.data(), static_cast<ULONG>(result.size()), 0) < 0) throw std::runtime_error("Finish remote hash failed."); return result;
}

void Release(const std::wstring& root, const std::wstring& relative) {
    Log("RELEASE_BEGIN");
    RelativePathCheck(relative);
    auto rootInfo = RequireRoot(root);
    if (rootInfo.hydration.Primary == CF_HYDRATION_POLICY_ALWAYS_FULL)
        throw std::runtime_error("ALWAYS_FULL hydration cannot release content; restart the owned Agent for a safe policy update.");
    if (!(rootInfo.hydration.Modifier & CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED))
        throw std::runtime_error("AUTO_DEHYDRATION_ALLOWED is missing; restart the owned Agent to update policies before direct release.");
    auto target = root + L"\\" + relative;
    CheckPathChain(target, root, true);
    ProtectedHandle file(target, (GetFileAttributesW(Extended(target).c_str()) & FILE_ATTRIBUTE_READONLY) == 0);
    Log("RELEASE_OPLOCK_ACQUIRED");
    ProtectedReference reference(file.value); // Hold throughout hash + dehydrate; conflicting opens wait.
    HANDLE win32 = CfGetWin32HandleFromProtectedHandle(file.value);
    auto metadata = GetMetadata(win32, false);
    Log("RELEASE_METADATA_READ");
    auto bytes = PlaceholderInfo(file.value);
    Log("RELEASE_PLACEHOLDER_READ");
    const auto* info = reinterpret_cast<const CF_PLACEHOLDER_STANDARD_INFO*>(bytes.data());
    auto identity = ParseIdentity(info->FileIdentity, info->FileIdentityLength);
    if (info->SyncRootFileId.QuadPart != rootInfo.fileId || identity.relative != relative)
        throw std::runtime_error("The file does not belong to this mapping.");
    if (metadata.standard.Directory) throw std::runtime_error("Stage 1 releases individual files only.");
    if (info->PinState == CF_PIN_STATE_PINNED) throw std::runtime_error("Pinned files cannot be released.");
    if (info->InSyncState != CF_IN_SYNC_STATE_IN_SYNC || info->ModifiedDataSize.QuadPart != 0)
        throw std::runtime_error("The local file has unsynchronized changes; release refused.");
    if (metadata.standard.EndOfFile.QuadPart != identity.header.size ||
        metadata.basic.LastWriteTime.QuadPart != identity.header.lastWriteTime)
        throw std::runtime_error("Local metadata changed; release refused.");
    if (!IsSmb(rootInfo.source)) CheckPathChain(rootInfo.source, rootInfo.source, false);
    if (Lower(RootIdentity(rootInfo.source)) != Lower(rootInfo.identity))
        throw std::runtime_error("The registered source directory was replaced; release refused.");
    Handle source;
    std::unique_ptr<RemoteRead> remote;
    if (IsSmb(rootInfo.source)) {
        if (gSmbPipe.empty()) throw std::runtime_error("Use Agent smb-release to verify remote content.");
        remote = std::make_unique<RemoteRead>(identity, nullptr);
    } else {
        auto sourcePath = rootInfo.source + L"\\" + identity.relative;
        CheckPathChain(sourcePath, rootInfo.source, false);
        source.value = CreateFileW(Extended(sourcePath).c_str(), GENERIC_READ, FILE_SHARE_READ,
            nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        WinCheck(source.value != INVALID_HANDLE_VALUE, "Open source for release verification");
        VerifySource(identity, GetMetadata(source, true));
    }
    FILE_ATTRIBUTE_TAG_INFO tag{};
    WinCheck(GetFileInformationByHandleEx(win32, FileAttributeTagInfo, &tag, sizeof(tag)), "Read release state");
    if (info->OnDiskDataSize.QuadPart < identity.header.size) {
        if (info->OnDiskDataSize.QuadPart == 0) { Log("ALREADY_RELEASED " + Utf8(relative)); return; }
        throw std::runtime_error("The file is only partially hydrated; release deferred to avoid triggering a download.");
    }
    if ((remote ? HashRemote(*remote, identity.header.size) : HashFile(source, identity.header.size, false)) != HashFile(win32, identity.header.size, true))
        throw std::runtime_error("Local SHA-256 differs from source; release refused and local data retained.");
    Log("RELEASE_HASH_VERIFIED");
    if (remote) remote->VerifyAlive(); // A timed-out/disconnected source lock cannot authorize release.
    // Keep the verified Win32 handle referenced through the FSCTL. The data API
    // requires the underlying file handle, rather than the opaque oplock token.
    LARGE_INTEGER offset{};
    LARGE_INTEGER length{};
    length.QuadPart = CF_EOF;
    {
        VerifiedReleaseGate gate(root, identity.relative);
        Log("RELEASE_DEHYDRATING");
        // CfOpenFileWithOplock's Win32 handle is asynchronous and has a pending oplock
        // request. A null OVERLAPPED waits on that handle instead of this FSCTL.
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        WinCheck(event.value != nullptr, "Create dehydration completion event");
        OVERLAPPED operation{}; operation.hEvent = event;
        HRESULT result = CfDehydratePlaceholder(win32, offset, length, CF_DEHYDRATE_FLAG_NONE, &operation);
        if (result == HRESULT_FROM_WIN32(ERROR_IO_PENDING)) {
            DWORD wait = WaitForSingleObject(event, 15000);
            if (wait != WAIT_OBJECT_0) {
                CancelIoEx(win32, &operation);
                DWORD ignored = 0; GetOverlappedResult(win32, &operation, &ignored, TRUE);
                throw std::runtime_error("Dehydration timed out; local file retained.");
            }
            DWORD transferred = 0; WinCheck(GetOverlappedResult(win32, &operation, &transferred, FALSE), "Complete dehydration");
        } else Check(result, "CfDehydratePlaceholder");
    }
    Log("RELEASED " + Utf8(relative) + " (source unchanged)");
}

void Pin(const std::wstring& root, const std::wstring& relative, bool pinned) {
    RelativePathCheck(relative); auto rootInfo = RequireRoot(root);
    auto target = root + L"\\" + relative; CheckPathChain(target, root, true);
    ProtectedHandle file(target, false); ProtectedReference reference(file.value);
    auto bytes = PlaceholderInfo(file.value); auto* info = reinterpret_cast<const CF_PLACEHOLDER_STANDARD_INFO*>(bytes.data());
    auto identity = ParseIdentity(info->FileIdentity, info->FileIdentityLength);
    if (identity.relative != relative || info->SyncRootFileId.QuadPart != rootInfo.fileId)
        throw std::runtime_error("Pin target identity mismatch.");
    Check(CfSetPinState(file.value, pinned ? CF_PIN_STATE_PINNED : CF_PIN_STATE_UNPINNED, CF_SET_PIN_FLAG_NONE, nullptr), "CfSetPinState");
    if (pinned && !identity.header.directory) {
        HANDLE win32 = CfGetWin32HandleFromProtectedHandle(file.value);
        LARGE_INTEGER offset{}, length{}; length.QuadPart = CF_EOF;
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr)); WinCheck(event.value != nullptr, "Create pin hydrate event");
        OVERLAPPED operation{}; operation.hEvent = event;
        auto result = CfHydratePlaceholder(win32, offset, length, CF_HYDRATE_FLAG_NONE, &operation);
        if (result == HRESULT_FROM_WIN32(ERROR_IO_PENDING)) {
            if (WaitForSingleObject(event, 120000) != WAIT_OBJECT_0) {
                CancelIoEx(win32, &operation); DWORD ignored; GetOverlappedResult(win32, &operation, &ignored, TRUE);
                throw std::runtime_error("Pin hydration timed out; pin intent retained.");
            }
            DWORD ignored; WinCheck(GetOverlappedResult(win32, &operation, &ignored, FALSE), "Complete pin hydration");
        } else Check(result, "CfHydratePlaceholder");
        auto after = PlaceholderInfo(file.value); const auto* complete = reinterpret_cast<const CF_PLACEHOLDER_STANDARD_INFO*>(after.data());
        if (complete->OnDiskDataSize.QuadPart < identity.header.size || complete->PinState != CF_PIN_STATE_PINNED)
            throw std::runtime_error("Pin intent saved but full content is not available.");
    }
    Log(pinned ? "PINNED" : "UNPINNED (content state must be queried separately)");
}

void Unregister(const std::wstring& root) {
    ProviderMutex mutex(root);
    RequireRoot(root);
    // CfUnregisterSyncRoot deletes dehydrated/partially hydrated placeholders.
    // Until migration is implemented, an empty root is the only supported safe teardown.
    WIN32_FIND_DATAW data{};
    HANDLE find = FindFirstFileW((Extended(root) + L"\\*").c_str(), &data);
    if (find != INVALID_HANDLE_VALUE) {
        bool empty = true;
        do {
            if (wcscmp(data.cFileName, L".") && wcscmp(data.cFileName, L"..")) { empty = false; break; }
        } while (FindNextFileW(find, &data));
        const DWORD error = GetLastError();
        FindClose(find);
        if (!empty) throw std::runtime_error("Unregister requires an empty sync root. Preserve local edits and remove local entries yourself before unregistering. Windows may delete unhydrated placeholders during unregister.");
        if (error != ERROR_NO_MORE_FILES) Check(HRESULT_FROM_WIN32(error), "Inspect root before unregister");
    } else if (GetLastError() != ERROR_FILE_NOT_FOUND) {
        Check(HRESULT_FROM_WIN32(GetLastError()), "Inspect root before unregister");
    }
    Check(CfUnregisterSyncRoot(root.c_str()), "CfUnregisterSyncRoot");
    Log("UNREGISTERED " + Utf8(root) + "; no source files were deleted.");
}

void Usage() {
    Log("CloudProviderHost 0.1.0 - Windows CFAPI local prototype\n"
        "  run --source C:\\DemoSource --root C:\\DemoCloud [--stop-event Local\\CloudDriveLite.Stop.name]\n"
        "  status --root C:\\DemoCloud --path relative\\file.txt\n"
        "  release --root C:\\DemoCloud --path relative\\file.txt\n"
        "  unregister --root C:\\DemoCloud\n"
        "  doctor\n"
        "Source contents are read-only. Local edits are NOT uploaded in stage 1.");
}
} // namespace

int wmain(int argc, wchar_t* argv[]) {
    SetConsoleOutputCP(CP_UTF8);
    try {
        if (argc < 2 || wcscmp(argv[1], L"--help") == 0 || wcscmp(argv[1], L"help") == 0) { Usage(); return 0; }
        std::wstring command = argv[1];
        std::map<std::wstring, std::wstring> options;
        for (int i = 2; i < argc; i += 2) {
            if (i + 1 >= argc || std::wstring(argv[i]).compare(0, 2, L"--") != 0 ||
                !options.emplace(argv[i], argv[i + 1]).second)
                throw std::runtime_error("Options must be unique --name value pairs.");
        }
        auto require = [&](const wchar_t* name) -> std::wstring {
            auto it = options.find(name);
            if (it == options.end() || it->second.empty()) throw std::runtime_error("A required argument is missing: " + Utf8(name));
            return it->second;
        };
        const std::map<std::wstring, std::vector<std::wstring>> allowed = {
            {L"run", {L"--source", L"--root", L"--stop-event"}},
            {L"run-smb", {L"--source", L"--root", L"--stop-event", L"--pipe", L"--agent-pid", L"--refresh-event"}},
            {L"status", {L"--root", L"--path"}}, {L"release", {L"--root", L"--path", L"--pipe", L"--agent-pid"}},
            {L"pin", {L"--root", L"--path"}}, {L"unpin", {L"--root", L"--path"}},
            {L"unregister", {L"--root"}}, {L"doctor", {}}
        };
        auto spec = allowed.find(command);
        if (spec == allowed.end()) throw std::runtime_error("Unknown command. Use --help.");
        for (const auto& option : options)
            if (std::find(spec->second.begin(), spec->second.end(), option.first) == spec->second.end())
                throw std::runtime_error("Unknown option: " + Utf8(option.first));
        if (command == L"doctor") {
            CF_PLATFORM_INFO platform{};
            Check(CfGetPlatformInfo(&platform), "CfGetPlatformInfo");
            Log("{\"cfapiAvailable\":true,\"buildNumber\":" + std::to_string(platform.BuildNumber) +
                ",\"revisionNumber\":" + std::to_string(platform.RevisionNumber) +
                ",\"integrationNumber\":" + std::to_string(platform.IntegrationNumber) + "}");
            return 0;
        }
        auto root = FullPath(require(L"--root"));
        if (options.contains(L"--pipe")) {
            gSmbPipe = require(L"--pipe");
            if (gSmbPipe.compare(0, 19, L"CloudDriveLite.Smb.") != 0 || gSmbPipe.find_first_of(L"\\/:") != std::wstring::npos || gSmbPipe.size() > 100)
                throw std::runtime_error("Invalid Agent pipe name.");
            const auto pid = require(L"--agent-pid");
            if (pid.find_first_not_of(L"0123456789") != std::wstring::npos) throw std::runtime_error("Invalid Agent PID.");
            gAgentPid = std::stoul(pid);
        }
        if (command == L"run" || command == L"run-smb") {
            auto stop = options.find(L"--stop-event");
            if (stop != options.end() && (stop->second.compare(0, 21, L"Local\\CloudDriveLite.") != 0 || stop->second.size() > 240))
                throw std::runtime_error("Stop event must start with Local\\CloudDriveLite. and be at most 240 characters.");
            if (command == L"run-smb") {
                const auto source = require(L"--source");
                if (source.size() != 32 || source.find_first_not_of(L"0123456789abcdefABCDEF") != std::wstring::npos || gSmbPipe.empty()) throw std::runtime_error("Invalid SMB connection identity.");
                const auto refresh = require(L"--refresh-event");
                if (refresh.compare(0, 21, L"Local\\CloudDriveLite.") != 0 || refresh.size() > 240) throw std::runtime_error("Invalid refresh event.");
                Run(L"smb:" + source, root, stop == options.end() ? L"" : stop->second, refresh);
            } else Run(FullPath(require(L"--source")), root, stop == options.end() ? L"" : stop->second);
        } else if (command == L"status") Status(root, require(L"--path"));
        else if (command == L"release") Release(root, require(L"--path"));
        else if (command == L"pin" || command == L"unpin") Pin(root, require(L"--path"), command == L"pin");
        else if (command == L"unregister") Unregister(root);
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "ERROR " << error.what() << std::endl;
        return 1;
    } catch (...) {
        std::cerr << "ERROR Unknown native failure." << std::endl;
        return 1;
    }
}
