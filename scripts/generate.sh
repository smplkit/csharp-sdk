#!/usr/bin/env bash
# Regenerate client code from OpenAPI specs using NSwag.
#
# Prerequisites:
#   dotnet tool install --global NSwag.ConsoleCore
#
# Usage:
#   ./scripts/generate.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
SPEC_DIR="$ROOT_DIR/openapi"
GENERATED_DIR="$ROOT_DIR/src/Smplkit/Internal/Generated"

echo "Regenerating clients from OpenAPI specs..."

for spec_file in "$SPEC_DIR"/*.json; do
    spec_name="$(basename "$spec_file" .json)"
    # Capitalize first letter for directory name
    dir_name="$(echo "$spec_name" | sed 's/^./\U&/')"
    output_dir="$GENERATED_DIR/$dir_name"

    echo "  Processing $spec_name -> $output_dir"
    mkdir -p "$output_dir"

    # TODO: Configure NSwag generation here once NSwag config is finalized.
    # nswag openapi2csclient /input:"$spec_file" /output:"$output_dir/Client.cs" \
    #     /namespace:"Smplkit.Internal.Generated.$dir_name" \
    #     /generateClientInterfaces:true
done

echo "Done. Generated code is in $GENERATED_DIR"
