# 本地摆位与背包替换搜索

`PlacementOptimizer` 在 BazaarLab 战斗核心上实现摆位搜索，并默认考虑整个玩家背包。

搜索器作为独立程序集发布，并与当前版本的 BazaarLab 战斗核心共同构建。

## 已实现

- 当前棋盘重新排列，支持小/中/大型物品和棋盘空格。
- 从 `Stash` 选择物品换上场，并返回完整移入/移出差集。
- `ReplaceableItemInstanceIds`、`StashCandidateInstanceIds` 和精确位置 `PinnedItems`。
- 默认采用等格数替换；将 `RequireEqualSwapSpan` 设为 `false` 可搜索未填满棋盘。
- 小搜索域精确枚举；可枚举的大搜索域先确定性打乱，再无放回抽取，避免重复浪费模拟预算。
- 超大搜索域采用库存粗筛、确定性种子排列和 beam/local 邻域；随后进行候选竞速、半决筛选和独立终验。
- 固定 master seed 和 run-index 前缀；相同输入与选项产生相同推荐。
- 始终让当前棋盘进入发现和最终验证，避免噪声候选未经对照就替换现有阵容。
- 发现阶段使用最多 8 路并行模拟；插件显示准备、发现、复筛、终验的实时进度。
- 输出搜索模式、覆盖量、候选/验证模拟次数、缓存命中和各阶段耗时。

## CLI

```powershell
dotnet src/BazaarLab.PlacementSearch/bin/Release/net8.0/BazaarLab.PlacementSearch.dll `
  path/to/official-cards.jsonl `
  snapshot.json `
  placement-result.json `
  options.json
```

`BepInEx/config/BazaarLab/state/live-inventory.json`

该文件包含玩家实时 `Hand`、`Stash` 和 `Skills`。常规摆位使用 10 秒无限血白板
目标，不要求对手棋盘可见。这里的 10 秒是单场评分时间轴，不是搜索超时。

## 默认预算

- screening：1 seed
- discovery：3 seeds
- racing：最多 64 个候选补足至 3 seeds
- semifinal：最多 16 个候选补足至 11 seeds
- validation：领先候选与当前阵容使用 50 个共同 seeds 终验
- 最多保留 32 套库存、评估 10000 个摆位；最多精确生成 50000 个排列
- beam width：16，最多 4 轮
- 最终验证名单包含前两名，并强制加入当前棋盘作为基准
- 发现阶段预算约 3 秒，为竞速、半决、最多三个候选的 50 样本终验及进程启动预留余量；
  插件进程硬上限 15 秒

这些默认值移植自 LookingIN 的分阶段搜索思路，并针对 BazaarLab 当前托管版模拟器调整；
所有预算都可以通过 `PlacementSearchOptions` 调整。
