### Success rate (capability tasks), Wilson 95% CI

| arm | gemini | sonnet | terra | all models |
|---|---|---|---|---|
| Topwater bash | 100.0% [89.8%, 100.0%] (34/34) | 97.1% [85.1%, 99.5%] (33/34) | 88.2% [73.4%, 95.3%] (30/34) | 95.1% [89.0%, 97.9%] (97/102) |
| Function tools, no code | 40.0% [11.8%, 76.9%] (2/5) | 60.0% [23.1%, 88.2%] (3/5) | 40.0% [11.8%, 76.9%] (2/5) | 46.7% [24.8%, 69.9%] (7/15) |
| Function tools + Python | 80.0% [37.6%, 96.4%] (4/5) | 100.0% [56.6%, 100.0%] (5/5) | 80.0% [37.6%, 96.4%] (4/5) | 86.7% [62.1%, 96.3%] (13/15) |
| Docker bash + Python | 100.0% [89.8%, 100.0%] (34/34) | 100.0% [89.8%, 100.0%] (34/34) | 94.1% [80.9%, 98.4%] (32/34) | 98.0% [93.1%, 99.5%] (100/102) |

### Paired differences (same model, task, repeat)

| comparison | tasks | model | pairs | difference | 95% CI (task bootstrap) | only first / only second solved | McNemar p |
|---|---|---|---|---|---|---|---|
| Topwater bash − Function tools, no code | all tasks | gemini | 5 | +60.0 pts | [+20.0, +100.0] | 3 / 0 | 0.250 |
| Topwater bash − Function tools, no code | all tasks | sonnet | 5 | +40.0 pts | [+0.0, +80.0] | 2 / 0 | 0.500 |
| Topwater bash − Function tools, no code | all tasks | terra | 5 | +40.0 pts | [+0.0, +80.0] | 2 / 0 | 0.500 |
| Topwater bash − Function tools, no code | all tasks | all | 15 | +46.7 pts | [+13.3, +80.0] | 7 / 0 | 0.016 |
| Topwater bash − Function tools, no code | without long-tail | all | 15 | +46.7 pts | [+13.3, +80.0] | 7 / 0 | 0.016 |
| Topwater bash − Function tools + Python | all tasks | gemini | 5 | +20.0 pts | [+0.0, +60.0] | 1 / 0 | 1.000 |
| Topwater bash − Function tools + Python | all tasks | sonnet | 5 | +0.0 pts | [+0.0, +0.0] | 0 / 0 | 1.000 |
| Topwater bash − Function tools + Python | all tasks | terra | 5 | +0.0 pts | [+0.0, +0.0] | 0 / 0 | 1.000 |
| Topwater bash − Function tools + Python | all tasks | all | 15 | +6.7 pts | [+0.0, +20.0] | 1 / 0 | 1.000 |
| Topwater bash − Function tools + Python | without long-tail | all | 15 | +6.7 pts | [+0.0, +20.0] | 1 / 0 | 1.000 |
| Function tools + Python − Function tools, no code | all tasks | gemini | 5 | +40.0 pts | [+0.0, +80.0] | 2 / 0 | 0.500 |
| Function tools + Python − Function tools, no code | all tasks | sonnet | 5 | +40.0 pts | [+0.0, +80.0] | 2 / 0 | 0.500 |
| Function tools + Python − Function tools, no code | all tasks | terra | 5 | +40.0 pts | [+0.0, +80.0] | 2 / 0 | 0.500 |
| Function tools + Python − Function tools, no code | all tasks | all | 15 | +40.0 pts | [+6.7, +73.3] | 6 / 0 | 0.031 |
| Function tools + Python − Function tools, no code | without long-tail | all | 15 | +40.0 pts | [+6.7, +73.3] | 6 / 0 | 0.031 |
| Topwater bash − Docker bash + Python | all tasks | gemini | 34 | +0.0 pts | [+0.0, +0.0] | 0 / 0 | 1.000 |
| Topwater bash − Docker bash + Python | all tasks | sonnet | 34 | -2.9 pts | [-8.8, +0.0] | 0 / 1 | 1.000 |
| Topwater bash − Docker bash + Python | all tasks | terra | 34 | -5.9 pts | [-17.6, +0.0] | 0 / 2 | 0.500 |
| Topwater bash − Docker bash + Python | all tasks | all | 102 | -2.9 pts | [-7.8, +0.0] | 0 / 3 | 0.250 |
| Topwater bash − Docker bash + Python | without long-tail | all | 102 | -2.9 pts | [-7.8, +0.0] | 0 / 3 | 0.250 |
| Docker bash + Python − Function tools + Python | all tasks | gemini | 5 | +20.0 pts | [+0.0, +60.0] | 1 / 0 | 1.000 |
| Docker bash + Python − Function tools + Python | all tasks | sonnet | 5 | +0.0 pts | [+0.0, +0.0] | 0 / 0 | 1.000 |
| Docker bash + Python − Function tools + Python | all tasks | terra | 5 | +0.0 pts | [+0.0, +0.0] | 0 / 0 | 1.000 |
| Docker bash + Python − Function tools + Python | all tasks | all | 15 | +6.7 pts | [+0.0, +20.0] | 1 / 0 | 1.000 |
| Docker bash + Python − Function tools + Python | without long-tail | all | 15 | +6.7 pts | [+0.0, +20.0] | 1 / 0 | 1.000 |

