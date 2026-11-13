# Microsoft Store listing kit

What is in Partner Center for BetterClipboard, and where each answer comes from: every submission page, and the
listing texts in the five languages the package declares (Store policy 10.7 asks for a description in each).
[`microsoft-store.md`](microsoft-store.md) has the reasons and the steps around it.

**State (2026-11-13):** the name is reserved (Store ID `9PHL574X9BF9`, publisher "Nucs"), and the product's first
submission is a draft that holds everything below. This file is the source: change a text here, check it, and
import it (§4).

Limits are Microsoft's, from its documentation (read 2026-11-12; sources at the end of `microsoft-store.md`) and
from Partner Center's own pages (2026-11-13): description up to 10,000 characters of plain text, without web
addresses; short description best under 270 characters; up to 20 product features of at most 200 characters each;
up to 7 search terms of at most 40 characters and 21 words in all; captions up to 200 characters; "What's new" up
to 1,500 characters and left empty for a first submission; the `runFullTrust` justification up to 500 characters;
at least one screenshot per language.

The Russian, Spanish, Hebrew and Chinese texts were written by Claude with the words the app itself uses
(`src/BetterClipboard.Core/Localization`). A native speaker has not reviewed them.

## 1. Pages before the listing

### Pricing and availability

| Field | Answer |
|---|---|
| Markets | All worldwide markets, and any future market |
| Visibility | Public audience; available and discoverable in the Store |
| Schedule | Release as soon as possible; never stop acquisition |
| Base price | Currency *USD - United States*, retail price *0* (the free tier) |
| Free trial, sale pricing | None |

### Properties

| Field | Answer |
|---|---|
| Category | Utilities + tools, without a subcategory (its two, *Backup + manage* and *File managers*, do not fit) |
| Secondary category | Productivity |
| Does this product access, collect, or transmit personal information? | *Yes, my product uses personal information*. It stores what the user copies, on the user's PC only, and transmits nothing. Answering yes makes the privacy policy mandatory, which Store policy 10.5.1 asks of a desktop app anyway |
| Privacy policy URL | `https://github.com/Nucs/BetterClipboard#privacy` |
| Website | `https://github.com/Nucs/BetterClipboard` |
| Support contact info | `https://github.com/Nucs/BetterClipboard/issues` |
| Phone number, address | Empty |
| Display mode | Nothing checked |
| Product declarations | The defaults, except: uncheck *Windows can include this product's data in automatic backups to OneDrive*. The history is encrypted with a key bound to the PC and the Windows account, so a backup restored on another PC could not be opened. *This product incorporates generative AI features* stays unchecked: tick it in the submission that first ships such a feature |
| System requirements | Nothing checked (keyboard and mouse are assumed) |

### Age ratings

The IARC questionnaire, once per product. *App type*: **All Other App Types**. Then every question: **No**.

| Question | Answer |
|---|---|
| Ratings-relevant content (sex, violence, language) in the app package? | No |
| Do users interact or exchange content with other users? | No |
| Does the app feature or promote content that is not part of the download but can be accessed from it (streams, shops, generated AI content)? | No |
| Does it promote or sell age-restricted products or activities? | No |
| Does it share the user's precise location with other users? | No |
| Can users purchase digital goods? | No |
| Cash rewards, gift cards, play-to-earn, cryptocurrency rewards, NFTs? | No |
| Is it a web browser or search engine? | No |
| Is it primarily a news or educational product? | No |
| Ratings from a ratings board, or distribution on physical media? | No |

The ratings these answers give (the preview of 2026-11-13): ESRB Everyone, PEGI 3, USK Everyone, IARC 3+, Microsoft
Store 3+, all ages in Brazil, Russia and Chile. Saving them takes one tick that only the account's owner can give:
"I agree to the IARC Terms of Use and I am the age of majority in my jurisdiction". IARC is sent the publisher
display name and the account's email address.

A feature that changes an answer (content from the internet inside the app, text written by an AI model, anything
users share with each other) means the questionnaire is answered again in the submission that ships it.

### Packages

`BetterClipboard-<version>-store-x64.msix` and `BetterClipboard-<version>-store-arm64.msix`, as the release
pipeline builds them (the `store-packages` artifact of the *Release* run; by hand:
`tools/release/package-msix.ps1 -Version <version> -ForStore`). Device family: Windows 10/11 Desktop, which Partner
Center ticks by itself. From the second submission on the release pipeline uploads them (`microsoft-store.md` §7).

