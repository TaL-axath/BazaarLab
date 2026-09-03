# BazaarLab

BazaarLab 是一个面向《The Bazaar》的本地阵容实验与战斗分析插件。

## 项目来源与声明

BazaarLab 基于 LookingIN 进行二次开发与独立功能扩展，并已获得 LookingIN 作者本人
授权。LookingIN 的官方发布、安装包
和项目说明请前往 **[LookingIN-Distribution](https://github.com/LookingIN97/LookingIN-Distribution)**。

BazaarLab 由本仓库独立维护；本仓库不包含或重新分发 LookingIN 的源码与二进制文件。

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
BazaarPlusPlus 和游戏本体。BPP 的官方安装包已经自带所需的 BepInEx，无需另外下载
或单独安装 BepInEx。

## 本地构建

当前构建脚本假定仓库位于游戏目录的 `.reverse/BazaarLab`：

```powershell
./build.ps1
```

构建产物位于各项目的 `bin` 目录，不会纳入 Git。

## Windows 分享安装包

**[直接下载 BazaarLab v1.0.10 Windows x64 安装包](https://github.com/TaL-axath/BazaarLab/releases/download/v1.0.10/BazaarLab-v1.0.10-Windows-x64.zip)**

在已安装游戏、BPP（已自带 BepInEx）和 .NET 8 Runtime 的开发机上运行：

```powershell
./installer/build-package.ps1
```

脚本会重新编译 BazaarLab，并在仓库外的 `.reverse/releases` 目录生成可分享的
`BazaarLab-v<版本>-Windows-x64.zip`。压缩包包含图形安装器、编译后的 BazaarLab
文件、运行时组件、文件哈希清单和中文说明，但不包含 BPP、BepInEx 或游戏文件。

安装器能够从 Steam 库自动发现《The Bazaar》，也支持手动选择游戏根目录。安装前会检查：

- BazaarPlusPlus 5.x（建议 5.2.1 或更高版本，其安装包已自带 BepInEx 5.4）；
- Microsoft .NET 8 Desktop/NETCore Runtime；
- 安装包载荷完整性和游戏是否正在运行。

缺少 .NET 8 时，安装器可调用 Windows Package Manager 安装；缺少 BPP 或 BepInEx
时会统一引导安装 BPP，避免让用户误以为需要分别安装两者。实际写入采用暂存目录、
SHA-256 复验和旧版本备份，失败时会尝试恢复原版本。

也可以用于自动化部署：

```powershell
./BazaarLab-Installer.exe --silent --game-dir "D:\\SteamLibrary\\steamapps\\common\\The Bazaar"
```

退出码 `0` 表示成功，`1` 表示安装错误，`2` 表示依赖或目标目录检查未通过。

## 发布说明规范

后续每个 GitHub Release 的说明必须包含实际的功能更新内容、完整 Commit SHA，以及
对应 Windows 安装包的 SHA-256。固定格式和发布前检查项见
[`RELEASING.md`](RELEASING.md)。只有修复而没有新增功能时，也必须在“功能更新”中
明确写明本版没有新增功能，不能省略该部分。

## 数据兼容性

BazaarLab 启动后会等待游戏静态数据管理器就绪，并以只读方式检查官方
`GameData.db`。首次运行或检测到数据库变化时，会自动把 `cards` 表导出为完整
JSONL 卡表，按内容 SHA-256 保存到 `BepInEx/config/BazaarLab/catalogs/`，验证完成后
再原子切换。卡表检查或生成期间暂停本地预测；未知战斗规则会在模拟前报告为
不兼容，不再继续输出可能误导的胜率。

新阵容码记录实际规则集 SHA-256。本地保留对应历史卡表时可以复现旧版本阵容；
双方规则集不同或对应历史卡表缺失时，本地对战会拒绝混合规则计算。旧版
`BL1/LIL1` 阵容码继续兼容，但因没有内容哈希，只能使用当前规则集作为参考。

BazaarLab 使用 `bazaarlab-combat-snapshot-v1` 快照格式，并继续只读兼容早期的
`lookingin-localcombat-bpp-snapshot-v1` 历史快照。升级时会尝试把
`BepInEx/config/LookingIN.LocalCapture` 迁移至 `BepInEx/config/BazaarLab`。

## 本地文件

实时状态位于 `state/`，正在运行的计算位于 `temp/`，失败、`UNRELIABLE` 和野怪
实战误判样本位于 `diagnostics/`，正式战斗快照位于 `combat-records/`。计算结果
成功读入内存后立即删除临时输入和输出；完整目录和自动迁移规则见
[`LOCAL_STORAGE.zh-CN.md`](src/BazaarLab.Plugin/LOCAL_STORAGE.zh-CN.md)。
