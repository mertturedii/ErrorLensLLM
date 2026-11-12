# Fault injection: root cause suggestions

Generated 2026-11-13 00:15 by `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- faults`.

## Protocol

Each run starts from an empty database and the demo (kayit, not, odeme and a traffic generator) at version 1.0.0. Then the scenario's steps run: normal traffic, releases, faults. The incident analyzer runs every 2 s. Each scenario is repeated 3 times. **Basic** scenarios start a continuously triggered fault right after its release. **Hard** scenarios target known weak spots of the correlator.

| Scenario | Kind | What happens | True root cause |
|---|---|---|---|
| `kayit-missing-column` | basic | kayit 1.1.0 reads a column whose migration never ran; not and odeme call kayit | kayit · PostgresException · DatabaseSchema · kayit 1.1.0 |
| `odeme-bank-timeout` | basic | odeme 1.1.0: the bank stops answering in time for 40 % of payments | odeme · TimeoutException · Timeout · odeme 1.1.0 |
| `not-null-reference` | basic | not 1.1.0 dereferences a missing elective course | not · NullReferenceException · CodeDefect · not 1.1.0 |
| `odeme-bank-outage` | hard | the bank goes down while odeme runs; no release is involved | odeme · TimeoutException · Timeout · no release |
| `kayit-delayed-fault` | hard | kayit 1.1.0 fails only 90 s after release; a healthy not 1.0.1 is released in between | kayit · PostgresException · DatabaseSchema · kayit 1.1.0 |
| `concurrent-releases` | hard | faulty kayit 1.1.0 and a healthy odeme 1.0.1 are released 5 s apart | kayit · PostgresException · DatabaseSchema · kayit 1.1.0 |
| `odeme-logged-error` | hard | odeme 1.1.0 logs bank rejections with LogError and returns 502, throwing nothing | odeme · LoggedError · Dependency · odeme 1.1.0 |

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
| `kayit-missing-column` | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 1.0 | 0 | 0.5 s |
| `odeme-bank-timeout` | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 1.0 | 0 | 2.5 s |
| `not-null-reference` | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 1.0 | 0 | 2.5 s |
| `odeme-bank-outage` | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 1.0 | 0 | 2.8 s |
| `kayit-delayed-fault` | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 1.0 | 3 | 0.4 s |
| `concurrent-releases` | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 100 % (3/3) | 1.0 | 0 | 0.4 s |
| `odeme-logged-error` | 100 % (3/3) | 100 % (3/3) | 0 % (0/3) | 100 % (3/3) | 1.0 | 0 | 2.4 s |
| **Basic** | 100 % (9/9) | 100 % (9/9) | 100 % (9/9) | 100 % (9/9) | 1.0 | 0 | 2.4 s |
| **Hard** | 100 % (12/12) | 100 % (12/12) | 75 % (9/12) | 100 % (12/12) | 1.0 | 3 | 2.4 s |

## Runs

| Scenario | # | Incident | Groups | Errors | True cause rank | Category | Suspected deployment | Detected after |
|---|---|---|---|---|---|---|---|---|
| `kayit-missing-column` | 1 | kayit 1.1.0 sonrası PostgresException — 3 modül, 1484 hata | 5 | 1,484 | 1 | ✓ | kayit 1.1.0 ✓ | 0.5 s |
| `kayit-missing-column` | 2 | kayit 1.1.0 sonrası PostgresException — 3 modül, 1469 hata | 5 | 1,469 | 1 | ✓ | kayit 1.1.0 ✓ | 0.4 s |
| `kayit-missing-column` | 3 | kayit 1.1.0 sonrası PostgresException — 3 modül, 1492 hata | 5 | 1,492 | 1 | ✓ | kayit 1.1.0 ✓ | 0.5 s |
| `odeme-bank-timeout` | 1 | odeme 1.1.0 sonrası TimeoutException — 60 hata | 1 | 60 | 1 | ✓ | odeme 1.1.0 ✓ | 2.5 s |
| `odeme-bank-timeout` | 2 | odeme 1.1.0 sonrası TimeoutException — 65 hata | 1 | 65 | 1 | ✓ | odeme 1.1.0 ✓ | 2.5 s |
| `odeme-bank-timeout` | 3 | odeme 1.1.0 sonrası TimeoutException — 72 hata | 1 | 72 | 1 | ✓ | odeme 1.1.0 ✓ | 2.4 s |
| `not-null-reference` | 1 | not 1.1.0 sonrası NullReferenceException — 122 hata | 1 | 122 | 1 | ✓ | not 1.1.0 ✓ | 2.4 s |
| `not-null-reference` | 2 | not 1.1.0 sonrası NullReferenceException — 118 hata | 1 | 118 | 1 | ✓ | not 1.1.0 ✓ | 2.5 s |
| `not-null-reference` | 3 | not 1.1.0 sonrası NullReferenceException — 107 hata | 1 | 107 | 1 | ✓ | not 1.1.0 ✓ | 2.6 s |
| `odeme-bank-outage` | 1 | odeme: TimeoutException — 58 hata | 1 | 58 | 1 | ✓ | none ✓ | 4.8 s |
| `odeme-bank-outage` | 2 | odeme: TimeoutException — 72 hata | 1 | 72 | 1 | ✓ | none ✓ | 2.8 s |
| `odeme-bank-outage` | 3 | odeme: TimeoutException — 51 hata | 1 | 51 | 1 | ✓ | none ✓ | 2.8 s |
| `kayit-delayed-fault` | 1 | kayit 1.1.0 sonrası PostgresException — 3 modül, 1458 hata | 3 | 1,458 | 1 | ✓ | kayit 1.1.0 ✓ | 0.5 s |
| `kayit-delayed-fault` | 2 | kayit 1.1.0 sonrası PostgresException — 3 modül, 1475 hata | 3 | 1,475 | 1 | ✓ | kayit 1.1.0 ✓ | 0.4 s |
| `kayit-delayed-fault` | 3 | kayit 1.1.0 sonrası PostgresException — 3 modül, 1452 hata | 3 | 1,452 | 1 | ✓ | kayit 1.1.0 ✓ | 0.4 s |
| `concurrent-releases` | 1 | kayit 1.1.0 sonrası PostgresException — 3 modül, 1625 hata | 5 | 1,625 | 1 | ✓ | kayit 1.1.0 ✓ | 0.4 s |
| `concurrent-releases` | 2 | kayit 1.1.0 sonrası PostgresException — 3 modül, 1643 hata | 5 | 1,643 | 1 | ✓ | kayit 1.1.0 ✓ | 0.4 s |
| `concurrent-releases` | 3 | kayit 1.1.0 sonrası PostgresException — 3 modül, 1684 hata | 4 | 1,684 | 1 | ✓ | kayit 1.1.0 ✓ | 0.5 s |
| `odeme-logged-error` | 1 | odeme 1.1.0 sonrası LoggedError — 81 hata | 1 | 81 | 1 | ✗ | odeme 1.1.0 ✓ | 2.4 s |
| `odeme-logged-error` | 2 | odeme 1.1.0 sonrası LoggedError — 65 hata | 1 | 65 | 1 | ✗ | odeme 1.1.0 ✓ | 2.4 s |
| `odeme-logged-error` | 3 | odeme 1.1.0 sonrası LoggedError — 55 hata | 1 | 55 | 1 | ✗ | odeme 1.1.0 ✓ | 2.5 s |
