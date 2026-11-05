# 内测与验收范围

首个内测版本为 v0.3.5。仓库整理没有修改 WPF 或 Python 后端代码；发布资产应对应明确的源码提交和 SHA-256。

## 已有验证

- Radeon 8060S / Windows 11 x64：真实原生安装、512/1K/2K 出图、参考图编辑和 PE-T2I / PE-I2I 曾完成验收。
- v0.3.5：113 项 Windows 检查与 14 项 Python 测试通过。Windows 检查覆盖硬件回退、驱动状态、向导集成与完成页，使用离屏渲染；该轮没有重新执行完整模型下载或全部尺寸推理。
- 历史推理证据按相关后端、运行时、配置与载荷哈希关联复用，不能等同于每个界面版本重新跑完所有推理。
- RTX 4070 Laptop / 621.01 的识别回归基于报告的数据及注入夹具。**它不等同于访问了真实 RTX 笔记本，也不是 CUDA 完整安装或出图的验收结果。**

个人机器上的原始日志、路径、作品、偏好和完整 QA 包不上传公共源码仓库。以上是验证范围说明，公共 CI 的编译产物另行记录源码提交，不能继承为新的 GPU 实测结果。

## 不依赖 GPU 的检查

在仓库根目录执行 `python -m unittest discover -s Tests -v`。检查下载续传/校验、协议与增强解析、CUDA/ROCm 配置选择和显存策略；不下载安装完整模型。

`python prepare_setup.py` 校验固定安装载荷，`python scripts/check_repository.py` 检查公共提交范围与源码来源。CI 还编译 App 和三个 C# 检查项目，不执行依赖显卡、默认浏览器或真实桌面的测试。

## AMD 离屏检查

在有 AMD 驱动的 Windows 测试机上发布并运行：

```powershell
dotnet publish SetupUiChecks/SetupUiChecks.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o artifacts/setup-checks
.\artifacts\setup-checks\SetupUiChecks.exe C:\LightX2V-QA\new-hardware-run hardware
```

输出目录使用全新路径。该模式不显示测试窗口，不操作真实桌面，不安装驱动；其中有“真实 AMD 设备应存在”的断言，因此不能直接当作无 GPU CI 或 NVIDIA 机器的通用测试。默认全套模式还会验证已有 AMD Python 环境，需先准备环境。

## RTX 实机验收

先用 App 完成 CUDA 环境配置，记录型号、驱动、显存和系统内存。至少验证：

1. 自动识别 NVIDIA GPU，已有可用驱动不要求重装。
2. 一键配置与模型下载、停止、重试、重启 App 后继续可用。
3. 关闭增强，512 / 1:1 文生图，检查真实 PNG 和完整日志。
4. 成功后依次验证 1K、2K、参考图编辑、Prompt 增强、取消和历史恢复。
5. 验证显存/系统内存不足时的实际错误和恢复，记录耗时；不承诺所有尺寸均可运行。

开发者可用 `rtx_acceptance.py` 对发布包的相同后端执行真实图像验证：

```powershell
python rtx_acceptance.py --app C:\Apps\LightX2V --python C:\LightX2V\environments\native-nvidia-cu128-v1\python.exe --repository C:\LightX2V\runtimes\lightx2v-native-HASH --model C:\LightX2V\models\Qwen\Qwen-Image-2.1-Turbo --output C:\LightX2V-RTX-acceptance --tiers 512
```

路径以 App 实际配置为准。确认资源足够后可用 `--tiers 512,1K,2K --edit`，会实际生成多张图片并保存请求、配置和日志。CLI 检查不能替代新用户向导和创作交互测试。

`Checks` 目录保留早期交互式验收程序，会打开窗口、运行真实推理，一些模式会下载/安装组件。仅在专用测试机显式运行，不能默认在用户正在工作的桌面运行。
