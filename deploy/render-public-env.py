#!/usr/bin/env python3
"""Render the ConfigMap's public settings as a raw env file for deploy/compose.yaml.

Usage: git show <commit>:k8s/discord-sky/configmap.yaml | python3 deploy/render-public-env.py > public.env
"""
import sys

import yaml  # PyYAML

data = yaml.safe_load(sys.stdin)["data"]
bad = [key for key, value in data.items() if not isinstance(value, str) or "\n" in value or "\r" in value]
if bad:
    sys.exit("Not representable in an env file: " + ", ".join(sorted(bad)))
sys.stdout.write("".join(f"{key}={value}\n" for key, value in sorted(data.items())))
