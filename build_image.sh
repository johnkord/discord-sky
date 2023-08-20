#!/usr/bin/env bash
set -Eeuo pipefail
docker build -t johnkordich/sky:$(cat VERSION) .
