"""Deploy times for the evaluation chapter (design §15.3), from the GitHub Actions run history.

  python tests/evaluation/deploy_times.py [limit]

For each deploy workflow it takes the successful runs and reports how long each took from the
trigger (push to dev, merge into main, or the release tag) to the end of the deploy, which is when
the new revision is running. Targets: push to running in dev < 15 min; tag to running in prod
< 10 min, with no rebuild.
"""
import json
import statistics
import subprocess
import sys
from datetime import datetime

LIMIT = int(sys.argv[1]) if len(sys.argv) > 1 else 40
WORKFLOWS = {
    "deploy-dev.yml": ("dev (push to running)", 15),
    "deploy-staging.yml": ("staging (merge to running)", None),
    "release-prod.yml": ("prod (tag to running)", 10),
}


def runs(workflow):
    out = subprocess.run(
        ["gh", "run", "list", "--workflow", workflow, "--limit", str(LIMIT), "--json",
         "conclusion,createdAt,updatedAt,headSha,displayTitle,databaseId"],
        capture_output=True, text=True, check=True).stdout
    return [r for r in json.loads(out) if r["conclusion"] == "success"]


def minutes(run):
    start = datetime.fromisoformat(run["createdAt"].replace("Z", "+00:00"))
    end = datetime.fromisoformat(run["updatedAt"].replace("Z", "+00:00"))
    return (end - start).total_seconds() / 60


print(f"| Environment | Runs | Median | p90 | Min | Max | Target |")
print(f"|---|---|---|---|---|---|---|")
for workflow, (label, target) in WORKFLOWS.items():
    durations = sorted(minutes(r) for r in runs(workflow))
    if not durations:
        continue
    p90 = durations[min(len(durations) - 1, int(round(0.9 * (len(durations) - 1))))]
    within = f"< {target} min: {sum(d < target for d in durations)}/{len(durations)}" if target else "n/a"
    print(f"| {label} | {len(durations)} | {statistics.median(durations):.1f} min | {p90:.1f} min | "
          f"{durations[0]:.1f} min | {durations[-1]:.1f} min | {within} |")
