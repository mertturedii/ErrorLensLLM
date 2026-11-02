# Drain parsing accuracy on HDFS

Generated 2026-11-02 20:53 by `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- hdfs-parse`.

- Ground truth: LogHub `Event_traces.csv` (29 manually labeled events), aligned with the parsed blocks position by position
- Grouping accuracy (Zhu et al., 2019): a message is correct only if its template groups exactly the messages of its true event; computed over block traces, so a message that mentions two blocks counts twice
- Similarity modes: **Original** = identical constant tokens / all tokens (Drain paper); **ConstantTokens** = identical constant tokens / constant tokens of the template (variables ignored)
- Path mask: also replace file paths (`/mnt/...`) with the wildcard; not part of the LogHub masks
- A run stops as *diverged* when Drain exceeds 2,000 templates (HDFS has 29 true events)
- Detection columns: test F1 with the main experiment's settings (chronological 0.5 split, IF contamination 0.03, DeepLog 5,000 blocks, g = 9)

| Mode | Path mask | Similarity | Templates | Grouping accuracy | Merged templates | Split events | IF F1 | DeepLog F1 |
|---|---|---|---|---|---|---|---|---|
| Original | no | 0.5 | 48 | **0.846** | 1 | 5 | 0.585 | 0.774 |
| Original | no | 0.6 | > 2,000 | diverged after 3,835 lines | – | – | – | – |
| Original | no | 0.7 | > 2,000 | diverged after 2,548 lines | – | – | – | – |
| Original | no | 0.8 | > 2,000 | diverged after 2,000 lines | – | – | – | – |
| Original | no | 0.9 | > 2,000 | diverged after 2,000 lines | – | – | – | – |
| ConstantTokens | no | 0.5 | 47 | **0.846** | 1 | 5 | 0.585 | 0.774 |
| ConstantTokens | no | 0.6 | 47 | **0.846** | 1 | 5 | 0.585 | 0.774 |
| ConstantTokens | no | 0.7 | 111 | **0.794** | 1 | 6 | 0.687 | 0.045 |
| ConstantTokens | no | 0.8 | 178 | **0.822** | 0 | 7 | 0.628 | 0.045 |
| ConstantTokens | no | 0.9 | 179 | **0.822** | 0 | 7 | 0.628 | 0.045 |
| Original | yes | 0.5 | 48 | **0.846** | 1 | 5 | 0.585 | 0.774 |
| Original | yes | 0.6 | > 2,000 | diverged after 2,053 lines | – | – | – | – |
| Original | yes | 0.7 | > 2,000 | diverged after 2,020 lines | – | – | – | – |
| Original | yes | 0.8 | > 2,000 | diverged after 2,000 lines | – | – | – | – |
| Original | yes | 0.9 | > 2,000 | diverged after 2,000 lines | – | – | – | – |
| ConstantTokens | yes | 0.5 | 47 | **0.846** | 1 | 5 | 0.585 | 0.774 |
| ConstantTokens | yes | 0.6 | 47 | **0.846** | 1 | 5 | 0.585 | 0.774 |
| ConstantTokens | yes | 0.7 | 49 | **0.846** | 1 | 5 | 0.585 | 0.774 |
| ConstantTokens | yes | 0.8 | 52 | **0.998** | 0 | 5 | 0.585 | 0.774 |
| ConstantTokens | yes | 0.9 | 53 | **0.998** | 0 | 5 | 0.585 | 0.774 |

## Errors per threshold

### Original ≥ 0.5

- **Merged** `PacketResponder <*> for block <*> <*>` ← E11 `[*]PacketResponder[*]for block[*]terminating[*]`, E8 `[*]PacketResponder[*]for block[*]Interrupted[*]`
- **Split** E18 `[*]Starting thread to transfer block[*]to[*]` → 2 templates
- **Split** E25 `[*]BLOCK* ask[*]to replicate[*]to[*]` → 2 templates
- **Split** E7 `[*]writeBlock[*]received exception[*]` → 8 templates
- **Split** E10 `[*]PacketResponder[*]Exception[*]` → 8 templates
- **Split** E14 `[*]Exception in receiveBlock for block[*]` → 5 templates

