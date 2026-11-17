# BGL anomaly detection results — random split

Generated 2026-11-17 20:21 by `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- bgl --shuffle`.

## Setup

- Dataset: LogHub BGL (Blue Gene/L supercomputer, 4,747,963 lines); a line is an alert when its first field is not `-`
- Parsing: Drain (depth 4, constant-token similarity ≥ 0.8 as in the collector; core ids and every token containing a digit are masked; templates with the same final text merged) → 412 templates
- Windows: BGL has no session id, so the log is cut into fixed windows of 20 consecutive lines (237,398 windows); a window is an anomaly when it contains an alert line
- Split: random (windows shuffled, seed 42), train ratio 0.8; train 189,918 (16,241 anomalies), test 47,480 (4,020 anomalies)
- Validation: the last 10 % of the training windows (18,992, 1,643 anomalies). The tuned rows choose the Isolation Forest contamination and DeepLog's top-g there; the test set is never used for a choice
- DeepLog trains on normal windows of the part before validation only (00:56 on CPU)
- 5 test windows (0.0 %) contain a template never seen in training; 3 of them are anomalies

## Results (test set)

| Method | Precision | Recall | F1 | TP | FP | FN | Time | Setting |
|---|---|---|---|---|---|---|---|---|
| Rule: FATAL/FAILURE level | 0.445 | 1.000 | **0.616** | 4,020 | 5,015 | 0 | 00:00 | any FATAL or FAILURE line in the window |
| Rule: rare template | 0.287 | 0.086 | **0.132** | 346 | 859 | 3,674 | 00:00 | template in < 0.1 % of training windows |
| Isolation Forest | 0.080 | 0.028 | **0.041** | 112 | 1,281 | 3,908 | 00:02 | count vectors, contamination 0.03 (HDFS default) |
| Isolation Forest | 0.310 | 0.522 | **0.389** | 2,097 | 4,658 | 1,923 | 00:02 | count vectors, contamination 0.15 (validation F1 0.413) |
| DeepLog (LSTM) | 0.283 | 1.000 | **0.441** | 4,020 | 10,173 | 0 | 00:00 | h=10, 2×64 LSTM, 20,000 normal windows, 10 epochs, top-g=9 (HDFS default) |
| DeepLog (LSTM) | 0.932 | 0.997 | **0.963** | 4,008 | 292 | 12 | 00:00 | h=10, 2×64 LSTM, 20,000 normal windows, 10 epochs, top-g=150 (validation F1 0.971) |
