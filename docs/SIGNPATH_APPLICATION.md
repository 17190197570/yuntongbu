# SignPath Foundation 申请准备材料

状态：草稿，2026-11-23；**未提交、未获批**。
以下英文内容可在仓库发布和 CI 实际通过后由维护者核实并用于申请。

> Project name: CloudDrive Lite
>
> Source repository: https://github.com/17190197570/yuntongbu
>
> License: MIT for project-owned source; third-party dependencies retain their
> own licenses (see THIRD_PARTY_NOTICES.md).
>
> Maintainer / reviewer / release approver: GitHub user 17190197570. The project
> currently has one maintainer; there is no independent second reviewer.
>
> Description: An experimental Windows desktop file provider using Windows CFAPI,
> the native Windows SMB client, C#/.NET and C++. It indexes user-configured NAS
> folders, creates cloud placeholders and streams file contents on demand.
> Remote access is read-only by default. Complete two-way synchronization and
> production deployment have not been validated.
>
> Privacy policy: PRIVACY.md in the source repository. No project-operated
> telemetry or file upload endpoint is included. Connections are made to
> user-configured SMB/DNS and explicitly configured HTTPS update endpoints.
>
> Code signing policy: docs/CODE_SIGNING.md in the source repository.
>
> Build: A committed GitHub Actions Windows workflow builds C++ and .NET from
> source and runs isolated fixture checks. Initial CI uploads logs only. A
> verified signing artifact workflow is not yet configured.
>
> Signing scope requested: Project-owned Windows user-mode EXE/DLL files and,
> after installer validation, the MSI package. Upstream third-party libraries
> will retain their original signatures and licenses.
>
> Current limitations: This is a newly published project. There is not yet a
> verified public release/reputation history, a completed dependency notice
> bundle for binary releases, or an approved SignPath integration. We understand
> that publishing source alone does not establish eligibility for free signing.

提交前由维护者补齐并核实：

- 实际公开提交、通过的 CI 运行链接，以及对应格式的发布页/制品与校验值。
- 可核实的维护/使用记录，不虚构用户数量、信誉、测试覆盖或评审人员。
- GitHub/SignPath MFA、仓库保护、发布手动审批，以及完整依赖许可检查。
- 当前 CFAPI/SMB 限制、正式签名测试计划和系统修改/安全卸载说明。

官方入口：https://signpath.org/ ；资格条件：https://signpath.org/terms 。
不要把该文件的存在当成基金会已受理申请或已经授予签名权限。
