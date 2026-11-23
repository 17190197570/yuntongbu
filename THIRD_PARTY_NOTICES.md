# Third-party software

CloudDrive Lite's own source code is licensed under MIT; this does not relicense
its dependencies. No downloaded SDKs, NuGet package payloads, native libraries,
certificates, or third-party binaries are included in this source repository.

Direct package versions and license expressions below were read from the locally
restored NuGet package manifests on 2026-11-23:

| Package | Version | License | Upstream |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite.Core | 10.0.15 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| Microsoft.PowerShell.SDK | 7.6.10 | MIT | https://github.com/PowerShell/PowerShell |

The authoritative direct package references are in
`src/CloudDrive.Storage/CloudDrive.Storage.csproj`. Restore downloads transitive
dependencies, including the native SQLite implementation. The bundle's license
must not be mistaken for the license of every library inside it.

Before distributing binary packages, inventory **all** transitive and bundled
components, preserve their license texts/copyright notices/required NOTICE files,
and check the redistribution terms of the .NET runtime, Windows components and
build tools. This source-level summary is not a complete binary redistribution
notice. The initial public CI uploads validation logs only, not runnable packages.

Windows CFAPI, the Windows SMB client, Windows Credential Manager, MSVC and the
Windows SDK are platform/build dependencies; they are not covered by this
repository's MIT license. The private `.tools` directory is excluded from Git.
