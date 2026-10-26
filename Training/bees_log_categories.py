"""Shared, process-local selection for managed Bees training log categories.

Read the inherited selection at the actual print site. The outer continual
service runs before the learner's managed stdout tee, and the same categories
must apply in both processes without depending on tee installation.
"""

from __future__ import annotations

import os


LOG_CATEGORIES_ENV = "BEES_LOG_CATEGORIES"
LOG_CATEGORY_NAMES = frozenset((
    "training", "learner-samples", "learner-stages", "wan-capacity",
    "wan-pipeline", "wan-lag", "wan-other", "continual", "ppo",
    "checkpoints", "distributed", "other",
))


def parse_log_categories(raw: str) -> frozenset[str]:
    value = str(raw or "all").strip().lower()
    if value == "all":
        return LOG_CATEGORY_NAMES
    if value == "none":
        return frozenset()
    categories = [name.strip() for name in value.split(",")]
    if not all(categories) or any(name not in LOG_CATEGORY_NAMES for name in categories):
        names = ", ".join(sorted(LOG_CATEGORY_NAMES))
        raise ValueError(
            f"Invalid logging categories {raw!r}; use all, none, or a comma-separated "
            f"selection from: {names}"
        )
    return frozenset(categories)


def log_category_enabled(category: str) -> bool:
    """Whether routine output in this category is enabled for this process."""
    return category in parse_log_categories(os.environ.get(LOG_CATEGORIES_ENV, "all"))


def log_category(category: str, message: str, *, flush: bool = False) -> None:
    """Print ordinary diagnostics only when their category is selected."""
    if log_category_enabled(category):
        print(message, flush=flush)
