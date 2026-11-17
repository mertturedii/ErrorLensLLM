# BGL anomaly detection results — chronological split

Generated 2026-11-17 20:19 by `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- bgl`.

## Setup

- Dataset: LogHub BGL (Blue Gene/L supercomputer, 4,747,963 lines); a line is an alert when its first field is not `-`
- Parsing: Drain (depth 4, constant-token similarity ≥ 0.8 as in the collector; core ids and every token containing a digit are masked; templates with the same final text merged) → 412 templates
- Windows: BGL has no session id, so the log is cut into fixed windows of 20 consecutive lines (237,398 windows); a window is an anomaly when it contains an alert line
- Split: chronological, train ratio 0.8; train 189,918 (17,250 anomalies), test 47,480 (3,011 anomalies)
- Validation: the last 10 % of the training windows (18,992, 879 anomalies). The tuned rows choose the Isolation Forest contamination and DeepLog's top-g there; the test set is never used for a choice
- DeepLog trains on normal windows of the part before validation only (00:57 on CPU)
- 22,580 test windows (47.6 %) contain a template never seen in training; 2,226 of them are anomalies

## Results (test set)

| Method | Precision | Recall | F1 | TP | FP | FN | Time | Setting |
|---|---|---|---|---|---|---|---|---|
| Rule: FATAL/FAILURE level | 0.579 | 1.000 | **0.733** | 3,011 | 2,189 | 0 | 00:00 | any FATAL or FAILURE line in the window |
| Rule: rare template | 0.113 | 0.885 | **0.201** | 2,665 | 20,819 | 346 | 00:00 | template in < 0.1 % of training windows |
| Isolation Forest | 0.078 | 0.016 | **0.026** | 47 | 558 | 2,964 | 00:02 | count vectors, contamination 0.03 (HDFS default) |
| Isolation Forest | 0.049 | 0.041 | **0.044** | 122 | 2,367 | 2,889 | 00:02 | count vectors, contamination 0.15 (validation F1 0.124) |
| DeepLog (LSTM) | 0.066 | 1.000 | **0.123** | 3,011 | 42,761 | 0 | 00:00 | h=10, 2×64 LSTM, 20,000 normal windows, 10 epochs, top-g=9 (HDFS default) |
| DeepLog (LSTM) | 0.070 | 1.000 | **0.131** | 3,010 | 40,009 | 1 | 00:00 | h=10, 2×64 LSTM, 20,000 normal windows, 10 epochs, top-g=126 (validation F1 0.135) |
