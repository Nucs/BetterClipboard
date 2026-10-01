"""Structure-only probe of the prompt histories Claude Code and Codex keep (CLAUDE.md §2.21).

Re-run it after an agent update: every number the prompt archive's design rests on comes from here. It reads the agents'
folders and never writes them; it prints key names, counts, shapes and time deltas — never a prompt, a pasted text, a
project path or a file name of a session.

Usage:
  python tools/probes/probe_agent_prompts.py [--claude DIR] [--codex DIR] [--skip-transcripts]

  --claude DIR        Claude Code's config folder (default: %CLAUDE_CONFIG_DIR% or ~/.claude)
  --codex DIR         Codex's home folder (default: %CODEX_HOME% or ~/.codex)
  --skip-transcripts  skip the slow comparison with Claude Code's transcripts (projects/**/*.jsonl, gigabytes)

What it checks:
  Claude Code history.jsonl: line count, CRLF lines, lines without a session id (and their dates), whether times only go
    forward, exact duplicates by timestamp + session (Claude Code's own identity of an entry), placeholder shapes,
    pastes inline vs in paste-cache/ and whether a cache file's name is the first 16 hex characters of its SHA-256,
    pastes no placeholder refers to, slash commands.
  Claude Code transcripts: how many prompts marked typed/queued (promptSource) are also in the history, and what the
    transcripts' other user messages are (generated notices, teammate messages, script sessions).
  Codex history.jsonl: line count, and how many lines have their twin (same session and text) in a session file, with
    the time difference.
  Codex session files: count (plain / .zst), CLI versions, sources and thread sources (whose prompts), how prompts are
    recorded (item_completed UserMessage vs user_message), item ids repeated across files (fork copies: same text;
    counter collisions: other text).
  Codex state databases: whether thread_history_*.sqlite projects the session files by byte offset (schema only).
"""
import argparse
import collections
import datetime
import glob
import hashlib
import json
import os
import re
import sqlite3
import statistics

PLACEHOLDER = re.compile(r"\[(Pasted text|Image|Audio|\.\.\.Truncated text) #(\d+)(?: \+\d+ lines)?\.*\]")


def default_dir(variable, name):
    value = os.environ.get(variable)
    return os.path.expandvars(value) if value else os.path.join(os.path.expanduser("~"), name)


def day(ms):
    return datetime.datetime.fromtimestamp(ms / 1000, datetime.timezone.utc).strftime("%Y-%m-%d")


def expand(entry, paste_dir):
    pastes = entry.get("pastedContents") or {}

    def sub(match):
        if match.group(1) not in ("Pasted text", "...Truncated text"):
            return match.group(0)
        paste = pastes.get(match.group(2))
        if not paste or paste.get("type") != "text":
            return match.group(0)
        if "content" in paste:
            return paste["content"]
        try:
            with open(os.path.join(paste_dir, paste.get("contentHash", "") + ".txt"), encoding="utf-8") as f:
                return f.read()
        except OSError:
            return match.group(0)

    return PLACEHOLDER.sub(sub, entry["display"])


