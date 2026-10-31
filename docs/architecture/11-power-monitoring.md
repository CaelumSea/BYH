# 11 · 功耗监控（LHM HTTP 轮询）

> **一句话**：BYH 不直读传感器。它把 Libre Hardware Monitor（LHM）当成外部传感器服务，
> 定时 `GET` 它的 Web Server JSON，用来显示功率/温度、累计能耗、按阈值告警，并提供一键拉起 LHM 的入口。

## 为什么是 HTTP 而不是直读

直读 MSR/SMBus 需要内核驱动 + 常驻提权，会把 NativeAOT 单文件、零驱动、普通用户权限的发布形态全部推翻。LHM 已经把这件事做完了：同一份 `data.json` 里就有 CPU Package Power、GPU Power、各温度与频率。BYH 只做客户端，代价是一条 HTTP 请求，收益是**不加驱动、不提权、不动 AOT 配置**。这也是 `06-security-invariants.md` 里「不引入新原生依赖」的直接推论。

## 组件与文件

| 关注点 | 文件 | 要点 |
|---|---|---|
| 配置 | `Core/PowerMonitoring/PowerMonitorSettings.cs` | record；`Default` / `Normalize` / `Validate` 三件套。端点必须是 http/https 绝对 URL（`Uri.TryCreate` 会把 `localhost:8085/...` 当合法，必须再校验 scheme） |
| HTTP 拉取 + 解析 | `Infrastructure/PowerMonitoring/HttpPowerMonitorClient.cs` | `JsonDocument` 解析（AOT 安全），失败不抛给调用方，返回 `Connected = false` 的快照 |
| 快照 | `PowerSnapshot.cs` | `record struct`，零堆分配（轮询是热路径）；CPU/GPU/SSD/电池字段全部可空 |
| 能耗积分 | `EnergyAccumulator.cs` | 梯形积分 Wh，总累计 + 今日切片 |
| 历史 | `PowerHistoryStore.cs` | `power-history.jsonl` 追加 / 按天裁剪 / 清空 / 统计 |
| 告警 | `AlertEvaluator.cs` | 三路阈值 + **5 °C 回滞**，只在「新触发」那一拍返回 true，避免持续刷屏 |
| 配置持久化 | `Infrastructure/Configuration/PowerMonitorSettingsStore.cs` | `power-monitoring.json` 里同时存设置、累计 Wh、告警状态 |

## 运行链路

1. **启动**：`SelectionRuntime.ConfigurePowerMonitor()` 载入设置/累计/告警态，读失败就整个 tier 关闭（不抛），并按 `HistoryRetentionDays` **裁剪一次**历史。
2. **循环开关**：`App.StartPowerMonitorLoop()` —— `Enabled = false` 时直接复位 tooltip 返回；否则 `PeriodicTimer(PollIntervalMs)`（默认 3000 ms），设置保存时取消旧循环、按新间隔重启。
3. **每拍**：`PollAndAccumulateAsync()` → 更新快照、积分能耗（`TrackEnergy` 开启时），并且**每 60 s 才**落盘累计值 / 追加一行历史。
4. **回主线程**：刷新托盘 tooltip、设置页手机框 4 个 view、`IsLhmOnline`、托盘「启动 LHM」的置灰状态；`AnyNewlyTriggered` 时播放对应 mp3。

## 展示面

- **托盘 tooltip**（`ShowInTray`）：两行，上面功率、下面温度 —— `CPU 45W · GPU 120W · SSD1 55°C` / `CPU 62°C · GPU 70°C · SSD2 51°C`。离线显示 `BYH · By Your Hand · (功耗离线)`；功能关闭时回到原始名字。
- **设置页 功耗 区**：CPU / GPU / 系统 / 能耗 四个 view，外加端点、间隔、告警阈值、启动 LHM、历史清空。
- **历史文件**：`%LOCALAPPDATA%\BYH\power-history.jsonl`，每分钟一行，默认保留 30 天（钳制 7–365）。

