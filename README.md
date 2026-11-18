# ErrorLensLLM

Machine learning based log anomaly detection and incident management for .NET applications.

> Term project — *Makine Öğrenmesi Tabanlı Uygulama Log Anomali Tespiti ve Akıllı Olay Yönetim Platformunun Geliştirilmesi*
> Status: all parts run end to end on a three-service demo: collection, live anomaly scores, incident correlation with
> root cause ranking, error clustering, LLM explanations and a dashboard with developer feedback. Experiments on HDFS and BGL.

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

## Current results — BGL

LogHub BGL: 4.7M lines, cut into windows of 20 consecutive lines (BGL has no session id). 80/20 split; top-g and the
Isolation Forest threshold are tuned on a validation part of the training windows. Full discussion: [docs/experiments/bgl.md](docs/experiments/bgl.md).

| Method | F1, chronological split | F1, random split |
|---|---|---|
| Rule: window has a FATAL / FAILURE line | **0.733** | 0.616 |
| Rule: rare template | 0.201 | 0.132 |
| Isolation Forest (tuned contamination) | 0.044 | 0.389 |
| DeepLog (tuned top-g) | 0.131 | **0.963** |

The split decides the ranking. In log order, 47.6 % of the test windows contain one of 100 templates that never occur
in the training part, most of them ordinary INFO lines, and DeepLog flags them all. With shuffled windows almost none
are unseen and DeepLog reaches 0.963. The level rule is strong because BGL's labels are themselves FATAL alert lines.
Parsing BGL needed the constant-token Drain similarity: with the original one, register dumps made Drain diverge.

### Reproduce

```bash
mkdir -p data/HDFS && cd data/HDFS
curl -L -o HDFS_v1.zip 'https://zenodo.org/records/8196385/files/HDFS_v1.zip?download=1'
unzip HDFS_v1.zip && cd ../..

dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- hdfs         # method comparison
dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- hdfs-tune    # DeepLog top-g / training size
dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- hdfs-parse   # Drain parsing accuracy

mkdir -p data/BGL && curl -L -o data/BGL/BGL.zip 'https://zenodo.org/records/8196385/files/BGL.zip?download=1' && unzip data/BGL/BGL.zip -d data/BGL
dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- bgl            # chronological split
dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- bgl --shuffle  # random split
```

The first run of each parser setting parses the log with Drain (about 10 s) and caches the sessions in `data/HDFS`.
On an Apple M-series CPU, `hdfs` takes about 20 s, `hdfs-tune` about 5 min and `hdfs-parse` about 6 min.
Options are listed at the top of [Program.cs](experiments/ErrorLensLLM.Experiments/Program.cs).

### Local database

```bash
docker compose -f deploy/docker-compose.yml up -d     # PostgreSQL 17 on 127.0.0.1:5432, data in volume errorlens-pgdata
```

## Collection pipeline

An application adds one sink to its Serilog configuration:

```csharp
builder.Host.UseSerilog((context, logger) => logger
    .WriteTo.ErrorLens(new ErrorLensSinkOptions { CollectorUrl = new("http://localhost:5025/"), Application = "kayit" }));
```

The sink batches events in the background and never blocks the application. It sends the message template,
structured properties, the W3C trace id of the current `Activity` (the id OpenTelemetry uses, so a request can be
followed across services), and, for exceptions, the stack frames of the exception that entered application code.
At startup it announces the release (version and commit from `AssemblyInformationalVersion`) as a deployment.

The collector (`src/ErrorLensLLM.Collector`, `POST /api/ingest/logs`) then:

1. masks personal data (e-mail, phone, Turkish ID number, configured fields and properties),
2. assigns a template: the application's message template, or Drain for plain-text messages,
3. fingerprints exceptions (type + message template + top application frames),
4. stores events, templates, error groups and deployments in PostgreSQL.

### Demo: a faulty release

`samples/ErrorLensLLM.Demo` is a small student information system with three modules, each a separate process:
`kayit` (enrollment), `not` (grades) and `odeme` (payments). The `not` and `odeme` modules call `kayit` over HTTP.
A traffic generator keeps the modules busy.

```bash
scripts/demo.sh start                                  # collector + modules 1.0.0 + traffic
scripts/demo.sh deploy kayit 1.1.0 kayit-missing-column  # release a version that reads a column with no migration
scripts/demo.sh status
scripts/demo.sh stop
```

In 25 seconds after the faulty release, one `PostgresException` in `kayit` produced 558 errors there, plus 218 errors
in `not` and 114 in `odeme`: five separate error groups.
Other scenarios with a known root cause: `odeme-bank-timeout` and `not-null-reference`.

