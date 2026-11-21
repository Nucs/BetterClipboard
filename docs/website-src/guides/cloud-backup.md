---
parent: how-it-works
---
# Cloud backup (design)

A design for backing up your clipboard history to Google Drive, OneDrive, Dropbox, a folder or a file: compressed,
then encrypted to a post-quantum key that only your password can rebuild. Nothing is built yet.

> [!NOTE]
> **Planned, not in BetterClipboard yet.** This page is the design, decided on 2026-11-21. Its measurements were taken
> on that day; when the feature ships, this page becomes its manual.

## At a glance

| | |
|---|---|
| **Where** | Google Drive, OneDrive or Dropbox (each only in a folder of BetterClipboard's own), any folder (a USB drive, a NAS, the folder of iCloud Drive or another sync app), or a single file |
| **What** | Your whole history: every copy with its formats, pins, groups, the prompt archive and the *Forget forever* list. Your settings travel too, and you choose at restore whether to apply them |
| **How it is packed** | Compressed first, with an algorithm per kind of content ([Compression](#compression)), then encrypted |
| **How it is locked** | ML-KEM-1024 and X25519 together, AES-256-GCM for the data. The key pair is rebuilt from your password with Argon2id each time; this PC keeps only the public key |
| **Password** | Any password. No password at all only after you agree that anyone with the backup can then read it |
| **When** | Nightly; a missed night catches up when you step away or 30 minutes after BetterClipboard starts; the hour moves when the PC is usually off then. Quiet: no window, no sound, no notification |
| **Shape** | A full backup weekly and the night's changes in between; two weeks of restore points |
| **Restore** | In Settings or with `bclip`, from the cloud or a file, on this PC or a new one; merged into your history, or replacing it |

## Your password and the keys

### The key comes from the password, every time

Your password is never stored, encrypted or not, and neither is any key that can open a backup. When you set the
password, BetterClipboard:

1. picks a random 32-byte **salt**;
2. runs **Argon2id** on the password and the salt, using 256 MiB of memory for 3 passes (0.6 to 1.9 s on the PC that
   measured it, depending on how busy it was);
3. expands the result with HKDF-SHA-512 into the seed of a **post-quantum key pair**: ML-KEM-1024 together with
   X25519;
4. keeps the **public key** (1.6 KB), the salt and the Argon2id settings in the encrypted history store, and forgets
   the rest.

The same password with the same salt rebuilds the same key pair, on any PC. That is all a restore needs, and every
backup file carries its salt and settings, so one file and the password restore everything.

| Where | What it holds | Can it open a backup? |
|---|---|---|
| This PC | The public key, the salt, the Argon2id settings | No |
| The cloud folder or a backup file | The encrypted backups. Each file's header has the salt, the settings and the key's fingerprint | No |
| Your memory | The password | Yes: it rebuilds the private key |

The public key needs no password: scheduled backups encrypt to it while nobody is around, and nothing on this PC can
decrypt what they wrote. It also checks a typed password offline: *Check password…* rebuilds the key and compares.

### Any password, or none

Any password is accepted, even a short one. A line under the box says roughly how long guessing it would take, never
refuses it ([Guessing a password](#guessing-a-password)).

To back up without a password you must agree to this, once:

> **Back up without a password?**
> Anyone who gets a copy of this backup can read your clipboard history: someone who signs in to your Google,
> Microsoft or Dropbox account, the cloud service, anyone you share the folder with. You can restore it on any PC
> without typing anything.
> ☐ I understand that anyone with the backup can read everything I copied.
> [ Set a password ] [ Back up without a password ]

The second button stays off until the box is ticked. Such a backup uses the same format, keyed by an empty password,
so it is still compressed and integrity-checked. *Settings › Backup* then says "No password: anyone with the backup
can read it". Setting a password later starts a new backup under it ([Changing the password](#changing-the-password)).

### Changing the password

A new password gets a new salt, so a new key. BetterClipboard starts a full backup under the new key at once, quietly.
The backups made under the old key open only with the old password. By default they are deleted once the new full
backup is uploaded and checked; you can keep them until they age out (two weeks) instead. Delete them if the old
password leaked.

The cloud services keep deleted files for a while (Google Drive, OneDrive and Dropbox about 30 days): those copies stay
encrypted, but someone who has your old password and your cloud login could open them until the service purges them.

### A recovery code (optional)

Right after you set a password, BetterClipboard offers a **recovery code**: 30 characters it generates (150 random
bits), such as `7KQ2M-X4RTW-9HPLC-UF3DN-6ZBVA-QE8JY`, to keep in a password manager or on paper. It rebuilds a second
key the same way the password does. From then on every backup file is sealed to both keys, so either one opens it.
Adding or replacing the code starts a new full backup, like a password change.

### If you forget the password

Nothing can open the backups: not BetterClipboard, not the cloud service, not its makers. There is no reset and no
back door; that is the point of the design. The recovery code is the only way back in.

## How a backup is locked

### Why post-quantum

A backup can sit in a cloud folder for years, and encrypted data copied today can be kept until it can be broken
("harvest now, decrypt later"). The classical ways to agree on a key (RSA, elliptic curves such as X25519) fall to a
large enough quantum computer; ML-KEM (FIPS 203) is built not to. BetterClipboard uses both together: the combined key
stays secret unless **both** are broken, so a flaw found later in the young ML-KEM still leaves X25519, and a quantum
computer still faces ML-KEM. The data itself is encrypted with AES-256, which keeps 128 bits of strength even against
Grover's quantum search.

### The keys

| Step | Algorithm | Settings |
|---|---|---|
| Password to key material | Argon2id v1.3 (RFC 9106) | 256 MiB, 3 passes, 4 lanes, 32-byte random salt, 32 bytes out. The password is Unicode-normalized (NFC) and encoded as UTF-8 first, so it rebuilds the same key whatever keyboard typed it |
| Key material to seeds | HKDF-SHA-512 (RFC 5869) | Info `BetterClipboard backup v1 key seeds`, 96 bytes: the 64-byte ML-KEM seed and the 32-byte X25519 private key |
| Key pair | ML-KEM-1024 (FIPS 203) and X25519 (RFC 7748) | Public key: 1,568 + 32 bytes. Fingerprint: the first 8 bytes of its SHA-256, shown as `3576-A3D0-D0C7-5732` |

### Each file

1. A fresh random 256-bit **file key**.
2. For each key the file is sealed to (the password's, and the recovery code's when there is one):
   - an ML-KEM-1024 encapsulation to that key: a 1,568-byte ciphertext and a 32-byte secret;
   - a fresh X25519 key pair, agreed with that key's X25519 half: another 32-byte secret;
   - HKDF-SHA-512 over both secrets, the ML-KEM ciphertext, the fresh X25519 public key and a hash of the recipient's
     public key gives a one-time wrapping key, which seals the file key with AES-256-GCM.
3. The compressed content is encrypted with AES-256-GCM under a key derived from the file key, in 1 MiB chunks. Each
   chunk's nonce is its number plus a last-chunk flag, and each chunk is bound to the file's header, so a chunk that is
   changed, moved, dropped or cut off is refused.

Every step is checked when a file is read: a wrong password is told apart from a damaged file by the key's fingerprint,
and nothing is restored from a file that fails its checks.

### The file format (`.bcbackup`)

| Part | What it holds | Who can read it |
|---|---|---|
| Header | `BCBACKUP`, the format version, full or changes, the date, the backup's chain id (and for changes, the id and hash of the full they build on), the compression of each section, and per key: the salt, the Argon2id settings, the fingerprint, the ML-KEM ciphertext, the fresh X25519 public key and the sealed file key | Anyone (it holds no secret and no content) |
| Content | The compressed sections, encrypted in chunks | Only with the password (or the recovery code) |

### Where the code comes from

- **Windows' own ML-KEM** through .NET 10 where Windows has it (Windows 11 24H2 or later, since its November 2025
  update), and
  [BouncyCastle](https://www.bouncycastle.org/) (MIT) elsewhere, Windows 10 included. Both were checked to derive the
  identical key from the same seed and to open each other's ciphertexts ([Measured](#measured)).
- **BouncyCastle** for Argon2id and X25519; **.NET** for AES-256-GCM, HKDF and SHA-2.
- No cloud provider's SDK: their web APIs with OAuth sign-in in the browser, like the update check.

## Compression

Compression comes first: encrypted data looks random and does not compress. It also cannot be the database file:
BetterClipboard's history is encrypted at rest, so a backup reads the history's content and packs that.

### Leave out what can be rebuilt, reshape the rest

Measured on public, clipboard-like data: 12,000 copied texts sampled from this repository's own files (1,281 of them
also copied as a code editor's colored HTML), and 50 screenshots stored the way BetterClipboard keeps a screenshot.
One compressor (zstd -19 with long-range matching) throughout, so only the preparation changes:

| The text stream holds | Raw | Compressed |
|---|---|---|
| What the history store keeps: text as Windows' UTF-16, plus its search text, preview and content hash | 121.4 MB | 6.61 MB |
| Only what cannot be rebuilt | 75.5 MB | 3.58 MB |
| … with text as UTF-8 (turned back into UTF-16 at restore, exactly) | 51.5 MB | 3.34 MB |
| … with entries grouped by where they came from | 51.5 MB | 3.29 MB |

| The 50 screenshots (59 megapixels) | Compressed |
|---|---|
| As stored: the PNG, a DIB of the same pixels and a thumbnail (253 MB) | 22.9 MB |
| The PNG alone: the DIB and the thumbnail are rebuilt at restore | 10.1 MB |
| The PNG with its pixel rows unpacked and its other parts kept, packed again into a PNG at restore | 6.40 MB |
| … with every row filtered again by the pixel to its left | 5.97 MB |

A PNG keeps its pixel rows deflated, which no stronger compressor can see through, so a backup unpacks them. Each row
was also *filtered* by the app that saved it: stored as its differences from the pixels to its left, above, or a guess
from both, whichever suited deflate for that row. Those mixed choices hide the picture's regularity from a stronger
compressor, so a backup filters every row the same way again: each pixel as its difference from the pixel to its left
(PNG's own *Sub* filter). The rows stay valid PNG rows, so a restore only deflates them again.

The unpacked rows are the one picture step that is not byte-exact: a restored PNG has the same pixels and the same
metadata (color profile, gamma, resolution), checked for every one of the 50, but its compressed bytes differ.
BetterClipboard already recognizes a picture by its pixels, so a restored picture is the same entry.

### Which algorithm

The prepared text stream (51.5 MB), every setting checked by decompressing it again:

| Algorithm | Size | Compresses at | Restores at |
|---|---|---|---|
| zlib -9 (deflate, as in zip) | 9.87 MB | 17 MB/s | 304 MB/s |
| zstd -19 | 4.96 MB | 1.2 MB/s | 722 MB/s |
| zstd -19, long-range matching | 3.29 MB | 1.3 MB/s | 944 MB/s |
| brotli -11 | 3.32 MB | 0.5 MB/s | 522 MB/s |
| xz -9e (LZMA2) | 3.04 MB | 1.3 MB/s | 177 MB/s |
| PPMd H, order 32 (7-Zip) | 2.99 MB | 5.4 MB/s | 6.5 MB/s |
| **PPMd I, order 16, 256 MB (SharpCompress, in .NET)** | **2.69 MB** | **5.3 MB/s** | **5.0 MB/s** |

On a night's new copies alone (0.76 MB, nothing repeated from earlier), PPMd's lead grows: 11.2 % of the raw size,
against 13.8 to 14.7 % for brotli, xz and zstd.

The screenshots' unpacked pixel rows (192.6 MB), as their apps filtered them and filtered again by the pixel to the
left:

| Algorithm | As the apps filtered them | By the pixel to the left | Compresses at | Restores at |
|---|---|---|---|---|
| PPMd I, order 16, 256 MB (SharpCompress) | 6.43 MB | 7.17 MB | 9 to 11 MB/s | 9 to 10 MB/s |
| zstd -19, long-range matching (ZstdSharp, one thread) | 6.40 MB | 5.97 MB | 5 to 6 MB/s | 0.5 to 1.1 GB/s |
| **zstd -19, long-range matching (ZstdSharp, four threads)** | 6.41 MB | **5.99 MB** | **10 to 13 MB/s** | **0.9 to 1.1 GB/s** |
| LZMA, 64 MiB dictionary (SharpCompress) | 5.80 MB | 5.42 MB | 0.9 to 1.3 MB/s | 0.08 to 0.24 GB/s |
| xz -9e (LZMA2), native | 5.78 MB | 5.41 MB | 1.4 to 2.5 MB/s | |

The filter matters as much as the algorithm: PPMd, best on text, packs the re-filtered rows 12 % larger, while zstd and
LZMA pack them 6 to 7 % smaller.

### The choice

- **Text: PPMd (variant I, order 16)** for the copied texts, HTML and RTF, the entry list and the prompt archive. It
  is the smallest (12 % below xz, 18 % below zstd), and one managed library
  ([SharpCompress](https://github.com/adamhathcock/sharpcompress), MIT) covers it. Its memory is sized to each part,
  up to 256 MB: with 64 MB, the text stream came out 55 % larger.
- **Pictures: zstd -19 with long-range matching** on the rows filtered again by the pixel to the left, through
  [ZstdSharp](https://github.com/oleg-st/ZstdSharp) (MIT), which BetterClipboard already ships to read Codex's
  compressed files. It is 7 % smaller than PPMd on the apps' rows at the same speed on four threads (never more than
  half the PC's processors), and it restores a hundred times faster.
- **Not LZMA for the pictures:** about 10 % smaller still, but ten times slower than zstd on four threads, and every
  weekly full backup packs every picture again. **Nor each picture packed alone**, to pack it once and reuse it in
  every later full backup: alone, the screenshots packed 40 % larger, because screenshots share much of their
  content.
- **Safety:** SharpCompress's PPMd threw an error at orders 24 and 32 in these tests, so order 16 it is. Every part is
  unpacked again and compared before it is encrypted, and a part PPMd fails on is packed with zstd instead. Each file's
  header says which algorithm packed each part.
- **Stored as they are:** content that is already compressed (JPEG and GIF pictures, archives, other formats that do
  not shrink).
- **Measured and left out:** a zstd dictionary trained on the history (5 % on a night's changes, not worth a second
  file to keep), and lossless WebP for pictures (5.97 MB, no smaller than zstd, but 6 to 10 s a screenshot and a new
  image codec to ship).

Altogether, the test history that fills a 438 MB database file (65.0 MB compressed, if it were not encrypted) becomes an
8.7 MB backup: 2.69 MB of text and 5.99 MB of pictures.

## What a backup holds

| In the backup | Left out, rebuilt at restore | Never in a backup |
|---|---|---|
| Every entry: its formats as copied, times, source app, use count, pins, groups (with their names and icons) | The search index, search text and previews | The history's own key, and anything that can open the history on this PC |
| The prompt archive (Claude Code, Codex) and its deletions | Thumbnails, picture sizes and pixel hashes | Sign-in tokens for the cloud |
| The *Forget forever* list, so forgotten content stays forgotten after a restore | Content hashes and path counts | Read positions of the integrations (the agents' files, ShareX, the Screenshots folder, Win+R) |
| Hidden Everything picks and shell commands, and the Cmd tab's kept commands | A screenshot's DIB, when its PNG holds the same pixels | Logs and temporary pictures |
| Your settings (applied at restore only if you choose) | | Records of this PC's install (shortcut, startup entry, Explorer settings) |

## Where backups go

| Where | Free space | What BetterClipboard can see there | Its folder |
|---|---|---|---|
| Google Drive | 15 GB, shared with Gmail and Google Photos | Only the files it created (Google's `drive.file` scope) | `BetterClipboard backups` in *My Drive* |
| OneDrive | 5 GB | Only its own folder (`Files.ReadWrite.AppFolder`) | `Apps\BetterClipboard` |
| Dropbox | 2 GB (Dropbox Basic) | Only its own folder (an *App folder* app) | `Apps\BetterClipboard` |
| A folder | Whatever the drive has | That folder | Any folder: a USB drive, a NAS, or the folder of iCloud Drive, Proton Drive or another sync app |
| A file | | | *Settings › Backup › Back up to a file…*, or `bclip backup --to` |

**Why these three:** Google Drive, OneDrive and Dropbox are the most used cloud storage services with a free plan and
a public API on Windows. iCloud Drive is popular too, but Apple offers no Windows API; its sync folder works as a
*folder* backup.

**Signing in:** *Connect* opens the service's own sign-in page in your browser (OAuth with PKCE). The browser hands
the answer back to BetterClipboard through a loopback address, which never leaves the PC. Dropbox accepts only
addresses registered in advance, port included, so for Dropbox that is one fixed port, and when another program holds
it, Dropbox shows a code to paste instead. BetterClipboard never sees your password there. It asks only for its own
folder's scope, so it can neither see nor change your other files. The sign-in token is kept in the encrypted history
store, never in a backup or a log. *Disconnect* forgets it; you can also remove BetterClipboard's access in your
account's app permissions.

**In the cloud,** one folder per PC, named after the PC, so several PCs can back up to one account:

```text
BetterClipboard backups/              (Google Drive; Apps/BetterClipboard on OneDrive and Dropbox)
  DESKTOP-4KQ2M 5f3a2c/
    2026-11-15T030212Z full.bcbackup
    2026-11-16T030115Z changes.bcbackup
    2026-11-17T030341Z changes.bcbackup
    ...
    2026-11-22T030208Z full.bcbackup
    2026-11-23T030127Z changes.bcbackup
```

The service sees those names (the PC's name, the dates), the files' sizes and times, and the headers. It never sees
what you copied, how much of it there is per kind, or your password.

## When backups run

- **Nightly at 03:00.** The backup waits until nobody has touched the PC for 10 minutes; after two hours of waiting
  it runs anyway, at low priority.
- **A missed night** (the PC was off or asleep) catches up at the first 10 minutes without input, or 30 minutes after
  BetterClipboard starts, whichever comes first.
- **The hour moves** when the PC is usually off then: when it was off at the backup hour on 3 of the last 7 nights,
  the hour moves to the one in which this PC was most often on and idle over the last 14 days (on a tie, the latest
  evening or night hour). It moves at most once a week. *Settings › Backup* shows the hour and why ("around 22:00:
  this PC is usually off at 03:00"), and you can pin an hour instead. To know, BetterClipboard keeps a tally of the
  hours it ran and whether you were away: counts only, in the encrypted history store.
- **Quiet.** A backup shows no window, makes no sound and sends no notification. It runs in Windows' background mode
  (low CPU and disk priority). A full backup that is still uploading when you come back pauses and goes on when you
  step away again: every upload can resume where it stopped. It skips Battery saver and metered connections unless
  *Settings › Backup* allows them, waits while a restore runs, and the silent restart after Settings waits for it.
- **Where to see it:** *Settings › Backup* ("Last backup: today 03:02, changes, 1.2 MB · Next: tomorrow around 03:00
  · 9 restore points"), the tray icon's tooltip, and `bclip backup status`. After 7 days without a successful backup,
  the card shows a warning and the tray menu gains *Backup needs attention…*; still no notification.
- **Back up now** in Settings, or `bclip backup`, runs the due backup at once.

## Weekly full backups and nightly changes

- **A full backup** holds the whole history. It runs when the last full one is 7 days old, after a restore, after a
  password change, and when the chain in the cloud is missing or damaged.
- **Every other night** uploads **the changes since that week's full backup**: the content of every entry that is new
  or changed since then, and the complete list of entries with their times, pins and groups (an entry missing from
  the list was deleted). The file grows through the week.
- **A restore point** is a full backup, or a full backup and one night's changes: never more than two files.
- **Two weeks are kept:** this week's full backup with its nights, and last week's. When a new full backup is uploaded
  and checked, the week before last is deleted. Nothing in this needs the password.
- **Nothing is lost to a failed upload.** A file counts only once the service reports the same size and checksum as
  the file BetterClipboard wrote (Google Drive's MD5, OneDrive's QuickXorHash, Dropbox's content hash; a folder is read
  back), and only then are older files deleted.

### What it costs

An example, estimated from the measured costs in [Compression](#compression): a history of 20,000 texts and 600
screenshots, growing by 100 texts and 3 screenshots a day.

| Upload | Size |
|---|---|
| A full backup | about 77 MB: the texts 4.5 MB, the screenshots 72 MB, the entry list 0.2 MB |
| The first night after it | about 0.6 MB |
| The sixth night (the whole week's changes so far) | about 2.7 MB |
| A week in all | about 87 MB |

- **Cloud space:** about 175 MB for the two weeks kept, and briefly about 250 MB while a new full backup is uploaded and
  checked, before the oldest week is deleted. Dropbox's free 2 GB holds eight such histories, with room for each one's
  weekly peak.
- **Pictures decide the size:** every 1,000 screenshots add about 120 MB to each full backup (at the measured 1.2
  megapixels a screenshot; full-screen ones are larger).
- **Time:** packing a full backup takes about 15 s for the texts and 3 minutes for the screenshots (on four threads), in
  the background while you are away. A restore takes a minute or two, plus the download: most of it is spent packing
  the pictures into PNGs again.

## Restoring

*Settings › Backup › Restore…* (also on a new PC, right after installing BetterClipboard):

1. **Choose where from:** a connected service lists the backups it holds, one entry per PC with its restore points
   (dates, full or changes, sizes; listing needs no password). Or pick a `.bcbackup` file; a night's changes need the
   full backup they build on, which BetterClipboard finds next to them.
2. **Type the password** (nothing for a backup without one). A wrong password is said so, quickly: the key's
   fingerprint does not match.
3. **Check what it holds:** the PC, the date, how many entries, pictures, pins, groups and archived prompts.
4. **Choose how:**
   - **Merge into this history** (the default): entries that are not here are added with their original times; an
     entry that is here keeps one copy, pinned if either is, in the groups of both, with the newer use; groups are
     matched by name. The *Forget forever* lists are joined, and anything on the joined list is dropped. Nothing here
     is deleted.
   - **Replace this history:** the current history is set aside, renamed and never deleted (like a history that can
     no longer be decrypted), and the backup becomes the history. BetterClipboard then restarts quietly.
   - **Also restore settings:** off when merging into a history you use, on when this history is empty. The backup
     connection and this PC's own records are never overwritten.
5. **Restore.** It runs in the background through the history's own writer, so the panel stays usable. The next
   backup afterwards is a full one.

## Command line

`bclip` gets the same abilities, behind *Settings › Command line (bclip)* like the rest of `bclip`:

| Command | What it does |
|---|---|
| `bclip backup` | Run the due backup now (a full one or the night's changes) to the connected service or folder |
| `bclip backup --full` | A full backup now |
| `bclip backup --to FILE` | Write a full backup to a file. Uses this PC's public key, so it asks for no password. Without a backup password set up: `--password-stdin`, or `--no-password` for a backup anyone can read |
| `bclip backup status` | Where, the last backup and its outcome, the next one and its hour, the restore points, the key's fingerprint |
| `bclip backup list [--from FILE\|FOLDER]` | Restore points: date, kind, size and PC. No password needed |
| `bclip restore [--from FILE] [--at DATE] [--replace] [--with-settings] [--password-stdin]` | Restore (merging, unless `--replace`). Asks for the password in the terminal without showing it, unless `--password-stdin` |
| `bclip backup check-password [--password-stdin]` | Check a password against this PC's backup key, offline |

The password goes to the running app over `bclip`'s own pipe (your account only, never the network) and is never
written to a log. Exit codes as for every `bclip` command, plus `5`: wrong password, or a file this key does not open.

```powershell
bclip backup --to D:\clipboard.bcbackup            # a full backup on a USB drive, no password asked
bclip backup list                                  # the restore points in the cloud
bclip restore --from D:\clipboard.bcbackup         # merge it into this history (asks for the password)
Get-Content pw.txt | bclip restore --at 2026-11-20 --password-stdin --json
```

## Security and privacy

| Who | Can they read your backups? |
|---|---|
| The cloud service, or someone in your cloud account | No: they hold encrypted files. They can delete them; the services' own version history and trash, a second backup place, and *Back up to a file* help against that |
| A thief of this PC | No more than before: the history itself is on the PC, encrypted at rest. This PC holds no key that opens a backup |
| Someone who copies a backup file | Only by guessing your password ([Guessing a password](#guessing-a-password)), unless the backup has none |
| A quantum computer, now or later | No: it would have to break ML-KEM-1024 and AES-256 |
| Anyone with your password (or recovery code) | Yes |

**Forging a backup** needs the public key, which only your PC and your password have: the cloud never holds it, so
someone with access to your cloud folder cannot slip in a backup of their own. A backup without a password is the
exception: its key comes from the empty password, so anyone with a copy could also write one.

**Forget forever** reaches the cloud as old backups are deleted: within two weeks. *Settings › Backup › Delete cloud
backups* removes them at once.

**Network use** happens only after you connect a service, and only with that service: uploads at the backup hour,
listings and downloads when you restore. The [privacy](privacy.md) section, the Microsoft Store listing and its notes
for certification will name it when backups ship.

### Guessing a password

Someone who copies a backup file can try passwords offline. Each try costs a full Argon2id run with 256 MiB of
memory, which is what makes short passwords slow to guess. As a rough guide, assume 100 graphics cards at about 500
tries a second each (each try moves a few gigabytes through memory, and a card's memory speed bounds it), so about 4.3
billion tries a day:

| Password | Possibilities | Time to try them all |
|---|---|---|
| 6 random lowercase letters | 3 × 10⁸ | about 2 hours |
| 8 random lowercase letters | 2 × 10¹¹ | about 7 weeks |
| 10 random letters and digits | 8 × 10¹⁷ | about 500,000 years |
| 4 random words from a list of 7,776 | 4 × 10¹⁵ | about 2,300 years |
| The recovery code | 10⁴⁵ | out of reach |

Words people pick themselves, names and dates fall much faster: those are tried first.

## Decisions

The owner chose on 2026-11-21, from rendered pages for each option:

| Question | Chosen | Instead of |
|---|---|---|
| How the private key comes back at restore | Rebuilt from the password every time: no private key stored anywhere | A random key pair whose private key is sealed by the password and kept next to the backups (instant password changes) |
| What a backup without a password is | Open: anyone with the file can read it, after the consent dialog | Locked to this PC with Windows' DPAPI; or a code the app generates |
| "Nightly and when the user is inactive or 30 minutes after startup" | Nightly, and a missed night catches up at the first idle moment or 30 minutes after start, with the owner's additions: the hour moves when the PC is usually off then, and backups are quiet | A small extra backup whenever you step away after copying something |
| What each backup uploads | A full backup weekly, and the changes since it nightly, two weeks kept | Only what changed, with restore points thinned over a year; or the whole history every night, two copies kept |

Decided by Claude, for the owner to change:

- **Dropbox as the third service,** Google Drive's visible folder (`drive.file`) over its hidden app data, and a
  *folder* target that covers iCloud Drive and other sync apps.
- **The algorithms and settings** in [How a backup is locked](#how-a-backup-is-locked), the compression in
  [Compression](#compression) (PPMd for text, zstd for pictures, and not LZMA: smaller, but ten times slower), and the
  file format.
- **Quiet** read as: no window, sound or notification, uploads that pause while you use the PC, and a failing backup
  shown only in Settings, the tray tooltip and the tray menu after 7 days.
- **The hour's rule** (3 of 7 nights; the most often on-and-idle hour of 14 days; at most once a week), and the
  two-hour wait for idleness at night.
- **Restoring merges by default,** and replacing sets the old history aside.
- **The recovery code** (optional), *Check password…*, the strength line, and what goes into a backup.
- **The `bclip` commands** and exit code `5`.

## Before it can ship

- **Sign-in registrations** in the owner's accounts: a Google Cloud project with an OAuth consent screen for
  `drive.file` (a non-sensitive scope: basic verification only); a Microsoft Entra app for personal and work accounts
  with `Files.ReadWrite.AppFolder` (a public client: no secret); a Dropbox app with an *App folder*, PKCE and its one
  loopback address. A Dropbox app links at most 500 users in development, and must apply for production within two
  weeks of linking its 50th.
- **The words:** the README's privacy section (it says BetterClipboard sends nothing about your clipboard), the
  Microsoft Store listing and its notes for certification, the third-party notices and *Settings › Third party*
  (BouncyCastle and SharpCompress; ZstdSharp is there already), and this page, turned into the manual.
- **Checks to make:** the sign-in from the Microsoft Store edition (a packaged app answering on a loopback address),
  Dropbox's fixed port and its code to paste, each service's resumable uploads and checksums, a restore of a large
  history, and a restore on Windows 10.

## Not in the first version

- Restoring single entries from a backup (a restore takes a whole restore point; delete what you do not want after).
- Keeping two PCs' histories in sync: a backup is not a sync.
- Other services (WebDAV, S3, MEGA, Box, pCloud); a folder synced by their app works today.
- Unlocking with Windows Hello or a security key instead of the password.
- A *smaller backups* choice that packs pictures with LZMA: about 10 % smaller, ten times slower.

## Measured

On the owner's PC (Windows 11 26200, .NET 10.0.1, 32 logical processors, busy with other work at the time), with
[probe_backup_dotnet.cs](https://github.com/Nucs/BetterClipboard/blob/main/tools/probes/probe_backup_dotnet.cs) and
[probe_backup_compression.py](https://github.com/Nucs/BetterClipboard/blob/main/tools/probes/probe_backup_compression.py),
on BC-TEST and public data only:

- **.NET 10's ML-KEM works on this Windows** (`MLKem.IsSupported`), and from one 64-byte seed Windows' ML-KEM and
  BouncyCastle derive the same 1,568-byte ML-KEM-1024 key; each opens the other's encapsulations. Deriving twice gives
  the same key.
- **Argon2id** (BouncyCastle), 256 MiB, 3 passes, 4 lanes: 0.6 s on a quiet PC, 1.9 s on a busy one (the native
  libsodium, single lane: 1.0 s). 64 MiB: 0.1 to 0.3 s.
- **ML-KEM-1024** key pair, encapsulation, decapsulation: about 1 ms or less each; **X25519**: under 1 ms;
  **AES-256-GCM**: over 1 GB/s.
- **Compression:** the tables in [Compression](#compression), on the repository's tracked text files at commit
  `64ac297` and 50 public screenshots, every result decompressed again and compared. The speeds come from a PC busy
  with other work: compare them with each other, not with your PC. 7-Zip 23.01 ran PPMd H; SharpCompress 1.0.0 ran
  PPMd I and LZMA, and ZstdSharp 0.8.8 zstd, in .NET; Python's zstd, xz and brotli bindings and Pillow ran the rest.
- **The entry list**, estimated with synthetic records (an id, times, flags, a group count and where the content
  lies), packs to about 0.2 MB for a 20,000-entry history: 11 bytes an entry.
