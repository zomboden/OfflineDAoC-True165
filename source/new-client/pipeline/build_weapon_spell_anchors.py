"""Extract original weapon SPELL socket transforms without modifying the client archive."""
import argparse
import json
from pathlib import Path
from zipfile import ZipFile

import numpy as np
from nif_static import local_matrix, read_nif


def find_socket(node, parent):
    if node is None:
        return None
    matrix = local_matrix(node) @ parent
    if (node.get("name") or "").lower() == "spell":
        return matrix
    for child in node.get("children") or ():
        found = find_socket(child, matrix)
        if found is not None:
            return found
    return None


def build(archive, models, output):
    catalog = json.loads(models.read_bytes())["models"]
    sockets = {}
    with ZipFile(archive) as z:
        paths = {name.lower(): name for name in z.namelist()}
        for model, row in catalog.items():
            name = row.get("nif", "").lower()
            if name not in sockets:
                sockets[name] = None
                path = paths.get(f"items/{name}.nif")
                if path:
                    raw = z.read(path)
                    if b"SPELL" in raw or b"spell" in raw:
                        try:
                            for node in read_nif(raw).roots:
                                frame = find_socket(node, np.eye(4))
                                if frame is not None:
                                    sockets[name] = [round(float(x), 6) for x in frame[:4, :3].flat]
                                    break
                        except (ValueError, IndexError, KeyError):
                            print(f"Could not read socket: {path}")
    result = {model: sockets[row.get("nif", "").lower()] for model, row in catalog.items()
              if sockets[row.get("nif", "").lower()] is not None}
    output.write_text(json.dumps(result, separators=(",", ":"), sort_keys=True) + "\n", encoding="utf-8")
    return len(result)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("models", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    print(f"Extracted {build(args.archive, args.models, args.output)} weapon socket mappings")
