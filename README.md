# Ramuh Loop

国服 Dalamud API 15 / .NET 10 的拉姆武器箱循环插件，使用 AGPL-3.0 许可证。流程为：切白魔配装 → 解除人数限制排本 → 执行指定宏 → 等待 DR 自动退本 → 切骑士配装 → 开箱 → 核对新增剑盾并分解 → 处理额外分解列表 → 下一轮。

## 0.2.5 自动自行维修

默认在白魔配装切换完成、准备排本时，以及骑士配装切换完成、准备开箱时，检查当前穿戴装备。最低耐久严格低于10%才维修；10%不触发。不在副本内、战斗中、已有排本队列或分解过程中抢修。

维修仅处理“装备中”分类，需要对应生产职业的已练等级及足够暗物质，可使用更高阶暗物质。核对本次维修窗口、确认框归属及原低耐久装备实际恢复后，才继续原来的排本或开箱步骤。等级不足、暗物质不足、装备/职业/区域变化、未知确认或超时会停止。

设置中新增“装备耐久低于10%时自动自行维修”，默认开启；窗口顶部显示最低耐久。原有配装、宏、物品、额外分解列表与猫叫设置保持兼容。

此前仓库只保存了插件清单。本次工程由已发布的0.2.4安装包还原，以匹配本机国服API15依赖重新编译，并在其循环边界加入维修。原版本还原工程与新版本均编译通过；隔离测试不覆盖实际游戏窗口，自动维修仍待游戏内验证。

## 安装与配置

在卫月的自定义插件仓库中保留原订阅地址：

```text
https://raw.githubusercontent.com/baiyi127/RamuhLoop/main/repo.json
```

刷新插件列表后安装或更新，输入 `/ramuhloop` 打开设置。首次使用需要配置：

- 白魔与骑士配装编号（游戏显示1–100）。
- 技能宏编号（0–99），并选择个人或共通宏。
- 准确的武器箱、骑士剑及可选盾牌物品ID，可搜索客户端物品名称。目标剑盾需为品级100、骑士可用且可分解。
- 任务搜索器开启解除人数限制，关闭等级同步与自由探索；插件不修改这些设置。
- 由你或已有插件完成战利品拾取，并预先配置 DR 自动退本。
- 背包至少四个空位。运行前移走同ID目标剑盾，避免本轮新增物品识别受到已有装备影响。

首次建议只运行一轮，确认进退本、开箱和分解均正常后再增加轮数；0表示持续循环。`/ramuhloop stop` 停止本插件；已发送的排本、开箱、分解或维修请求无法撤回，其他插件的自动化不会随之停止。

额外分解列表会处理背包四页中原有的匹配物品；不扫描兵装库或已穿戴装备。分解前保留原版本的格子、名称、图标和珍稀/独占确认检查。

## 编译

需要 .NET 10 SDK 和与当前国服客户端匹配的 Dalamud API 15 依赖。构建脚本默认使用 `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`，也可显式指定依赖路径：

```powershell
./build.ps1 -DalamudPath 'C:\path\to\matching\Dalamud'
```

输出为 `dist/RamuhLoop.dll`、同名清单及 `RamuhLoop.zip`。包中不包含卫月或FFXIVClientStructs宿主程序集。

维护者运行 `./prepare-release.ps1` 生成同版本安装包，并同步 `repo.json` 与兼容的 `pluginmaster.json`。先上传对应Git标签的Release附件，再推送公开插件源。

## 验证

```powershell
dotnet run --project tests/RepairPolicyChecks/RepairPolicyChecks.csproj -c Release
dotnet run --project tests/RepairCheckpointChecks/RepairCheckpointChecks.csproj -c Release
./tests/Validate-Repository.ps1 -RepositoryManifestPath ./repo.json -ArchivePath ./dist/RamuhLoop.zip
```

维修实现与 [SkyLoop](https://github.com/baiyi127/SkyLoop) 使用相同的自行维修服务与策略，并增加适合RamuhLoop的副本外检查边界。代码由AI辅助还原和修改；尚未完成游戏内整轮与低耐久维修验证。
