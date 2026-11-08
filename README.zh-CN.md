# BetterClipboard 简体中文支持

基于 [Nucs/BetterClipboard](https://github.com/Nucs/BetterClipboard) 的简体中文界面适配。保留原项目 MIT 许可证、作者署名、数据格式和英文界面，不翻译剪贴板内容、文件路径、命令、提示词或用户创建的分组名称。

## 选择语言

在 **Settings / 设置 → App / 应用 → Language / 语言** 选择：

- 跟随 Windows（默认）：简体中文地区使用中文，其他语言使用英文回退。
- English：英文。
- 简体中文：不依赖 Windows 显示语言，强制使用简体中文。

保存后退出并重新打开 BetterClipboard。切换语言不会清除历史，也不会立即中断采集。

设置以 `InterfaceLanguage` 字段保存，可选值为 `system`、`en`、`zh-Hans`。旧设置没有此字段时自动跟随系统。

## 覆盖范围

包含主剪贴板面板、设置页、应用自带菜单和工具提示、空状态、提示词与命令历史标签页、托盘操作、更新说明界面，以及动态计数与状态消息。

英文版本说明、第三方组件名称、上游错误原文、系统快捷键名称和技术标识不进行机器替换。翻译仅作用于应用拥有的显示模板；插值参数原样保留。

## Windows 安装包

分支的 `Simplified Chinese build` GitHub Actions 工作流执行解决方案构建、完整测试及官方 `tools/release/package.ps1`，生成 Windows x64 自包含 ZIP、7z 和 `SHA256SUMS.txt`，无需另装 .NET 运行时。

下载构建产物并解压外层 artifact ZIP。内层 `BetterClipboard-0.2.9-zh.1-win-x64.zip` 是官方格式的安装包，不是单文件 EXE 安装器。请保留 `SHA256SUMS.txt` 在安装包旁，使用已经审阅的官方 `install.ps1` 的 `-Package` 参数安装。安装脚本在内层 ZIP 中；建议先解压到独立目录，阅读脚本参数后再运行。

注意：上游自动更新当前仍指向官方发布版，升级到未合并中文支持的版本会覆盖本分支的界面改动。使用此构建时可以在设置中关闭自动检查更新，待本地化合并后恢复。本次构建不自动修改已安装的应用、开机项、快捷键或剪贴板历史。

## 开发

- 资源：`src/BetterClipboard.Core/Localization/en.json`、`zh-Hans.json`。
- XAML 使用 `loc:Tr`，C# 使用 `UiText.Translate` / `UiText.Format`。
- 稳定资源键由英文原文的 SHA-256 前 12 位构成，前缀为 `T`。
- 新增翻译时须同时维护两个目录；保持复合格式的参数类型和格式说明符有效。简体中文可以省略英文复数后缀参数，但不得翻译传入的用户内容。
- `LocalizationTests` 验证语言回退、旧设置兼容、资源完整性、格式参数及用户内容保留。
- 日志、CLI 协议和数据标识不是本次界面本地化的目标。

构建通过和单元测试通过不等于真实桌面验收；发布说明应另行记录中文显示、文字裁切和实际粘贴的验证结果。
