# Fault injection: root cause suggestions

Generated 2026-11-12 23:02 by `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- faults`.

## Protocol

Each run starts from an empty database. The demo (kayit, not, odeme + traffic generator) runs version 1.0.0 with normal traffic for 20 s. Then one module is released as 1.1.0 with an injected fault and runs for 40 s. The incident analyzer runs every 2 s. Each scenario is repeated 3 times.

| Scenario | Injected fault | True root cause |
|---|---|---|
| `kayit-missing-column` | kayit 1.1.0 reads a column whose migration never ran; not and odeme call kayit | kayit · PostgresException · DatabaseSchema |
| `odeme-bank-timeout` | the bank behind odeme stops answering in time for 40 % of payments | odeme · TimeoutException · Timeout |
| `not-null-reference` | not 1.1.0 dereferences a missing elective course | not · NullReferenceException · CodeDefect |

Measures, on the largest incident that started after the release:
- **Top-1 / Top-3:** the true root cause (module + exception type) is ranked first / within the first three candidates
- **Category / deployment:** the incident's category and suspected deployment match the injected fault
- **Incidents per fault:** how many incidents the release produced (ideal: 1); **false alarms:** incidents during normal traffic
- **Detection time:** from the faulty process start to the incident first being stored

## Results

| Scenario | Top-1 | Top-3 | Category | Deployment | Incidents per fault | False alarms | Median detection |
|---|---|---|---|---|---|---|---|
| `kayit-missing-column` | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 1.0 | 0 | 0.4 s |
| `odeme-bank-timeout` | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 1.0 | 0 | 2.4 s |
| `not-null-reference` | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 1.0 | 0 | 2.4 s |
| **All** | 100 % (9/9) | 100 % (9/9) | 100 % (9/9) | 100 % (9/9) | 1.0 | 0 | 2.4 s |

## Runs

| Scenario | # | Incident | Groups | Errors | True cause rank | Category | Deployment | Detected after |
|---|---|---|---|---|---|---|---|---|
| `kayit-missing-column` | 1 | kayit 1.1.0 sonrası PostgresException — 3 modül, 1443 hata | 5 | 1,443 | 1 | ✓ | ✓ | 0.4 s |
| `kayit-missing-column` | 2 | kayit 1.1.0 sonrası PostgresException — 3 modül, 1483 hata | 4 | 1,483 | 1 | ✓ | ✓ | 0.4 s |
| `kayit-missing-column` | 3 | kayit 1.1.0 sonrası PostgresException — 3 modül, 1451 hata | 5 | 1,451 | 1 | ✓ | ✓ | 0.4 s |
| `odeme-bank-timeout` | 1 | odeme 1.1.0 sonrası TimeoutException — 66 hata | 1 | 66 | 1 | ✓ | ✓ | 2.4 s |
| `odeme-bank-timeout` | 2 | odeme 1.1.0 sonrası TimeoutException — 61 hata | 1 | 61 | 1 | ✓ | ✓ | 2.4 s |
| `odeme-bank-timeout` | 3 | odeme 1.1.0 sonrası TimeoutException — 50 hata | 1 | 50 | 1 | ✓ | ✓ | 2.5 s |
| `not-null-reference` | 1 | not 1.1.0 sonrası NullReferenceException — 105 hata | 1 | 105 | 1 | ✓ | ✓ | 2.4 s |
| `not-null-reference` | 2 | not 1.1.0 sonrası NullReferenceException — 114 hata | 1 | 114 | 1 | ✓ | ✓ | 2.5 s |
| `not-null-reference` | 3 | not 1.1.0 sonrası NullReferenceException — 129 hata | 1 | 129 | 1 | ✓ | ✓ | 2.4 s |
