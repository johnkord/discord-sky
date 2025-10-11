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
    -e BOT_MESSAGE_LIMIT \
    -e OPENAI_IMAGE_MODEL \
    -e OPENAI_IMAGE_SIZE \
    -e DM_USER_ID \
    -e URL_TO_FETCH \
    -e URL_TO_FETCH2 \
    -e MINUTES_BETWEEN_MESSAGES \
    johnkordich/sky:$(cat VERSION)