## Incidents and root cause

Every 10 seconds the collector correlates the errors of the last hour into incidents
([IncidentCorrelator](src/ErrorLensLLM.Core/Incidents/IncidentCorrelator.cs)):

- **Linking.** Two error groups belong to the same incident when their errors occur in the same distributed trace, or
  when both start within 60 s of each other shortly after the same deployment. The second case catches callers that
  only see "connection refused" while a service restarts. An error group that returns after 15 quiet minutes starts
  a new episode, and can therefore start a new incident.
- **Suspected trigger.** The most recent release (a deployment that changed the version) of the service the failure
  starts from, within 5 min before its first error. That service leads on trace order and onset; see the evaluation
  below for why it is not simply the latest deployment.
- **Root cause score** of each member, with the evidence behind it:
  0.5 × *upstream* (share of shared traces in which its error comes first) +
  0.3 × *release* (it belongs to the suspected release's application, decaying over 5 min) +
  0.2 × *onset* (how early it started within the incident).
- **Logged-only errors.** An `Error`/`Fatal` log without an exception is grouped by application and message template.
- **Category.** Rule-based, from the exception type and message: database schema, database, dependency, timeout,
  code defect, validation, authorization, configuration.
- **Closing.** An incident with no new errors for 15 minutes (`Analysis:CloseAfterMinutes`) is closed; errors after
  that open a new incident. There is no minimum size: a single error is enough to open one.

For the faulty release above, all five error groups form one incident, titled
*"kayit 1.1.0 sonrası PostgresException — 3 modül"*. The `PostgresException` in `kayit` ranks first with a score of
0.88; its evidence is "first error in the chain in 909 of 909 shared traces" and "started 0 s after the kayit 1.1.0
deployment". The HTTP errors in `not` and `odeme` score 0–0.08 and are classified as dependency failures.

## Live anomaly detection

The collector also scores the live stream ([AnomalyMonitor](src/ErrorLensLLM.Collector/Anomalies/AnomalyMonitor.cs),
[StreamAnomalyModel](src/ErrorLensLLM.Ml/Streaming/StreamAnomalyModel.cs)). A demo request writes one or two log lines,
so a trace is too short to be a DeepLog session; as on BGL, each module's stream is cut into **10-second buckets**:

- **DeepLog** predicts each template from the ten before it in the module's stream; a bucket's score is the share of its
  events outside the top-g predictions. Templates never seen in training are always outside.
- **Isolation Forest** scores the bucket's template count vector: an unusual mix of ordinary lines.
- **A volume rule** flags an event count more than 4σ (and at least 50 %) from the training mean. Isolation Forest cannot
  see this: its splits lie inside the training range, so a silent module scores like the quietest training bucket.

A model per module trains on the error-free buckets of the last 10 minutes (at least 18 buckets and 200 events). Top-g is
the smallest g that flags at most 1 % of the events in a held-out quarter of those buckets; the DeepLog threshold is the
highest share seen there (at least 5 %); the forest threshold is the highest training score. Models retrain every
30 minutes so that a healthy release's new lines become normal.

Measured on the demo ([docs/experiments/live-anomaly.md](docs/experiments/live-anomaly.md)): no alarm in 230 healthy
buckets; a restart of the demo flagged by DeepLog in all three modules (correctly unusual, but no incident); the faulty
kayit release flagged by DeepLog in all three modules from the first bucket after the release (27 of 27 buckets). The
score arrived about 10 s after the incident opened, so for this fault it is a second, independent signal, not an earlier
one. Isolation Forest flagged none of the faulty buckets.

## Grouping similar errors

A fingerprint separates errors that differ in type, message template or code location, so one failure often appears as
several error groups: the same HTTP 500 seen by `not` and by `odeme`. [ErrorGroupClusterer](src/ErrorLensLLM.Core/Grouping/ErrorGroupClusterer.cs)
turns each group into a TF-IDF vector of its exception type, message and frame terms (a sparse embedding computed in
process; no text leaves the machine) and joins groups by average-linkage clustering at cosine similarity ≥ 0.5. The error
group list marks clustered groups and the group page lists its most similar groups. There is no labeled data for error
similarity, so this part is shown, not measured.

## LLM explanation layer

When the collector has an Anthropic API key (`ANTHROPIC_API_KEY` or `Claude:ApiKey`), it asks Claude
([IncidentExplainer](src/ErrorLensLLM.AI.Claude/IncidentExplainer.cs), default model `claude-opus-5-5`, effort medium) to
explain every new incident 30 s after it opens, and again when its root cause changes. The model receives the ranked
candidates with their evidence, the suspected release, one request chain, the root cause's stack trace and the live
anomaly notes. All of these texts were masked by the collector. It answers in Turkish, in a fixed JSON schema: summary,
root cause, impact, next steps, confidence, and a separate field for disagreeing with the ranking. The rule-based ranking stays
the decision and the feedback form still judges it; the explanation helps a developer read it. A refusal is retried by a
fallback model on the server (`fallbacks: "default"`). Without a key the layer is off and the dashboard says so.

Gemini is the second provider ([GeminiExplainer](src/ErrorLensLLM.AI.Claude/GeminiExplainer.cs), default model
`gemini-3.5-flash-lite`, key from `GEMINI_API_KEY`), with the same prompt and JSON schema; `Explanation:Provider`
(`claude` or `gemini`) picks one, otherwise whichever key is set wins, Claude first. 429/5xx answers are retried with
backoff. Tried on the demo's faulty `kayit 1.1.0`: Gemini named the missing `donem_id` column in `KayitService.GetirAsync`
as the root cause, called the `not` and `odeme` HTTP 500s its consequences, agreed with the ranking (confidence
"yüksek") and proposed a roll-back and a migration first (2671 input, 359 output tokens).

Incidents that started more than an hour before the layer was switched on are not explained on their own;
`POST /api/incidents/{id}/explain` explains one on demand (it needs the collector key when one is set). Three such
incidents were explained this way, all correctly: the faulty release, an `odeme`/`not` connection failure while `kayit`
was down (no suspected release), and a port conflict that kept the traffic generator from starting.

## Evaluation with fault injection

`dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- faults` runs each scenario three times against a
fresh database (`errorlens_eval`; the development database is not touched). Each run compares the resulting incident
with the known root cause. Three **basic** scenarios release a module with a continuously triggered fault. Four **hard**
scenarios target known weak spots:

| Hard scenario | What happens |
|---|---|
| `odeme-bank-outage` | the bank goes down while odeme runs; no release is involved |
| `kayit-delayed-fault` | kayit 1.1.0 fails only 90 s after release; a healthy not 1.0.1 is released in between |
| `concurrent-releases` | the faulty kayit 1.1.0 and a healthy odeme 1.0.1 are released 5 s apart |
| `odeme-logged-error` | odeme 1.1.0 logs bank rejections with `LogError` and throws nothing |

Full results: [v1 on the hard scenarios](docs/experiments/fault-injection-v1-hard.md) and
[v2 on all scenarios](docs/experiments/fault-injection.md).

| Hard scenarios (12 runs) | Top-1 | Category | Suspected deployment |
|---|---|---|---|
| v1: blame the last deployment before the incident | 6/12 | 6/12 | **0/12** |
| v2: releases only, looked up for the leading service | **12/12** | 9/12 | **12/12** |

v1 blamed the services' startup for the outage, and the healthy release in between for the delayed fault. When two
releases landed together, it ranked a caller above the real cause. It opened no incident for logged-only errors. v2
makes three changes:

1. A deployment that does not change the version (a restart, or the first one seen) is not a release and is never suspected.
2. The suspected release is the one of the service the failure starts from. That service is chosen by trace order and
   onset before deployments are considered, not simply the latest release.
3. Error-level logs without an exception are grouped by application and template, with the logger's category as the
   code location.

All three basic scenarios stayed at 9/9 on every measure. The median detection time is 2.4 s, with the analyzer running
every 2 s.

**Limitations.**

- The v2 changes were made after seeing the hard scenarios, so their 12/12 shows that these weaknesses are fixed. It
  does not estimate accuracy on new kinds of failures; that needs scenarios written independently of the correlator,
  or the developer feedback the dashboard collects.
- The category of a logged-only error stays wrong (*Unknown* instead of a dependency failure). There is no exception
  type to go on, and a keyword rule written for this one message would only inflate the score.
- All scenarios run in a three-service demo with steady synthetic traffic.

## Dashboard

`src/ErrorLensLLM.Dashboard` (Blazor Server, http://localhost:5200, started by `scripts/demo.sh`) reads the same database:

- **Overview (`/`):** open incidents, errors, log volume and deployments in the chosen window (15 min to 7 days), one
  card per module (running version, error rate, error sparkline), errors per module over time with release markers.
- **Incidents (`/olaylar`):** open, closed or all; search, category and module filters; a sparkline of each incident.
- **Incident detail:**
  - the root cause with its evidence, and how far ahead of the second candidate it scored;
  - the LLM explanation (summary, cause, impact, next steps, confidence, disagreement), when the layer is on;
  - a timeline of errors per module with deployment markers, and a table view;
  - every root cause candidate with its score;
  - one request chain showing how the failure spread across modules, with a link to the full request trace,
    and the latest errors;
  - the suspected deployment (version, commit) and the feedback form, kept in view beside the rest.
- **Error groups (`/hatalar`):** every fingerprint with its count, modules, category and the incident it belongs to;
  similar groups are clustered; the group page adds its timeline, the most similar groups, a sample stack trace and the
  latest occurrences.
- **Deployments (`/deploylar`):** the history per module, which ones changed the version (only those can be
  suspected), errors logged under each, and the incident a deployment is suspected of.
- **Log monitoring (`/izleme`):** the live anomaly score of each module over time against its threshold, each module's
  model (training period, top-g, thresholds), the latest flagged buckets with the detectors that fired and the unseen
  templates; then all logs, not only errors: volume by level, error rate per module, the log templates seen in the
  window and a searchable tail. The search also takes a trace id or a log id (`#123`), which find their logs whatever
  the time range.
- **Log rows** on the incident, error group, monitoring and trace screens open on a click to the full event: log id,
  time to the millisecond, level, module and environment, the release and commit it came from, host, trace and span id
  (linking to the request trace), error group, the full message, exception type and message, the whole stack trace and
  the structured properties. The details are read only for the opened row.
- **Request trace (`/izler/{traceId}`):** every log of one request across modules in time order, with the milliseconds
  since its start; the services it reached, those that failed, and where it failed first. Services are listed in the
  order of their first log, which is not always the call order (parent span ids are not stored yet).
- **Evaluation (`/degerlendirme`):** Top-1 and Top-3 accuracy of the suggested root cause from developer verdicts.
- **Feedback:** developers mark the suggested root cause correct or wrong, and can pick the real one.
  Verdicts are stored with the suggestion they judged (`incident_feedback`), which makes them evaluation data for the
  correlator.

Every screen refreshes every 10 s, follows the system light/dark preference (with a toggle), and collapses the side
menu into a top strip on narrow screens. Stylesheet and script links carry a build version, so a browser does not keep
an old one after an update.

## Repository layout

| Path | Contents | Status |
|---|---|---|
| `src/ErrorLensLLM.Ml` | Drain parser, Isolation Forest, DeepLog, detection metrics, live stream model | ✅ |
| `src/ErrorLensLLM.Core` | PII masking, message normalization, fingerprinting, incident correlation, error clustering | ✅ |
| `experiments/ErrorLensLLM.Experiments` | Reproducible experiments: HDFS, BGL, fault injection | ✅ |
| `tests/` | Unit and integration tests for Core, Ml and the collector (88) | ✅ |
| `src/ErrorLensLLM.Serilog` | Serilog sink that sends logs to the collector | ✅ |
| `src/ErrorLensLLM.Collector` | Ingestion API (masking, templating, fingerprinting), incident analyzer, anomaly monitor, explanations | ✅ |
| `src/ErrorLensLLM.Dashboard` | Blazor Server dashboard: overview, incidents, error groups, deployments, log monitoring, evaluation | ✅ |
| `src/ErrorLensLLM.Storage.PostgreSql` | Logs, incidents, feedback, anomaly scores, explanations | ✅ |
| `src/ErrorLensLLM.AI.Claude` | LLM explanation of incidents | ✅ Claude or Gemini (needs an API key) |
| `src/ErrorLensLLM.Source.Bitbucket` | Fetches source code at the deployed commit for root cause context | planned |
| `samples/ErrorLensLLM.Demo` | Demo modules with controlled fault injection | ✅ |

## Roadmap

- [x] Drain, rule baselines, Isolation Forest, DeepLog on HDFS
- [x] DeepLog tuning (top-g sweep, training size) and Drain parsing accuracy against the ground-truth templates
- [x] TF-IDF embedding + clustering for grouping similar errors
- [x] BGL dataset (chronological and random split)
- [x] Live anomaly scores (DeepLog, Isolation Forest, volume rule) on the collected stream
- [x] Collection pipeline: Serilog sink (W3C trace ids) → collector → PostgreSQL, demo modules with fault injection
- [x] Incident correlation (trace chain, deployment, onset) and root cause ranking
- [x] Dashboard with developer feedback
- [x] LLM explanation layer
- [x] Evaluation with fault injection scenarios (top-k root cause accuracy)
- [x] Request trace screen, full log details, search by trace id and log id
- [ ] Parent span ids, to show the real call order on the trace screen
- [ ] LogBERT (future work)
- [ ] Source code at the deployed commit as context for the LLM (`ErrorLensLLM.Source.Bitbucket`)

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
- A. Oliner, J. Stearley, *What Supercomputers Say: A Study of Five System Logs*, DSN 2007 (BGL dataset)