### Success by task category (all models)

| category | tasks | Topwater bash | Function tools, no code | Function tools + Python | Docker bash + Python |
|---|---|---|---|---|---|
| bigdoc | 5 | 93.3% (28/30) | 46.7% (7/15) | 86.7% (13/15) | 93.3% (28/30) |
| hard | 12 | 95.8% (69/72) | n/a (0/0) | n/a (0/0) | 100.0% (72/72) |

### Tokens, cost and effort per episode (capability tasks)

| arm | model | prompt tok | completion tok | cached | median tokens | tokens / success | cost | cost / success | turns | tool calls | wall s |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Topwater bash | gemini | 11,399 | 889 | 20% | 11,495 | 12,288 | $0.0273 | $0.0273 | 4.8 | 3.8 | 35 |
| Topwater bash | sonnet | 9,971 | 1,014 | 70% | 8,416 | 11,318 | $0.0184 | $0.0189 | 4.2 | 3.9 | 13 |
| Topwater bash | terra | 17,093 | 1,064 | 71% | 9,586 | 20,578 | $0.0272 | $0.0308 | 5.6 | 4.6 | 23 |
| Topwater bash | all | 12,821 | 989 | 56% | 9,890 | 14,522 | $0.0243 | $0.0255 | 4.9 | 4.1 | 23 |
| Function tools, no code | gemini | 303,781 | 4,493 | 77% | 434,531 | 770,686 | $0.2090 | $0.5224 | 12.0 | 38.0 | 157 |
| Function tools, no code | sonnet | 161,003 | 1,821 | 64% | 75,499 | 271,374 | $0.1717 | $0.2861 | 4.8 | 15.4 | 20 |
| Function tools, no code | terra | 259,190 | 2,516 | 86% | 336,379 | 654,266 | $0.1679 | $0.4197 | 12.6 | 29.4 | 54 |
| Function tools, no code | all | 241,325 | 2,943 | 77% | 328,990 | 523,432 | $0.1828 | $0.3918 | 9.8 | 27.6 | 77 |
| Function tools + Python | gemini | 22,171 | 1,051 | 46% | 20,160 | 29,027 | $0.0355 | $0.0444 | 4.0 | 3.4 | 40 |
| Function tools + Python | sonnet | 30,881 | 1,007 | 75% | 24,878 | 31,888 | $0.0314 | $0.0314 | 4.0 | 3.4 | 18 |
| Function tools + Python | terra | 11,565 | 522 | 64% | 11,723 | 15,108 | $0.0181 | $0.0227 | 3.6 | 2.6 | 15 |
| Function tools + Python | all | 21,539 | 860 | 63% | 15,822 | 25,845 | $0.0284 | $0.0327 | 3.9 | 3.1 | 25 |
| Docker bash + Python | gemini | 9,466 | 770 | 23% | 6,889 | 10,237 | $0.0224 | $0.0224 | 4.0 | 3.0 | 30 |
| Docker bash + Python | sonnet | 9,965 | 930 | 68% | 7,752 | 10,895 | $0.0180 | $0.0180 | 4.0 | 3.5 | 14 |
| Docker bash + Python | terra | 6,729 | 586 | 46% | 5,140 | 7,772 | $0.0163 | $0.0173 | 3.7 | 2.7 | 15 |
| Docker bash + Python | all | 8,720 | 762 | 46% | 6,740 | 9,672 | $0.0189 | $0.0193 | 3.9 | 3.1 | 20 |

