---
description: "The Win+V clipboard history Windows should have shipped: same shortcut, same panel by your cursor, but it remembers everything, survives restarts, searches instantly and keeps it all encrypted on your PC."
_disableToc: true
_disableAffix: true
_disableBreadcrumb: true
_disableNextArticle: true
_disableContribution: true
---
<!--
  The home page, written by hand: everything else in the manual is cut from README.md by scripts/generate_pages.py.
  Raw HTML, styled by templates/betterclipboard/public/main.css (.bc-*). Keep blank lines only between the top-level
  sections: Markdown ends an HTML block at a blank line, and a following line indented by four spaces or more would
  become a code block.
-->
<div class="bc-home">
  <section class="bc-hero">
    <div>
      <p class="bc-kicker">Clipboard history for Windows</p>
      <h1 class="bc-title">BetterClipboard</h1>
      <p class="bc-lede">The Win+V clipboard history Windows should have shipped. Same shortcut, same panel by your cursor — but it remembers everything, survives restarts, searches instantly, and keeps it all encrypted on your PC. It also keeps what other tools forget: your screenshots, your Win+R commands, your shell history and the prompts you send to Claude Code and Codex.</p>
      <div class="bc-install">
        <pre><code class="lang-powershell">irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex</code></pre>
        <p>In PowerShell. Per user, no admin rights, SHA-256 checked. <a href="docs/ways-to-install.md">Every other way to install</a></p>
      </div>
      <div class="bc-actions" aria-label="Where to start">
        <a class="bc-button bc-button-primary" href="docs/install.md">Install</a>
        <a class="bc-button" href="docs/features.md">Features</a>
        <a class="bc-button" href="docs/use.md">Using the panel</a>
        <a class="bc-button" href="docs/command-line-for-scripts-and-ai-agents.md">Command line</a>
      </div>
      <p class="bc-facts">Windows 10 (2004 or newer) and Windows 11 · x64 and ARM64 · free and open source (MIT)</p>
    </div>
    <!-- Both photos are 947 px tall (taken at 150 % in one session, the second after widening the same panel), so one
         height scales both alike: --bc-shot-w = 692 x 600 / 947, --bc-wide-w = 1414 x 600 / 947. -->
    <figure class="bc-shot" tabindex="0" style="--bc-shot-h: 600px; --bc-shot-w: 438px; --bc-wide-w: 896px" aria-label="The BetterClipboard panel. Rest the mouse on it, or focus it, to see the same panel widened until every tab shows.">
      <img class="bc-shot-usual" src="images/panel-usual.png" width="692" height="947" alt="The BetterClipboard panel, opened with Win+V: the groups column with four groups, the search box with its Aa, W and .* toggles, the tabs All to ShareX with an arrow to more, two pinned items, a Snipping Tool screenshot and a text copied in Chrome.">
      <img class="bc-shot-wide" src="images/panel-wide.png" width="1414" height="947" alt="The same panel, widened until every tab shows: All, Pinned, Text, Images, Links, Files, ShareX, Snipping, Notepad++, Run, Pwsh, Cmd, Claude, Codex and Everything." aria-hidden="true">
      <figcaption><span class="bc-shot-hint">Rest the mouse on the panel to widen it: every tab shows.</span></figcaption>
    </figure>
  </section>

  <section class="bc-section">
    <div class="bc-section-head">
      <h2>Find your way</h2>
      <p>The manual, one page per part of the README, plus guides for every way to install.</p>
    </div>
    <div class="bc-cards">
      <a class="bc-card" href="docs/ways-to-install.md"><span>Install</span><strong>Every way to install</strong><p>The one-line installer, the portable zip, for every user, Intune and RMM tools, Chocolatey and the Microsoft Store edition.</p></a>
      <a class="bc-card" href="docs/use.md"><span>Use</span><strong>The panel</strong><p>Keys, tabs, search, previews, pins and groups.</p></a>
      <a class="bc-card" href="docs/tabs-for-other-apps.md"><span>Sources</span><strong>Tabs for other apps</strong><p>ShareX, Snipping Tool, Notepad++, Win+R, PowerShell, Command Prompt, Everything, Claude Code and Codex.</p></a>
      <a class="bc-card" href="docs/search.md"><span>Find</span><strong>Search</strong><p>Any language, with match case, whole word and regular expressions.</p></a>
      <a class="bc-card" href="docs/taking-over-winv.md"><span>Shortcuts</span><strong>Taking over Win+V</strong><p>Released from Explorer or answered by a keyboard hook, or any shortcuts you choose.</p></a>
      <a class="bc-card" href="docs/command-line-for-scripts-and-ai-agents.md"><span>Scripts &amp; agents</span><strong>bclip</strong><p>List, search, grep and wait for copies, with JSON output.</p></a>
      <a class="bc-card" href="docs/privacy.md"><span>Trust</span><strong>Privacy</strong><p>What it reads, what it keeps, and the one request it makes by itself.</p></a>
      <a class="bc-card" href="docs/how-it-works.md"><span>Inside</span><strong>How it works</strong><p>Capture, encryption at rest, memory and updates.</p></a>
    </div>
  </section>

  <section class="bc-links" aria-label="More">
    <a href="https://github.com/Nucs/BetterClipboard">GitHub repository</a>
    <a href="https://github.com/Nucs/BetterClipboard/releases">Releases</a>
    <a href="https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.zh-CN.md">README: 简体中文</a>
    <a href="https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.ru.md">Русский</a>
    <a href="https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.es.md">Español</a>
    <a href="https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.he.md">עברית</a>
  </section>
</div>
