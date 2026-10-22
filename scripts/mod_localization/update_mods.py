# SPDX-License-Identifier: GPL-3.0-only
"""Refresh the embedded mod-name catalog from MC Encyclopedia.

This command never runs from the launcher. Running it normally makes live requests;
use --check-only to validate or --reconcile-only to apply saved candidates offline.
"""

from __future__ import annotations

import argparse
import base64
import binascii
import json
import os
import random
import re
import tempfile
import time
from datetime import datetime, timedelta, timezone
from html.parser import HTMLParser
from pathlib import Path
from urllib import error, parse, request, robotparser

ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / "Launcher.Infrastructure/Resources/ResourceLocalization/Data/mods.v1.jsonl"
STATE = Path(__file__).with_name("crawl-state.json")
BASE_URL = "https://www.mcmod.cn"
CLASS_PATH = re.compile(r"^/class/(\d+)\.html$")
SLUG = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._+-]*$")
CURSEFORGE_HOSTS = frozenset(("curseforge.com", "www.curseforge.com", "legacy.curseforge.com"))
MODRINTH_HOSTS = frozenset(("modrinth.com", "www.modrinth.com"))
USER_AGENT = "BlockHelm-ModLocalization/1.0 (+https://github.com/zqq-699/BlockHelm-Launcher)"
MAX_HTML_BYTES = 4_000_000
CATALOG_FIELDS = ("mcmodId", "chineseName", "englishName", "abbreviation",
                  "curseForgeSlugs", "modrinthSlugs", "modIds")


class ParseFailure(Exception):
    pass


class TransientFailure(Exception):
    pass


class StopCrawl(Exception):
    pass


class BudgetExhausted(Exception):
    pass


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def iso_date(value: datetime) -> str:
    return value.date().isoformat()


def due_on_or_before(value: str | None, today: str) -> bool:
    return not value or value <= today


def atomic_write(path: Path, contents: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", newline="\n", dir=path.parent,
                                         prefix=path.name + ".", suffix=".tmp", delete=False) as file:
            temporary = Path(file.name)
            file.write(contents)
            file.flush()
            os.fsync(file.fileno())
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def load_catalog(path: Path) -> dict[int, dict]:
    if not path.is_file():
        raise ValueError(f"Catalog is missing: {path}")
    catalog: dict[int, dict] = {}
    with path.open(encoding="utf-8") as file:
        for number, line in enumerate(file, 1):
            if not line.strip():
                continue
            record = json.loads(line)
            if not isinstance(record, dict):
                raise ValueError(f"Invalid catalog record at line {number}")
            mod_id = record.get("mcmodId")
            if type(mod_id) is not int or mod_id <= 0 or mod_id in catalog:
                raise ValueError(f"Invalid or duplicate mcmodId at line {number}")
            if (not isinstance(record.get("chineseName"), str) or not record["chineseName"].strip()
                    or any(record.get(field) is not None and not isinstance(record[field], str)
                           for field in ("englishName", "abbreviation"))
                    or any(not isinstance(record.get(field), list)
                           or any(not isinstance(value, str) for value in record[field])
                           for field in ("curseForgeSlugs", "modrinthSlugs", "modIds"))):
                raise ValueError(f"Invalid catalog fields at line {number}")
            catalog[mod_id] = record
    return catalog


def load_state(path: Path, catalog: dict[int, dict]) -> dict:
    if path.exists():
        state = json.loads(path.read_text(encoding="utf-8"))
        if state.get("version") != 1 or type(state.get("nextNewId")) is not int:
            raise ValueError("Unsupported or malformed crawl state")
        return state
    return {
        "version": 1,
        "nextNewId": max(catalog, default=0) + 1,
        "records": {},
        "pendingUpdates": {},
        "quarantine": {},
        "recentEditedIds": [],
    }


