# VocaLink

## 项目简介

VocaLink 是一款面向 Windows 的虚拟歌手桌面宠物Agent。项目整合 Live2D 桌面角色、大语言模型、本地语音合成、本地音乐播放和ACE Studio MCP接口，为用户提供对话、语音生成及ACE Studio工程自动化准备的一体化交互体验。

## 技术栈

- **桌面开发：** C# + WPF
- **角色渲染：** WebView2
- **语音合成：** Python、GPT-SoVITS
- **音频处理：** NAudio
- **数据存储：** SQLite

## 配置与运行

### 环境要求

- Windows 10/11 64 位
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
- [Python 3.10.11（64 位）](https://www.python.org/downloads/release/python-31011/)
- ACE Studio（仅使用 Sing 模式时需要）

### 运行发布版本

1. 从 [Releases](../../releases/latest) 下载完整运行包并解压，然后安装上述运行环境。

2. 打开解压后的 VocaLink 文件夹，在文件夹空白位置右键选择“在终端中打开”，依次执行：

```powershell
py -3.10 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\TTS\requirements.txt
```

如果第一行提示找不到 `py`，请重新安装 Python 3.10.11，并确认安装时勾选了 `Add Python to PATH`。

3. 申请自己的 [DashScope API Key](https://help.aliyun.com/zh/model-studio/get-api-key)，然后将下面命令中的 `api-key` 替换为实际 API Key：

```powershell
[Environment]::SetEnvironmentVariable("DASHSCOPE_API_KEY", "api-key", "User")
```

只需替换 `api-key`，不要修改 `DASHSCOPE_API_KEY` 和 `User`。

4. 双击 `VocaLink.exe` 启动程序。

使用 Sing 模式前，请先安装并启动 ACE Studio。

### 从源码编译

确保已安装 .NET 10 SDK 和 Python 3.10.11，在项目根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

构建完成后运行：

```powershell
.\bin\VocaLink.exe
```