### ConstantTokens ≥ 0.5

- **Merged** `PacketResponder <*> for block <*> <*>` ← E11 `[*]PacketResponder[*]for block[*]terminating[*]`, E8 `[*]PacketResponder[*]for block[*]Interrupted[*]`
- **Split** E18 `[*]Starting thread to transfer block[*]to[*]` → 2 templates
- **Split** E25 `[*]BLOCK* ask[*]to replicate[*]to[*]` → 2 templates
- **Split** E7 `[*]writeBlock[*]received exception[*]` → 8 templates
- **Split** E10 `[*]PacketResponder[*]Exception[*]` → 7 templates
- **Split** E14 `[*]Exception in receiveBlock for block[*]` → 5 templates

### ConstantTokens ≥ 0.6

- **Merged** `PacketResponder <*> for block <*> <*>` ← E11 `[*]PacketResponder[*]for block[*]terminating[*]`, E8 `[*]PacketResponder[*]for block[*]Interrupted[*]`
- **Split** E18 `[*]Starting thread to transfer block[*]to[*]` → 2 templates
- **Split** E25 `[*]BLOCK* ask[*]to replicate[*]to[*]` → 2 templates
- **Split** E7 `[*]writeBlock[*]received exception[*]` → 8 templates
- **Split** E10 `[*]PacketResponder[*]Exception[*]` → 7 templates
- **Split** E14 `[*]Exception in receiveBlock for block[*]` → 5 templates

### ConstantTokens ≥ 0.7

- **Merged** `PacketResponder <*> for block <*> <*>` ← E11 `[*]PacketResponder[*]for block[*]terminating[*]`, E8 `[*]PacketResponder[*]for block[*]Interrupted[*]`
- **Split** E22 `[*]BLOCK* NameSystem[*]allocateBlock:[*]` → 63 templates
- **Split** E18 `[*]Starting thread to transfer block[*]to[*]` → 2 templates
- **Split** E25 `[*]BLOCK* ask[*]to replicate[*]to[*]` → 2 templates
- **Split** E7 `[*]writeBlock[*]received exception[*]` → 9 templates
- **Split** E10 `[*]PacketResponder[*]Exception[*]` → 8 templates
- **Split** E14 `[*]Exception in receiveBlock for block[*]` → 5 templates

### ConstantTokens ≥ 0.8

- **Split** E22 `[*]BLOCK* NameSystem[*]allocateBlock:[*]` → 63 templates
- **Split** E18 `[*]Starting thread to transfer block[*]to[*]` → 2 templates
- **Split** E25 `[*]BLOCK* ask[*]to replicate[*]to[*]` → 2 templates
- **Split** E21 `[*]Deleting block[*]file[*]` → 65 templates
- **Split** E7 `[*]writeBlock[*]received exception[*]` → 11 templates
- **Split** E10 `[*]PacketResponder[*]Exception[*]` → 8 templates
- **Split** E14 `[*]Exception in receiveBlock for block[*]` → 5 templates

### ConstantTokens ≥ 0.9

- **Split** E22 `[*]BLOCK* NameSystem[*]allocateBlock:[*]` → 63 templates
- **Split** E18 `[*]Starting thread to transfer block[*]to[*]` → 2 templates
- **Split** E25 `[*]BLOCK* ask[*]to replicate[*]to[*]` → 2 templates
- **Split** E21 `[*]Deleting block[*]file[*]` → 65 templates
- **Split** E7 `[*]writeBlock[*]received exception[*]` → 11 templates
- **Split** E10 `[*]PacketResponder[*]Exception[*]` → 8 templates
- **Split** E14 `[*]Exception in receiveBlock for block[*]` → 6 templates

### Original ≥ 0.5 + path mask

