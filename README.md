# Codex Monitor

<p align="center">
  <img src="Codex-icon-preview.png" width="96" alt="Codex Monitor icon">
</p>

<p align="center">
  一款轻量、清晰的 Windows Codex 桌面监控工具。<br>
  实时查看活跃任务、累计 Token、可用模型，以及 5 小时与每周使用限额。
</p>

<p align="center">
  <a href="https://github.com/Yxianshe/Codex-Monitor/releases/latest"><strong>下载最新版</strong></a>
  ·
  <a href="https://github.com/Yxianshe/Codex-Monitor/releases">全部版本</a>
</p>

![Codex Monitor V2](assets/codex-monitor-v2.png)

## V2.1 特性

- **实时任务列表**：优先显示任务标题，而不是截取整段提示词。
- **模型 / Token 切换**：点击 `Token`，在任务当前模型色标和每个任务的累计 Token 之间切换。
- **新任务默认模型**：从 Codex 动态模型目录选择新任务的默认模型和智能等级；运行中任务只读显示真实状态，不同模型族与等级使用独立色标。
- **限额概览**：展示 5 小时与每周剩余额度、已用比例、重置倒计时和准确日期卡片。
- **V2.1 液态玻璃**：使用 Skia SDF 位移透镜、边缘 RGB 色散、低振幅 turbulence、柔和 Fresnel 高光与循环珠光描边。
- **日夜场景**：07:00–18:59 使用太阳主题，19:00–06:59 使用月球主题；右上角按钮可手动切换。
- **桌面窗口体验**：支持置顶、最小化、快速拖动，以及四边和四角缩放。
- **远程桌面兼容**：使用适合集成显卡与远程桌面的 Skia 渲染路径。
- **本地优先**：任务、Token 和限额数据只在本机读取与显示；只有用户主动更改“新任务默认”时才写入本机 Codex 配置。

## 快速开始

1. 从 [Releases](https://github.com/Yxianshe/Codex-Monitor/releases) 下载最新的 `CodexTaskMonitor-v2.x.x.exe`。
2. 双击运行，无需安装。
3. 保持 Codex 桌面端已登录并使用过至少一个任务。

> Windows 可能会对未签名的个人开发程序显示 SmartScreen 提示。请确认下载来源为本仓库后再运行。

## 顶部控件

| 控件 | 功能 |
|---|---|
| `Token` | 切换当前模型色标 / 任务累计 Token |
| 日月按钮 | 手动切换太阳 / 月球场景 |
| 图钉 | 切换窗口置顶 |
| `—` | 最小化 |
| `×` | 退出 |

窗口顶部空白区域可拖动；四条边与四个角均可调整大小。

> Codex 不支持从另一个客户端把已经开始推理的当前回合热切换到另一模型。任务行只读显示当前状态；顶部“默认”选择器写入 Codex 的新任务默认模型与智能等级。

## 从源码构建

环境要求：

- Windows 10 / 11
- .NET 8 SDK
- PowerShell 5.1 或更高版本

```powershell
cd .\v2-native
.\build.ps1
```

构建结果位于：

```text
dist/CodexMonitorV2/CodexMonitorV2.exe
```

## 数据来源

| 信息 | 本地来源 |
|---|---|
| 任务标题 | Codex `session_index.jsonl` |
| 活跃状态与累计 Token | Codex `state_5.sqlite` |
| 模型与详细 Token | Codex rollout 日志 |
| 限额与重置时间 | Codex 本地 App Server 的速率限制状态 |
| 新任务默认模型 / 智能等级 | Codex 本机用户配置（仅在用户主动选择时写入） |

“累计 Token”表示该任务被模型处理的累计文本量，不等同于计费金额，也不等同于 5 小时或每周限额百分比。状态数据可能存在数秒延迟。

## 项目结构

```text
v2-native/
├─ CodexMonitorV2/             V2 Avalonia 应用
├─ LiquidGlassAvaloniaUI/      Skia 液态玻璃渲染层
├─ ShaderSmoke/                Shader 与 Codex 模型目录冒烟检查
├─ build.ps1                   V2 构建脚本
└─ README.md                   V2 技术说明

assets/                        GitHub 产品图
monitor.ps1                    V1 PowerShell 版本
```

## 隐私

程序不包含遥测、账号上传或第三方数据服务。任务标题、Token 与限额数据只在本机界面显示；仅“新任务默认”控件会在用户操作后更新本机 Codex 配置。README 产品图使用脱敏的示例任务名称。

## 开源致谢

- [LiquidGlassAvaloniaUI](v2-native/LICENSE.LiquidGlassAvaloniaUI)
- SDF 透镜思路参考 [Cloudy](https://github.com/skydoves/Cloudy) 与 [FletchMcKee/liquid](https://github.com/FletchMcKee/liquid)

## 许可证

[MIT License](LICENSE)。欢迎二次开发与提交改进。