### Submission options

Publishing hold: publish as soon as it passes certification.

**Restricted capabilities: why runFullTrust** (at most 500 characters)

```text
BetterClipboard is a clipboard history manager, a classic desktop app (WinUI 3). It needs runFullTrust for Win32 APIs an AppContainer app cannot use: it listens for clipboard changes of every app (AddClipboardFormatListener), answers a global shortcut (RegisterHotKey; for Win+V a keyboard hook, only with the user's consent, only while it runs), pastes into the window the user came from (SendInput) and shows a tray icon. Its data stays on the device, encrypted. The app makes no network requests.
```

### Additional testing information

*Supplemental info › Additional Testing Information*; the submission options page links to it. No credentials.

**Notes for certification**

```text
BetterClipboard is a clipboard history manager, a full-trust desktop app. No account, no sign-in and no network connection are needed; the app makes no network requests.

How to test:
1. Start the app. It asks for the interface language, then shows a welcome window. Nothing is recorded and no keyboard shortcut is taken until "Start" is pressed there.
2. Copy some text in any app (for example Notepad), a few times.
3. Press Win+V. BetterClipboard's panel opens instead of Windows' own clipboard history. Type to search, use the arrow keys, press Enter to paste the selected item into the app you came from, Esc to close the panel.
4. The app's icon in the notification area opens Settings and exits the app. After "Exit", Win+V opens Windows' own clipboard history again.

Consent and Windows settings (policy 10.2.8, 10.5.5): Win+V is answered only after the user left "Open with Win+V" switched on in the welcome window, only through a low-level keyboard hook, and only while the app runs. No Windows setting is changed. "Start with Windows" uses the package's startup task and is enabled only from the welcome window or the app's Settings; the user can overrule it in Windows' Startup apps list.

Other copies of the same product: BetterClipboard is also distributed outside the Store (GitHub). If such a copy is installed for the same user, the Store edition says so at its first start and offers to take its place. Only after the user presses "Move to this edition" does it copy that copy's history into its own folder, check it, and run that copy's uninstaller (the PowerShell script that product ships; a copy of it is inside this package). Nothing is downloaded.

Uninstall: the history is in the package's own data folder, which Windows removes with the app.
```

## 2. Screenshots and logos

- **Screenshots**: the six pictures in [`packaging/msix/Listing/Screenshots`](../packaging/msix/Listing/Screenshots)
  (PNG, 2560 × 1440), in the order of their file names: the panel over a document, a search with a toggle on, the
  groups column, the image preview, Settings › Privacy, the welcome window. The Store takes PNG of 1366 × 768 or
  larger, at most 50 MB each; at least one per language, four or more recommended. All five listings show the same
  six uploads.
- **How they were made** (repeat it when the panel, Settings or the welcome change visibly): whole-screen pictures
  of a test-signed twin of the Store package in a claude-desktops Windows desktop at 2560 × 1440 and 150 %, dark
  theme, with invented demo data, never the owner's screen. CLAUDE.md §3.5 has the steps;
  `tools/readme/capture-screen.ps1` takes the pictures and `tools/readme/screen-shot.py` rounds the window corners
  that a desktop without a GPU draws square.
- **1:1 app tile icon, 300 × 300**:
  [`packaging/msix/Listing/AppTileIcon.300x300.png`](../packaging/msix/Listing/AppTileIcon.300x300.png), drawn by
  `tools/make_store_assets.py` from the app icon's own drawing, like the package's logos. In every language's
  listing.
- Poster art, box art, hero art and trailers are for games and promotions: empty. *Developed by*, *Short title*,
  *Voice title* and *Additional license terms*: empty.

Captions: every language's section in §3 has a **Screenshot captions** block, one line per screenshot, in the order
of the pictures above (the first line belongs to the first screenshot).

## 3. Listing texts

### English (en-US)

**Product name:** BetterClipboard

**Short description**

```text
Clipboard history that is kept, searchable and encrypted. Everything you copy stays after a restart and opens with Win+V. Pin it, group it, search it; nothing leaves your PC.
```

**Description**

