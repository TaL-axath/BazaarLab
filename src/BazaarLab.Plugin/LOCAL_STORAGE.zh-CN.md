# 本地文件布局

BazaarLab 的运行数据位于 `BepInEx/config/BazaarLab/`：

- `state/`：当前实时阵容、最近稳定阵容、最近开战阵容、状态与野怪校准；
- `temp/<功能>/`：子进程正在使用的输入和结果；
- `diagnostics/<功能>/`：失败、异常、`UNRELIABLE` 和野怪误判样本；
- `combat-records/openings/`：每场官方战斗的开局快照；
- `combat-records/results/`：每场官方战斗的逐帧实际结果；
- `catalogs/`：按规则集 SHA 保存的官方卡表；
- `pvp-lineups/`：按 Run 和天数保存的 PvP 双方阵容码；
- `local-duels/`：阵容码本地对战和原生回放；
- `decision-traces/`：决策界面、选项、动作和页面跳转记录；
- `install-backups/`：安装器覆盖旧版本前生成的备份。

白板曲线、摆位、选怪页预测和选中野怪预测成功读入内存后，会立即删除对应的
`temp` 文件。失败或不可靠结果会连同错误信息转存至 `diagnostics`。野怪可靠预测
不会为了等待实战而保留临时文件；预测内容暂存在内存，若最终胜负不一致，才重新
写入 `diagnostics/monster/mismatches/`。

确认一局 Run 结束并稳定离开三秒后，插件会终止仍在运行的临时计算、清空 `temp`
工作状态并删除 `state/live-inventory.json`。最近稳定阵容、PvP 阵容码、野怪校准、
卡表和正式战斗记录不会随 Run 清理。

升级后第一次启动会把旧版散落在根目录的文件移动到上述目录；旧计算样本进入对应
`diagnostics/<功能>/legacy/`，不会直接删除。
