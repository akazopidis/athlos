"""Fails when a Lighthouse summary (tests/evaluation/lighthouse_summary.py, CSV on stdin) breaks the
budget: the CI check after every staging deploy (deploy-staging.yml, backlog item 40).

    python tests/evaluation/lighthouse_summary.py <report dir> | python tests/evaluation/lighthouse_budget.py

The limits sit below the numbers in docs/evaluation/lighthouse.md (desktop 98-99, phone 65-77, layout
shift 0), with room for a GitHub runner's slower, noisier CPU: they catch a regression, not a bad run.
"""
import csv
import sys

# form factor -> column -> (kind, limit): "min" scores, "max" timings and shift.
BUDGET = {
    "desktop": {"performance": ("min", 85), "accessibility": ("min", 95), "best_practices": ("min", 90), "seo": ("min", 90),
                "cls": ("max", 0.1), "tbt_ms": ("max", 300), "lcp_ms": ("max", 2500)},
    "mobile": {"performance": ("min", 50), "accessibility": ("min", 95), "best_practices": ("min", 90), "seo": ("min", 90),
               "cls": ("max", 0.1), "tbt_ms": ("max", 600), "lcp_ms": ("max", 12000)},
}

rows = list(csv.DictReader(sys.stdin))
if not rows:
    sys.exit("No Lighthouse results to check.")

failures = []
for row in rows:
    for column, (kind, limit) in BUDGET[row["form"]].items():
        value = float(row[column])
        if (kind == "min" and value < limit) or (kind == "max" and value > limit):
            failures.append(f"{row['page']} ({row['form']}): {column} {value:g}, budget {'at least' if kind == 'min' else 'at most'} {limit:g}")
    print(f"{row['page']:<10} {row['form']:<7} performance {row['performance']:>3}  accessibility {row['accessibility']:>3}  "
          f"LCP {float(row['lcp_ms']) / 1000:.2f} s  TBT {row['tbt_ms']} ms  CLS {row['cls']}")

if failures:
    print("\nOver budget:\n  " + "\n  ".join(failures))
    sys.exit(1)
print(f"\nAll {len(rows)} page and form factor medians are within budget.")