- **Merged** `PacketResponder <*> for block <*> <*>` ← E11 `[*]PacketResponder[*]for block[*]terminating[*]`, E8 `[*]PacketResponder[*]for block[*]Interrupted[*]`
- **Split** E18 `[*]Starting thread to transfer block[*]to[*]` → 2 templates
- **Split** E25 `[*]BLOCK* ask[*]to replicate[*]to[*]` → 2 templates
- **Split** E7 `[*]writeBlock[*]received exception[*]` → 8 templates
- **Split** E10 `[*]PacketResponder[*]Exception[*]` → 8 templates
- **Split** E14 `[*]Exception in receiveBlock for block[*]` → 5 templates

### ConstantTokens ≥ 0.5 + path mask

- **Merged** `PacketResponder <*> for block <*> <*>` ← E11 `[*]PacketResponder[*]for block[*]terminating[*]`, E8 `[*]PacketResponder[*]for block[*]Interrupted[*]`
- **Split** E18 `[*]Starting thread to transfer block[*]to[*]` → 2 templates
- **Split** E25 `[*]BLOCK* ask[*]to replicate[*]to[*]` → 2 templates
- **Split** E7 `[*]writeBlock[*]received exception[*]` → 8 templates
- **Split** E10 `[*]PacketResponder[*]Exception[*]` → 7 templates
- **Split** E14 `[*]Exception in receiveBlock for block[*]` → 5 templates

### ConstantTokens ≥ 0.6 + path mask

- **Merged** `PacketResponder <*> for block <*> <*>` ← E11 `[*]PacketResponder[*]for block[*]terminating[*]`, E8 `[*]PacketResponder[*]for block[*]Interrupted[*]`
- **Split** E18 `[*]Starting thread to transfer block[*]to[*]` → 2 templates
- **Split** E25 `[*]BLOCK* ask[*]to replicate[*]to[*]` → 2 templates
- **Split** E7 `[*]writeBlock[*]received exception[*]` → 8 templates
- **Split** E10 `[*]PacketResponder[*]Exception[*]` → 7 templates
- **Split** E14 `[*]Exception in receiveBlock for block[*]` → 5 templates

### ConstantTokens ≥ 0.7 + path mask

- **Merged** `PacketResponder <*> for block <*> <*>` ← E11 `[*]PacketResponder[*]for block[*]terminating[*]`, E8 `[*]PacketResponder[*]for block[*]Interrupted[*]`
- **Split** E18 `[*]Starting thread to transfer block[*]to[*]` → 2 templates
- **Split** E25 `[*]BLOCK* ask[*]to replicate[*]to[*]` → 2 templates
- **Split** E7 `[*]writeBlock[*]received exception[*]` → 9 templates
- **Split** E10 `[*]PacketResponder[*]Exception[*]` → 8 templates
- **Split** E14 `[*]Exception in receiveBlock for block[*]` → 5 templates

### ConstantTokens ≥ 0.8 + path mask

- **Split** E18 `[*]Starting thread to transfer block[*]to[*]` → 2 templates
- **Split** E25 `[*]BLOCK* ask[*]to replicate[*]to[*]` → 2 templates
- **Split** E7 `[*]writeBlock[*]received exception[*]` → 11 templates
- **Split** E10 `[*]PacketResponder[*]Exception[*]` → 8 templates
- **Split** E14 `[*]Exception in receiveBlock for block[*]` → 5 templates

### ConstantTokens ≥ 0.9 + path mask

- **Split** E18 `[*]Starting thread to transfer block[*]to[*]` → 2 templates
- **Split** E25 `[*]BLOCK* ask[*]to replicate[*]to[*]` → 2 templates
- **Split** E7 `[*]writeBlock[*]received exception[*]` → 11 templates
- **Split** E10 `[*]PacketResponder[*]Exception[*]` → 8 templates
- **Split** E14 `[*]Exception in receiveBlock for block[*]` → 6 templates

