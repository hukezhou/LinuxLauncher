# Linux Launcher

一个 Windows 桌面工具(WPF / .NET 8),用于通过 SSH 在远程 Linux 服务器上启动和管理 vLLM 等服务。

## 功能

- **多模型配置**:每个模型一份启动脚本,可随时编辑、切换
- **命令标记**:在脚本编辑器中框选文本标记为「命令」,显示为可点击的链接;点击后自动连接 SSH → 等待就绪 → 粘贴命令 → 回车执行
- **命令卷起**:多行命令组(如 `vllm serve ... \` 续行块)可折叠为一行,保持编辑器清爽;点击折叠组仍会执行完整命令,行尾箭头按钮或右键菜单可展开
- **终端管理**:优先使用 Windows Terminal 标签页,自动定位并复用已登录的 SSH 会话

## 构建

```
dotnet build vllmLauncher/vllmLauncher.csproj -c Release
```

产物:`vllmLauncher/bin/Release/net8.0-windows/LinuxLauncher.exe`

## 配置

程序在 exe 同目录读写 `config.json`(服务器地址、模型脚本、命令标记、折叠状态)。首次使用可从 `config.example.json` 复制一份后修改;通常在程序内「服务器地址」和模型编辑界面里配置即可,无需手动编辑 JSON。

> `config.json` 包含真实服务器地址与密钥,已在 `.gitignore` 中排除,请勿提交。

## 依赖

- Windows 10/11(.NET 8 Desktop Runtime)
- `ssh` 客户端(OpenSSH for Windows)
- 可选:Windows Terminal(更好的会话标签体验)
