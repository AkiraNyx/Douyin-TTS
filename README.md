# DouyinTTS - 抖音弹幕播报

抖音直播间弹幕语音播报软件，基于 WinUI 3 + Fluent Design 构建。

## 功能

- **实时弹幕接收** - 通过 WebSocket 连接抖音直播间，实时接收弹幕、礼物、进场消息
- **Edge TTS 播报** - 使用微软 Edge TTS 引擎，支持 18 种中文语音
- **消息过滤** - 关键词过滤、消息长度限制、去重窗口
- **Fluent Design** - 原生 WinUI 3 界面，支持 Mica 背景材质

## 快速开始

1. 输入抖音直播间号或分享链接
2. 点击"连接"
3. 软件自动接收弹幕并语音播报

## 开发环境

- Windows 10 1809+ / Windows 11
- Visual Studio 2022 或更高版本
- .NET 8.0 SDK
- Windows App SDK 1.6

## 构建

```bash
dotnet restore
dotnet build --configuration Release
```

## MSIX 打包

```bash
dotnet publish src/DouyinTTS.App/DouyinTTS.App.csproj -c Release -p:GenerateAppxPackageOnBuild=true
```

## GitHub Actions

Push 到 `main` 分支或创建 `v*` tag 时自动构建 MSIX 安装包。

## 许可证

MIT License
