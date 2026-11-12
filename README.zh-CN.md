# LightX2V Windows App

[English](README.md)

[![构建 Windows App](https://github.com/ModelTC/LightX2V-Windows-App/actions/workflows/build-windows.yml/badge.svg)](https://github.com/ModelTC/LightX2V-Windows-App/actions/workflows/build-windows.yml)

## 下载

| 通道 | Windows 11 · x64 | 状态 |
| --- | --- | --- |
| Latest | [下载 Latest APP](https://github.com/ModelTC/LightX2V-Windows-App/releases/download/latest/LightX2V-APP-Windows-x64.zip) | 开发版；对应提交和 SHA-256 见发布页 |
| Stable | 尚未提供 | 完成 AMD 与 RTX 验收后首次发布 |

每个通道保留一个当前安装包，下载地址固定。普通成功构建更新 Latest；Stable 手动晋升已验证的 Latest 安装包，不重新编译。

[使用说明](docs/getting-started.md) · [验证范围](docs/testing.md) · [发布管理](docs/releasing.md)

## 从源码构建

在 Windows x64 安装 .NET SDK 10.0.403 和 Python 3.12，然后运行：

```powershell
git clone https://github.com/ModelTC/LightX2V-Windows-App.git
cd LightX2V-Windows-App
.\build.ps1
```

[开发说明](docs/development.md) · [第三方组件](THIRD_PARTY_NOTICES.md)
