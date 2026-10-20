# Windows 发布流程

Windows 与 [macOS 仓库](https://github.com/qx0657/allinai-quota-menu)分别维护、打标签和发布。业务同步前先看 [NATIVE_PARITY.md](NATIVE_PARITY.md)，对应功能可以同版本号发布，不能假定另一个仓库已发布。

## 版本与提交

1. 同步项目中的 `Version`、`AssemblyVersion`、`FileVersion`、`InformationalVersion`，以及 README / CHANGELOG。
2. 保持 AGENTS.md 与 CLAUDE.md 的标记区一致。运行跨平台业务回归；Windows 侧还需原生编译与烟雾检查。
3. 提交并推送所有源文件和文档，确认 `main` 的验证工作流通过。
4. 给本次发布提交创建 `v<major>.<minor>.<patch>` 标签，推送标签。不要移动已发布标签。

0.2.0 示例：

```sh
git tag -a v0.2.0 -m "AllinAI Quota Tray v0.2.0"
git push origin v0.2.0
gh workflow run validate.yml --repo qx0657/allinai-quota-tray-windows --ref v0.2.0 -f releaseBuild=true
```

工作流拒绝从非标签或版本不匹配的标签生成正式发布包。`releaseBuild=true` 会运行同一套业务、原生渲染、随机凭据、版本、启动与单实例检查，使用 `build.ps1 -SelfContained`，然后生成 `windows-release` 构建产物。

## 产物和校验

构建产物包含：

- `AllinAIQuotaTray-v0.2.0-win-x64.zip`：自包含程序与用户安装说明。
- `SHA256SUMS.txt`：ZIP 和 ZIP 内 `AllinAIQuotaTray.exe` 的 SHA-256。

在 Actions 页面选择对应 **workflow_dispatch、tag、commit** 的成功运行，下载 `windows-release`。也可用 CLI：

```sh
gh run download <run-id> --repo qx0657/allinai-quota-tray-windows --name windows-release --dir publish/release-download
```

解压 ZIP，确认仅包含程序和 README.txt，核对 ZIP 与 exe 的 SHA-256。Windows 上可使用 `Get-FileHash <file> -Algorithm SHA256`；macOS 上可将 exe 放到校验文件旁后运行 `shasum -a 256 -c SHA256SUMS.txt`。

正式发布使用自包含包，不要求用户安装 .NET。CI 的常规推送构建仍采用框架依赖模式，依赖 .NET 8 Windows Desktop Runtime；两种产物不要混淆。`build.ps1` 的本地自包含构建与 CI 使用相同入口。

## 发布 Release

先基于已存在标签创建草稿，上传 ZIP、从该 ZIP 解出的 exe 和校验文件。正文写明版本变化、Windows 10/11 x64、自包含、未签名，以及已经执行的验证范围。核对资产完整后再发布为正式版并设为 latest。未来发布按当前版本替换示例中的版本号与文件名。

```sh
gh release create v0.2.0 --repo qx0657/allinai-quota-tray-windows --verify-tag --draft --title "AllinAI Quota Tray v0.2.0" --notes-file <notes.md> <zip-path> <exe-path> <checksums-path>
gh release edit v0.2.0 --repo qx0657/allinai-quota-tray-windows --draft=false --latest
```

最后确认 `releases/latest` 返回本版本、不是草稿或预发布、附件均已上传，再按 GitHub 返回的 digest 核对本地 SHA-256。只创建 Git 标签、只推源码或只上传 Actions artifact 都不会成为应用可发现的正式更新。

CI 不会读取真实账号，也不代表用户已验收任务栏操作、通知送达、自启动、睡眠恢复、高 DPI。实际系统行为的验证范围应随 Release 如实说明。
