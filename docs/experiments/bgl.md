# BGL: the same methods on a second dataset

Generated reports: [chronological split](bgl-results.md) · [random split](bgl-results-random.md).
Reproduce with `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- bgl [--shuffle]` after
downloading `BGL.zip` from LogHub (Zenodo record 8196385) into `data/BGL`.

## Setup in short

- 4,747,963 lines of the Blue Gene/L RAS log, 348,460 of them labeled as alerts.
- BGL has no session id like HDFS's block id, so the log is cut into **fixed windows of 20 consecutive lines**
  (237,398 windows, 8.5 % anomalous). A window is anomalous when it contains an alert line.
- 80 % of the windows train, 20 % test. The last 10 % of the training part is a validation set; the Isolation Forest
  contamination and DeepLog's top-g are chosen there, never on the test set. The "HDFS default" rows use the settings
  of the HDFS experiment without tuning.

### Parsing had to change

| Setting | Templates | Note |
|---|---|---|
| HDFS masks, original Drain similarity ≥ 0.5 | 2,480 | register dumps (`fpr7=3fd8426b …`) open a template per value; 2,080 templates seen ≤ 5 times |
| Every token with a digit masked, original similarity | diverged (> 5,000 after 700k lines) | such lines are mostly wildcards, and the original similarity never counts a wildcard as similar |
| Every token with a digit masked, **constant-token similarity ≥ 0.8** | **412** | the setting the collector uses; identical texts merged |

This is the same finding as the [HDFS parsing experiment](hdfs-parsing.md) from the other side: the original Drain
similarity cannot handle templates that are mostly variables.

## Results

| Method | Chronological F1 | Random F1 |
|---|---|---|
| Rule: window has a FATAL / FAILURE line | **0.733** | 0.616 |
| Rule: rare template | 0.201 | 0.132 |
| Isolation Forest, contamination 0.03 | 0.026 | 0.041 |
| Isolation Forest, contamination tuned (0.15) | 0.044 | 0.389 |
| DeepLog, top-g = 9 | 0.123 | 0.441 |
| DeepLog, top-g tuned | 0.131 (g = 126) | **0.963** (g = 150) |

## What the numbers say

1. **The split decides the ranking.** In log order, the last 20 % of BGL contains 100 templates that never occur in
   the first 80 %, and 47.6 % of the test windows contain at least one of them. Most of them are ordinary: the two most
   frequent (`… total interrupts …` and `… microseconds spent in the rbs signal handler …`, about 13,600 windows each)
   are INFO lines that only appear late in the log. DeepLog flags every unseen template, so its precision collapses to
   0.07. With shuffled windows only 5 test windows have an unseen template and DeepLog reaches 0.963. Published BGL
   numbers that use a random split measure the second situation; a deployed detector lives in the first.
2. **The level rule is strong on BGL** because the labels are themselves alert lines, and 348,398 of the 348,460 alert
   lines are FATAL. Its precision (0.58) shows how many FATAL lines are not alerts. On HDFS, where labels come from
   block-level investigation, the level rule reached only F1 0.61 and DeepLog was best.
3. **Tuned top-g ended at the edge of the search range** (150 of 412 templates on the random split). At that point
   DeepLog flags a window only when an event is very unlikely in its context, which on BGL mostly means an alert
   template. That is still a learned result, but it is closer to a "never seen here" rule than to sequence modeling.
4. **Isolation Forest on count vectors is weak on both datasets.** A 20-line window holds few events, and the forest
   isolates rare *combinations* of counts, which on BGL are not the alert windows.

## Consequences for the project

- The live detector ([live-anomaly.md](live-anomaly.md)) retrains every 30 minutes, so that a healthy release's new
  log lines become normal instead of producing the chronological-split effect forever.
- Unseen templates are shown by name on the dashboard, because "a template never seen before" is the main reason
  DeepLog fires, both here and in the demo.
- The level rule is not used as the live detector: in the demo the error lines are what the incident correlator
  already groups, and the anomaly score is meant to add what the errors do not show.