def save_state(path: Path, state: dict) -> None:
    atomic_write(path, json.dumps(state, ensure_ascii=False, indent=2, sort_keys=True) + "\n")


class McmodHtmlParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.stack: list[tuple[str, set[str]]] = []
        self.captures: list[tuple[str, int, list[str]]] = []
        self.names: dict[str, str] = {}
        self.links: list[tuple[str, str]] = []
        self.link_frame_seen = False
        self.list_ids: list[int] = []

    def has_class(self, name: str) -> bool:
        return any(name in classes for _, classes in self.stack)

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attributes = dict(attrs)
        classes = set((attributes.get("class") or "").split())
        in_title = self.has_class("class-title")
        in_links = self.has_class("common-link-frame")
        in_list = self.has_class("modlist-block")
        if "common-link-frame" in classes:
            self.link_frame_seen = True
        self.stack.append((tag, classes))
        if in_title and (tag in ("h3", "h4") or "short-name" in classes):
            field = "abbr" if "short-name" in classes else tag
            self.captures.append((field, len(self.stack), []))
        if tag == "a":
            href = attributes.get("href") or ""
            if in_links:
                hint = (attributes.get("data-original-title") or attributes.get("title") or "").strip().lower()
                self.links.append((href, hint))
            if in_list:
                try:
                    match = CLASS_PATH.fullmatch(parse.urlsplit(href).path)
                except ValueError:
                    match = None
                if match:
                    self.list_ids.append(int(match.group(1)))

    def handle_data(self, data: str) -> None:
        if self.captures:
            # An abbreviation nested in a title is not part of the title.
            inside_abbreviation = self.has_class("short-name")
            for field, _, parts in self.captures:
                if field == "abbr" or not inside_abbreviation:
                    parts.append(data)

    def handle_endtag(self, tag: str) -> None:
        while self.stack:
            depth = len(self.stack)
            current, _ = self.stack.pop()
            for capture in self.captures[:]:
                field, started, parts = capture
                if started == depth:
                    value = " ".join("".join(parts).split())
                    if field == "abbr" and value.startswith("[") and value.endswith("]"):
                        value = value[1:-1].strip()
                    self.names[field] = value
                    self.captures.remove(capture)
            if current == tag:
                break


def decode_target(href: str) -> str:
    url = parse.urlsplit(href)
    if not url.path.startswith("/target/") or url.hostname not in (None, "mcmod.cn", "www.mcmod.cn", "link.mcmod.cn"):
        return href
    # MC Encyclopedia appends separately encoded query parameters after '@'.
    # The destination URL alone is enough to identify a mod platform and slug.
    encoded = url.path[len("/target/"):].split("@", 1)[0]
    try:
        decoded = base64.b64decode(encoded + "=" * (-len(encoded) % 4), altchars=b"-_", validate=True)
        return decoded.decode("utf-8")
    except (binascii.Error, ValueError, UnicodeDecodeError) as exc:
        raise ParseFailure("Malformed target link") from exc


