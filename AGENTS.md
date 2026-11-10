# Repository guidance

This repository contains the user's original multipart bundle and an experimental offline editor for its TheBrain BRZ exports.

For brain-content edits, read `skills/brz-edit/SKILL.md` and `docs/FORMAT.md` before changing any export. Use `python3 -m brain_toolkit` and source-SHA-bound JSON plans; output a separate new BRZ and a change report. Follow the user's existing authorization without adding routine approval steps.

Keep original `Brainmind.7z.*` files and extracted source BRZ files unchanged. Put extraction, scratch analysis, and temporary outputs in `work/`, which is ignored by Git. Do not execute activation utilities or modify licensing files. Do not infer a live database schema from DLL filenames.

Preserve unknown fields, unrelated ZIP members and untouched record bytes. Do not claim application compatibility from offline structure checks. Application import verification requires a disposable test brain and the round-trip procedure documented in `docs/FORMAT.md`.

For editor code changes, run `python3 -m unittest discover -s tests -v`. The core has no runtime dependencies beyond Python 3.10+.
