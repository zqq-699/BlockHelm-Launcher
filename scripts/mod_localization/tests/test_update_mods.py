# SPDX-License-Identifier: GPL-3.0-only
"""Offline regression tests for the MC Encyclopedia catalog updater."""

import base64
import json
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch
from urllib import error

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import update_mods as scraper  # noqa: E402


def make_record(mod_id, name="示例", curseforge=None, modrinth=None, mod_ids=None):
    return {
        "mcmodId": mod_id,
        "chineseName": name,
        "englishName": "Example",
        "abbreviation": None,
        "curseForgeSlugs": curseforge or [],
        "modrinthSlugs": modrinth or [],
        "modIds": mod_ids or [],
    }


class ParserTests(unittest.TestCase):
    def test_mod_page_extracts_names_and_exact_platform_hosts(self):
        redirect = base64.urlsafe_b64encode(b"https://modrinth.com/mod/example-mod?ref=mcmod").decode().rstrip("=")
        html = f"""
        <div class="class-title"><h3>示例<span class="short-name">[EX]</span>模组</h3>
        <h4>Example Mod</h4></div>
        <div class="common-link-frame">
          <a href="/target/{redirect}">Modrinth</a>
          <a href="https://www.curseforge.com/minecraft/mc-mods/example-cf">CurseForge</a>
          <a href="https://evilcurseforge.com/minecraft/mc-mods/fake">Fake</a>
        </div>"""
        record = scraper.parse_mod(html, 123)
        self.assertEqual("示例模组", record["chineseName"])
        self.assertEqual("Example Mod", record["englishName"])
        self.assertEqual("EX", record["abbreviation"])
        self.assertEqual(["example-cf"], record["curseForgeSlugs"])
        self.assertEqual(["example-mod"], record["modrinthSlugs"])

    def test_challenge_page_is_not_a_mod(self):
        with self.assertRaises(scraper.ParseFailure):
            scraper.parse_mod("<h1>Verify you are human</h1>", 123)

    def test_missing_link_section_is_not_treated_as_removed_links_for_existing_mod(self):
        html = "<div class='class-title'><h3>更名后的模组</h3></div>"
        with self.assertRaisesRegex(scraper.ParseFailure, "related-link section disappeared"):
            scraper.parse_mod(html, 123, expect_link_frame=True)
        self.assertEqual([], scraper.parse_mod(html, 123)["curseForgeSlugs"])

    def test_unrecognized_labeled_platform_link_fails_instead_of_erasing_slug(self):
        html = """<div class='class-title'><h3>示例</h3></div>
                  <div class='common-link-frame'>
                    <a data-original-title='Modrinth' href='https://modrinth.com/project/unknown'>Link</a>
                  </div>"""
        with self.assertRaisesRegex(scraper.ParseFailure, "Unrecognized modrinth project link"):
            scraper.parse_mod(html, 123, expect_link_frame=True)

    def test_changed_platform_path_fails_even_without_link_label(self):
        html = """<div class='class-title'><h3>示例</h3></div>
                  <div class='common-link-frame'>
                    <a href='https://modrinth.com/project/example'>Link</a>
                  </div>"""
        with self.assertRaisesRegex(scraper.ParseFailure, "Unrecognized modrinth project link"):
            scraper.parse_mod(html, 123, expect_link_frame=True)

    def test_modrinth_datapack_link_is_not_treated_as_a_mod_slug(self):
        html = """<div class='class-title'><h3>示例</h3></div>
                  <div class='common-link-frame'>
                    <a data-original-title='Modrinth' href='https://modrinth.com/datapack/lodestone-tp'>Link</a>
                  </div>"""
        record = scraper.parse_mod(html, 31678, expect_link_frame=True)
        self.assertEqual([], record["modrinthSlugs"])

    def test_modrinth_datapack_does_not_discard_curseforge_mod(self):
        html = """<div class='class-title'><h3>示例</h3></div>
                  <div class='common-link-frame'>
                    <a href='https://www.curseforge.com/minecraft/mc-mods/epic-structures-villages-standalone-edition'>Mod</a>
                    <a data-original-title='Modrinth' href='https://modrinth.com/datapack/epic-structures-villages-standalone-edition'>Data pack</a>
                  </div>"""
        record = scraper.parse_mod(html, 31706, expect_link_frame=True)
        self.assertEqual(["epic-structures-villages-standalone-edition"], record["curseForgeSlugs"])
        self.assertEqual([], record["modrinthSlugs"])

    def test_extra_curseforge_datapack_does_not_discard_valid_mod_links(self):
        html = """<div class='class-title'><h3>示例</h3></div>
                  <div class='common-link-frame'>
                    <a data-original-title='CurseForge' href='https://www.curseforge.com/minecraft/mc-mods/integrated-cobblemon'>Mod</a>
                    <a data-original-title='CurseForge' href='https://www.curseforge.com/minecraft/data-packs/integrated-cobblemon-config'>Data pack</a>
                    <a data-original-title='Modrinth' href='https://modrinth.com/mod/integrated-cobblemon'>Modrinth</a>
                  </div>"""
        record = scraper.parse_mod(html, 31711, expect_link_frame=True)
        self.assertEqual(["integrated-cobblemon"], record["curseForgeSlugs"])
        self.assertEqual(["integrated-cobblemon"], record["modrinthSlugs"])

    def test_mislabeled_platform_link_to_another_site_is_ignored(self):
        html = """<div class='class-title'><h3>示例</h3></div>
                  <div class='common-link-frame'>
                    <a data-original-title='CurseForge' href='https://github.com/Mr-HorseDuck/create-hand-made'>Link</a>
                  </div>"""
        record = scraper.parse_mod(html, 31728, expect_link_frame=True)
        self.assertEqual([], record["curseForgeSlugs"])

    def test_malformed_related_link_fails_without_aborting_crawl(self):
        html = """<div class='class-title'><h3>示例</h3></div>
                  <div class='common-link-frame'><a href='https://[broken'>Link</a></div>"""
        with self.assertRaisesRegex(scraper.ParseFailure, "Malformed related link"):
            scraper.parse_mod(html, 123, expect_link_frame=True)

    def test_malformed_target_link_cannot_clear_existing_platform_slugs(self):
        html = """<div class='class-title'><h3>示例</h3></div>
                  <div class='common-link-frame'><a href='/target/%%%'>Link</a></div>"""
        with self.assertRaisesRegex(scraper.ParseFailure, "Malformed target link"):
            scraper.parse_mod(html, 123, expect_link_frame=True)

    def test_other_sites_target_paths_are_not_decoded_as_mcmod_redirects(self):
        self.assertEqual("https://example.com/target/link", scraper.decode_target("https://example.com/target/link"))

    def test_mcmod_link_host_redirects_resolve_platform_slugs(self):
        curseforge = base64.urlsafe_b64encode(
            b"https://www.curseforge.com/minecraft/mc-mods/blood-magic-altar-automation-optimization"
        ).decode()
        modrinth = base64.urlsafe_b64encode(b"https://modrinth.com/mod/laychens-lie").decode()
        html = f"""<div class='class-title'><h3>示例</h3></div>
                   <div class='common-link-frame'>
                     <a href='//link.mcmod.cn/target/{curseforge}' title='CurseForge'>CF</a>
                     <a href='https://link.mcmod.cn/target/{modrinth}' title='Modrinth'>MR</a>
                   </div>"""
        record = scraper.parse_mod(html, 31775, expect_link_frame=True)
        self.assertEqual(["blood-magic-altar-automation-optimization"], record["curseForgeSlugs"])
        self.assertEqual(["laychens-lie"], record["modrinthSlugs"])

    def test_legacy_curseforge_http_and_modrinth_plus_slugs_are_preserved(self):
        links = [
            "http://www.curseforge.com/minecraft/mc-mods/tinkers-construct",
            "https://legacy.curseforge.com/minecraft/mc-mods/extraordinary-extra-totems",
            "https://modrinth.com/mod/paintings++",
            "https://modrinth.com/mod/x+-autofish",
        ]
        html = "<div class='class-title'><h3>示例</h3></div><div class='common-link-frame'>" + "".join(
            f"<a href='/target/{base64.urlsafe_b64encode(link.encode()).decode()}'>Link</a>"
            for link in links
        ) + "</div>"
        record = scraper.parse_mod(html, 123)
        self.assertEqual(["extraordinary-extra-totems", "tinkers-construct"], record["curseForgeSlugs"])
        self.assertEqual(["paintings++", "x+-autofish"], record["modrinthSlugs"])

    def test_title_preserves_trailing_brackets_while_abbreviation_is_unwrapped(self):
        html = """<div class="class-title"><h3>The Shooting Star [Demo]</h3>
                  <h4>Example [Fabric]</h4><span class="short-name">[TSS]</span></div>"""
        record = scraper.parse_mod(html, 31570)
        self.assertEqual("The Shooting Star [Demo]", record["chineseName"])
        self.assertEqual("Example [Fabric]", record["englishName"])
        self.assertEqual("TSS", record["abbreviation"])

    def test_mod_list_requires_modlist_block_and_preserves_order(self):
        html = """<a href="/class/999.html">outside</a><div class="modlist-block">
                  <a href="/class/42.html">first</a><a href="/class/41.html">second</a>
                  <a href="/class/42.html">duplicate</a></div>"""
        self.assertEqual([42, 41], scraper.parse_mod_list(html))
        with self.assertRaises(scraper.ParseFailure):
            scraper.parse_mod_list("<div class='modlist-block'>No results</div>")

    def test_malformed_list_link_does_not_hide_valid_mod_ids(self):
        html = """<div class='modlist-block'><a href='https://[broken'>Bad</a>
                  <a href='/class/42.html'>Valid</a></div>"""
        self.assertEqual([42], scraper.parse_mod_list(html))


