#!/usr/bin/env bash
set -Eeuo pipefail
docker push johnkordich/sky:$(cat VERSION)
