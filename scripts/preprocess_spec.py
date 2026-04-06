#!/usr/bin/env python3
"""Preprocess OpenAPI 3.1 specs for NSwag compatibility.

Converts `anyOf: [{type: X, ...}, {type: null}]` nullable patterns to
`{type: X, ..., nullable: true}`, which NSwag handles correctly. Also
downgrades the spec version to 3.0.3 for maximum NSwag compatibility.
"""

import json
import sys


def flatten_nullable_anyof(obj):
    """Recursively walk the spec and flatten nullable anyOf patterns."""
    if isinstance(obj, dict):
        # Check for anyOf with exactly two members where one is {type: null}
        if "anyOf" in obj and isinstance(obj["anyOf"], list) and len(obj["anyOf"]) == 2:
            schemas = obj["anyOf"]
            null_schema = None
            real_schema = None
            for s in schemas:
                if isinstance(s, dict) and s.get("type") == "null":
                    null_schema = s
                else:
                    real_schema = s

            if null_schema is not None and real_schema is not None:
                # Merge the non-null schema into the parent, add nullable: true
                merged = {k: v for k, v in obj.items() if k != "anyOf"}
                for k, v in real_schema.items():
                    merged[k] = v
                merged["nullable"] = True
                return {k: flatten_nullable_anyof(v) for k, v in merged.items()}

        return {k: flatten_nullable_anyof(v) for k, v in obj.items()}

    if isinstance(obj, list):
        return [flatten_nullable_anyof(item) for item in obj]

    return obj


def downgrade_version(spec):
    """Downgrade OpenAPI version from 3.1.x to 3.0.3 for NSwag."""
    if spec.get("openapi", "").startswith("3.1"):
        spec["openapi"] = "3.0.3"
    return spec


def main():
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <input.json> <output.json>", file=sys.stderr)
        sys.exit(1)

    input_path, output_path = sys.argv[1], sys.argv[2]

    with open(input_path) as f:
        spec = json.load(f)

    spec = flatten_nullable_anyof(spec)
    spec = downgrade_version(spec)

    with open(output_path, "w") as f:
        json.dump(spec, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    main()
