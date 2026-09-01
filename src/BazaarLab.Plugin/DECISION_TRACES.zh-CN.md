# 决策界面轨迹采集 v1

版本：BazaarLab.Plugin v1.2.0

## 目标

采集器只观察和记录，不会点击按钮、选择卡牌、刷新商店或改变游戏状态。输出用于后续规则分析、标注和训练。

输出目录：

`BepInEx/config/BazaarLab/decision-traces/decision-trace-*.jsonl`

每行都是一个独立 JSON 对象。异常退出最多影响正在写入的一行，不会破坏此前记录。

## 记录类型

- `session_start` / `session_end`：一次插件运行的边界和版本信息。
- `surface`：稳定约 0.18 秒后的完整决策界面节点。
- `action`：客户端命令或游戏确认事件，包含动作类型、参数和来源节点。
- `transition`：连接前后节点，并给出 `open_child`、`refresh`、`same_surface_mutation`、`close` 等初步关系。

## 多级界面

父事件、商店合集、具体商店和刷新后的商品列表分别记录为节点。节点通过：

- `node_id`
- `parent_node_id`
- `depth`
- `transition.from_node_id / to_node_id`
- `transition.action_id`

连接。刷新或购买后仍停留在同一商店时保持相同深度；进入新的遭遇或 AppState 时初步标记为子界面。关系是启发式标签，训练前可以利用明确动作和前后状态再次校正。

## Surface 内容

- AppState、RunState 和识别出的界面类型；
- 当前父遭遇实例/模板 ID；
- 刷新价格与剩余次数；
- 当前允许的操作；
- SelectionContextRules 的可读字段；
- 所有选项的实例 ID、模板 ID、类型、品质、尺寸、附魔、标签和属性；
- 控制器类型、是否可见及卡牌屏幕中心坐标；
- 玩家天数、小时、英雄、属性、布阵、背包和技能。

## 已识别的初始界面类型

- `encounter_choice`
- `event_step_choice`
- `merchant_or_reroll`
- `skill_choice`
- `item_choice`
- `reward_choice`
- `level_up_reward`
- `loot_reward`
- `pedestal_target`
- `mixed_choice`
- `encounter_subscreen`

未知界面仍会完整记录为 `decision_surface`，不会丢弃。
