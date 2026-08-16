#!/usr/bin/env bash
# Downloads the local embedding model OnnxEmbeddingService needs.
# Not committed (models/ is gitignored) -- run this once locally, and as a
# cached step in CI, before anything that constructs OnnxEmbeddingService.
set -euo pipefail

MODEL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/models/all-MiniLM-L6-v2"
mkdir -p "$MODEL_DIR"

BASE_URL="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main"

if [ ! -f "$MODEL_DIR/model.onnx" ]; then
    echo "Downloading model.onnx..."
    curl -sL -o "$MODEL_DIR/model.onnx" "$BASE_URL/onnx/model.onnx"
fi

if [ ! -f "$MODEL_DIR/vocab.txt" ]; then
    echo "Downloading vocab.txt..."
    curl -sL -o "$MODEL_DIR/vocab.txt" "$BASE_URL/vocab.txt"
fi

echo "Model ready at $MODEL_DIR"
