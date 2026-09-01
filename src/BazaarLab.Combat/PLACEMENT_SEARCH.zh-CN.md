# 本地摆位与背包替换搜索

`PlacementOptimizer` 在 BazaarLab 战斗核心上实现摆位搜索，并默认考虑整个玩家背包。

搜索器作为独立程序集发布，并与当前版本的 BazaarLab 战斗核心共同构建。

## 已实现

- 当前棋盘重新排列，支持小/中/大型物品和棋盘空格。
- 从 `Stash` 选择物品换上场，并返回完整移入/移出差集。
- `ReplaceableItemInstanceIds`、`StashCandidateInstanceIds` 和精确位置 `PinnedItems`。
- 默认采用等格数替换；将 `RequireEqualSwapSpan` 设为 `false` 可搜索未填满棋盘。
- 小搜索域精确枚举；大搜索域采用库存粗筛、确定性种子排列、beam/local 邻域、候选 racing 和独立验证。
- 固定 master seed 和 run-index 前缀；相同输入与选项产生相同推荐。
- 始终让当前棋盘进入发现和最终验证，避免噪声候选未经对照就替换现有阵容。
- 输出搜索模式、覆盖量、候选/验证模拟次数、缓存命中和各阶段耗时。

## CLI

```powershell
dotnet src/BazaarLab.PlacementSearch/bin/Release/net8.0/BazaarLab.PlacementSearch.dll `
  path/to/official-cards.jsonl `
  snapshot.json `
  placement-result.json `
  options.json
```

`BepInEx/config/BazaarLab/live-inventory.json`

该文件包含玩家实时 `Hand`、`Stash` 和 `Skills`。常规摆位使用 10 秒无限血白板
目标，不要求对手棋盘可见。

## 默认预算

- screening：1 seed
- discovery：3 seeds
- validation：11 seeds
- 最多保留 32 套库存、评估 32 个摆位
- beam width：4，最多 3 轮
- 最终验证候选：2（其中始终包含当前棋盘）

这些默认值针对当前托管版模拟器，所有预算都可以通过 `PlacementSearchOptions` 调整。
