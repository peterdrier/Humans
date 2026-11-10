#!/bin/bash
# Freshness check: dependency-graph
#
# The graph is generated from the section .csproj references; the check is that
# regenerating it changes nothing.

set -euo pipefail

bash "$(cd "$(dirname "$0")/.." && pwd)/generate-dependency-graph.sh" --check
