# Codex用量统计

Windows Codex 桌面客户端的独立用量统计工具：侧栏吸附圆环、托盘额度提示、本机 Token 使用趋势。

**[下载最新 EXE 安装包](https://github.com/wandered8/codex-usage-ui/releases/latest)**

## 安装与使用

1. 在 Releases 下载 `CodexUsageUI-版本-Setup-x64.exe`，双击安装。安装包内置 Node.js，无需自行安装 Node.js、npm 或 Codex CLI，也无需管理员权限。
2. 通过原有图标正常打开 Codex，并登录账号。安装器会创建独立的「Codex用量统计」入口，不修改客户端原图标。
3. 统计程序启动后驻留系统托盘。未启动 Codex 时在后台等待；两者启动顺序不限。
4. 左键托盘图标打开统计，右键管理开机自启或退出。关闭统计窗口会继续驻留托盘。

适用环境：Windows 10/11 x64、.NET Framework 4.8，以及已安装的 Windows Codex 桌面客户端。当前适配 Microsoft Store `OpenAI.Codex_*` 包、进程名 `ChatGPT.exe` 的客户端；其他发行形式暂未验证。非官方工具，未进行代码签名，Windows 可能显示未知发布者。

默认安装在当前用户的 `%LOCALAPPDATA%\Programs\CodexUsageUI`。

从 v1.2.3 起，统计窗口右上角和托盘右键菜单都可检查更新。程序启动后会检查 GitHub 正式发行版，发现新版本时在托盘提示。点击「更新到 v…」并确认后，程序会下载并校验安装包，在当前目录更新并重新驻留托盘。本地设置、统计缓存与快捷方式会保留，Codex 可以继续使用。更早的版本可以先退出统计托盘，再运行带有更新功能的新版安装包。

卸载可在 Windows「已安装的应用」找到「Codex用量统计」，或运行安装目录的 `Uninstall.exe --uninstall`。卸载会删除本工具及其聚合缓存。

## 功能

- 侧栏上下两个独立圆环：5 小时、每周剩余额度，保持百分数和标签。90%–100% 绿色，50%–80% 蓝色渐变，0%–10% 红色，其余区间平滑过渡；0% 显示淡红色底环，未知额度显示灰色底环。
- 贴合客户端内部左栏，按检测到的原生底部按钮位置避让用量和更新入口。最大化、还原时立即调整位置并短时加速校正。
- 圆环悬停显示额度重置时间；有重置卡时逐张展示到期时间。
- 托盘悬停显示两种剩余额度，右键菜单提供开机自启。
- 额度查询短暂失败时保留最近有效读数并提示更新延迟，5 秒后自动重试；超过 150 秒或跨过额度重置时间后不再沿用旧读数。
- 今天 / 7 天 / 30 天本机 Token 统计，包含输入、输出、缓存命中率、每日趋势及模型汇总；柱状图支持悬浮详情。
- 按官方模型标准单价估算 USD 金额，区分普通输入、缓存输入和输出；明细与每日悬浮显示金额，计价说明提供单价和按模型汇总。

## 数据与边界

统计读取本机 `CODEX_HOME`（默认为 `%USERPROFILE%\.codex`）中的近 30 天 `sessions` / `archived_sessions` 日志，缓存聚合数字，不存会话正文和登录凭据，不代表跨设备账户官方总量。

金额按 2026-11-18 核验的[官方 API 价格](https://developers.openai.com/api/docs/pricing)，采用 Standard、短上下文文本价格估算，包含 GPT-6.2 Astra 和 Sol。金额用于估算 API 成本。普通输入 = 输入减缓存命中；缓存输入按单独费率计算。未知价格明确显示待定价，合计带星号时表示部分模型未计价。不含长上下文、Fast/Ultrafast、地区溢价、缓存写入及工具费用；价格不会自动联网更新。总量为输入加输出；缓存命中包含在输入中，不重复累计。

额度通过客户端内置的 app-server 只读查询。客户端升级、接口或可访问性树变化可能导致额度暂不可用或位置偏差。程序识别主窗口侧栏，避免将同进程的宠物窗口作为吸附目标。浮层保持鼠标穿透、不抢焦点；失焦后随可见的 Codex 保持显示，被其他窗口遮挡时随客户端一起被遮挡，最小化或关闭后隐藏。它不注入客户端，不修改更新机制，不要求调试端口。

`quota-state.json`、`token-stats.json`、`usage-cache.json` 等数据保存在本工具安装目录，不上传至 GitHub。发布包不包含个人账户、会话或缓存。

## 从源码构建

在 Windows x64 上使用自带 Windows PowerShell 5.1 与 .NET Framework 4.8：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

构建脚本从 Node.js 官方站下载固定版本并核对 SHA-256，使用系统 C# 编译器，输出 `dist/CodexUsageUI-版本-Setup-x64.exe` 和 `SHA256SUMS.txt`。传入 `-Version 1.2.3` 可指定主程序和安装包的共同版本，`-AppOnly` 可只构建主程序。

在主分支提升 `build.ps1` 中的默认版本，或推送 `v主版本.次版本.修订版本` 标签后，GitHub Actions 会构建、测试并发布对应的 GitHub Release。已有发行版会保留原有附件。客户端从正式发行版读取安装包的名称、大小及 GitHub 提供的 SHA-256。

Node 单元测试（构建后可使用随包运行时）：

```powershell
.\.build\app\runtime\node.exe --test tests\*.test.cjs
```

C# 原生浮层、DPI、统计和更新检测测试入口见源码中的 `--overlay-test`、`--dpi-test`、`--statistics-test`、`--update-test` 参数。安装包构建完毕后，可运行 `./tests/update-install.ps1 -InstallerPath ./dist/CodexUsageUI-1.2.3-Setup-x64.exe` 验证更新写入、回滚和本地数据保留。自检使用合成数据。

## 致谢与许可证

基于 [Lorie7813/codex-usage-card](https://github.com/Lorie7813/codex-usage-card) 的 MIT 许可代码扩展。第三方声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，许可见 [LICENSE](LICENSE)。