def claude_history(claude):
    path = os.path.join(claude, "history.jsonl")
    print(f"== Claude Code history.jsonl: {'missing' if not os.path.exists(path) else f'{os.path.getsize(path):,} bytes'}")
    if not os.path.exists(path):
        return {}
    paste_dir = os.path.join(claude, "paste-cache")
    lines = crlf = bad = no_session = backwards = commands = 0
    no_session_days = set()
    keys = collections.Counter()
    shapes = collections.Counter()
    storage = collections.Counter()
    cache = collections.Counter()
    unreferenced = 0
    previous = None
    texts = collections.defaultdict(set)
    with open(path, "rb") as f:
        for raw in f:
            lines += 1
            crlf += raw.endswith(b"\r\n")
            try:
                entry = json.loads(raw)
            except ValueError:
                bad += 1
                continue
            stamp = entry.get("timestamp", 0)
            if previous is not None and stamp < previous:
                backwards += 1
            previous = stamp
            if "sessionId" not in entry:
                no_session += 1
                no_session_days.add(day(stamp))
            keys[(stamp, entry.get("sessionId"))] += 1
            display = entry.get("display", "")
            commands += bool(re.match(r"\s*/[A-Za-z][\w:.-]*(\s|$)", display))
            referenced = {int(m.group(2)) for m in PLACEHOLDER.finditer(display)}
            for m in PLACEHOLDER.finditer(display):
                shapes[re.sub(r"\d+", "N", m.group(0))] += 1
            for paste_id, paste in (entry.get("pastedContents") or {}).items():
                storage["inline" if "content" in paste else "paste-cache" if "contentHash" in paste else "other"] += 1
                unreferenced += paste.get("id") not in referenced
                if "contentHash" in paste:
                    file = os.path.join(paste_dir, paste["contentHash"] + ".txt")
                    if os.path.exists(file):
                        with open(file, "rb") as cached:
                            cache["name = sha256 prefix" if hashlib.sha256(cached.read()).hexdigest().startswith(paste["contentHash"]) else "name differs"] += 1
                    else:
                        cache["file gone"] += 1
            texts[entry.get("sessionId")].add(expand(entry, paste_dir).replace("\r\n", "\n").strip())
    duplicates = sum(c - 1 for c in keys.values() if c > 1)
    print(f"   {lines:,} lines ({bad} malformed), {crlf} with CRLF, {no_session} without a session id (days: {sorted(no_session_days)})")
    print(f"   times going backwards: {backwards}; exact duplicates (timestamp + session): {duplicates}; slash commands: {commands}")
    print(f"   placeholders: {dict(shapes)}")
    print(f"   pastes: {dict(storage)}; paste-cache files: {dict(cache)}; pastes no placeholder refers to: {unreferenced}")
    return texts


def claude_transcripts(claude, history_texts):
    root = os.path.join(claude, "projects")
    print(f"== Claude Code transcripts ({root})")
    if not os.path.isdir(root):
        print("   none")
        return
    every = set().union(*history_texts.values()) if history_texts else set()
    stats = collections.Counter()
    for dirpath, _, filenames in os.walk(root):
        if "subagents" in dirpath.split(os.sep):
            continue
        for name in filenames:
            if not name.endswith(".jsonl"):
                continue
            with open(os.path.join(dirpath, name), "rb") as f:
                for raw in f:
                    if b'"type":"user"' not in raw or b'"tool_use_id"' in raw:
                        continue
                    try:
                        record = json.loads(raw)
                    except ValueError:
                        continue
                    if record.get("type") != "user" or record.get("isMeta") or record.get("isSidechain") or record.get("isCompactSummary"):
                        continue
                    content = record.get("message", {}).get("content")
                    text = "\n".join(x.get("text", "") for x in content if x.get("type") == "text") if isinstance(content, list) else (content or "")
                    text = text.replace("\r\n", "\n").strip()
                    source = record.get("promptSource")
                    origin = (record.get("origin") or {}).get("kind")
                    found = text in history_texts.get(record.get("sessionId"), ()) or text in every
                    if source in ("typed", "queued"):
                        stats[f"typed/queued: {'in history' if found else 'NOT in history'}"] += 1
                    elif not found:
                        head = "interrupt notice" if text.startswith("[Request interrupted") else (
                            "<" + re.match(r"<([A-Za-z_-]*)", text).group(1) + ">" if text.startswith("<") else "other")
                        stats[f"not in history, no promptSource: {head} (origin {origin})"] += 1
    for key, value in sorted(stats.items(), key=lambda kv: -kv[1])[:12]:
        print(f"   {key}: {value:,}")


