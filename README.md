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

**DeepLog tuning** ([report](docs/experiments/hdfs-deeplog-tuning.md)). Top-g and the training size are chosen on a validation split
taken from the training part, never on the test set. The chosen setting is N = 50,000 normal blocks and g = 8, which gives
**test F1 0.800**. Validation F1 is about 0.93; the gap comes from templates that appear only in the second half of the log
(4,524 test blocks contain a template never seen in training). The original paper reports F1 ≈ 0.96 under a different split.

**Parsing accuracy** ([report](docs/experiments/hdfs-parsing.md)). Drain's original similarity cannot reach thresholds above
the share of constant tokens, so at ≥ 0.6 it diverges into thousands of templates. A *constant-token* similarity
(variables ignored) combined with masking file paths raises grouping accuracy from **0.846 to 0.998** at a threshold of 0.8.
It separates `PacketResponder … terminating` from `… interrupted`, which the default setting merges. Detection F1 does not change,
because the merged event occurs only 49 times. A higher parsing accuracy alone does not improve detection.

### Reproduce

```bash
mkdir -p data/HDFS && cd data/HDFS
curl -L -o HDFS_v1.zip 'https://zenodo.org/records/8196385/files/HDFS_v1.zip?download=1'
unzip HDFS_v1.zip && cd ../..

dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- hdfs         # method comparison
dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- hdfs-tune    # DeepLog top-g / training size
dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- hdfs-parse   # Drain parsing accuracy
```

The first run of each parser setting parses the log with Drain (about 10 s) and caches the sessions in `data/HDFS`.
On an Apple M-series CPU, `hdfs` takes about 20 s, `hdfs-tune` about 5 min and `hdfs-parse` about 6 min.
Options are listed at the top of [Program.cs](experiments/ErrorLensLLM.Experiments/Program.cs).

### Local database

```bash
docker compose -f deploy/docker-compose.yml up -d     # PostgreSQL 17 on 127.0.0.1:5432, data in volume errorlens-pgdata
```

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
- [x] DeepLog tuning (top-g sweep, training size) and Drain parsing accuracy against the ground-truth templates
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
