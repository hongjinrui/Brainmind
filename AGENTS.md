# Repository guidance

Brainmind creates new TheBrain BRZ archives from structured content and edits existing exports offline. The runtime requires only Python 3.10+ and the standard library.

For brain-content work, read `skills/brz-edit/SKILL.md` and `docs/FORMAT.md`. Use `create --content` for new works without an input BRZ. Edit existing archives with source-SHA-bound JSON plans and publish a separate new BRZ. Follow the user's authorization without adding routine approval steps.

Preserve user-provided input files, unknown fields, unrelated ZIP members and untouched record bytes. Put temporary inputs, reports and generated test artifacts in ignored `work/`. Do not edit live databases, synchronization data or licensing files.

Offline validation does not establish application compatibility. Verify outputs in an independent test brain in TheBrain, reopen and re-export when application verification is required; see `docs/VALIDATION.md`.

For code changes, run `python -X utf8 -m unittest discover -s tests -v`. Keep `releases/brz-edit.zip` consistent with source: build to a new temporary path using `tools/package_skill.py --output`, deliberately replace the release after verification, then run `tools/package_skill.py --check releases/brz-edit.zip`. Maintain core code only in `brain_toolkit`; the standalone package receives its runtime at build time.
