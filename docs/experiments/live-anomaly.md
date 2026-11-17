# Live anomaly detection on the demo

A manual run on 2026-11-17 with `scripts/demo.sh`, numbers read from `anomaly_scores` (times in UTC). The method is
described in the README (*Live anomaly detection*); the code is in
[AnomalyMonitor](../../src/ErrorLensLLM.Collector/Anomalies/AnomalyMonitor.cs) and
[StreamAnomalyModel](../../src/ErrorLensLLM.Ml/Streaming/StreamAnomalyModel.cs).

## Run

| Time | Step |
|---|---|
| 17:15 | demo started (kayit, not, odeme 1.0.0 and traffic) |
| 17:19 – 17:22 | models trained for not and odeme (20 error-free buckets each), then kayit (it logs less, and needed 200 events) |
| 17:23:00 | demo restarted with a new collector build; the collector retrained all three models from the stored history at startup |
| 17:23:19 | faulty release `kayit 1.1.0` (`kayit-missing-column`) |
| 17:23:25 | incident opened: *kayit 1.1.0 sonrası PostgresException — 3 modül* |
| 17:25:05 | rolled back to kayit 1.0.0 |
| 17:25:10 – 17:35 | healthy again |

Models (after the restart): 44 buckets each; kayit 252 events, not 2,768, odeme 1,474; 7–8 templates per module;
top-g 1 for kayit and not, 2 for odeme; DeepLog threshold 5 % (the floor; held-out buckets had no unexpected events);
forest thresholds 0.785–0.804.

## Result

| Period | Buckets (3 modules) | DeepLog | Isolation Forest | Volume | Any |
|---|---|---|---|---|---|
| Healthy, before the release (17:18:50 – 17:22:50) | 56 | 0 | 0 | 0 | **0** |
| Demo restart (17:23:00 – 17:23:10) | 6 | 3 | 2 | 0 | 3 |
| Faulty release (17:23:20 – 17:24:40) | 27 | **27** | 0 | 9 (kayit) | **27** |
| Roll-back (17:24:50 – 17:25:00) | 6 | 6 | 1 | 2 | 6 |
| Healthy, after the roll-back (17:25:10 – 17:34:50) | 174 | 0 | 0 | 0 | **0** |

- **Detection.** The first bucket that started after the release, 17:23:20, was flagged in all three modules with
  every event unexpected (DeepLog share 1.00): kayit logs `PostgresException`, and not and odeme log the HTTP 500 they get
  from kayit. The unexpected template is the ASP.NET Core "unhandled exception" line, absent from the error-free training
  buckets. The incident correlator opened the incident at 17:23:25; the anomaly score therefore does not detect this
  scenario earlier, but it is an independent signal that does not depend on exceptions being grouped.
- **Restart.** Shutting down and starting the modules writes lines (`Application is shutting down…`, startup banner) in
  an order the models had seen only rarely; DeepLog flagged the bucket in all three modules. That is a correct anomaly but
  not an incident, and no incident was opened because no error was logged.
- **Roll-back.** The two buckets around the roll-back still contained errors of requests in flight and the restart of
  kayit, then the score returned to zero.
- **No false alarms** in 230 healthy buckets (about 38 minutes of module-time).
- **Isolation Forest flagged none of the faulty buckets.** The bucket count vectors of the faulty period are dominated by
  a template the forest does not know (it only counts known templates), and the remaining counts are within the training
  range. This matches the offline results, where count-vector Isolation Forest was the weakest method on both HDFS and BGL.
  The volume rule caught kayit, whose log grew from about 6 to about 190 lines per bucket.

## Limitations

- One scenario, one run, steady synthetic traffic. The fault-injection experiment measures the correlator over many runs;
  the anomaly monitor needs several minutes of training data per run, which those 60-second runs do not have.
- Training treats buckets without Error / Fatal lines as normal. A fault that logs no error is learned as normal at the
  next retrain (every 30 minutes).
- A model lives in the collector's memory and is retrained from the stored history when the collector restarts.