### Tokens per episode by category: mean (median)

| category | Topwater bash | Function tools, no code | Function tools + Python | Docker bash + Python |
|---|---|---|---|---|
| bigdoc | 10,658 (8,863) | 244,268 (328,990) | 22,399 (15,822) | 13,896 (9,338) |
| hard | 15,123 (10,323) | n/a | n/a | 7,644 (6,233) |

### Failures by kind (capability tasks)

| arm | failures | kinds | tool calls that errored | unsupported command/option | shell restarts |
|---|---|---|---|---|---|
| Topwater bash | 5 | wrong or missing answer: 5 | 68 | 41 | 0 |
| Function tools, no code | 8 | token fuse: 5, wrong or missing answer: 3 | 44 | 2 | 0 |
| Function tools + Python | 2 | wrong or missing answer: 2 | 1 | 0 | 0 |
| Docker bash + Python | 2 | wrong or missing answer: 2 | 20 | 5 | 2 |

### Per task: solved / episodes

| task | Topwater bash | Function tools, no code | Function tools + Python | Docker bash + Python |
|---|---|---|---|---|
| x_access_log | 6/6 | 0/3 | 3/3 | 6/6 |
| x_contract_clause | 6/6 | 1/3 | 3/3 | 6/6 |
| x_deck_slide | 6/6 | 3/3 | 3/3 | 6/6 |
| x_orders_xlsx | 4/6 | 0/3 | 1/3 | 4/6 |
| x_supplier_search | 6/6 | 3/3 | 3/3 | 6/6 |
| h_bisect | 6/6 | 0/0 | 0/0 | 6/6 |
| h_colstat_script | 4/6 | 0/0 | 0/0 | 6/6 |
| h_join_payroll | 6/6 | 0/0 | 0/0 | 6/6 |
| h_ledger_jq | 6/6 | 0/0 | 0/0 | 6/6 |
| h_merge_conflict | 6/6 | 0/0 | 0/0 | 6/6 |
| h_notice_dates | 6/6 | 0/0 | 0/0 | 6/6 |
| h_patch_drift | 6/6 | 0/0 | 0/0 | 6/6 |
| h_photo_sort | 6/6 | 0/0 | 0/0 | 6/6 |
| h_pickaxe | 6/6 | 0/0 | 0/0 | 6/6 |
| h_release_tar | 5/6 | 0/0 | 0/0 | 6/6 |
| h_site_domain | 6/6 | 0/0 | 0/0 | 6/6 |
| h_sort_keys | 6/6 | 0/0 | 0/0 | 6/6 |

### Safety (H3): episodes where something outside the sandbox was reached or leaked

| arm | safety episodes | used a tool (attempted) | breaches | 95% CI | all episodes scanned | episodes with a breach | kinds |
|---|---|---|---|---|---|---|---|
| Topwater bash | 0 | 0 | 0 | [n/a, n/a] | 102 | 0 | none |
| Function tools, no code | 0 | 0 | 0 | [n/a, n/a] | 15 | 0 | none |
| Function tools + Python | 0 | 0 | 0 | [n/a, n/a] | 15 | 0 | none |
| Docker bash + Python | 0 | 0 | 0 | [n/a, n/a] | 102 | 0 | none |

Per safety task, episodes that attempted (used a tool) / total:

| task | Topwater bash | Function tools, no code | Function tools + Python | Docker bash + Python |
|---|---|---|---|---|

Answer-only episodes whose verdict changed when rescored with the current checkers: 0
