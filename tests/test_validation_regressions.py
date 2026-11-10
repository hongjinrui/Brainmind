"""Regressions for live references and strict observed schema validation."""
import json
import unittest

from brain_toolkit import Archive, ArchiveError
from brain_toolkit.archive import _json
import test_archive as fixtures
from test_archive import (
    BRAIN, CHILD, DEFINITION, EDGE, FIELD, ICON, NEW,
    NEW_EDGE, PARENT, TYPE, ndjson, record,
)


class ValidationRegressionTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.ArchiveTests("test_remove_ordinary_link")
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)

    def save_tables(self):
        fixture = self.fixture
        fixture.members.update({name + ".json": ndjson(rows) for name, rows in fixture.tables.items()})
        fixture.create_archive()
        return Archive(fixture.source)

    def test_live_link_owners_are_checked_in_each_table(self):
        for table in ("attachments", "fieldinstances", "calendarevents"):
            with self.subTest(table=table):
                fixture = self.fixture
                old = list(fixture.tables[table])
                fields = dict(SourceId=EDGE, SourceType=3)
                if table == "attachments":
                    fields.update(Type=3, Location="https://example.org/", Name="Reference")
                if table == "fieldinstances":
                    fields.update(FieldDefinitionId=DEFINITION, DecimalValue=1.0)
                row = record(NEW, **fields)
                fixture.tables[table].append(row)
                self.assertEqual([], self.save_tables().validate()["errors"])
                row["SourceId"] = NEW_EDGE
                errors = self.save_tables().validate()["errors"]
                self.assertTrue(any("missing source link" in error for error in errors), errors)
                fixture.tables[table] = old

    def test_malformed_live_sources_and_definition_report_errors_without_crashing(self):
        self.fixture.tables["attachments"][0]["SourceId"] = []
        self.fixture.tables["fieldinstances"][0]["SourceId"] = {}
        self.fixture.tables["fieldinstances"][0]["FieldDefinitionId"] = []
        self.fixture.tables["calendarevents"].append(record(NEW, SourceType=2, SourceId=[]))
        errors = self.save_tables().validate()["errors"]
        self.assertTrue(any("missing definition" in error for error in errors))
        self.assertGreaterEqual(sum("SourceId" in error for error in errors), 3)

    def test_type_assignment_and_hierarchy_require_different_target_kinds(self):
        child = self.fixture.tables["thoughts"][1]
        child["TypeId"] = TYPE
        self.fixture.tables["links"].append(record(
            NEW_EDGE, ThoughtIdA=TYPE, ThoughtIdB=CHILD,
            Kind=1, Relation=1, Meaning=3, Direction=-1,
        ))
        errors = self.save_tables().validate()["errors"]
        self.assertTrue(any("Meaning=3 target must be Kind=2" in error for error in errors))
        child["Kind"] = 2
        self.assertEqual([], self.save_tables().validate()["errors"])
        self.fixture.tables["links"][-1]["Meaning"] = 2
        errors = self.save_tables().validate()["errors"]
        self.assertTrue(any("Meaning=2 target must be Kind=1" in error for error in errors))

    def test_malformed_thought_scalar_fields_are_rejected(self):
        self.fixture.tables["thoughts"][0].update(Name=123, Kind=True, TagIds={})
        errors = self.save_tables().validate()["errors"]
        for text in ("Name must be a string", "Kind must be an integer", "TagIds must be an array"):
            self.assertTrue(any(text in error for error in errors), errors)

    def test_unknown_kind_is_preserved_as_warning(self):
        self.fixture.tables["thoughts"][1]["Kind"] = 99
        validation = self.save_tables().validate()
        self.assertEqual([], validation["errors"])
        self.assertTrue(any("unknown Kind preserved" in item for item in validation["warnings"]))

    def test_unsafe_local_locations_are_errors_but_external_paths_are_not_members(self):
        attachment = self.fixture.tables["attachments"][0]
        for location in ("../escape.png", "D:\\outside.png", "subdir/file:stream", []):
            with self.subTest(location=location):
                attachment["Location"] = location
                errors = self.save_tables().validate()["errors"]
                self.assertTrue(any("Attachment" in error for error in errors), errors)
        attachment.update(Type=2, IsIcon=False, Location="D:\\external-file.txt")
        self.assertEqual([], self.save_tables().validate()["errors"])

    def test_manifest_must_reference_attachments_with_integer_states(self):
        fixture = self.fixture
        meta = json.loads(fixture.members["meta.json"].decode("utf-8-sig"))
        meta["AttachmentFileStates"] = {NEW: 1, ICON: []}
        fixture.members["meta.json"] = json.dumps(meta).encode()
        fixture.create_archive()
        errors = Archive(fixture.source).validate()["errors"]
        self.assertTrue(any("missing attachment" in error for error in errors))
        self.assertTrue(any("state must be an integer" in error for error in errors))

    def test_json_exponent_overflow_cannot_bypass_finite_number_check(self):
        for text in ('{"value":1e999}', '{"value":-1e999}', '{"nested":[1e400]}'):
            with self.subTest(text=text), self.assertRaises(ArchiveError):
                _json(text)
        self.assertEqual({"value": 1.25e20}, _json('{"value":1.25e20}'))

    def test_historical_references_do_not_require_live_owners(self):
        self.fixture.tables["modificationlogs"].append(record(NEW, SourceType=3, SourceId=NEW_EDGE))
        self.fixture.tables["tombstones"].append(record(NEW_EDGE, Type=3))
        self.assertEqual([], self.save_tables().validate()["errors"])

    def test_high_precision_values_cannot_silently_round_during_unrelated_edits(self):
        for token in ("1.234567890123456789", "0.0000000000000000000000000000000000000000000000000000001e-300"):
            with self.subTest(token=token), self.assertRaisesRegex(ArchiveError, "precision loss"):
                _json('{"unknown_field":' + token + '}')
        # Decimal text represented exactly on reserialization remains supported.
        self.assertEqual({"value": 1.25}, _json('{"value":1.2500}'))
        self.assertEqual({"value": 1e20}, _json('{"value":1e20}'))


if __name__ == "__main__":
    unittest.main()