```text
BetterClipboard keeps everything you copy (text, images, files, links, colors) in a searchable history that survives restarts, and opens with Win+V.

Windows' own clipboard history holds 25 items and forgets them when you restart. BetterClipboard keeps thousands, for as long as you want, encrypted on your PC.

• Press Win+V (or any shortcut you choose) and the panel opens next to your text cursor with your newest copy selected. Type to search, press Enter to paste.
• Search as you type, with match case, whole word and regular expressions.
• Pin what you use again and again, and sort items into groups.
• Text, formatted text, images with a zoomable preview, file lists, links and colors.
• Tabs for what you copied where: screenshots from Snipping Tool and ShareX, commands you ran with Win+R, your PowerShell and Command Prompt history, files you opened in Everything and Notepad++, and the prompts you sent to Claude Code and Codex.
• Private by design: the history is encrypted on your PC and bound to your Windows account. Nothing is sent anywhere. Copies that password managers mark as private are never kept, well-known password managers are ignored from the start, and "Forget forever" keeps a text out for good.
• A command line (bclip) for scripts and AI assistants. It is off until you turn it on.
• In English, Simplified Chinese, Russian, Spanish and Hebrew.

BetterClipboard asks first: nothing is recorded until you press Start, and Win+V goes back to Windows the moment you close the app.

Free and open source (MIT license).
```

**Product features**

```text
Keeps your clipboard history across restarts: thousands of items instead of Windows' 25
Opens with Win+V or any shortcut you choose, next to your text cursor
Search as you type, with match case, whole word and regular expressions
Pin items and sort them into groups
Text, formatted text, images with a zoomable preview, files, links and colors
Tabs for screenshots (Snipping Tool, ShareX), Win+R commands, PowerShell and Command Prompt history
Tabs for Everything, Notepad++, and the prompts you sent to Claude Code and Codex
Encrypted on your PC and bound to your Windows account; nothing is sent anywhere
Never keeps copies that password managers mark as private
Forget forever: a text you never want kept again stays out
bclip, a command line for scripts and AI assistants (off by default)
```

**Search terms:** clipboard, clipboard manager, clipboard history, copy paste, paste, Win+V, snippets

**Screenshot captions**

```text
Win+V opens your whole clipboard history, right where you type.
Search as you type: match case, whole words, regular expressions.
Pin what you need again and sort it into groups.
Preview images at full size before you paste.
You decide what is kept: ignored apps, limits, Forget forever.
Nothing is recorded until you say Start.
```

**Copyright and trademark info:** `Copyright (c) Eli Belash. MIT license.`

### Simplified Chinese (zh-Hans)

**Short description**

```text
保留、可搜索并且加密的剪贴板历史。你复制的一切在重启后仍然保留，按 Win+V 即可打开。可以固定、分组、搜索；任何内容都不会离开你的电脑。
```

**Description**

```text
BetterClipboard 会把你复制的一切（文本、图片、文件、链接、颜色）保存在可搜索的历史中，重启后仍然保留，按 Win+V 即可打开。

Windows 自带的剪贴板历史只保留 25 项，重启后就会清空。BetterClipboard 可以保留成千上万项，想保留多久就保留多久，并且加密保存在你的电脑上。

• 按 Win+V（或你选择的任意快捷键），面板会在文本光标旁边打开，并选中最新复制的内容。输入即可搜索，按 Enter 粘贴。
• 边输入边搜索，支持区分大小写、全字匹配和正则表达式。
• 固定常用内容，并把条目整理到分组中。
• 支持文本、带格式文本、图片（可缩放预览）、文件列表、链接和颜色。
• 按来源分类的标签页：截图工具和 ShareX 的截图、用 Win+R 运行过的命令、PowerShell 和命令提示符历史、在 Everything 和 Notepad++ 中打开的文件，以及你发给 Claude Code 和 Codex 的提示词。
• 注重隐私：历史加密保存在你的电脑上，并与你的 Windows 账户绑定。不会向任何地方发送任何内容。密码管理器标记为私密的复制内容绝不会保存，常见的密码管理器默认被忽略，“永久忘记”可以让某段内容永远不再被记录。
• 面向脚本和 AI 助手的命令行（bclip），默认关闭，由你决定是否开启。
• 界面语言：英语、简体中文、俄语、西班牙语和希伯来语。

BetterClipboard 会先征求你的同意：在你按下“开始”之前不会记录任何内容；关闭应用后，Win+V 立即回到 Windows 手中。

免费、开源（MIT 许可证）。
```