## 启动 LHM：两个入口，一条链路

设置页「启动 LHM」与托盘右键「启动 LHM 温度监控」都落到 `SelectionRuntime.StartLhm(LhmExePath)`：

```
LhmExePath（空 → 随包 tools\LibreHardwareMonitor\LibreHardwareMonitor.exe）
  → LauncherRunner → ShellExecuteEx verb=runas（UAC）
  → 3 秒内轮询自动转在线：tooltip 变两行、两个入口同时置灰
```

同步结果（正在启动 / 找不到程序）在设置页写状态行、在托盘写 tooltip。**随包目录必须完整**：`tools\LibreHardwareMonitor\` 不入库，publish 由 csproj 整目录带出，缺 `LibreHardwareMonitor.exe.config`（唯一一条绑定重定向）会让 LHM 建窗即崩 —— 机理见 `07-build-publish-run.md` 与 AGENTS 铁律 9。

## 配置项（`power-monitoring.json`）

| 键 | 默认 | 说明 |
|---|---|---|
| `Enabled` | `false` | 总开关，opt-in；关闭时循环不启动 |
| `Endpoint` | `http://localhost:8085/data.json` | LHM GUI → Options → Remote Web Server |
| `PollIntervalMs` | `3000` | 钳制 1000–60000。再快会污染 BYH 自身的 CPU 功耗读数 |
| `ShowInTray` | `true` | 是否把功耗/温度拼进 tooltip |
| `TrackEnergy` | `true` | 关闭则不积分、不落盘，能耗 view 显示 — |
| `AlertEnabled` | `false` | 温度告警总开关 |
| `CpuTempThresholdC` | `93` | 钳制 50–110（6800H Tctl 降频点 ~95） |
| `GpuTempThresholdC` | `85` | 钳制 50–110；无传感器时自动跳过 |
| `SsdTempThresholdC` | `73` | 钳制 40–90；取两盘最高温 |
| `HistoryRetentionDays` | `30` | 钳制 7–365，启动时裁剪 |
| `LhmExePath` | `""` | 空 = 随包默认路径 |

## 失败模式与排查

| 现象 | 原因 | 排查 |
|---|---|---|
| tooltip 显示 `(功耗离线)` | LHM 没开 / Web Server 未启用 / 端口不符 | `curl http://127.0.0.1:8085/data.json` 看 200；LHM Options → Remote Web Server |
| 有数据但某项是 `—` | 该传感器不存在（iGPU 无温度、台式机无电池） | 正常，不告警 |
| 点「启动 LHM」后软件没起来 | 随包目录不完整（缺 `exe.config`）→ LHM 秒退 | 事件日志 CLR20r3 + `Aga.Controls.Properties.Resources.get_check`；补齐整目录 |
| 启动了但仍离线 | 端点不是 8085，或 LHM 端口被改 | 设置页端点与实际一致 |
| 告警不响 | 告警开关默认关 / 未跨阈值 / 5 °C 回滞内不重复 | 设置页「试听告警」依次播三条 mp3 |

## 边界（不要做）

- 不引用 `LibreHardwareMonitorLib`、不自带驱动、不提权读 MSR —— 保持 NativeAOT 与普通权限。
- 不把轮询压到 1000 ms 以下；不把历史裁剪挪到每次追加（只启动时裁一次）。
- 不要在 `PowerSnapshot` 里塞引用类型或改回 `readonly`（热路径零分配 + 就地 mutate）。

## 验证清单

1. `curl http://127.0.0.1:8085/data.json` → 200。
2. 日志标签 `[PowerMonitor]`：`StartLhm: launch issued ...`、轮询失败/恢复、历史裁剪条数。
3. 托盘 tooltip 两行有实时数值；关 LHM 后 ≤ 一个轮询周期变 `(功耗离线)`。
4. `power-history.jsonl` 每分钟 +1 行；设置页清空后文件归零。
5. 测试基线 972（Core 792 / Providers 51 / Windows 集成 129）。
