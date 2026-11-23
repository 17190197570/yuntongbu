// Version 2 current-user Agent protocol. No credentials or UNC paths cross this pipe.
class AgentPipe {
    Handle pipe;
    HANDLE stop;
    const std::atomic<bool>* cancelled;
    void Io(void* bytes, DWORD length, bool write) {
        auto* cursor = static_cast<BYTE*>(bytes);
        while (length) {
            Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            WinCheck(event.value != nullptr, "Create bridge I/O event");
            OVERLAPPED overlapped{}; overlapped.hEvent = event;
            DWORD completed = 0;
            BOOL ok = write ? WriteFile(pipe, cursor, length, &completed, &overlapped) : ReadFile(pipe, cursor, length, &completed, &overlapped);
            if (!ok && GetLastError() == ERROR_IO_PENDING) {
                DWORD elapsed = 0;
                while (WaitForSingleObject(event, 100) == WAIT_TIMEOUT) {
                    elapsed += 100;
                    if (elapsed >= 30000 || Stopping(stop) || (cancelled && cancelled->load())) {
                        CancelIoEx(pipe, &overlapped);
                        GetOverlappedResult(pipe, &overlapped, &completed, TRUE);
                        throw std::runtime_error("SMB bridge cancelled or timed out.");
                    }
                }
                ok = GetOverlappedResult(pipe, &overlapped, &completed, FALSE);
            }
            WinCheck(ok, "Agent bridge I/O");
            if (!completed) throw std::runtime_error("Agent bridge disconnected.");
            length -= completed; cursor += completed;
        }
    }
public:
    AgentPipe(DWORD operation, HANDLE stopEvent, const std::atomic<bool>* cancellation = nullptr)
        : stop(stopEvent), cancelled(cancellation) {
        auto name = L"\\\\.\\pipe\\" + gSmbPipe;
        pipe.value = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
        if (pipe.value == INVALID_HANDLE_VALUE && GetLastError() == ERROR_PIPE_BUSY) {
            WinCheck(WaitNamedPipeW(name.c_str(), 5000), "Wait for Agent bridge");
            pipe.value = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
        }
        WinCheck(pipe.value != INVALID_HANDLE_VALUE, "Connect Agent bridge");
        ULONG server = 0; WinCheck(GetNamedPipeServerProcessId(pipe, &server), "Verify Agent PID");
        if (server != gAgentPid) throw std::runtime_error("Agent pipe server PID mismatch.");
        Put<DWORD>(0x324C4443); Put<DWORD>(operation);
    }
    template<class T> T Get() { T value{}; Io(&value, sizeof(value), false); return value; }
    template<class T> void Put(T value) { Io(&value, sizeof(value), true); }
    void Bytes(void* data, DWORD size) { Io(data, size, false); }
    void String(const std::wstring& value) {
        Put<DWORD>(static_cast<DWORD>(value.size() * sizeof(wchar_t)));
        if (!value.empty()) Io(const_cast<wchar_t*>(value.data()), static_cast<DWORD>(value.size() * sizeof(wchar_t)), true);
    }
    std::wstring String() {
        DWORD size = Get<DWORD>(); if (size > 16384 || size % 2) throw std::runtime_error("Invalid bridge string length.");
        std::wstring value(size / 2, L'\0'); if (size) Io(value.data(), size, false);
        if (value.find(L'\0') != std::wstring::npos) throw std::runtime_error("Invalid bridge string."); return value;
    }
    void Success() { if (Get<DWORD>() != 0) throw std::runtime_error("Agent rejected stale, inaccessible or offline remote source."); }
};

struct IndexedEntry { std::wstring relative; Metadata metadata; };

class RemoteRead {
    AgentPipe pipe;
public:
    RemoteRead(const FileIdentity& identity, HANDLE stop, const std::atomic<bool>* cancelled = nullptr) : pipe(2, stop, cancelled) {
        // File ID bytes are sent individually through the typed binary protocol.
        for (BYTE b : identity.header.fileId.Identifier) pipe.Put<BYTE>(b);
        pipe.Put<ULONGLONG>(identity.header.volumeSerial);
        pipe.Put<LONGLONG>(identity.header.size); pipe.Put<LONGLONG>(identity.header.creationTime); pipe.Put<LONGLONG>(identity.header.lastWriteTime); pipe.Success();
    }
    void Read(LONGLONG offset, DWORD count, void* buffer) {
        pipe.Put<LONGLONG>(offset); pipe.Put<DWORD>(count); pipe.Success();
        if (pipe.Get<DWORD>() != count) throw std::runtime_error("SMB stream returned an incomplete block.");
        pipe.Bytes(buffer, count);
    }
    void VerifyAlive() { pipe.Put<LONGLONG>(0); pipe.Put<LONG>(-1); pipe.Success(); }
    ~RemoteRead() { try { pipe.Put<LONGLONG>(0); pipe.Put<DWORD>(0); } catch (...) {} }
};

