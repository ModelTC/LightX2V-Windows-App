# 发布内测版

源码仓库为 [ModelTC/LightX2V-Windows-App](https://github.com/ModelTC/LightX2V-Windows-App)。`main` 保存可构建源码；模型、环境、个人验收报告和历史安装包不提交。App 原创代码尚未单独选择许可证，第三方许可证已保留。

## 分支构建

推送分支或提交 PR 时，GitHub Actions 校验源码与固定安装载荷，运行 Python 测试，编译 C# 检查项目并构建完整 Windows x64 ZIP。workflow artifact 保留 30 天，不自动发布该分支的 Release。

## 发布版本标签

先更新项目版本和 `docs/releases/v版本.md`，完成代码审查及相关实机验证，再对已提交的代码打版本标签：

```powershell
git status --short
git tag -a v0.3.5 -m "First Windows internal beta v0.3.5"
git push origin main
git push origin v0.3.5
```

标签必须与项目版本一致。标签构建仍使用只读权限；独立发布任务仅在版本标签运行，使用 GitHub 自动提供的临时 token，无需配置个人访问令牌。

发布前自动验证 ZIP 的 SHA-256、全部文件哈希、`build-manifest.json` 的源码提交与干净状态；先创建 Draft Pre-release 并上传 App ZIP 与 `.sha256`，确认两个资产上传完成后再公开。若同名 Release 已存在，流程拒绝覆盖，避免替换已分发的文件。失败时检查 Actions 日志；已有草稿或正式版本需先人工核对，不能盲目重跑覆盖。

发布页永久保留该版本资产；workflow artifact 的 30 天保留期不影响 Release 下载。首版使用 Pre-release，不标为 Stable，也不滚动覆盖 Latest。后续渠道策略可以再按 Mac 版扩展。

## 实机验收

CI 不安装显卡驱动，也不执行 GPU 推理。发布说明必须区分真实出图、离屏/模拟检查与未完成的硬件验证。首版 AMD 已验证范围和 CUDA 待验收状态见 [testing.md](testing.md)。

开发机手动构建可用 `build.ps1`；只有来自干净、已提交源码且与目标版本标签匹配的包，才能用于正式内测分发。迁移前工作区的临时包、含个人路径的 QA 归档不上传 Release。
