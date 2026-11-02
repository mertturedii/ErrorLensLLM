# ErrorLensLLM

Machine learning based log anomaly detection and incident management for .NET applications.

> Term project — *Makine Öğrenmesi Tabanlı Uygulama Log Anomali Tespiti ve Akıllı Olay Yönetim Platformunun Geliştirilmesi*
> Status: early development. The anomaly detection experiments run; the collection pipeline and dashboard are not built yet.

## Problem

In a large multi-module system, a single faulty release can produce hundreds of errors across different services
within minutes. They look unrelated, yet often share one root cause. Reading them one by one is slow.

This project aims to:

- collect logs from .NET applications centrally (Serilog / OpenTelemetry),
- detect anomalous log sequences and assign an anomaly score,
- group related errors into a single incident instead of hundreds of separate alerts,
- suggest a likely category and root cause, explained in natural language by an LLM,
- learn from developer feedback (correct / wrong) on each detection.

## Architecture

```
.NET applications ──Serilog / OpenTelemetry──► Collection
                                                 masking (PII), normalization, fingerprinting
                                                       │
                                                       ▼
                                               Analysis (C#)
                                 Drain parsing → sessions / windows → detectors
                                 rule baselines · Isolation Forest · DeepLog · clustering
                                                       │ anomaly score + incident
                                                       ▼
                                 Root cause signals + LLM explanation
                                                       │
                                                       ▼
                                 Dashboard + developer feedback
```

Everything, including the models, is written in .NET. DeepLog uses [TorchSharp](https://github.com/dotnet/TorchSharp).

## Current results — HDFS

LogHub HDFS_v1: 11.2M log lines, 575,061 blocks (sessions). Blocks are split chronologically 50/50.
The test set has 287,531 blocks, of which 6,668 are anomalies.

| Method | Precision | Recall | F1 |
|---|---|---|---|
| Rule: block contains a WARN/ERROR line | 1.000 | 0.439 | 0.610 |
| Rule: block contains a rare template | 0.988 | 0.060 | 0.112 |
| Isolation Forest (event count vectors) | 0.664 | 0.523 | 0.585 |
| **DeepLog (LSTM, top-g = 9)** | 0.722 | 0.834 | **0.774** |

Full setup and confusion counts: [docs/experiments/hdfs-results.md](docs/experiments/hdfs-results.md).
DeepLog's hyper-parameters are not tuned yet. The original paper reports an F1 of about 0.96 under a different split.

### Reproduce

```bash
mkdir -p data/HDFS && cd data/HDFS
curl -L -o HDFS_v1.zip 'https://zenodo.org/records/8196385/files/HDFS_v1.zip?download=1'
unzip HDFS_v1.zip && cd ../..

dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- hdfs
```

The first run parses the log with Drain (about 10 s) and caches the sessions in `data/HDFS`. A full run takes about 20 s on an
Apple M-series CPU. Options: `--train-ratio`, `--epochs`, `--deeplog-sessions`, `--skip-deeplog`, `--report`.

## Repository layout

| Path | Contents | Status |
|---|---|---|
| `src/ErrorLensLLM.Ml` | Drain parser, Isolation Forest, DeepLog, detection metrics | ✅ |
| `src/ErrorLensLLM.Core` | Exception capture model, PII masking, message normalization, fingerprinting, background queue | ✅ first version |
| `experiments/ErrorLensLLM.Experiments` | Reproducible experiments on public datasets | ✅ HDFS |
| `tests/` | Unit tests for Core and Ml | ✅ |
| `src/ErrorLensLLM.AspNetCore` | Middleware, Serilog / OpenTelemetry integration, dashboard | planned |
| `src/ErrorLensLLM.Storage.PostgreSql` | Central log and incident store | planned |
| `src/ErrorLensLLM.AI.Claude` | LLM explanation of incidents | planned |
| `src/ErrorLensLLM.Source.Bitbucket` | Fetches source code at the deployed commit for root cause context | planned |
| `samples/ErrorLensLLM.Demo` | Demo app with controlled fault injection | planned |

## Roadmap

- [x] Drain, rule baselines, Isolation Forest, DeepLog on HDFS
- [ ] DeepLog tuning (top-g sweep, training size) and Drain parsing accuracy against the ground-truth templates
- [ ] Embedding + clustering for grouping similar errors
- [ ] BGL dataset
- [ ] Collection pipeline: Serilog / OpenTelemetry → PostgreSQL
- [ ] Incident correlation (deploy time, shared causes, trace chain) and root cause ranking
- [ ] LLM explanation layer and dashboard with feedback
- [ ] Evaluation with fault injection scenarios (top-k root cause accuracy)

## Privacy

Application logs can contain personal data. Messages are masked (e-mail, phone, Turkish ID number, configured fields)
before they are stored or sent to an LLM. No institutional log data is included in this repository; experiments use
public LogHub datasets only.

## References

- P. He et al., *Drain: An Online Log Parsing Approach with Fixed Depth Tree*, ICWS 2017
- F. T. Liu, K. M. Ting, Z.-H. Zhou, *Isolation Forest*, ICDM 2008
- M. Du et al., *DeepLog: Anomaly Detection and Diagnosis from System Logs through Deep Learning*, CCS 2017
- J. Zhu et al., *Loghub: A Large Collection of System Log Datasets for AI-driven Log Analytics*, ISSRE 2023
- W. Xu et al., *Detecting Large-Scale System Problems by Mining Console Logs*, SOSP 2009 (HDFS dataset)