**Product features**

```text
重启后仍保留剪贴板历史：成千上万项，而不是 Windows 的 25 项
用 Win+V 或你选择的任意快捷键打开，就在文本光标旁边
边输入边搜索，支持区分大小写、全字匹配和正则表达式
固定条目，并整理到分组中
文本、带格式文本、可缩放预览的图片、文件、链接和颜色
截图（截图工具、ShareX）、Win+R 命令、PowerShell 和命令提示符历史的标签页
Everything、Notepad++ 以及发给 Claude Code 和 Codex 的提示词的标签页
加密保存在你的电脑上，并与你的 Windows 账户绑定；不会向任何地方发送任何内容
绝不保存密码管理器标记为私密的复制内容
永久忘记：不想再保存的内容永远不会再被记录
bclip：面向脚本和 AI 助手的命令行（默认关闭）
```

**Search terms:** 剪贴板, 剪贴板管理器, 剪贴板历史, 复制粘贴, 粘贴, Win+V, 剪贴板工具

**Screenshot captions**

```text
按 Win+V，就在你输入的位置打开完整的剪贴板历史。
边输入边搜索：区分大小写、全字匹配、正则表达式。
固定常用内容，并整理到分组中。
粘贴前先以完整尺寸预览图片。
保留什么由你决定：忽略的应用、数量限制、永久忘记。
在你按下“开始”之前，不会记录任何内容。
```

### Russian (ru)

**Short description**

```text
Журнал буфера обмена, который сохраняется, доступен для поиска и зашифрован. Всё, что вы копируете, остаётся после перезагрузки и открывается по Win+V. Закрепляйте, группируйте, ищите; ничего не покидает ваш компьютер.
```

**Description**

```text
BetterClipboard сохраняет всё, что вы копируете (текст, изображения, файлы, ссылки, цвета), в журнале с поиском, который переживает перезагрузку и открывается по Win+V.

Собственный журнал буфера обмена Windows хранит 25 элементов и забывает их при перезагрузке. BetterClipboard хранит тысячи элементов столько, сколько вы захотите, в зашифрованном виде на вашем компьютере.

• Нажмите Win+V (или любое выбранное вами сочетание клавиш), и панель откроется рядом с текстовым курсором, а последняя копия уже будет выбрана. Начните печатать, чтобы искать; Enter вставляет.
• Поиск по мере ввода: с учётом регистра, только слово целиком и регулярные выражения.
• Закрепляйте то, что нужно снова и снова, и раскладывайте элементы по группам.
• Текст, форматированный текст, изображения с увеличиваемым предпросмотром, списки файлов, ссылки и цвета.
• Вкладки по источникам: снимки экрана из «Ножниц» и ShareX, команды, выполненные через Win+R, журнал PowerShell и командной строки, файлы, открытые в Everything и Notepad++, и запросы, отправленные в Claude Code и Codex.
• Конфиденциальность прежде всего: журнал зашифрован на вашем компьютере и привязан к вашей учётной записи Windows. Никуда ничего не отправляется. Копии, которые менеджеры паролей помечают как личные, никогда не сохраняются, известные менеджеры паролей игнорируются с самого начала, а «Забыть навсегда» больше никогда не даст сохранить выбранный текст.
• Командная строка (bclip) для скриптов и ИИ-помощников. Она выключена, пока вы её не включите.
• На английском, упрощённом китайском, русском, испанском и иврите.

BetterClipboard сначала спрашивает: ничего не записывается, пока вы не нажмёте «Начать», а Win+V возвращается к Windows, как только вы закроете приложение.

Бесплатно, с открытым исходным кодом (лицензия MIT).
```

**Product features**

```text
Сохраняет журнал буфера обмена после перезагрузки: тысячи элементов вместо 25 в Windows
Открывается по Win+V или любому выбранному сочетанию клавиш, рядом с текстовым курсором
Поиск по мере ввода: с учётом регистра, слово целиком, регулярные выражения
Закрепление элементов и группы
Текст, форматированный текст, изображения с увеличиваемым предпросмотром, файлы, ссылки и цвета
Вкладки для снимков экрана («Ножницы», ShareX), команд Win+R, журнала PowerShell и командной строки
Вкладки для Everything, Notepad++ и запросов, отправленных в Claude Code и Codex
Зашифровано на вашем компьютере и привязано к учётной записи Windows; ничего никуда не отправляется
Никогда не сохраняет копии, которые менеджеры паролей помечают как личные
«Забыть навсегда»: текст, который вы не хотите хранить, больше не сохраняется
bclip — командная строка для скриптов и ИИ-помощников (по умолчанию выключена)
```

