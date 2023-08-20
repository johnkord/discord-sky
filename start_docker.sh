#!/usr/bin/env bash
set -Eeuo pipefail

source env.sh

# run with the above envvars passed in
docker run --name discord-sky \
    -d \
    -e BOT_CHANNELS \
    -e BOT_CONTEXT \
    -e BOT_PREFIX \
    -e BOT_TOKEN \
    -e CHATGPT_API_KEY \
    -e CHATGPT_MODEL \
    -e CHATGPT_PROMPT_PREFIX \
    -e CHATGPT_PROMPT_SUFFIX \
    -e CHATGPT_USER_SPECIFIED_MIDDLE_SECTION \
    johnkordich/sky:$(cat VERSION)
