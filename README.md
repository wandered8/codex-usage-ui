# Codex用量统计

Windows Codex 桌面客户端的独立用量统计工具：侧栏吸附圆环、托盘额度提示、本机 Token 使用趋势。

**[下载最新 EXE 安装包](https://github.com/wandered8/codex-usage-ui/releases/latest)**

## 安装与使用

1. 在 Releases 下载 `CodexUsageUI-版本-Setup-x64.exe`，双击安装。安装包内置 Node.js，无需自行安装 Node.js、npm 或 Codex CLI，也无需管理员权限。
2. 通过原有图标正常打开 Codex，并登录账号。安装器会创建独立的「Codex用量统计」入口，不修改客户端原图标。
3. 统计程序启动后驻留系统托盘。未启动 Codex 时在后台等待；两者启动顺序不限。
4. 左键托盘图标打开统计，右键管理开机自启或退出。关闭统计窗口会继续驻留托盘。

适用环境：Windows 10/11 x64、.NET Framework 4.8，以及已安装的 Windows Codex 桌面客户端。当前适配 Microsoft Store `OpenAI.Codex_*` 包、进程名 `ChatGPT.exe` 的客户端；其他发行形式暂未验证。非官方工具，未进行代码签名，Windows 可能显示未知发布者。

默认安装在当前用户的 `%LOCALAPPDATA%\Programs\CodexUsageUI`。更新前请先从托盘退出统计程序，再运行新版安装包；无需关闭 Codex。卸载可在 Windows「已安装的应用」找到「Codex用量统计」，或运行安装目录的 `Uninstall.exe --uninstall`。卸载删除本工具及其聚合缓存，不删除 Codex 客户端、登录和会话记录。

## 功能

- 侧栏上下两个独立圆环：5 小时、每周剩余额度，保持百分数和标签。
- 贴合客户端内部左栏，按检测到的原生底部按钮位置避让用量和更新入口。最大化、还原时立即调整位置并短时加速校正。
- 圆环悬停显示额度重置时间；有重置卡时逐张展示到期时间。
- 托盘悬停显示两种剩余额度，右键菜单提供开机自启。
- 额度查询短暂失败时保留最近有效读数并提示更新延迟，5 秒后自动重试；超过 150 秒或跨过额度重置时间后不再沿用旧读数。
- 今天 / 7 天 / 30 天本机 Token 统计，包含输入、输出、缓存命中率、每日趋势及模型汇总；柱状图支持悬浮详情。
- 按官方模型标准单价估算 USD 金额，区分普通输入、缓存输入和输出；明细与每日悬浮显示金额，计价说明提供单价和按模型汇总。

## 数据与边界

统计读取本机 `CODEX_HOME`（默认为 `%USERPROFILE%\.codex`）中的近 30 天 `sessions` / `archived_sessions` 日志，缓存聚合数字，不存会话正文和登录凭据，不代表跨设备账户官方总量。

金额按 2026-11-05 核验的[官方 API 价格](https://developers.openai.com/api/docs/pricing)，采用 Standard、短上下文文本价格估算，并非订阅实际扣费。普通输入 = 输入减缓存命中；不重复累计缓存费用。未知价格明确显示待定价，合计带星号时表示部分模型未计价。不含长上下文、Fast/Ultrafast、地区溢价、缓存写入及工具费用；价格不会自动联网更新。总量为输入加输出；缓存命中包含在输入中，不重复累计。

额度通过客户端内置的 app-server 只读查询。客户端升级、接口或可访问性树变化可能导致额度暂不可用或位置偏差。浮层保持鼠标穿透、不抢焦点；失焦后随可见的 Codex 保持显示，被其他窗口遮挡时随客户端一起被遮挡，最小化或关闭后隐藏。它不注入客户端，不修改更新机制，不要求调试端口。

`quota-state.json`、`token-stats.json`、`usage-cache.json` 等数据保存在本工具安装目录，不上传至 GitHub。发布包不包含个人账户、会话或缓存。

## 从源码构建

在 Windows x64 上使用自带 Windows PowerShell 5.1 与 .NET Framework 4.8：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

构建脚本从 Node.js 官方站下载固定版本并核对 SHA256，使用系统 C# 编译器，输出 `dist/CodexUsageUI-1.1.1-Setup-x64.exe` 和 `SHA256SUMS.txt`。无需 npm 依赖或外部安装器编译工具。

Node 单元测试（构建后可使用随包运行时）：

```powershell
.\.build\app\runtime\node.exe --test tests\*.test.cjs
```

C# 原生浮层、DPI 和统计测试入口见源码中的 `--overlay-test`、`--dpi-test`、`--statistics-test` 参数；自检使用合成数据，不操作真实 Codex 窗口。

## 致谢与许可证

基于 [Lorie7813/codex-usage-card](https://github.com/Lorie7813/codex-usage-card) 的 MIT 许可代码扩展。第三方声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，许可见 [LICENSE](LICENSE)。
