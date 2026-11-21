# LightX2V Windows App

[简体中文](README.zh-CN.md)

[![Build Windows App](https://github.com/ModelTC/LightX2V-Windows-App/actions/workflows/build-windows.yml/badge.svg)](https://github.com/ModelTC/LightX2V-Windows-App/actions/workflows/build-windows.yml)

## Download

| Channel | Windows 11 · x64 | Commit | Date (UTC+8) |
| --- | --- | --- | --- |
| Latest | [Download Latest APP](https://github.com/ModelTC/LightX2V-Windows-App/releases/download/latest/LightX2V-APP-Windows-x64.zip) | [`960c149`](https://github.com/ModelTC/LightX2V-Windows-App/commit/960c1498d0823a91f93080ee32f9a8c29eef0955) | 2026-11-21 11:50 |
| Stable | Not available yet | — | — |

Each channel keeps one current package at a fixed download URL. Ordinary successful builds update Latest; Stable is manually promoted from the verified Latest package without rebuilding.

[Latest release and SHA-256](https://github.com/ModelTC/LightX2V-Windows-App/releases/tag/latest)

[Usage](docs/getting-started.md) · [Testing](docs/testing.md) · [Release management](docs/releasing.md)

## Build from source

On Windows x64, install .NET SDK 10.0.403, Python 3.12, and Visual Studio C++ x64 Build Tools with a Windows SDK, then run:

```powershell
git clone https://github.com/ModelTC/LightX2V-Windows-App.git
cd LightX2V-Windows-App
.\build.ps1
```

[Development](docs/development.md) · [Third-party notices](THIRD_PARTY_NOTICES.md)
