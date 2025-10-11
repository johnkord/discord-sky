"""Logging helpers for the Discord Sky bot."""
from __future__ import annotations

import logging
import os
from typing import Optional

_DEFAULT_LOG_FORMAT = "%(asctime)s %(levelname)s [%(name)s] %(message)s"


def configure_logging(level: Optional[str] = None) -> None:
    """Configure application logging.

    Parameters
    ----------
    level:
        Optional log level name (e.g. "INFO", "DEBUG"). Defaults to INFO or the
        value from the LOG_LEVEL environment variable.
    """

    env_level = (level or os.getenv("LOG_LEVEL", "INFO")).upper()
    numeric_level = getattr(logging, env_level, logging.INFO)

    logging.basicConfig(level=numeric_level, format=_DEFAULT_LOG_FORMAT)
    logging.getLogger("discord").setLevel(logging.WARNING)
    logging.getLogger("aiohttp.access").setLevel(logging.WARNING)
