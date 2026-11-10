"""Launch the bundled editor, or the single source copy in a repository checkout."""
from __future__ import annotations

import json
from pathlib import Path
import sys


def main():
    script_dir = Path(__file__).resolve().parent
    candidates = [script_dir, script_dir.parents[2]]
    runtime = next((path for path in candidates
                    if (path / "brain_toolkit" / "__main__.py").is_file()), None)
    if runtime is None:
        print(json.dumps({"error": "Editor runtime not found. Install the complete brz-edit.zip "
                         "bundle, or run this launcher from the Brainmind checkout."}), file=sys.stderr)
        return 1
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(runtime))
    from brain_toolkit.__main__ import main as editor_main
    return editor_main()


if __name__ == "__main__":
    sys.exit(main())
