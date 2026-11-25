# AI 额度桌面小组件

AI Quota Widget 是一个 Windows 桌面悬浮小组件，用来查看 Codex、Antigravity 和其他 AI 工具的用量、剩余额度和重置时间。

## 支持的数据来源

| 工具 | 额度来源 |
| --- | --- |
| Codex | 从本机 Codex 会话日志读取最新的额度快照，汇总 token 用量 |
| Antigravity | 根据本机对话日志估算剩余额度，可对照 Antigravity 的额度页面校准 |
| Claude Code、Gemini CLI | 从本机日志汇总用量，估算剩余额度 |
| DeepSeek、OpenRouter、Kimi、硅基流动 | 查询服务商的余额接口 |
| 其他 AI 工具 | 配置自定义余额接口，或手动记录额度 |

本机日志估算的额度和费用仅供参考。

## 安装与运行

运行环境为 Windows 10 或 11，使用系统自带的 .NET Framework 4.x。

从源码运行时，在项目根目录执行下列命令。

```bat
build.cmd
AIQuotaWidget.exe
```

也可以先按下文构建安装包，再选择一种运行方式。

- 安装版。运行 `AIQuotaWidget-Setup.exe`，在安装界面选择是否创建快捷方式和开启开机自启。卸载入口位于 Windows 的已安装应用列表。
- 便携版。解压 `AIQuotaWidget-Portable.zip` 后，双击目录中的 `AIQuotaWidget.exe`。

首次启动后，小组件会显示在屏幕顶部。点击服务图标展开面板，拖动悬浮条可以调整位置。右键菜单提供添加 AI 应用、校准 Antigravity 额度和开机自启等设置。

## 配置与数据

程序默认读取用户目录下的 `.codex` 和 `.gemini` 中对应工具的日志。自定义路径可通过右键菜单的「编辑设置文件」修改 `config.json` 中的 `codexHome` 和 `agBrainDir`。

`config.json` 保存个人配置，`antigravity_quota.json` 保存 Antigravity 的额度校准记录。添加余额查询服务时，可以在 API Key 输入框填入 `env:变量名`，由程序从环境变量获取密钥。直接输入的密钥会以明文保存在本机配置文件中。

## 构建安装包

在项目根目录运行 `pack.cmd`，生成以下文件。

- `dist/AIQuotaWidget-Setup.exe`
- `dist/AIQuotaWidget-Portable.zip`

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
