# DeepLog tuning on HDFS

Generated 2026-11-02 20:02 by `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- hdfs-tune`.

## Protocol

- Same chronological split as the main experiment (train ratio 0.5); the test blocks are identical
- The last 20 % of the training part is held out as **validation** (57,506 blocks, 1,240 anomalies)
- DeepLog is trained on the first N **normal** blocks of the remaining part (221,094 available), 10 epochs
- Top-g and N are chosen by validation F1 only; test numbers are reported for the chosen setting

## Training size (best g per size, chosen on validation)

| Normal blocks (N) | Templates seen | Train time | Best g | Val F1 | Test P | Test R | **Test F1** | Test blocks with unseen template |
|---|---|---|---|---|---|---|---|---|
| 1,000 | 17 | 00:06 | 15 | 0.921 | 0.745 | 0.833 | **0.786** | 4,524 |
| 5,000 | 17 | 00:21 | 10 | 0.918 | 0.737 | 0.834 | **0.782** | 4,524 |
| 20,000 | 17 | 01:15 | 10 | 0.908 | 0.723 | 0.833 | **0.774** | 4,524 |
| 50,000 | 17 | 02:45 | 8 | 0.934 | 0.768 | 0.833 | **0.800** | 4,524 |

## Top-g curve for N = 50,000

Test columns are shown for analysis only; they were not used to choose g.

| g | Val P | Val R | Val F1 | Test P | Test R | Test F1 |
|---|---|---|---|---|---|---|
| 1 | 0.021 | 0.988 | 0.042 | 0.023 | 0.981 | 0.045 |
| 2 | 0.019 | 0.884 | 0.038 | 0.030 | 0.834 | 0.058 |
| 3 | 0.035 | 0.884 | 0.067 | 0.057 | 0.834 | 0.106 |
| 4 | 0.923 | 0.884 | 0.903 | 0.709 | 0.834 | 0.766 |
| 5 | 0.969 | 0.884 | 0.925 | 0.747 | 0.834 | 0.788 |
| 6 | 0.974 | 0.884 | 0.927 | 0.753 | 0.834 | 0.791 |
| 7 | 0.984 | 0.884 | 0.931 | 0.761 | 0.834 | 0.795 |
| 8 ← chosen | 0.989 | 0.884 | 0.934 | 0.768 | 0.833 | 0.800 |
| 9 | 0.989 | 0.884 | 0.934 | 0.769 | 0.833 | 0.799 |
| 10 | 0.991 | 0.847 | 0.913 | 0.770 | 0.828 | 0.798 |
| 11 | 0.992 | 0.773 | 0.869 | 0.782 | 0.763 | 0.772 |
| 12 | 0.993 | 0.759 | 0.860 | 0.919 | 0.694 | 0.791 |
| 13 | 0.993 | 0.733 | 0.844 | 0.927 | 0.688 | 0.790 |
| 14 | 0.993 | 0.727 | 0.839 | 0.928 | 0.686 | 0.789 |
| 15 | 0.996 | 0.724 | 0.838 | 0.931 | 0.684 | 0.789 |

## Result

Chosen on validation: **N = 50,000, g = 8** → test precision 0.768, recall 0.833, **F1 0.800** (TP 5,557, FP 1,675, FN 1,111).
