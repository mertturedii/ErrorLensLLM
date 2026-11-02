# HDFS anomaly detection results

Generated 2026-10-28 00:30 by `dotnet run --project experiments/ErrorLensLLM.Experiments -- hdfs`.

## Setup

- Dataset: LogHub HDFS_v1, sessions = blocks (`blk_*`), labels from `anomaly_label.csv`
- Parsing: Drain (depth 4, similarity 0.5, LogHub masks) → 48 templates
- Split: chronological by first appearance, train ratio 0.5
- Sessions: 575,061 total; train 287,530 (10,170 anomalies), test 287,531 (6,668 anomalies)
- All methods are evaluated on the same test blocks; DeepLog trains on normal blocks only (semi-supervised, as in the paper)

## Results (test set)

| Method | Precision | Recall | F1 | TP | FP | FN | Time | Setting |
|---|---|---|---|---|---|---|---|---|
| Rule: WARN/ERROR level | 1.000 | 0.439 | **0.610** | 2,925 | 0 | 3,743 | 00:00 | any WARN/ERROR line in the block |
| Rule: rare template | 0.988 | 0.060 | **0.112** | 397 | 5 | 6,271 | 00:00 | template in < 0.1 % of training blocks |
| Isolation Forest | 0.664 | 0.523 | **0.585** | 3,489 | 1,766 | 3,179 | 00:01 | event count vectors, 100 trees, ψ=256, contamination 0.03 |
| DeepLog (LSTM) | 0.722 | 0.834 | **0.774** | 5,559 | 2,138 | 1,109 | 00:18 | h=10, 2×64 LSTM, top-g=9, 5,000 normal training blocks, 10 epochs |
