# Codex 额度悬浮窗

Windows 专用的小型悬浮窗，自动读取当前 Codex 登录账户的额度。

- 打开 Codex 后自动显示，并在首次启动时贴靠 Codex 窗口右侧。
- 关闭 Codex 后悬浮窗自动隐藏。
- 每 30 秒自动刷新；双击悬浮窗可立即刷新。
- 网络波动时会自动重试；已有成功数据时不会因单次失败立即显示错误。
- 支持标准 HTTPS 代理环境变量，并在需要时自动读取 Windows 当前用户的手动代理设置；连接失败后会重建客户端并重新读取代理。
- 按住悬浮窗任意位置即可拖动，位置会自动记忆。
- 右键选择“恢复自动贴靠”可清除手动位置。
- 默认使用“同层”档位并绑定 Codex 主窗口；切换到“最前”后才会系统级置顶。
- 右侧“同层 / 最前”双档开关与右键菜单同步，选择会自动记忆。
- 显示剩余百分比、重置时间、账户套餐和加购余额状态。
- 中式米白与暖金界面，当前时间每秒更新并作为主视觉显示。
- 不保存、上传或输出登录令牌，只读取 `%USERPROFILE%\.codex\auth.json` 并通过 HTTPS 请求 Codex 自身使用的额度接口。

## 安装

1. 在 [Releases](https://github.com/Stnwso2/CodexQuotaFloat/releases) 下载最新的 `CodexQuotaFloat-v*.zip` 并解压。
2. 右键 `install.ps1`，选择“使用 PowerShell 运行”。

正式 Release 是 Windows x64 自包含版本，无需另外安装 .NET Runtime。安装器会把同目录的 `CodexQuotaFloat.exe` 复制到 `%LOCALAPPDATA%\CodexQuotaFloat`，并添加名为 `Codex Quota Float` 的用户级启动项。后台监听器在 Windows 登录后静默等待，只有 Codex 运行时才显示悬浮窗。

如果 PowerShell 阻止运行下载的脚本，可在解压目录执行：

```powershell
Unblock-File .\install.ps1, .\uninstall.ps1, .\CodexQuotaFloat.exe
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

## 操作

- 双击悬浮窗：立即刷新。
- 右键悬浮窗或系统托盘图标：刷新、切换最前显示、退出。

## 卸载

右键 `uninstall.ps1`，选择“使用 PowerShell 运行”。

## 从源码构建

需要安装 [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)。在项目目录运行：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

将生成的 `CodexQuotaFloat.exe` 与 `install.ps1`、`uninstall.ps1` 放在同一目录即可安装。

## 验证下载来源

每个正式 Release 都由 GitHub Actions 从对应标签自动构建，同时附带 SHA-256 校验文件和 GitHub Artifact Attestation。安装前可验证：

```powershell
Get-FileHash .\CodexQuotaFloat-v*-win-x64.zip -Algorithm SHA256
gh attestation verify .\CodexQuotaFloat-v*-win-x64.zip --repo Stnwso2/CodexQuotaFloat
```

校验文件可确认下载内容是否完整，Artifact Attestation 可确认安装包由本仓库对应的 GitHub Actions 工作流生成。

## 兼容性

当前版本针对 Codex Windows x64 桌面版。Codex 若改变内部额度接口，工具会显示“额度数据暂时不可用”，不会影响 Codex 本身。

## 隐私与免责声明

本工具只在本机读取 Codex 登录信息并向 Codex 自身的额度接口发起 HTTPS 请求，不会把令牌发送给其他服务。项目并非 OpenAI 官方产品，Codex 内部接口发生变化时可能暂时不可用。

程序禁止额度请求自动跳转，Bearer Token 和账户标识只会发送给代码中固定的 `https://chatgpt.com/backend-api/wham/usage` 地址。窗口设置文件只保存位置与置顶选项，不保存登录信息。

发现安全问题时，请通过仓库的 [Private vulnerability reporting](https://github.com/Stnwso2/CodexQuotaFloat/security/advisories/new) 私下报告，不要在公开 Issue 中粘贴令牌或账户信息。

## 许可证

[MIT](LICENSE)
