# Fault injection: root cause suggestions

Generated 2026-11-12 23:44 by `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- faults`.

## Protocol

Each run starts from an empty database and the demo (kayit, not, odeme and a traffic generator) at version 1.0.0. Then the scenario's steps run: normal traffic, releases, faults. The incident analyzer runs every 2 s. Each scenario is repeated 3 times. **Basic** scenarios start a continuously triggered fault right after its release. **Hard** scenarios target known weak spots of the correlator.

| Scenario | Kind | What happens | True root cause |
|---|---|---|---|
| `odeme-bank-outage` | hard | the bank goes down while odeme runs; no release is involved | odeme · TimeoutException · Timeout · no release |
| `kayit-delayed-fault` | hard | kayit 1.1.0 fails only 90 s after release; a healthy not 1.0.1 is released in between | kayit · PostgresException · DatabaseSchema · kayit 1.1.0 |
| `concurrent-releases` | hard | faulty kayit 1.1.0 and a healthy odeme 1.0.1 are released 5 s apart | kayit · PostgresException · DatabaseSchema · kayit 1.1.0 |
| `odeme-logged-error` | hard | odeme 1.1.0 logs bank rejections with LogError and returns 502, throwing nothing | odeme · (logged error, no exception) · Dependency · odeme 1.1.0 |

Measures, on the largest incident that started after the fault became active:
- **Top-1 / Top-3:** the true root cause (module + exception type) is ranked first / within the first three candidates
- **Category:** the incident's category matches the fault
- **Deployment:** the suspected deployment is the faulty release, or there is none when no release caused the fault
- **Incidents per fault:** incidents that started once the fault was active (ideal: 1)
- **Other incidents:** incidents before the fault, e.g. errors while a healthy release restarts a service
- **Detection time:** from the fault becoming active to the incident first being stored

## Results

| Scenario | Top-1 | Top-3 | Category | Deployment | Incidents per fault | Other incidents | Median detection |
|---|---|---|---|---|---|---|---|
| `odeme-bank-outage` | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 0 % (0/3) | 1.0 | 0 | 2.8 s |
| `kayit-delayed-fault` | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 0 % (0/3) | 1.0 | 3 | 0.5 s |
| `concurrent-releases` | 0 % (0/3) | 100 % (3/3) | 0 % (0/3) | 0 % (0/3) | 1.0 | 0 | 0.5 s |
| `odeme-logged-error` | 0 % (0/3) | 0 % (0/3) | 0 % (0/3) | 0 % (0/3) | 0.0 | 0 | – |
| **Hard** | 50 % (6/12) | 75 % (9/12) | 50 % (6/12) | 0 % (0/12) | 0.8 | 3 | 0.5 s |

## Runs

| Scenario | # | Incident | Groups | Errors | True cause rank | Category | Suspected deployment | Detected after |
|---|---|---|---|---|---|---|---|---|
| `odeme-bank-outage` | 1 | traffic 1.0.0 sonrası TimeoutException — 83 hata | 1 | 83 | 1 | ✓ | traffic 1.0.0 ✗ | 2.8 s |
| `odeme-bank-outage` | 2 | traffic 1.0.0 sonrası TimeoutException — 69 hata | 1 | 69 | 1 | ✓ | traffic 1.0.0 ✗ | 2.9 s |
| `odeme-bank-outage` | 3 | traffic 1.0.0 sonrası TimeoutException — 60 hata | 1 | 60 | 1 | ✓ | traffic 1.0.0 ✗ | 2.8 s |
| `kayit-delayed-fault` | 1 | not 1.0.1 sonrası PostgresException — 3 modül, 1461 hata | 3 | 1,461 | 1 | ✓ | not 1.0.1 ✗ | 0.5 s |
| `kayit-delayed-fault` | 2 | not 1.0.1 sonrası PostgresException — 3 modül, 1495 hata | 3 | 1,495 | 1 | ✓ | not 1.0.1 ✗ | 0.5 s |
| `kayit-delayed-fault` | 3 | not 1.0.1 sonrası PostgresException — 3 modül, 1487 hata | 3 | 1,487 | 1 | ✓ | not 1.0.1 ✗ | 0.5 s |
| `concurrent-releases` | 1 | odeme 1.0.1 sonrası HttpRequestException — 3 modül, 1681 hata | 5 | 1,681 | 2 | ✗ | odeme 1.0.1 ✗ | 0.5 s |
| `concurrent-releases` | 2 | odeme 1.0.1 sonrası HttpRequestException — 3 modül, 1647 hata | 5 | 1,647 | 2 | ✗ | odeme 1.0.1 ✗ | 0.5 s |
| `concurrent-releases` | 3 | odeme 1.0.1 sonrası HttpRequestException — 3 modül, 1682 hata | 5 | 1,682 | 2 | ✗ | odeme 1.0.1 ✗ | 0.4 s |
| `odeme-logged-error` | 1 | no incident | 0 | 0 | – | ✗ | none ✗ | – |
| `odeme-logged-error` | 2 | no incident | 0 | 0 | – | ✗ | none ✗ | – |
| `odeme-logged-error` | 3 | no incident | 0 | 0 | – | ✗ | none ✗ | – |
