[ENG](https://github.com/Nucs/BetterClipboard/releases/tag/v0.2.13) / **简体中文**

<!-- source sha256:a2403a26422fecdaf4202dfd05a46442c83c9a31119d5556e9f9970c7f3ff3e9 -->

BetterClipboard 0.2.13

```powershell
irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex
```

**等待时可以占用更少内存。** *设置 › 应用 › 将面板保留在内存中* 是一个新的滑块。BetterClipboard 会把面板保留在内存（RAM）中，这样你按下 `Win+V` 时它能立即打开；任务管理器的 *内存* 列统计的就是这部分内存。这个滑块决定每次使用后它在内存中保留多久：
- *从不从内存中释放* 是默认选项，会一直保留，与以前相同。
- *从不保留在内存中* 会在每次使用几秒后把它交还给 Windows。BetterClipboard 等待时，*内存* 列只有几 MB；下一次按 `Win+V` 会稍慢一点，如果其他程序在此期间用过这部分内存，会更慢一些。
- 介于两者之间，从 1 秒到 7 天：面板闲置达到所选时长后，它会交还给 Windows。
- BetterClipboard 预留的内存，即任务管理器的 *提交大小*，在每个位置都相同：滑块只决定其中有多少留在内存（RAM）中。

| 下载 | 说明 |
|---|---|
| [Microsoft Store](https://apps.microsoft.com/detail/9PHL574X9BF9) | 由 Microsoft 签名，并由商店保持更新 |
| [`install.ps1`](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.13/install.ps1) | `irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 \| iex` |
| [x64 zip](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.13/BetterClipboard-0.2.13-win-x64.zip) · [ARM64 zip](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.13/BetterClipboard-0.2.13-win-arm64.zip) | 便携版：完整的应用和 `bclip`，自带运行时；解压到任意位置后运行 `BetterClipboard.exe` |
| [x64 .7z](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.13/BetterClipboard-0.2.13-win-x64.7z) · [ARM64 .7z](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.13/BetterClipboard-0.2.13-win-arm64.7z) | 相同的文件，体积更小（7-Zip） |
| [`SHA256SUMS.txt`](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.13/SHA256SUMS.txt) | 每个 zip 和 .7z 的 SHA-256；安装程序和更新按钮会校验它 |
| [Chocolatey 包](https://github.com/Nucs/BetterClipboard/releases/download/v0.2.13/betterclipboard.0.2.13.nupkg) | 内含 .7z 压缩包；尚未发布到 Chocolatey 源 |

- 尚未进行代码签名：用浏览器下载的 zip 可能触发 SmartScreen；PowerShell 安装程序、更新按钮和 Microsoft Store 版不受影响。

---

**代码签名政策：** 本版本是否已签名、签名了什么以及由谁批准，请参阅 [README](https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.zh-CN.md#code-signing-policy)。