**Search terms:** буфер обмена, менеджер буфера обмена, журнал буфера обмена, копировать вставить, вставка, Win+V, история буфера

**Screenshot captions**

```text
Win+V открывает весь журнал буфера обмена прямо там, где вы печатаете.
Поиск по мере ввода: с учётом регистра, слово целиком, регулярные выражения.
Закрепляйте нужное и раскладывайте по группам.
Просматривайте изображения в полном размере перед вставкой.
Вы решаете, что хранить: игнорируемые приложения, ограничения, «Забыть навсегда».
Ничего не записывается, пока вы не нажмёте «Начать».
```

### Spanish (es)

**Short description**

```text
Historial del portapapeles que se conserva, con búsqueda y cifrado. Todo lo que copias sigue ahí después de reiniciar y se abre con Win+V. Ancla, agrupa y busca; nada sale de tu PC.
```

**Description**

```text
BetterClipboard guarda todo lo que copias (texto, imágenes, archivos, vínculos, colores) en un historial con búsqueda que sobrevive a los reinicios y se abre con Win+V.

El historial del portapapeles de Windows guarda 25 elementos y los olvida al reiniciar. BetterClipboard guarda miles, durante el tiempo que quieras, cifrados en tu PC.

• Pulsa Win+V (o el atajo de teclado que elijas) y el panel se abre junto al cursor de texto, con tu copia más reciente seleccionada. Escribe para buscar y pulsa Enter para pegar.
• Búsqueda mientras escribes, con coincidencia de mayúsculas y minúsculas, palabras completas y expresiones regulares.
• Ancla lo que usas una y otra vez y ordena los elementos en grupos.
• Texto, texto con formato, imágenes con vista previa ampliable, listas de archivos, vínculos y colores.
• Pestañas según el origen: capturas de Recortes y de ShareX, comandos que ejecutaste con Win+R, tu historial de PowerShell y del Símbolo del sistema, archivos que abriste en Everything y Notepad++, y los mensajes que enviaste a Claude Code y Codex.
• Privado por diseño: el historial está cifrado en tu PC y ligado a tu cuenta de Windows. No se envía nada a ningún sitio. Las copias que los gestores de contraseñas marcan como privadas nunca se guardan, los gestores de contraseñas conocidos se ignoran desde el principio y «Olvidar para siempre» impide que un texto vuelva a guardarse.
• Una línea de comandos (bclip) para scripts y asistentes de IA. Está desactivada hasta que la actives.
• En inglés, chino simplificado, ruso, español y hebreo.

BetterClipboard pregunta primero: no se registra nada hasta que pulses Empezar, y Win+V vuelve a Windows en cuanto cierras la aplicación.

Gratis y de código abierto (licencia MIT).
```

**Product features**

```text
Conserva el historial del portapapeles tras reiniciar: miles de elementos en lugar de los 25 de Windows
Se abre con Win+V o con el atajo que elijas, junto al cursor de texto
Búsqueda mientras escribes, con mayúsculas y minúsculas, palabras completas y expresiones regulares
Ancla elementos y ordénalos en grupos
Texto, texto con formato, imágenes con vista previa ampliable, archivos, vínculos y colores
Pestañas para capturas (Recortes, ShareX), comandos de Win+R e historial de PowerShell y del Símbolo del sistema
Pestañas para Everything, Notepad++ y los mensajes enviados a Claude Code y Codex
Cifrado en tu PC y ligado a tu cuenta de Windows; no se envía nada a ningún sitio
Nunca guarda las copias que los gestores de contraseñas marcan como privadas
Olvidar para siempre: un texto que no quieres guardar no vuelve a guardarse
bclip, una línea de comandos para scripts y asistentes de IA (desactivada de forma predeterminada)
```

**Search terms:** portapapeles, gestor de portapapeles, historial del portapapeles, copiar y pegar, pegar, Win+V, administrador de portapapeles

