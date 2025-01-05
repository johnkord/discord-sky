#!/usr/bin/env bash
cat character_moods.json | jq -c . | sed ':a;N;$!ba;s/\n/\\n/g' > characters.json
cat characters.json | base64 -w 0 > characters.json.b64