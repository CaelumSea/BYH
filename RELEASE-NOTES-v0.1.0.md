# Release Notes — BYH v0.1.0 (2026-07-27)

> 第一个标记版本。本文档是发版动作的检查清单与验证记录，供将来发新版时对照。
> 面向用户的说明见 `CHANGELOG.md`；开发细节见 `handoff/BACKLOG-roadmap.md`。

---

## 发版包内容

| 文件 | 说明 |
|---|---|
| `BYH.exe` | NativeAOT 单文件可执行，28,589,568 字节，win-x64 |
| `README.md` | 面向用户的说明（快捷键 / 配置 / 安装） |
| `CHANGELOG.md` | 版本演进记录 |
| `handoff/` | 完整开发交接上下文（可选，仅内部/接续开发用） |

> v0.1.0 **不包含** LICENSE、安装包、签名、开机自启配置器。这些排进 v0.2。

---

## 发版前验证清单（已全部通过）

- [x] `dotnet build SelectionAssistant.slnx -c Release` — 0 警告 0 错误
- [x] `dotnet test` — 661/661 通过
- [x] NativeAOT publish — 0 trim/AOT 警告
- [x] exe 同步到 `artifacts/publish/win-x64-nativeuia/BYH.exe`
- [x] 真机启动 — 日志零异常（Runtime / KeyboardHook / ClipboardHistory 全启动）
- [x] 中英文切换 — Settings 各页无残留英文（品牌名 / hex / 字体名除外）
- [x] csproj `<Version>0.1.0</Version>` — exe 属性面板版本号正确
- [x] README.md / CHANGELOG.md 完整
- [x] git tag `v0.1.0` 打在发版 commit 上

---

## 发版 commit 与 tag

```
tag:   v0.1.0
commit:<发版 commit sha>
内容:  csproj 版本号 + README + CHANGELOG + 本 RELEASE-NOTES + rebuild exe
```

---

## 将来发新版的步骤（v0.2+）

1. 改 `src/SelectionAssistant.App/SelectionAssistant.App.csproj` 的 `<Version>` / `<FileVersion>` / `<InformationalVersion>`（AssemblyVersion 仅在破坏性变更时 bump）
2. 在 `CHANGELOG.md` 顶部加新版本段（能力快照 / 质量门槛 / 关键决策 / DEFER 项）
3. 在 `handoff/BACKLOG-roadmap.md` 加对应批次段落（开发细节）
4. `dotnet build` + `dotnet test` + NativeAOT publish 全过
5. 同步 exe 到 `artifacts/publish/win-x64-nativeuia/BYH.exe`
6. taskkill 旧 BYH → 起新 exe → 真机验证日志零异常
7. commit + `git tag v<x.y.z>`
8. 写 `RELEASE-NOTES-v<x.y.z>.md`（复制本文件模板）

---

## 验证 exe 版本号

发布后右键 `BYH.exe` → 属性 → 详细信息，应看到：

```
文件版本     0.1.0.0
产品名称     BYH
产品版本     0.1.0
公司         By Your Hand
说明         BYH — By Your Hand. Context-aware selection assistant for Windows.
```

或命令行：

```powershell
(Get-Item BYH.exe).VersionInfo | Format-List ProductVersion, FileVersion, ProductName, CompanyName
```
