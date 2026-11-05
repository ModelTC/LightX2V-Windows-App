# 构建与目录

本仓库独立于机器验证工作区，可直接克隆构建。应用与后端保留 v0.3.5 的目录和运行逻辑；首次整理调整文档、构建编码、缓存排除与仓库基础设施。

```text
LightX2V.Windows/       WPF App、Python 桥接后端、样式和固定安装清单
Checks/                早期完整桌面/推理验收，仅手动运行
HeadlessChecks/        Windows Shell 分发检查
SetupUiChecks/         向导、下载、硬件与离屏 UI 检查
Tests/                 不依赖 GPU 的 Python 单元测试
scripts/               发布包装、仓库检查及本地验证入口
third_party/           固定推理源码的来源、兼容补丁与许可证
docs/                  使用、测试、发布说明
build.ps1              Windows 构建与 ZIP 入口
prepare_setup.py       离线校验固定安装清单和源码归档
prepare_runtime.py     获取并校验固定版本 llama.cpp Vulkan 运行时
prepare_cuda_profile.py 维护者显式刷新 CUDA 清单的工具，普通构建不调用
rtx_acceptance.py      使用 App 已准备的环境进行实际 RTX 出图验收
```

## 本地构建

使用 Windows x64、.NET SDK 10.0.403（`global.json` 允许该 feature band 的后续补丁）、Python 3.12。普通构建不需要安装 GPU 推理环境。

```powershell
.\build.ps1 -Dotnet dotnet -Python python
```

构建会校验 `Resources/Setup` 中两个后端的固定清单、源码 ZIP 和显卡助手下载清单，再校验并解压固定 llama.cpp 归档，最后生成自包含 win-x64 程序和 ZIP。网络需求主要是 NuGet、官方 llama.cpp Release；这里不会下载数十 GB 模型。

可以用 `-Destination C:\Builds\LightX2V-Windows-0.3.5` 指定全新输出目录。为避免混入旧版文件，输出目录必须不存在，或为空目录。构建会同时复制用户文档、组件声明及 .NET 许可证，并生成逐文件 SHA-256 清单；版本从项目文件读取。

`scripts/package_app.py` 也能单独包装一个已 publish 的完整文件夹。它会验证固定载荷和 llama.cpp 实际文件，检查缺失组件和版本，不把编译成功描述成实机验收通过。已有私有验收数据不参与公共打包。

## 测试

```powershell
python prepare_setup.py
python scripts/check_repository.py
python -m unittest discover -s Tests -v
dotnet build SetupUiChecks/SetupUiChecks.csproj -c Release
dotnet build HeadlessChecks/HeadlessChecks.csproj -c Release
dotnet build Checks/Checks.csproj -c Release
```

GitHub Actions 运行这些检查并构建安装包，产物保存在 workflow artifact。普通分支不修改 Releases；推送与项目版本一致的 `v*` 标签时，在独立发布任务中再次验证源码提交、完整文件哈希和上传资产，再发布 Pre-release。CI 不声称 GPU 已验收。见 [测试范围](testing.md)。

## 安装载荷与源码来源

两个环境清单固定 Python、PyTorch 及每个 wheel 的版本、大小和 SHA-256，构建时不解析“最新版本”。`prepare_cuda_profile.py` 会改动固定清单，只在明确更新依赖时运行，并重新验收。

`LightX2V.Windows/Resources/Setup/lightx2v-native.zip` 是运行时必需的推理源码，约 13 MB，因此作为唯一的大型固定源码归档入库，未使用 Git LFS。它以 LightX2V 提交 `58b8e2592519711457e9fface5a804d7c337a1ae` 为基线；16 个原生 Windows 兼容文件的差异见 `third_party/native-windows.patch`，准确归档哈希和变更文件见 `third_party/source-provenance.json`。归档内容保留上游许可证。

不提交模型、环境、下载缓存、生成图片、个人设置、QA 日志、开发 SSH 配置或历史安装包。原始开发验收记录留在仓库外。维护用户偏好和已有环境的兼容读取逻辑仍保留，不因整理首版而更改推理行为。