def platform_slugs(links: list[tuple[str, str]]) -> tuple[list[str], list[str]]:
    curseforge: set[str] = set()
    modrinth: set[str] = set()
    for href, hint in links:
        try:
            url = parse.urlsplit(decode_target(href))
            host = (url.hostname or "").lower()
        except ParseFailure:
            if hint and hint not in ("curseforge", "modrinth"):
                continue  # A malformed, clearly unrelated link cannot block mod links.
            raise  # An unknown or platform link must not silently clear old slugs.
        except ValueError as exc:
            if hint and hint not in ("curseforge", "modrinth"):
                continue
            raise ParseFailure("Malformed related link") from exc
        segments = [parse.unquote(part) for part in url.path.split("/") if part]
        target = None
        slug = ""
        if host in CURSEFORGE_HOSTS:
            platform = "curseforge"
            if segments[:2] == ["minecraft", "data-packs"]:
                continue  # A valid CurseForge link, but not a mod project.
        elif host in MODRINTH_HOSTS:
            platform = "modrinth"
            if segments[:1] == ["datapack"]:
                continue  # Modrinth data packs cannot be matched as mods.
        else:
            if host and url.scheme in ("http", "https"):
                continue  # Trust the destination, not a mismatched link label.
            platform = hint
        if (url.scheme in ("http", "https") and host in CURSEFORGE_HOSTS
                and segments[:2] == ["minecraft", "mc-mods"]):
            target = curseforge
            slug = segments[2] if len(segments) > 2 else ""
        elif url.scheme in ("http", "https") and host in MODRINTH_HOSTS and segments[:1] == ["mod"]:
            target = modrinth
            slug = segments[1] if len(segments) > 1 else ""
        if target is not None and SLUG.fullmatch(slug):
            target.add(slug.lower())
        elif platform in ("curseforge", "modrinth"):
            raise ParseFailure(f"Unrecognized {platform} project link")
    return sorted(curseforge), sorted(modrinth)


def parse_mod(html: str, mod_id: int, expect_link_frame: bool = False) -> dict:
    parser = McmodHtmlParser()
    parser.feed(html)
    chinese = parser.names.get("h3", "").strip()
    english = parser.names.get("h4", "").strip()
    if not chinese and not english:
        raise ParseFailure(f"Mod {mod_id}: missing class title (possibly a challenge page)")
    if len(chinese) > 200 or len(english) > 200:
        raise ParseFailure(f"Mod {mod_id}: implausibly long title")
    if expect_link_frame and not parser.link_frame_seen:
        raise ParseFailure(f"Mod {mod_id}: related-link section disappeared")
    curseforge, modrinth = platform_slugs(parser.links)
    return {
        "mcmodId": mod_id,
        "chineseName": chinese or english,
        "englishName": english or None,
        "abbreviation": parser.names.get("abbr") or None,
        "curseForgeSlugs": curseforge,
        "modrinthSlugs": modrinth,
        "modIds": [],
    }


def parse_mod_list(html: str) -> list[int]:
    parser = McmodHtmlParser()
    parser.feed(html)
    if not parser.list_ids:
        raise ParseFailure("Mod list contained no class links; layout or access may have changed")
    return list(dict.fromkeys(parser.list_ids))


class BudgetClient:
    def __init__(self, max_requests: int, min_delay: float = 5, max_delay: float = 10) -> None:
        if max_requests < 5 or min_delay < 0 or max_delay < min_delay:
            raise ValueError("Invalid request budget or delay")
        self.max_requests = max_requests
        self.used = 0
        self.min_delay = min_delay
        self.max_delay = max_delay
        self.last_request_at: float | None = None
        self.robots: robotparser.RobotFileParser | None = None

    def _get_once(self, url: str) -> str:
        if self.used >= self.max_requests:
            raise BudgetExhausted("HTTP request budget exhausted")
        if self.last_request_at is not None:
            remaining = random.uniform(self.min_delay, self.max_delay) - (time.monotonic() - self.last_request_at)
            if remaining > 0:
                time.sleep(remaining)
        self.used += 1
        self.last_request_at = time.monotonic()
        http_request = request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": "text/html"})
        try:
            with request.urlopen(http_request, timeout=15) as response:
                if parse.urlsplit(response.geturl()).hostname != "www.mcmod.cn":
                    raise StopCrawl("MC Encyclopedia redirected to another host")
                payload = response.read(MAX_HTML_BYTES + 1)
                if len(payload) > MAX_HTML_BYTES:
                    raise TransientFailure("HTML response exceeded the size limit")
                return payload.decode("utf-8", errors="replace")
        except error.HTTPError as exc:
            if exc.code in (403, 429, 503):
                retry_after = exc.headers.get("Retry-After", "")
                exc.close()
                raise StopCrawl(f"HTTP {exc.code}; stop and respect Retry-After={retry_after!r}") from exc
            if exc.code == 404:
                raise
            exc.close()
            raise TransientFailure(f"HTTP {exc.code}") from exc
        except error.URLError as exc:
            raise TransientFailure(f"Network request failed: {exc.reason}") from exc
        except TimeoutError as exc:
            raise TransientFailure(f"Network request timed out: {exc}") from exc

    def load_robots(self) -> None:
        text = self._get_once(BASE_URL + "/robots.txt")
        if "<html" in text[:512].lower() or not re.search(r"(?im)^\s*user-agent\s*:", text):
            raise StopCrawl("robots.txt was not a valid robots policy")
        robots = robotparser.RobotFileParser()
        robots.parse(text.splitlines())
        self.robots = robots

    def get(self, path: str) -> str:
        if self.robots is None:
            raise StopCrawl("robots.txt must be checked before crawling")
        url = BASE_URL + path
        if not self.robots.can_fetch(USER_AGENT, url):
            raise StopCrawl(f"robots.txt disallows {path}")
        try:
            return self._get_once(url)
        except TransientFailure:
            # Exactly one retry; both attempts count against the global budget.
            return self._get_once(url)