class StateAndMergeTests(unittest.TestCase):
    def test_invalid_catalog_fields_are_rejected_before_crawling(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "mods.jsonl"
            for invalid in ({**make_record(2), "chineseName": " "},
                            {**make_record(2), "modrinthSlugs": "not-a-list"}):
                path.write_text(json.dumps(invalid, ensure_ascii=False) + "\n", encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "Invalid catalog fields"):
                    scraper.load_catalog(path)

    def test_bootstrap_uses_max_id_but_preserves_gaps_for_backfill(self):
        catalog = {2: make_record(2), 4: make_record(4)}
        state = scraper.load_state(Path("missing-state-file.json"), catalog)
        self.assertEqual(5, state["nextNewId"])
        self.assertEqual({}, state["records"])

    def test_failure_advances_new_checkpoint_and_has_retry_date(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.json"
            state = scraper.load_state(path, {2: make_record(2)})
            now = datetime(2026, 10, 22, tzinfo=timezone.utc)

            class MissingClient:
                def get(self, path):
                    raise error.HTTPError(path, 404, "Not Found", {}, None)

            status = scraper.fetch_mod(MissingClient(), state, path, 3, now, True)
            persisted = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual("missing", status)
            self.assertEqual(4, persisted["nextNewId"])
            self.assertEqual("2027-01-20", persisted["records"]["3"]["nextRetry"])
            self.assertNotIn("3", persisted["pendingUpdates"])

    def test_missing_link_section_preserves_existing_mapping_and_schedules_retry(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.json"
            state = scraper.load_state(path, {2: make_record(2, curseforge=["old-cf"])})

            class TitleOnlyClient:
                def get(self, path):
                    return "<div class='class-title'><h3>新名字</h3></div>"

            status = scraper.fetch_mod(TitleOnlyClient(), state, path, 2,
                                       datetime(2026, 10, 22, tzinfo=timezone.utc), False, True)
            self.assertEqual("failed", status)
            self.assertEqual("2026-10-29", state["records"]["2"]["nextRetry"])
            self.assertNotIn("2", state["pendingUpdates"])

    def test_merge_keeps_imported_mod_ids_and_replaces_obsolete_slugs(self):
        catalog = {2: make_record(2, curseforge=["old-cf"], mod_ids=["local-id"])}
        state = scraper.load_state(Path("missing-state-file.json"), catalog)
        unchanged_slug = make_record(2, "新名字", curseforge=["old-cf"], modrinth=["new-md"])
        state["pendingUpdates"]["2"] = unchanged_slug
        changed, anomalies = scraper.merge_updates(catalog, state)
        self.assertEqual((1, []), (changed, anomalies))
        self.assertEqual(["local-id"], catalog[2]["modIds"])
        self.assertEqual("新名字", catalog[2]["chineseName"])

        state["pendingUpdates"]["2"] = make_record(2, curseforge=["different-cf"])
        changed, anomalies = scraper.merge_updates(catalog, state)
        self.assertEqual((1, []), (changed, anomalies))
        self.assertEqual({}, state["quarantine"])
        self.assertEqual(["different-cf"], catalog[2]["curseForgeSlugs"])
        self.assertEqual([], catalog[2]["modrinthSlugs"])
        self.assertEqual(["local-id"], catalog[2]["modIds"])

    def test_cross_record_slug_is_allowed(self):
        catalog = {2: make_record(2, modrinth=["taken"])}
        state = scraper.load_state(Path("missing-state-file.json"), catalog)
        state["pendingUpdates"]["3"] = make_record(3, modrinth=["taken"])
        changed, anomalies = scraper.merge_updates(catalog, state)
        self.assertEqual((1, []), (changed, anomalies))
        self.assertEqual(["taken"], catalog[3]["modrinthSlugs"])
        self.assertEqual({}, state["quarantine"])

    def test_saved_slug_change_quarantine_is_reconciled_with_current_page(self):
        catalog = {2: make_record(2, curseforge=["original"])}
        state = scraper.load_state(Path("missing-state-file.json"), catalog)
        candidate = make_record(2, curseforge=["fabric", "forge"])
        state["quarantine"]["2"] = {"reason": "previous strict rule", "candidate": candidate}
        changed, anomalies = scraper.merge_updates(catalog, state)
        self.assertEqual((1, []), (changed, anomalies))
        self.assertEqual(["fabric", "forge"], catalog[2]["curseForgeSlugs"])
        self.assertEqual({}, state["quarantine"])

    def test_successful_refresh_can_remove_all_platform_links(self):
        catalog = {2: make_record(2, curseforge=["old-cf"], modrinth=["old-md"])}
        state = scraper.load_state(Path("missing-state-file.json"), catalog)
        state["pendingUpdates"]["2"] = make_record(2)
        changed, anomalies = scraper.merge_updates(catalog, state)
        self.assertEqual((1, []), (changed, anomalies))
        self.assertEqual([], catalog[2]["curseForgeSlugs"])
        self.assertEqual([], catalog[2]["modrinthSlugs"])

    def test_current_page_replaces_old_chinese_title_and_clears_removed_names(self):
        catalog = {2: make_record(2, "已有中文名")}
        catalog[2]["abbreviation"] = "OLD"
        state = scraper.load_state(Path("missing-state-file.json"), catalog)
        candidate = make_record(2, "English Only")
        candidate["englishName"] = None
        state["pendingUpdates"]["2"] = candidate
        changed, anomalies = scraper.merge_updates(catalog, state)
        self.assertEqual((1, []), (changed, anomalies))
        self.assertEqual("English Only", catalog[2]["chineseName"])
        self.assertIsNone(catalog[2]["englishName"])
        self.assertIsNone(catalog[2]["abbreviation"])
        self.assertEqual({}, state["quarantine"])

    def test_atomic_catalog_round_trip_is_stable(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "mods.jsonl"
            scraper.write_catalog(path, {4: make_record(4), 2: make_record(2)})
            self.assertEqual([2, 4], list(scraper.load_catalog(path)))
            before = path.read_bytes()
            scraper.write_catalog(path, scraper.load_catalog(path))
            self.assertEqual(before, path.read_bytes())

    def test_write_catalog_restores_field_order_after_state_round_trip(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "mods.jsonl"
            record = make_record(8)
            shuffled = json.loads(json.dumps(record, sort_keys=True))
            scraper.write_catalog(path, {8: shuffled})
            self.assertTrue(path.read_text(encoding="utf-8").startswith('{"mcmodId":8,"chineseName":'))

    def test_live_run_resumes_new_ids_and_backfills_a_gap_without_network(self):
        with tempfile.TemporaryDirectory() as directory:
            catalog_path = Path(directory) / "mods.jsonl"
            state_path = Path(directory) / "state.json"
            scraper.write_catalog(catalog_path, {2: make_record(2), 4: make_record(4)})

            class FakeClient:
                def __init__(self):
                    self.used = 0

                def load_robots(self):
                    self.used += 1

                def get(self, path):
                    self.used += 1
                    if "sort=createtime" in path:
                        return "<div class='modlist-block'><a href='/class/7.html'>Newest</a></div>"
                    if "sort=lastedittime" in path:
                        return "<div class='modlist-block'><a href='/class/2.html'>Edited</a></div>"
                    if path == "/class/6.html":
                        raise error.HTTPError(path, 404, "Not Found", {}, None)
                    return "<div class='class-title'><h3>新模组</h3><h4>New Mod</h4></div>"

            fake = FakeClient()
            with patch.object(scraper, "BudgetClient", return_value=fake):
                scraper.run_live(catalog_path, state_path, 20, 1, 0, 0)

            catalog = scraper.load_catalog(catalog_path)
            state = json.loads(state_path.read_text(encoding="utf-8"))
            self.assertEqual({2, 3, 4, 5, 7}, set(catalog))
            self.assertEqual(8, state["nextNewId"])
            self.assertEqual("missing", state["records"]["6"]["status"])
            self.assertEqual({}, state["pendingUpdates"])
            self.assertEqual(8, fake.used)
            self.assertEqual(0, state["lastRun"]["newIdBacklog"])

    def test_existing_only_refreshes_old_records_and_due_retries_without_new_scan(self):
        with tempfile.TemporaryDirectory() as directory:
            catalog_path = Path(directory) / "mods.jsonl"
            state_path = Path(directory) / "state.json"
            scraper.write_catalog(catalog_path, {2: make_record(2), 4: make_record(4)})
            state = scraper.load_state(state_path, scraper.load_catalog(catalog_path))
            state["records"]["3"] = {"status": "failed", "nextRetry": "2099-01-01"}
            scraper.save_state(state_path, state)

            class FakeClient:
                def __init__(self):
                    self.used = 0
                    self.paths = []

                def load_robots(self):
                    self.used += 1

                def get(self, path):
                    self.used += 1
                    self.paths.append(path)
                    if "sort=lastedittime" in path:
                        return "<div class='modlist-block'><a href='/class/2.html'>Old</a><a href='/class/4.html'>Old</a><a href='/class/99.html'>New</a></div>"
                    if path in ("/class/2.html", "/class/3.html", "/class/4.html"):
                        return "<div class='class-title'><h3>更新后的名称</h3></div>"
                    raise AssertionError(f"Unexpected request: {path}")

            fake = FakeClient()
            with patch.object(scraper, "BudgetClient", return_value=fake):
                scraper.run_live(catalog_path, state_path, 10, 1, 0, 0, True, True)

            catalog = scraper.load_catalog(catalog_path)
            saved = scraper.load_state(state_path, catalog)
            self.assertEqual({2, 3, 4}, set(catalog))
            self.assertEqual(5, saved["nextNewId"])
            self.assertEqual({"new": 0, "edited": 2, "retry": 1, "gap": 0},
                             saved["lastRun"]["attempted"])
            self.assertTrue(saved["lastRun"]["existingOnly"])
            self.assertTrue(saved["lastRun"]["retryNow"])
            self.assertEqual({"/class/2.html", "/class/3.html", "/class/4.html"},
                             {path for path in fake.paths if path.startswith("/class/")})

    def test_recently_checked_mod_is_refreshed_again_when_still_in_edited_list(self):
        with tempfile.TemporaryDirectory() as directory:
            catalog_path = Path(directory) / "mods.jsonl"
            state_path = Path(directory) / "state.json"
            catalog = {2: make_record(2, "旧名字", curseforge=["old-cf"]), 3: make_record(3)}
            scraper.write_catalog(catalog_path, catalog)
            state = scraper.load_state(state_path, catalog)
            state["recentEditedIds"] = [2]
            state["records"]["2"] = {"status": "ok", "lastChecked": "2026-10-22"}
            scraper.save_state(state_path, state)

            class FakeClient:
                def __init__(self):
                    self.used = 0

                def load_robots(self):
                    self.used += 1

                def get(self, path):
                    self.used += 1
                    if "sort=createtime" in path:
                        return "<div class='modlist-block'><a href='/class/3.html'>Newest</a></div>"
                    if "sort=lastedittime" in path:
                        return "<div class='modlist-block'><a href='/class/2.html'>Edited</a></div>"
                    if path == "/class/2.html":
                        return "<div class='class-title'><h3>新名字</h3></div><div class='common-link-frame'></div>"
                    raise AssertionError(f"Unexpected request: {path}")

            with patch.object(scraper, "BudgetClient", return_value=FakeClient()), \
                    patch.object(scraper, "utc_now", return_value=datetime(2026, 10, 22, tzinfo=timezone.utc)):
                scraper.run_live(catalog_path, state_path, 10, 1, 0, 0)
            self.assertEqual("新名字", scraper.load_catalog(catalog_path)[2]["chineseName"])
            self.assertEqual([], scraper.load_catalog(catalog_path)[2]["curseForgeSlugs"])

    def test_limited_edited_budget_checks_oldest_record_first(self):
        with tempfile.TemporaryDirectory() as directory:
            catalog_path = Path(directory) / "mods.jsonl"
            state_path = Path(directory) / "state.json"
            catalog = {2: make_record(2), 3: make_record(3), 4: make_record(4)}
            scraper.write_catalog(catalog_path, catalog)
            state = scraper.load_state(state_path, catalog)
            state["records"]["2"] = {"status": "ok", "lastChecked": "2026-10-22"}
            state["records"]["3"] = {"status": "ok", "lastChecked": "2026-08-01"}
            scraper.save_state(state_path, state)

            class FakeClient:
                def __init__(self):
                    self.used = 0
                    self.checked = []

                def load_robots(self):
                    self.used += 1

                def get(self, path):
                    self.used += 1
                    if "sort=createtime" in path:
                        return "<div class='modlist-block'><a href='/class/4.html'>Newest</a></div>"
                    if "sort=lastedittime" in path:
                        return "<div class='modlist-block'><a href='/class/2.html'>First</a><a href='/class/3.html'>Second</a></div>"
                    self.checked.append(path)
                    return "<div class='class-title'><h3>更新</h3></div>"

            fake = FakeClient()
            with patch.object(scraper, "BudgetClient", return_value=fake), \
                    patch.object(scraper, "utc_now", return_value=datetime(2026, 10, 22, tzinfo=timezone.utc)):
                scraper.run_live(catalog_path, state_path, 5, 1, 0, 0)
            self.assertEqual(["/class/3.html"], fake.checked)

    def test_stop_crawl_preserves_completed_results_for_review_pr(self):
        with tempfile.TemporaryDirectory() as directory:
            catalog_path = Path(directory) / "mods.jsonl"
            state_path = Path(directory) / "state.json"
            scraper.write_catalog(catalog_path, {2: make_record(2)})

            class FakeClient:
                def __init__(self):
                    self.used = 0

                def load_robots(self):
                    self.used += 1

                def get(self, path):
                    self.used += 1
                    if "sort=createtime" in path:
                        return "<div class='modlist-block'><a href='/class/4.html'>Latest</a></div>"
                    if "sort=lastedittime" in path:
                        return "<div class='modlist-block'><a href='/class/2.html'>Edited</a></div>"
                    if path == "/class/3.html":
                        return "<div class='class-title'><h3>新模组</h3></div>"
                    raise scraper.StopCrawl("HTTP 429")

            with patch.object(scraper, "BudgetClient", return_value=FakeClient()):
                with self.assertRaisesRegex(scraper.StopCrawl, "HTTP 429"):
                    scraper.run_live(catalog_path, state_path, 10, 1, 0, 0)
            self.assertIn(3, scraper.load_catalog(catalog_path))
            state = scraper.load_state(state_path, scraper.load_catalog(catalog_path))
            self.assertEqual(4, state["nextNewId"])
            self.assertEqual(1, state["lastRun"]["catalogRowsChanged"])
            self.assertEqual({}, state["pendingUpdates"])


class BudgetTests(unittest.TestCase):
    def test_oversized_response_is_rejected_instead_of_parsed_partially(self):
        client = scraper.BudgetClient(5, 0, 0)
        response = unittest.mock.MagicMock()
        response.__enter__.return_value = response
        response.geturl.return_value = "https://www.mcmod.cn/class/123.html"
        response.read.return_value = b"x" * (scraper.MAX_HTML_BYTES + 1)
        with patch.object(scraper.request, "urlopen", return_value=response):
            with self.assertRaisesRegex(scraper.TransientFailure, "size limit"):
                client._get_once("https://www.mcmod.cn/class/123.html")
        self.assertEqual(1, client.used)

    def test_robots_fetch_counts_and_disallowed_paths_are_blocked(self):
        client = scraper.BudgetClient(5, 0, 0)
        response = unittest.mock.MagicMock()
        response.__enter__.return_value = response
        response.geturl.return_value = "https://www.mcmod.cn/robots.txt"
        response.read.return_value = b"User-agent: *\nDisallow: /class/add/\n"
        with patch.object(scraper.request, "urlopen", return_value=response) as open_url:
            client.load_robots()
            self.assertEqual(1, client.used)
            self.assertTrue(client.robots.can_fetch(scraper.USER_AGENT, "https://www.mcmod.cn/class/2.html"))
            self.assertFalse(client.robots.can_fetch(scraper.USER_AGENT, "https://www.mcmod.cn/class/add/"))
            self.assertEqual(1, open_url.call_count)

    def test_html_challenge_cannot_masquerade_as_robots_policy(self):
        client = scraper.BudgetClient(5, 0, 0)
        response = unittest.mock.MagicMock()
        response.__enter__.return_value = response
        response.geturl.return_value = "https://www.mcmod.cn/robots.txt"
        response.read.return_value = b"<html><title>Verification required</title></html>"
        with patch.object(scraper.request, "urlopen", return_value=response):
            with self.assertRaises(scraper.StopCrawl):
                client.load_robots()
        self.assertIsNone(client.robots)

    def test_rate_limit_stops_without_retry(self):
        client = scraper.BudgetClient(5, 0, 0)
        client.robots = scraper.robotparser.RobotFileParser()
        client.robots.parse(["User-agent: *", "Allow: /"])
        limited = error.HTTPError("https://www.mcmod.cn/class/2.html", 429,
                                  "Too Many Requests", {"Retry-After": "120"}, None)
        with patch.object(scraper.request, "urlopen", side_effect=limited) as open_url:
            with self.assertRaisesRegex(scraper.StopCrawl, "Retry-After='120'"):
                client.get("/class/2.html")
            self.assertEqual(1, client.used)
            self.assertEqual(1, open_url.call_count)

    def test_retry_and_budget_count_every_attempt(self):
        client = scraper.BudgetClient(5, 0, 0)
        client.robots = scraper.robotparser.RobotFileParser()
        client.robots.parse(["User-agent: *", "Allow: /"])
        response = unittest.mock.MagicMock()
        response.__enter__.return_value = response
        response.geturl.return_value = "https://www.mcmod.cn/class/2.html"
        response.read.return_value = b"<div class='class-title'><h3>Example</h3></div>"
        failure = error.URLError("temporary failure")
        with patch.object(scraper.request, "urlopen", side_effect=[failure, response]) as open_url:
            self.assertIn("Example", client.get("/class/2.html"))
            self.assertEqual(2, client.used)
            self.assertEqual(2, open_url.call_count)

        client.used = client.max_requests
        with self.assertRaises(scraper.BudgetExhausted):
            client.get("/class/3.html")

    def test_read_timeout_retries_then_marks_mod_failed(self):
        client = scraper.BudgetClient(5, 0, 0)
        client.robots = scraper.robotparser.RobotFileParser()
        client.robots.parse(["User-agent: *", "Allow: /"])
        response = unittest.mock.MagicMock()
        response.__enter__.return_value = response
        response.geturl.return_value = "https://www.mcmod.cn/class/2.html"
        response.read.side_effect = TimeoutError("The read operation timed out")
        with tempfile.TemporaryDirectory() as directory:
            state_path = Path(directory) / "state.json"
            state = scraper.load_state(state_path, {1: make_record(1)})
            with patch.object(scraper.request, "urlopen", return_value=response) as open_url:
                status = scraper.fetch_mod(client, state, state_path, 2,
                                           datetime(2026, 10, 22, tzinfo=timezone.utc), True)
            self.assertEqual("failed", status)
            self.assertEqual(2, client.used)
            self.assertEqual(2, open_url.call_count)
            self.assertEqual(3, state["nextNewId"])
            self.assertEqual("2026-10-29", state["records"]["2"]["nextRetry"])
            self.assertNotIn("2", state["pendingUpdates"])


class CommandLineTests(unittest.TestCase):
    def test_default_run_starts_live_update(self):
        with patch.object(sys, "argv", ["update_mods.py"]), \
                patch.object(scraper, "load_catalog", return_value={1: make_record(1)}), \
                patch.object(scraper, "run_live") as run_live:
            scraper.main()
        run_live.assert_called_once_with(scraper.CATALOG, scraper.STATE, 200, 3, 5, 10, False, False)

    def test_existing_only_option_is_passed_to_live_update(self):
        with patch.object(sys, "argv", ["update_mods.py", "--existing-only"]), \
                patch.object(scraper, "load_catalog", return_value={1: make_record(1)}), \
                patch.object(scraper, "run_live") as run_live:
            scraper.main()
        run_live.assert_called_once_with(scraper.CATALOG, scraper.STATE, 200, 3, 5, 10, True, False)

    def test_retry_now_requires_existing_only(self):
        with patch.object(sys, "argv", ["update_mods.py", "--retry-now"]), \
                patch.object(scraper, "load_catalog", return_value={1: make_record(1)}), \
                patch.object(scraper, "run_live") as run_live:
            with self.assertRaises(SystemExit):
                scraper.main()
        run_live.assert_not_called()

    def test_check_only_does_not_start_live_update(self):
        with patch.object(sys, "argv", ["update_mods.py", "--check-only"]), \
                patch.object(scraper, "load_catalog", return_value={1: make_record(1)}), \
                patch.object(scraper, "run_live") as run_live:
            scraper.main()
        run_live.assert_not_called()

    def test_reconcile_only_applies_saved_candidates_without_network(self):
        with tempfile.TemporaryDirectory() as directory:
            catalog_path = Path(directory) / "mods.jsonl"
            state_path = Path(directory) / "state.json"
            scraper.write_catalog(catalog_path, {2: make_record(2)})
            state = scraper.load_state(state_path, scraper.load_catalog(catalog_path))
            state["quarantine"]["3"] = {
                "reason": "previous duplicate rule",
                "candidate": make_record(3, modrinth=["shared"]),
            }
            scraper.save_state(state_path, state)
            with patch.object(sys, "argv", ["update_mods.py", "--reconcile-only",
                                            "--catalog", str(catalog_path), "--state", str(state_path)]), \
                    patch.object(scraper, "run_live") as run_live:
                scraper.main()
            run_live.assert_not_called()
            self.assertIn(3, scraper.load_catalog(catalog_path))
            self.assertEqual({}, scraper.load_state(state_path, {})["quarantine"])


if __name__ == "__main__":
    unittest.main()