def codex(codex_dir):
    print(f"== Codex ({codex_dir})")
    files = [p for folder in ("sessions", "archived_sessions") for p in glob.glob(os.path.join(codex_dir, folder, "**", "rollout-*"), recursive=True)]
    compressed = sum(p.endswith(".zst") for p in files)
    print(f"   session files: {len(files):,} ({compressed} compressed .jsonl.zst)")
    versions = collections.Counter()
    whose = collections.Counter()
    kinds = collections.Counter()
    by_id = collections.defaultdict(set)
    records = collections.defaultdict(list)
    for path in files:
        if path.endswith(".zst"):
            continue
        with open(path, "rb") as f:
            first = f.readline()
            try:
                meta = json.loads(first).get("payload", {})
            except ValueError:
                meta = {}
            source = meta.get("source")
            source = "subagent" if isinstance(source, dict) and "subagent" in source else json.dumps(source) if isinstance(source, dict) else source
            versions[meta.get("cli_version")] += 1
            whose[(source, meta.get("thread_source"))] += 1
            thread = meta.get("id")
            for raw in f:
                if b'"UserMessage"' not in raw and b'"user_message"' not in raw:
                    continue
                try:
                    line = json.loads(raw)
                except ValueError:
                    continue
                payload = line.get("payload") or {}
                item = payload.get("item") or {}
                if payload.get("type") == "item_completed" and item.get("type") == "UserMessage":
                    kinds["item_completed UserMessage"] += 1
                    text = "".join(c.get("text", "") for c in item.get("content", []) if c.get("type") == "text")
                    by_id[item.get("id")].add((path, hashlib.sha256(text.encode()).hexdigest()))
                    records[(thread, hashlib.sha256(text.strip().encode()).hexdigest())].append(line.get("timestamp"))
                elif payload.get("type") == "user_message":
                    kinds["user_message event"] += 1
    print(f"   CLI versions: {dict(versions.most_common(8))}")
    print(f"   (source, thread_source): {dict(whose.most_common())}")
    print(f"   prompt records: {dict(kinds)}")
    repeated = [v for v in by_id.values() if len({p for p, _ in v}) > 1]
    print(f"   item ids in several files: {len(repeated)} ({sum(len({h for _, h in v}) == 1 for v in repeated)} same text = fork copies, "
          f"{sum(len({h for _, h in v}) > 1 for v in repeated)} other text = counter collisions)")

    history = os.path.join(codex_dir, "history.jsonl")
    if os.path.exists(history):
        deltas = []
        lines = twins = 0
        with open(history, "rb") as f:
            for raw in f:
                lines += 1
                try:
                    entry = json.loads(raw)
                except ValueError:
                    continue
                times = records.get((entry.get("session_id"), hashlib.sha256(entry.get("text", "").strip().encode()).hexdigest()))
                if times:
                    twins += 1
                    seconds = [datetime.datetime.fromisoformat(t.replace("Z", "+00:00")).timestamp() for t in times if t]
                    deltas.append(min(abs(s - entry.get("ts", 0)) for s in seconds))
        median = f"{statistics.median(deltas):.1f} s" if deltas else "-"
        p95 = f"{sorted(deltas)[int(len(deltas) * 0.95)]:.1f} s" if deltas else "-"
        print(f"   history.jsonl: {lines:,} lines, {twins:,} with a session-file twin (median {median} apart, 95% within {p95})")

    for db in glob.glob(os.path.join(codex_dir, "thread_history_*.sqlite")):
        try:
            connection = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
            tables = [r[0] for r in connection.execute("select name from sqlite_master where type='table'")]
            columns = [r[1] for r in connection.execute("pragma table_info('thread_history_projection_state')")] if "thread_history_projection_state" in tables else []
            connection.close()
            print(f"   {os.path.basename(db)}: tables {tables}; projection state columns {columns}")
        except sqlite3.Error as error:
            print(f"   {os.path.basename(db)}: unreadable ({error.__class__.__name__})")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--claude", default=default_dir("CLAUDE_CONFIG_DIR", ".claude"))
    parser.add_argument("--codex", default=default_dir("CODEX_HOME", ".codex"))
    parser.add_argument("--skip-transcripts", action="store_true")
    args = parser.parse_args()
    texts = claude_history(args.claude)
    if not args.skip_transcripts:
        claude_transcripts(args.claude, texts)
    codex(args.codex)


if __name__ == "__main__":
    main()