def retry_date(today: datetime, failures: int, missing: bool = False) -> str:
    days = 90 if missing else min(30, 7 * (2 ** min(failures - 1, 2)))
    return iso_date(today + timedelta(days=days))


def record_result(state: dict, mod_id: int, status: str, now: datetime,
                  candidate: dict | None = None) -> None:
    key = str(mod_id)
    previous = state["records"].get(key, {})
    failures = 0 if status == "ok" else previous.get("failures", 0) + 1
    state["records"][key] = {
        "status": status,
        "lastChecked": iso_date(now),
        "lastSuccess": iso_date(now) if status == "ok" else previous.get("lastSuccess"),
        "failures": failures,
        "nextRetry": None if status == "ok" else retry_date(now, failures, status == "missing"),
    }
    if candidate is not None:
        state["pendingUpdates"][key] = candidate


def fetch_mod(client: BudgetClient, state: dict, state_path: Path, mod_id: int,
              now: datetime, is_new: bool, expect_link_frame: bool = False) -> str:
    try:
        html = client.get(f"/class/{mod_id}.html")
        candidate = parse_mod(html, mod_id, expect_link_frame)
        status = "ok"
    except error.HTTPError as exc:
        try:
            if exc.code != 404:
                raise
            candidate = None
            status = "missing"
        finally:
            exc.close()
    except (TransientFailure, ParseFailure) as exc:
        print(f"Mod {mod_id}: {exc}")
        candidate = None
        status = "failed"
    record_result(state, mod_id, status, now, candidate)
    if is_new:
        state["nextNewId"] = mod_id + 1
    save_state(state_path, state)
    return status


def merge_updates(catalog: dict[int, dict], state: dict) -> tuple[int, list[str]]:
    changed = 0
    anomalies: list[str] = []
    candidates = {key: entry["candidate"] for key, entry in state["quarantine"].items()}
    candidates.update(state["pendingUpdates"])
    for key, candidate in candidates.items():
        mod_id = int(key)
        existing = catalog.get(mod_id)
        merged = dict(existing) if existing else dict(candidate)
        for field in ("chineseName", "englishName", "abbreviation"):
            merged[field] = candidate[field]
        for field in ("curseForgeSlugs", "modrinthSlugs"):
            # MC Encyclopedia is authoritative for platform links. A successful
            # refresh replaces obsolete slugs, including when a list becomes empty.
            merged[field] = candidate[field]
        # MC Encyclopedia does not supply modIds; retain imported IDs unchanged.
        merged["modIds"] = existing.get("modIds", []) if existing else []
        if merged != existing:
            catalog[mod_id] = merged
            changed += 1
        state["pendingUpdates"].pop(key, None)
        state["quarantine"].pop(key, None)
    return changed, anomalies


