"""Independently validate exported regression documents and their schema-bound examples.

Requires openapi-spec-validator (which also installs openapi-schema-validator).
Usage: python test/validate_generated_openapi.py artifacts/validation
"""

import argparse
import json
from pathlib import Path

from openapi_schema_validator import OAS30Validator
from openapi_spec_validator import validate


def validate_examples(node, location=""):
    count = 0
    if isinstance(node, dict):
        if "schema" in node and "examples" in node:
            for name, example in node["examples"].items():
                if "value" in example:
                    errors = list(OAS30Validator(node["schema"]).iter_errors(example["value"]))
                    if errors:
                        raise ValueError(f"{location}/examples/{name}: {errors[0].message}")
                    count += 1
        for key, child in node.items():
            if not key.startswith("x-"):
                count += validate_examples(child, location + "/" + key)
    elif isinstance(node, list):
        for index, child in enumerate(node):
            count += validate_examples(child, location + "/" + str(index))
    return count


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    files = sorted(args.directory.glob("*.json"))
    if not files:
        parser.error("No generated JSON documents found.")
    example_count = 0
    for file in files:
        document = json.loads(file.read_text(encoding="utf-8"))
        try:
            validate(document)
            example_count += validate_examples(document)
        except Exception as error:
            raise RuntimeError(f"{file}: {error}") from error
    print(f"Validated {len(files)} OpenAPI documents and {example_count} parameter/body/response examples against their schemas.")


if __name__ == "__main__":
    main()
