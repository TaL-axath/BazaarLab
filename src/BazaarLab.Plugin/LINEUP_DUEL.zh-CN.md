# 阵容码、本地对战与原生回放（v1.4.4）

## 阵容码

阵容码以 `LIL1:` 开头，内容是 Deflate 压缩后的版本化 JSON，再使用
Base64URL 编码。码内保存英雄属性、场上物品、技能、槽位、品质、附魔、标签、
卡牌运行时属性、游戏版本和本地卡表指纹，并用稳定校验值检测损坏或篡改。

导入器限制代码长度、解压后大小、卡牌数量、模板 GUID、槽位跨度和重叠情况。
旧版卡表指纹不一致时仍兼容模拟，但 UI 会显示 `catalog differs` 警告；这种结果
只能作为参考。新版 SHA-256 规则集不允许静默混算。

v1.0.6 起，新导出的阵容码使用完整卡表 SHA-256 作为规则集标识。插件会优先从
`BepInEx/config/BazaarLab/catalogs/<ruleset>/` 选择对应的历史卡表；双方规则集不同
或本地缺少对应卡表时会停止对战，避免把旧数值和新效果混合计算。旧版指纹格式
仍按当前卡表兼容运行，并明确作为参考结果。

## 任意状态导出规则

- 普通流程：直接读取当前玩家阵容；拖拽、移动和服务器响应期间暂不取样。
- 阵容连续稳定 0.5 秒后：写入 `state/last-stable-lineup.json` 和
  `state/last-stable-lineup.code.txt`。
- 战斗中：优先导出战斗开始时冻结的玩家阵容，不导出被战斗临时效果污染的状态。
- 主菜单、加载界面或当前 Run 不可用时：导出上一次持久化的稳定阵容。
- `OPPONENT -> B`：导出最近一次开战时冻结的对方阵容。

缓存位于 `BepInEx/config/BazaarLab/state/`。因此退出一局或重启游戏后，
仍能导出最近一次稳定阵容。

标题栏的“打开历史目录”按钮在展开和最小化状态下都常驻可用，会直接打开
`BepInEx/config/BazaarLab/pvp-lineups/`。其中按本轮游戏和天数保存玩家、对手阵容码。

## 使用

展开左上角 `Local Lineup Duel`：

- 标题栏的 `COPY LINEUP` 在展开和最小化状态下都可用。它会把当前阵容直接写入
  系统剪贴板，并弹出短时确认提示；战斗中优先导出开战时冻结阵容，其余状态优先
  导出实时阵容，无法读取时回退到最近稳定缓存。
- v1.4.4 会把历史快照和旧阵容码中的英雄别名 `TheDragons` / `The Dragons`
  规范化为原生枚举 ID `Hero8`。旧码会在校验原始内容后自动迁移，因此无需重导。

1. 用 `CURRENT -> A/B` 填入当前（或缓存）阵容，或粘贴两个 `LIL1` 阵容码。
2. 可用 `OPPONENT -> B` 取最近一次战斗对手，`SWAP` 交换先后手。
3. 设置整数种子，点击 `CONFIRM & PLAY`。
4. 插件执行一场确定性本地战斗，生成逐帧消息，然后调用 BPP 的原生回放运行时。
5. BPP 接受后会进入与历史战斗回放相同的 ReplayState；播放结束后使用 BPP 的继续按钮退出。

模拟输入和结果保存在 `BepInEx/config/BazaarLab/local-duels/`，便于
复现和排错。`latest-native-replay.json` 记录最近一次原生回放所使用的阵容输入、
轨迹、模板和帧数。原生回放复用本机最新一份 BPP 保存回放作为场景外壳，并替换
双方英雄、卡牌快照、开场属性和全部 `NetMessageCombatSim` 帧；至少需要先让 BPP
保存过一份真实回放。若 BPP 拒绝当前状态下启动，请返回主菜单后重试。

v1.4.1 起，回放事件保留本地规则执行时的真实 AbilityId、ActionType、
ExecutionContext、TriggerSource 和卡牌配置中的 VFXOverrideKey，并同时生成
EffectTriggered/EffectExecuted。未被本地核心实现的规则仍不会产生对应动画。

v1.4.2 起，回放额外写入每个战斗帧发生变化的卡牌运行时属性。Cooldown 包括
普通倒计时、加速、减速、冻结、充能和使用后的重置，因此原生卡牌 CD 遮罩会
随战斗运行；Haste、Slow、Freeze 及动态伤害/治疗/护盾数值也会同步。生命调整
同时保留 Damage、Burn、Poison、Heal、Regen、Shield 和暴击语义，用于原生飘字
与受击表现。
