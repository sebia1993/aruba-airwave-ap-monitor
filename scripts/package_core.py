"""Build the core monitoring bundle. Windows execution and onsite validation are separate gates."""

import argparse
from pathlib import Path
import subprocess

from package_initial import create_package


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("published", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    repository = Path(__file__).resolve().parent.parent
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository, text=True).strip()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=normal"],
                                        cwd=repository, text=True).strip())
    print(create_package(args.published, args.destination, repository, commit, dirty, stage="core-monitoring"))
