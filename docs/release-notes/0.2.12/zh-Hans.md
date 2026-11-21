[ENG](https://github.com/Nucs/BetterClipboard/releases/tag/v0.2.12) / **简体中文**

<!-- source sha256:4bd92414c9da630b0188cfead11967b752239d8bdf8b2f62d2f706aefdfe86f0 -->

BetterClipboard 0.2.12

```powershell
irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex
```

**上架 Microsoft Store。** BetterClipboard 现在也上架了 [Microsoft Store](https://apps.microsoft.com/detail/9PHL574X9BF9)，由 Microsoft 签名，并由商店保持更新：
- 它在记录任何内容之前都会先征得你的同意：首次启动时询问你的语言并显示欢迎页，在你按下 *开始* 之前不会保存任何内容。
- 它运行时会响应 `Win+V`，无需更改任何 Windows 设置。
- 从这个版本起，它把历史保存在 `%LOCALAPPDATA%\BetterClipboard`，也就是其他所有安装方式使用的同一个文件夹，因此在它们之间切换时，你的历史、固定项目和设置都会保留。如果它发现你用安装程序安装的副本，会提议取而代之；反过来，安装程序也会在已安装 Microsoft Store 版时提示你。README 中的 [Microsoft Store 版](https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.zh-CN.md#microsoft-store-edition) 一节有详细说明。

**更快。** 粘贴不再等待面板，面板也不再等待内存：
- `Enter` 会立即粘贴。以前面板会先为下一次 `Win+V` 做好准备，然后才粘贴；现在先粘贴。在一台测试机上，文本在按下 `Enter` 后 0.06 秒就到达应用，以前要 0.19 秒；在电脑内存不足时只需 0.1 秒，以前最长要 1.4 秒。
- 停顿一段时间后按 `Win+V` 也会立即打开：BetterClipboard 会保留面板需要的内容，而不是每次用完都交还给 Windows。等待期间，任务管理器的 *内存* 列显示几十 MB。
- 打开设置的时间缩短了约一半，启动后的第一次 `Win+V` 也是如此：程序现在以预编译形式发布，因此下载会变大，约 90 MB。
- 应用以位图形式复制的大图片，在速览和查看器中打开的时间缩短了约一半。README 中的 [工作原理](https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.zh-CN.md#how-it-works) 一节有详细说明。

**容纳一切。** 历史现在默认不限项目数量，只受 16 GB 的大小上限约束；以前是 10,000 个项目和 4 GB。如果你从未改过这两项限制，它们会改为新的值；你自己设定的值保持不变。
- 用到大小上限的 90 % 时会有通知提醒你，到 92 %、94 %、96 %、98 % 和用满时会再次提醒，届时最早的项目会开始被删除以腾出空间：单击通知可打开设置，也可以让 AI 助手用 `bclip` 来清理。
- *设置 › 历史* 现在会显示每项限制当前的计数，以及大小上限已用了多少。`bclip status` 显示大小上限，`bclip list --sort size` 先列出最大的项目，`bclip delete` 可以一次删除多个 ID。
- 其他应用的标签页也取消了限制：Cmd 标签页保留所有命令，而不再只是最后 1,000 条；`Delete` 可以隐藏任意数量的命令和文件；BetterClipboard 未运行时保存的截图会在它下次启动时全部导入，而不再只是最新的 100 张；Pwsh、Cmd 和 Everything 标签页会在你滚动时列出全部内容。这些限制在 *设置* 中各自的卡片里都有一个数字，0 表示不限制。

**列表从固定项目下方打开。** `Win+V` 和每个标签页现在都会打开在你最新复制的内容上，它位于列表顶部并被选中，你的固定项目就在它的上方。向上滚动或按 `↑` 即可看到它们；搜索结果或分组则从顶部开始显示，其中的固定项目可见。README 中的 [使用](https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.zh-CN.md#use) 一节有详细说明。

**保持输入位置。** `Win+V` 不再从你正在输入的应用中夺走键盘。你在文件资源管理器中正在重命名的文件、它的搜索框或地址栏，或者 Obsidian 中笔记的名称，在你搜索时都会保持编辑状态，所选项目会直接进入其中，就像 Windows 自带的 `Win+V` 一样。打字会搜索面板；方向键、`Enter` 和 `Esc` 的作用与以前相同，单击别处或切换应用时面板仍会关闭。单击面板的搜索框会让面板获得键盘。*设置 › 面板 › 将键盘留给你的应用* 可以关闭此功能，供屏幕阅读器和需要组字的输入法（例如拼音输入法）使用。

**逐个连续粘贴。** 面板标题栏中新增的固定按钮会让面板在粘贴后保持打开：逐个单击项目，每一项都会粘贴到你最后所在的应用。
- 它会一直保持固定，重启后也是如此，直到你再次单击它；`Esc` 仍会关闭面板，`Win+V` 会把固定的面板在原位置带到最前面。在你关闭之前，固定按钮旁会有一条提示，说明固定的用法。
- 在 *设置 › 面板 › 面板中的按钮* 中，你可以隐藏标题栏中除用于打开设置的齿轮按钮以外的任何按钮，并用每个按钮旁的箭头按你喜欢的顺序排列；齿轮按钮始终在最后。
- 用于清空历史的垃圾桶按钮现在默认隐藏；在那里勾选 *清空历史* 即可让它重新显示。README 中的 [使用](https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.zh-CN.md#use) 一节有详细说明。

**表格对齐。** 面板中的文本卡片、速览以及文本查看器的两种视图现在都使用等宽字体。在 *格式化* 视图中，每一行还会保留自己的空格和制表符，因此从终端复制的表格（PowerShell 的、psql 的、用框线字符画的）看起来与在终端中一样，同时 Markdown 仍会被渲染：标题、列表、粗体文字、链接和代码块。引用中的 Markdown 表格、标题和列表也会被渲染，例如引用了每条消息的聊天记录；列表项下的表格，以及不含代码的代码块中的表格也是如此。表格会适应窗口宽度：单元格太长时会在本列内换行，各列保持对齐，包括表情符号和中文文本。README 中的 [文本预览](https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.zh-CN.md#text-previews) 一节有详细说明。

**每张卡片最多 40 行。** *设置 › 面板 › 每张卡片的行数* 用于设置每张卡片显示文本的行数。在你更改之前，卡片保持原来的四行，可设为 1 到 40 之间的任何值；复制的文件卡片最多列出 40 个文件。README 中的 [文本预览](https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.zh-CN.md#text-previews) 一节有详细说明。

**更通透或更磨砂。** *设置 › 面板 › 背景透明度* 用于设置面板后面的内容能透过它的磨砂玻璃看到多少。范围从 0（最磨砂）到 100（最通透），默认为 50，即 Windows 自己的外观；玻璃始终保持模糊。固定的面板现在在你使用其他应用时也会保持玻璃效果。

**设置重新分组。** 面板的设置现在有了单独的一节。*面板* 一节汇集了面板在哪里打开、是否获得键盘、固定项目优先显示、每张卡片的行数、标题栏中的按钮、主题和背景透明度。决定记录哪些内容的设置移到了新的 *采集* 一节，*命令行（bclip）* 移到了 *隐私*，各项集成现在按照面板中标签页的顺序排列。

**手册网站。** README 现在也有了网站 [nucs.github.io/BetterClipboard](https://nucs.github.io/BetterClipboard/)（英文），支持搜索。其中的指南涵盖所有安装方式、PowerShell 安装程序及其全部选项、zip 包、Intune 以及其他为所有用户安装的工具，并列出 BetterClipboard 在你电脑上更改的所有内容。

**中文更新说明。** 从本版开始，更新说明也提供简体中文版。
- 在 GitHub 上和更新对话框中，顶部的 *ENG* / *简体中文* 可在英文和中文之间切换；更新对话框现在会在有对应语言时，以应用的语言打开更新说明。
- Microsoft Store 的中文页面会在其 *新增内容* 中显示它们。

**修复**
- Windows 自带的剪贴板历史中被固定的项目，无论你在这里取消固定多少次，每次启动都会再次以固定状态回来；你删除的项目也会在一个月后回来。现在 Windows 的固定项目只会导入一次，你在这里对它们所做的操作会保持不变。
- 当 Windows 自带的剪贴板历史占用剪贴板超过半秒时，可能会丢失一次复制。BetterClipboard 现在最多等待 10 秒。
- 当你的历史按天数保留项目或已满时，取消固定一个很久以前固定的项目，可能会在下一次复制时把它删除。现在取消固定时，它在历史中的保留时间会重新开始计算，就像它离开最后一个分组时一样。
- 再次运行安装程序会重新打开 *随 Windows 启动*，并且在你已于设置中把 `Win+V` 交还给 Windows 之后再次接管它。现在再次运行会保持这两项不变。
- 在文本查看器中，右键单击所选内容或其他窗口获得键盘时，所选内容会消失；文本滚动后行号会出错；更改文字大小会跳回顶部。在速览中，按键会在其文本收到之前就关闭速览，因此在 *全选* 后按 `Ctrl+C` 什么也复制不了。
- 长文本的速览只要鼠标轻微移动就会关闭。现在只要鼠标仍在其打开位置的 15 像素范围内，它就会保持打开。
- 在中文、俄语、西班牙语和希伯来语中，仍有一些文字显示为英文：大小、更新对话框中的发布日期、更新无法安装的原因，以及卡片对图片、一长串文件或没有文本的带格式内容的描述。在希伯来语中，Everything 标签页中文件的说明行和永久忘记的项目的标题现在会从右向左显示。
- 在繁忙的电脑上，粘贴到响应缓慢的窗口（尤其是终端）时，可能会输入一个 `v` 而不是该项目。现在 `Ctrl` 会一直保持按下，直到窗口接收了 `V`；粘贴期间你输入的按键会紧跟在粘贴的文本之后，而不会变成 `Ctrl` 加该键。
- 只有一个空格或一个空行的复制会留在历史中，每次粘贴都会让它回到你最新复制的内容正下方，因此第二个项目可能只粘贴出一个空格。现在只含空格、制表符或空行的复制不再保留，之前保留的会被一次性删除，固定的或分组中的除外。
- 长文本的速览打开时，`Enter`、方向键、`Delete` 和 `Esc` 都不起作用：第一次按键只会关闭速览，而速览又会在静止的鼠标下重新打开，因此在鼠标移动之前 `Enter` 永远不会粘贴。现在每个按键都会立即生效，就像图片的速览一样。
- 当面板正在显示查看器、速览或菜单时切换到另一个应用，面板会一直停留在那个应用之上，直到再次按下 `Win+V`。现在面板会与它的查看器一起关闭，就像没有打开其他内容时一样；用 `Esc` 关闭查看器后，也不会在静止的鼠标下重新打开卡片的速览。
- 设置打开时，卡片的速览可能一直不出现；长文本的速览可能在鼠标已经离开面板后才打开，并一直停留到鼠标再次移动。现在速览只在鼠标停在其卡片上时打开，并且只要停在上面就会打开。
- 在多个屏幕的情况下，*设置 › 面板 › 面板位置* 中的 *当前屏幕中央* 会把面板打开在你正在输入的屏幕上，即使鼠标在另一个屏幕上。现在它会打开在鼠标指针所在的屏幕上。
- 在“开始”菜单或搜索打开时按 `Win+V`，面板会显示在“开始”菜单后面。现在它会在“开始”菜单旁边打开，“开始”菜单保持打开，所选项目会进入“开始”菜单的搜索框，就像 Windows 自带的 `Win+V` 一样。在快速设置、通知中心或任务视图之上，或者“开始”菜单旁边没有空间时，它们会先关闭，面板在你的应用之上打开。如果在以管理员身份运行的 PowerShell 中使用安装程序新的 `-UIAccess` 选项安装，面板会像 Windows 自带的那样在“开始”菜单和搜索之上打开，其更新按钮在每次更新时都会请求管理员权限。网站上的 [PowerShell 安装程序](https://nucs.github.io/BetterClipboard/docs/powershell-installer.html)（英文）有详细说明。
- 在设置中对 Cmd 标签页的卡片执行 *删除已存储的内容…* 之后，仍然打开着的命令提示符窗口中的命令会在设置关闭几秒后回来。现在它们会保持删除状态，重启后也是如此。
- 当 BetterClipboard 安装在其他文件夹中时，不带 `-InstallDir` 再次运行安装程序会在常用文件夹中放入第二个副本。现在它会就地更新已有的副本。
- 面板中的搜索框有一个不起任何作用的搜索按钮：面板会随你的输入而搜索。这个按钮已被移除，为你输入的内容留出了更多空间。
- *设置 › 面板 › 面板位置* 中的默认选项 *文本光标旁边*，在 Chrome、Edge、VS Code 等基于 Chromium 的应用、Firefox 以及传统控制台中，会把面板打开在鼠标指针旁边。现在它会像屏幕阅读器那样询问这些应用文本光标的位置。在 Windows 终端、资源管理器的地址栏和“设置”应用中，面板仍会在鼠标旁边打开。
- 在面板中，单击标签页或顶部的某个按钮，或者按 `Tab` 键，会让搜索框失去键盘，随后输入的字母都会丢失。现在搜索框会保留键盘；只有菜单、对话框和查看器会获得键盘，并在关闭时把它交还。
- 对指向图片、文档、文件夹或网址的 `Win+R` 命令选择 *以管理员身份运行* 时没有任何反应：既没有打开窗口，也没有弹出权限提示。现在，对于 Windows 无法以管理员身份打开的项目，卡片菜单会像资源管理器的菜单一样将此项显示为灰色；`Ctrl+Shift+Enter` 会像 `Win+R` 一样正常打开这类项目。程序仍会请求管理员权限。

| 下载 | 说明 |
|---|---|
| [Microsoft Store](https://apps.microsoft.com/detail/9PHL574X9BF9) | 由 Microsoft 签名，并由商店保持更新 |
| [`install.ps1`](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.12/install.ps1) | `irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 \| iex` |
| [x64 zip](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.12/BetterClipboard-0.2.12-win-x64.zip) · [ARM64 zip](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.12/BetterClipboard-0.2.12-win-arm64.zip) | 便携版：完整的应用和 `bclip`，自带运行时；解压到任意位置后运行 `BetterClipboard.exe` |
| [x64 .7z](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.12/BetterClipboard-0.2.12-win-x64.7z) · [ARM64 .7z](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.12/BetterClipboard-0.2.12-win-arm64.7z) | 相同的文件，体积更小（7-Zip） |
| [`SHA256SUMS.txt`](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.12/SHA256SUMS.txt) | 每个 zip 和 .7z 的 SHA-256；安装程序和更新按钮会校验它 |
| [Chocolatey 包](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.12/betterclipboard.0.2.12.nupkg) | 内含 .7z 压缩包；尚未发布到 Chocolatey 源 |

- 尚未进行代码签名：用浏览器下载的 zip 可能触发 SmartScreen；PowerShell 安装程序、更新按钮和 Microsoft Store 版不受影响。

---

**代码签名政策：** 本版本是否已签名、签名了什么以及由谁批准，请参阅 [README](https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.zh-CN.md#code-signing-policy)。