**Screenshot captions**

```text
Win+V abre todo tu historial del portapapeles, justo donde escribes.
Búsqueda mientras escribes: mayúsculas y minúsculas, palabras completas, expresiones regulares.
Ancla lo que vuelves a necesitar y ordénalo en grupos.
Mira las imágenes a tamaño completo antes de pegarlas.
Tú decides qué se guarda: aplicaciones ignoradas, límites, Olvidar para siempre.
No se registra nada hasta que pulses Empezar.
```

### Hebrew (he)

**Short description**

```text
היסטוריית לוח שנשמרת, ניתנת לחיפוש ומוצפנת. כל מה שמעתיקים נשאר גם אחרי הפעלה מחדש ונפתח עם Win+V. אפשר להצמיד, לקבץ ולחפש; שום דבר לא יוצא מהמחשב שלך.
```

**Description**

```text
BetterClipboard שומר את כל מה שמעתיקים (טקסט, תמונות, קבצים, קישורים, צבעים) בהיסטוריה עם חיפוש, ששורדת הפעלה מחדש ונפתחת עם Win+V.

היסטוריית הלוח של Windows שומרת 25 פריטים ושוכחת אותם בהפעלה מחדש. BetterClipboard שומר אלפי פריטים, לכל זמן שתרצו, מוצפנים במחשב שלכם.

• לוחצים על Win+V (או על כל קיצור מקשים שתבחרו) והחלונית נפתחת ליד סמן הטקסט, כשההעתקה האחרונה כבר מסומנת. מקלידים כדי לחפש, ולוחצים Enter כדי להדביק.
• חיפוש תוך כדי הקלדה, עם התאמת רישיות, התאמת מילה שלמה וביטויים רגולריים.
• מצמידים את מה שצריך שוב ושוב, וממיינים פריטים לקבוצות.
• טקסט, טקסט מעוצב, תמונות עם תצוגה מקדימה שאפשר להגדיל, רשימות קבצים, קישורים וצבעים.
• כרטיסיות לפי מקור: צילומי מסך מכלי החיתוך ומ-ShareX, פקודות שהפעלתם עם Win+R, היסטוריית PowerShell ושורת הפקודה, קבצים שפתחתם ב-Everything וב-Notepad++, וההנחיות ששלחתם ל-Claude Code ול-Codex.
• פרטיות מהיסוד: ההיסטוריה מוצפנת במחשב שלכם וקשורה לחשבון ה-Windows שלכם. שום דבר לא נשלח לשום מקום. העתקות שמנהלי סיסמאות מסמנים כפרטיות לעולם אינן נשמרות, מנהלי סיסמאות מוכרים מוחרגים מראש, ו״שכח לצמיתות״ מונע מטקסט להישמר שוב.
• שורת פקודה (bclip) לסקריפטים ולעוזרי בינה מלאכותית. היא כבויה עד שתפעילו אותה.
• באנגלית, סינית מפושטת, רוסית, ספרדית ועברית.

BetterClipboard שואל קודם: שום דבר לא נרשם עד שלוחצים על ״התחל״, ו-Win+V חוזר ל-Windows ברגע שסוגרים את האפליקציה.

חינמי ובקוד פתוח (רישיון MIT).
```

**Product features**

```text
שומר את היסטוריית הלוח גם אחרי הפעלה מחדש: אלפי פריטים במקום 25 של Windows
נפתח עם Win+V או עם כל קיצור מקשים שתבחרו, ליד סמן הטקסט
חיפוש תוך כדי הקלדה, עם התאמת רישיות, מילה שלמה וביטויים רגולריים
הצמדת פריטים ומיון לקבוצות
טקסט, טקסט מעוצב, תמונות עם תצוגה מקדימה שאפשר להגדיל, קבצים, קישורים וצבעים
כרטיסיות לצילומי מסך (כלי החיתוך, ShareX), לפקודות Win+R ולהיסטוריית PowerShell ושורת הפקודה
כרטיסיות ל-Everything, ל-Notepad++ ולהנחיות ששלחתם ל-Claude Code ול-Codex
מוצפן במחשב שלכם וקשור לחשבון ה-Windows שלכם; שום דבר לא נשלח לשום מקום
לעולם אינו שומר העתקות שמנהלי סיסמאות מסמנים כפרטיות
שכח לצמיתות: טקסט שאינכם רוצים לשמור לא יישמר שוב
bclip: שורת פקודה לסקריפטים ולעוזרי בינה מלאכותית (כבויה כברירת מחדל)
```

