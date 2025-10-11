"""Service for scraping job listings from configured HTML pages."""
from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import Iterable, List, Optional

import aiohttp
from bs4 import BeautifulSoup

logger = logging.getLogger(__name__)


@dataclass
class JobPosting:
    """Representation of a job posting snippet."""

    title: str
    shift: str
    highlighted: bool = False


class JobScraperService:
    """Fetch and parse job listings from configured URLs."""

    def __init__(self, *, session: aiohttp.ClientSession) -> None:
        self._session = session

    async def fetch_html(self, url: str) -> str:
        logger.debug("Fetching job listings from %s", url)
        async with self._session.get(url) as response:
            response.raise_for_status()
            return await response.text()

    def parse_job_section(self, html_content: str, *, filter_keyword: Optional[str] = None) -> List[JobPosting]:
        soup = BeautifulSoup(html_content, "html.parser")
        job_list_section = soup.find("div", id="job-list-section")
        if not job_list_section:
            logger.warning("No job-list-section found in provided HTML")
            return []

        job_divs = job_list_section.find_all("div", recursive=False)
        postings: List[JobPosting] = []
        for job_div in job_divs:
            h3_tag = job_div.find("h3")
            if not h3_tag:
                continue
            job_title = h3_tag.get_text(strip=True)
            shift_span = job_div.find("span", class_="shift")
            shift_dd = shift_span.find("dd") if shift_span else None
            shift_type = shift_dd.get_text(strip=True) if shift_dd else "N/A"

            lowered_title = job_title.lower()
            if filter_keyword and filter_keyword.lower() not in lowered_title:
                continue

            highlight = "day" in job_title.lower() or "day" in shift_type.lower()
            postings.append(
                JobPosting(
                    title=job_title,
                    shift=shift_type,
                    highlighted=highlight,
                )
            )
        return postings

    @staticmethod
    def format_postings(postings: Iterable[JobPosting], heading: str) -> str:
        lines = [heading]
        for posting in postings:
            title = posting.title
            shift = posting.shift
            if posting.highlighted:
                title = f"**{title}**" if "**" not in title else title
                shift = f"**{shift}**" if "**" not in shift else shift
            lines.append(f"{title} | Shift: {shift}")
        if len(lines) == 1:
            lines.append("No results found.")
        return "\n".join(lines)
