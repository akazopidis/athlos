"""CSV summary of the Lighthouse reports written by tests/evaluation/lighthouse.sh: per page and form
factor, the median of the runs for every column.

    python tests/evaluation/lighthouse_summary.py <report dir>
"""
import glob
import json
import os
import statistics
import sys
from collections import defaultdict

METRICS = ["first-contentful-paint", "largest-contentful-paint", "total-blocking-time", "cumulative-layout-shift", "speed-index"]

runs = defaultdict(list)
for path in sorted(glob.glob(os.path.join(sys.argv[1], "*.report.json"))):
    report = json.load(open(path, encoding="utf-8"))
    page, form, _run = os.path.basename(path).removesuffix(".report.json").rsplit("-", 2)
    scores = [report["categories"][c]["score"] * 100 for c in ("performance", "accessibility", "best-practices", "seo")]
    audits = report["audits"]
    metrics = [audits[a]["numericValue"] for a in METRICS]
    requests = audits["network-requests"]["details"]["items"]
    transfer = sum(r.get("transferSize", 0) for r in requests) / 1024
    runs[page, form].append([*scores, *metrics, len(requests), transfer])

print("page,form,runs,performance,accessibility,best_practices,seo,fcp_ms,lcp_ms,tbt_ms,cls,speed_index_ms,requests,transfer_kb")
for (page, form), values in sorted(runs.items()):
    medians = [statistics.median(column) for column in zip(*values)]
    cells = [round(v, 3) if i == 7 else round(v) for i, v in enumerate(medians)]
    print(",".join(map(str, [page, form, len(values), *cells])))
