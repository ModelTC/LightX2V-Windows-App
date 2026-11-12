# LightX2V Windows App

[简体中文](README.zh-CN.md)

[![Build Windows App](https://github.com/ModelTC/LightX2V-Windows-App/actions/workflows/build-windows.yml/badge.svg)](https://github.com/ModelTC/LightX2V-Windows-App/actions/workflows/build-windows.yml)

## Download

| Channel | Windows 11 · x64 | Status |
| --- | --- | --- |
| Latest | [Download Latest APP](https://github.com/ModelTC/LightX2V-Windows-App/releases/download/latest/LightX2V-APP-Windows-x64.zip) | Development build; see the release for its exact commit and SHA-256 |
| Stable | Not available yet | First Stable requires completed AMD and RTX acceptance |

Each channel keeps one current package at a fixed download URL. Ordinary successful builds update Latest; Stable is manually promoted from the verified Latest package without rebuilding.

[Usage](docs/getting-started.md) · [Testing](docs/testing.md) · [Release management](docs/releasing.md)

## Build from source

On Windows x64, install .NET SDK 10.0.403 and Python 3.12, then run:

```powershell
git clone https://github.com/ModelTC/LightX2V-Windows-App.git
cd LightX2V-Windows-App
.\build.ps1
```

[Development](docs/development.md) · [Third-party notices](THIRD_PARTY_NOTICES.md)