**Search terms:** לוח, מנהל לוח, היסטוריית לוח, העתק הדבק, הדבקה, Win+V, ניהול לוח

**Screenshot captions**

```text
Win+V פותח את כל היסטוריית הלוח, בדיוק במקום שבו מקלידים.
חיפוש תוך כדי הקלדה: התאמת רישיות, מילה שלמה, ביטויים רגולריים.
מצמידים את מה שצריך שוב וממיינים לקבוצות.
תצוגה מקדימה של תמונות בגודל מלא לפני ההדבקה.
אתם מחליטים מה נשמר: אפליקציות מוחרגות, מגבלות, ״שכח לצמיתות״.
שום דבר לא נרשם עד שלוחצים על ״התחל״.
```

## 4. Keeping it true

A listing is a promise. When a release changes what the app does, change the listing in the same submission:
a new tab or kind of item goes into the description and the features; a new data source also goes into the README's
*Privacy* section, which is the privacy policy the listing points at. The Store edition makes no network request;
if that ever changes, the description's "Nothing is sent anywhere" and the certification notes must change first.

After any edit of the texts, run `python tools/release/check-store-listing.py`: it checks every language's short
description, description, features, search terms and captions against the Store's limits (§3's fenced blocks and
**Search terms** lines are what it reads, so keep their layout).

**Getting the texts into Partner Center** takes one import instead of pasting some forty fields per language:

1. On the app's overview page in Partner Center, *Export listings* saves a `.csv` with one column per listing
   language and one row per field. That file is the template: its first three columns must stay as they are.
2. `python tools/release/make-store-listing-csv.py --template <exported.csv> --output <import.csv> --screenshots 6`
   fills it from this kit: descriptions, short descriptions, features, search terms, the copyright line, and each
   screenshot's caption. Everything else in the file stays as exported (pictures that are already uploaded appear
   there as web addresses and are kept).
3. *Import listings* › *Upload .csv* takes the result. Partner Center saves nothing unless the whole file is valid.
4. **Export again and check:** `python tools/release/make-store-listing-csv.py --check --template <new export.csv>
   --screenshots 6` compares what Partner Center now holds with this kit: every text, every caption under its
   picture, and every language's screenshots in the order of §2. It names what differs.
5. **Put the screenshots in order** in the languages the check names (below), then repeat step 4 until it says
   that the export is what the kit says.

**An import does not keep the order of the screenshots** (measured 2026-11-13, three imports). After the import
that added the captions, the English listing showed Settings first and the main panel last. After the next one, the
Spanish and the Hebrew listing had changed their order too, although the file had not changed them, and the English
one was still wrong although the file asked for the right order. A caption always stayed with its picture. So:

- The script writes **each caption under its own picture**, wherever that picture is. It tells the pictures apart
  by the captions they already have; for pictures that have none yet it takes the order the languages agree on, or
  the language named by `--screenshots-in-order <language>`. It stops when it cannot tell, and when the languages
  do not show the same pictures.
- The order is put right **on the listing page of each language**: a screenshot dragged onto another one changes
  places with it, and the page saves that at once (no *Save*). Until the page is loaded again it shows *Add image
  caption* on the cards it moved; the captions are still there. Through the browser tools,
  [`tools/release/order-store-screenshots.js`](../tools/release/order-store-screenshots.js) does the dragging: run
  it on a freshly loaded listing page with that language's captions in the order of §3.

Pictures: upload the screenshots and the tile icon once, in the English listing, in the order of §2. The next export
names them by web address; `--copy-pictures-from en-us` puts those addresses into every other language's column, so
all five listings show the same pictures without five uploads.

**Right-to-left listings** (Hebrew): the scripts add a left-to-right mark (U+200E) after the signs that end a Latin
word, as the app does for its own Hebrew texts. Without it a right-to-left Store page shows "Notepad++" as
"++Notepad". Write the texts in §3 plainly; the kit itself must stay free of invisible characters, and the check in
step 4 fails when Partner Center's Hebrew texts lack their marks.
