# BazaarLab

BazaarLab 是一个面向《The Bazaar》的本地阵容实验与战斗分析插件。

当前功能包括：

- 本地战斗模拟与双方阵容码对战；
- 阵容码导入、导出和本地回放；
- 包含背包候选物品的摆位搜索与自动移动；
- 野怪战斗结果预览；
- 无限血白板环境下的伤害、护盾和治疗曲线；
- PvP 开战阵容的本地归档；
- 遭遇和二级选择界面的本地记录。

## 项目结构

- `src/BazaarLab.Plugin`：BepInEx 插件和游戏内界面；
- `src/BazaarLab.Combat`：本地战斗模拟核心；
- `src/BazaarLab.PlacementSearch`：摆位搜索命令行工具；
- `src/BazaarLab.BaselineMetrics`：白板输出曲线工具。

## 依赖边界

本仓库只保存 BazaarLab 自行维护的源码和文档，不包含：

- BazaarPlusPlus（BPP）的源码或二进制文件；
- 游戏程序集和 BepInEx 二进制文件；
- 反编译产物；
- 官方卡牌目录、玩家快照、日志和历史战绩；
- 编译后的 DLL、PDB、EXE 或运行时目录。

BazaarLab 通过公开的 BPP 接口读取必要的游戏状态。编译插件前，需要用户自行安装
BepInEx、BazaarPlusPlus 和游戏本体。

## 本地构建

当前构建脚本假定仓库位于游戏目录的 `.reverse/BazaarLab`：

```powershell
./build.ps1
```

构建产物位于各项目的 `bin` 目录，不会纳入 Git。

## 数据兼容性

BazaarLab 使用 `bazaarlab-combat-snapshot-v1` 快照格式，并继续只读兼容早期的
`lookingin-localcombat-bpp-snapshot-v1` 历史快照。升级时会尝试把
`BepInEx/config/LookingIN.LocalCapture` 迁移至 `BepInEx/config/BazaarLab`。