std::vector<IndexedEntry> IndexedPage(const std::wstring& parent, const std::wstring& after, HANDLE stop) {
    AgentPipe pipe(1, stop); pipe.String(parent); pipe.String(after); pipe.Success();
    DWORD count = pipe.Get<DWORD>(); if (count > 256) throw std::runtime_error("Invalid indexed page size.");
    std::vector<IndexedEntry> entries; entries.reserve(count);
    for (DWORD i = 0; i < count; ++i) {
        IndexedEntry entry; entry.relative = pipe.String(); RelativePathCheck(entry.relative);
        const auto split = entry.relative.find_last_of(L'\\');
        if ((split == std::wstring::npos ? L"" : entry.relative.substr(0, split)) != parent)
            throw std::runtime_error("Index page escaped its parent.");
        pipe.Bytes(&entry.metadata.id.FileId, 16);
        entry.metadata.id.VolumeSerialNumber = pipe.Get<ULONGLONG>();
        entry.metadata.standard.EndOfFile.QuadPart = pipe.Get<LONGLONG>();
        entry.metadata.basic.CreationTime.QuadPart = pipe.Get<LONGLONG>();
        entry.metadata.basic.LastWriteTime.QuadPart = pipe.Get<LONGLONG>();
        DWORD directory = pipe.Get<DWORD>();
        if (directory > 1 || entry.metadata.standard.EndOfFile.QuadPart < 0 || entry.metadata.basic.CreationTime.QuadPart <= 0 || entry.metadata.basic.LastWriteTime.QuadPart <= 0)
            throw std::runtime_error("Invalid indexed metadata.");
        entry.metadata.standard.Directory = directory ? TRUE : FALSE;
        entry.metadata.basic.LastAccessTime = entry.metadata.basic.LastWriteTime;
        entry.metadata.basic.ChangeTime = entry.metadata.basic.LastWriteTime;
        entry.metadata.basic.FileAttributes = directory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_READONLY;
        entries.push_back(std::move(entry));
    }
    return entries;
}

void PopulateIndexed(const std::wstring& root, const std::wstring& parent, LONGLONG rootId, HANDLE stop, std::uint64_t& created) {
    const auto current = parent.empty() ? root : root + L"\\" + parent;
    CheckPathChain(current, root, true);
    std::wstring after;
    for (;;) {
        if (Stopping(stop)) throw std::runtime_error("Indexed population cancelled.");
        auto entries = IndexedPage(parent, after, stop); if (entries.empty()) break;
        std::vector<std::vector<BYTE>> identities; identities.reserve(entries.size());
        std::vector<std::wstring> names; names.reserve(entries.size());
        std::vector<CF_PLACEHOLDER_CREATE_INFO> batch; batch.reserve(entries.size());
        std::vector<std::wstring> directories;
        for (const auto& entry : entries) {
            const auto target = root + L"\\" + entry.relative;
            DWORD attrs = GetFileAttributesW(Extended(target).c_str());
            if (attrs != INVALID_FILE_ATTRIBUTES) {
                try {
                    CheckPathChain(target, root, true); auto existing = OpenMetadata(target);
                    auto bytes = PlaceholderInfo(existing);
                    const auto* info = reinterpret_cast<const CF_PLACEHOLDER_STANDARD_INFO*>(bytes.data());
                    auto identity = ParseIdentity(info->FileIdentity, info->FileIdentityLength);
                    if (info->SyncRootFileId.QuadPart != rootId || identity.relative != entry.relative) throw std::runtime_error("Local mapping conflict.");
                    ++gSmbPlaceholderCount;
                    VerifySource(identity, entry.metadata);
                    if (!entry.metadata.standard.Directory && !(attrs & FILE_ATTRIBUTE_READONLY))
                        throw std::runtime_error("Local read-only attribute was removed; entry retained for review.");
                    if (entry.metadata.standard.Directory) directories.push_back(entry.relative);
                } catch (const std::exception& error) { Log("PLACEHOLDER_RETAINED " + Utf8(entry.relative) + " " + error.what()); }
                continue;
            }
            DWORD error = GetLastError();
            if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) Check(HRESULT_FROM_WIN32(error), "Inspect indexed placeholder");
            auto identity = MakeIdentity(entry.metadata, entry.relative); identities.push_back(std::move(identity));
            names.push_back(entry.relative.substr(parent.empty() ? 0 : parent.size() + 1));
            CF_PLACEHOLDER_CREATE_INFO info{}; info.RelativeFileName = names.back().c_str();
            info.FsMetadata.BasicInfo = entry.metadata.basic; info.FsMetadata.FileSize = entry.metadata.standard.EndOfFile;
            info.FileIdentity = identities.back().data(); info.FileIdentityLength = static_cast<DWORD>(identities.back().size());
            info.Flags = CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC;
            if (entry.metadata.standard.Directory) { info.Flags |= CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION; directories.push_back(entry.relative); }
            batch.push_back(info);
        }
        if (!batch.empty()) {
            DWORD processed = 0;
            Check(CfCreatePlaceholders(current.c_str(), batch.data(), static_cast<DWORD>(batch.size()), CF_CREATE_FLAG_STOP_ON_ERROR, &processed), "Batch CfCreatePlaceholders");
            for (const auto& item : batch) Check(item.Result, "Indexed placeholder result");
            if (processed != batch.size()) throw std::runtime_error("Incomplete indexed placeholder batch.");
            created += processed;
            gSmbPlaceholderCount += processed;
        }
        for (const auto& directory : directories) PopulateIndexed(root, directory, rootId, stop, created);
        after = entries.back().relative;
    }
}