def write_catalog(path: Path, catalog: dict[int, dict]) -> None:
    lines = []
    for mod_id in sorted(catalog):
        record = catalog[mod_id]
        ordered = {field: record[field] for field in CATALOG_FIELDS if field in record}
        ordered.update((field, value) for field, value in record.items() if field not in ordered)
        lines.append(json.dumps(ordered, ensure_ascii=False, separators=(",", ":")))
    atomic_write(path, "\n".join(lines) + "\n")


def run_live(catalog_path: Path, state_path: Path, max_requests: int,
             edit_pages: int, min_delay: float, max_delay: float,
             existing_only: bool = False, retry_now: bool = False) -> None:
    catalog = load_catalog(catalog_path)
    state = load_state(state_path, catalog)
    client = BudgetClient(max_requests, min_delay, max_delay)
    now = utc_now()
    today = iso_date(now)
    client.load_robots()
    if existing_only:
        newest = state.get("lastRun", {}).get("newestSeenId", state["nextNewId"] - 1)
    else:
        latest_ids = parse_mod_list(client.get("/modlist.html?sort=createtime"))
        newest = max(latest_ids)
        if newest < state["nextNewId"] - 1:
            raise ParseFailure("Newest list ID is behind the saved checkpoint")

    edited: list[int] = []
    for page in range(1, edit_pages + 1):
        try:
            suffix = "" if page == 1 else f"&page={page}"
            edited.extend(parse_mod_list(client.get("/modlist.html?sort=lastedittime" + suffix)))
        except (BudgetExhausted, StopCrawl):
            raise
        except (TransientFailure, ParseFailure) as exc:
            print(f"Edited-list page {page} unavailable: {exc}")
            break

    edited = list(dict.fromkeys(edited))
    state["recentEditedIds"] = edited
    save_state(state_path, state)
    attempted = {"new": 0, "edited": 0, "retry": 0, "gap": 0}
    outcomes = {"ok": 0, "missing": 0, "failed": 0}
    attempted_ids: set[int] = set()

    def phase(name: str, candidates, allowance: int, is_new: bool = False) -> None:
        starting_requests = client.used
        for mod_id in candidates:
            if client.used - starting_requests >= allowance or client.used >= max_requests:
                break
            if mod_id in attempted_ids:
                continue
            existing = catalog.get(mod_id, {})
            expect_link_frame = bool(existing.get("curseForgeSlugs") or existing.get("modrinthSlugs"))
            status = fetch_mod(client, state, state_path, mod_id, now, is_new, expect_link_frame)
            attempted_ids.add(mod_id)
            attempted[name] += 1
            outcomes[status] += 1

    try:
        if not existing_only:
            phase("new", range(state["nextNewId"], newest + 1), max(1, max_requests * 55 // 100), True)
        edited_candidates = []
        for position, mod_id in enumerate(edited):
            if mod_id not in catalog or str(mod_id) in state["pendingUpdates"]:
                continue
            metadata = state["records"].get(str(mod_id), {})
            checked = metadata.get("lastChecked") or ""
            missing_link = not catalog[mod_id].get("curseForgeSlugs") or not catalog[mod_id].get("modrinthSlugs")
            edited_candidates.append((checked, not missing_link, position, mod_id))
        # Revisit every ID in the edited list, oldest check first. A repeated
        # edit is no longer hidden for 30 days by last week's list snapshot.
        edited_candidates.sort()
        phase("edited", (mod_id for _, _, _, mod_id in edited_candidates),
              max(1, max_requests * (10 if retry_now else 70 if existing_only else 25) // 100))

        retries = [int(key) for key, metadata in state["records"].items()
                   if metadata.get("status") in ("failed", "missing")
                   and (retry_now or due_on_or_before(metadata.get("nextRetry"), today))]
        phase("retry", sorted(retries), max(1, max_requests * (70 if retry_now else 25 if existing_only else 10) // 100))

        if existing_only:
            phase("edited", (mod_id for _, _, _, mod_id in edited_candidates), max_requests)
        else:
            gaps = (mod_id for mod_id in range(min(catalog, default=1), max(catalog, default=0))
                    if mod_id not in catalog and str(mod_id) not in state["records"])
            phase("gap", gaps, max(1, max_requests * 5 // 100))
            # Spend unused phase allocations on the new-ID backlog, but keep a
            # small reserve for retries already consumed within each phase.
            if state["nextNewId"] <= newest and client.used < max_requests:
                phase("new", range(state["nextNewId"], newest + 1), max_requests, True)
    except BudgetExhausted:
        print("HTTP request budget reached; progress is saved.")
    finally:
        changed, anomalies = merge_updates(catalog, state)
        if changed:
            write_catalog(catalog_path, catalog)
        state["lastRun"] = {
            "date": today,
            "existingOnly": existing_only,
            "retryNow": retry_now,
            "requests": client.used,
            "requestLimit": max_requests,
            "newestSeenId": newest,
            "nextNewId": state["nextNewId"],
            "newIdBacklog": max(0, newest - state["nextNewId"] + 1),
            "attempted": attempted,
            "outcomes": outcomes,
            "catalogRowsChanged": changed,
            "newQuarantines": len(anomalies),
        }
        save_state(state_path, state)
        print(f"Requests={client.used}/{max_requests} LatestId={newest} NextNewId={state['nextNewId']} "
              f"Attempted={attempted} Changed={changed} Quarantined={len(anomalies)}")
        for anomaly in anomalies:
            print("Review required:", anomaly)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    offline_modes = parser.add_mutually_exclusive_group()
    offline_modes.add_argument("--check-only", action="store_true",
                               help="Validate the catalog without network access or file changes")
    offline_modes.add_argument("--reconcile-only", action="store_true",
                               help="Apply saved candidates using the current merge rules without network access")
    parser.add_argument("--max-requests", type=int, default=200)
    parser.add_argument("--edit-pages", type=int, default=3)
    parser.add_argument("--existing-only", action="store_true",
                        help="Refresh previously known mods and due retries without scanning new IDs or gaps")
    parser.add_argument("--retry-now", action="store_true",
                        help="With --existing-only, retry previously failed IDs even before their scheduled date")
    parser.add_argument("--min-delay", type=float, default=5)
    parser.add_argument("--max-delay", type=float, default=10)
    parser.add_argument("--catalog", type=Path, default=CATALOG)
    parser.add_argument("--state", type=Path, default=STATE)
    args = parser.parse_args()
    catalog = load_catalog(args.catalog)
    print(f"Catalog valid: {len(catalog)} unique MC Encyclopedia IDs; max={max(catalog, default=0)}")
    if args.check_only:
        print("Validation only. No network request or file change was made.")
        return
    if args.reconcile_only:
        state = load_state(args.state, catalog)
        changed, anomalies = merge_updates(catalog, state)
        if changed:
            write_catalog(args.catalog, catalog)
        state["lastReconciliation"] = {
            "date": iso_date(utc_now()),
            "catalogRowsChanged": changed,
            "newQuarantines": len(anomalies),
            "remainingQuarantines": len(state["quarantine"]),
        }
        save_state(args.state, state)
        print(f"Reconciled saved candidates: Changed={changed} Quarantined={len(state['quarantine'])}")
        for anomaly in anomalies:
            print("Review required:", anomaly)
        return
    if args.edit_pages < 1:
        parser.error("--edit-pages must be at least 1")
    if args.retry_now and not args.existing_only:
        parser.error("--retry-now requires --existing-only")
    run_live(args.catalog, args.state, args.max_requests, args.edit_pages,
             args.min_delay, args.max_delay, args.existing_only, args.retry_now)


if __name__ == "__main__":
    main()
