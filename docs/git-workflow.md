# BYH 多 Agent Git 并行工作流

> 本文档定义 BYH (SelectionAssistant) 项目基于 **本地 git + worktree** 的多 Agent 并行开发流程。
> 适用对象：你自己、ZCode/omp/Pi 等 coding agent、未来的协作者。

---

## 1. 关键事实

| 项 | 路径 / 值 |
|---|---|
| **主仓库根** | `<repo-root>\` |
| **主仓库物理路径**（`<repo-parent>` 是 junction） | `<user-home>\gh-kb\selection-assistant\` |
| **默认长期分支** | `main` |
| **可执行产物** | `artifacts\publish\win-x64-nativeuia\BYH.exe` |
| **根目录启动器** | `BYH.cmd` → 双击即起 BYH.exe |
| **桌面快捷方式** | `%USERPROFILE%\Desktop\BYH.lnk` → 指向同一个 exe |
| **运行时密钥/配置** | `%LOCALAPPDATA%\BYH\`（**仓库外**，所有 worktree 共享） |
| **并行 worktree 根** | `<worktree-parent>\`（主仓库的同级目录） |

**重要**：源码、构建产物、启动器、文档全在 `selection-assistant\` 这一个根下，**没有独立的安装目录**。"程序位置 vs 项目位置" 本就是一处 —— git 化后这个事实被显式化。

---

## 2. 分支约定

```
main                          ← 唯一长期分支，始终可发布
├── task/REQ-010-qr-recognize   ← 一个 Agent 任务一个分支
├── task/REQ-011-number-annotate
└── spike/<slug>                ← 探索性实验（可能不合并）
```

**规则**：
- 分支名 = `task/<REQ-###>-<kebab-slug>`，slug 用英文小写连字符，便于跨工具
- 一个分支只对应一个 REQ/TASK（reqbase 的 dispatch 阶段已天然分配，不会两个 agent 抢同一 TASK）
- 不开 `develop` / `release/*` 等中间分支 —— 单人项目，直接在 `main` 上集合并整理

---

## 3. worktree 约定

**为什么用 worktree 而非普通分支**：
普通分支共享一个工作目录 —— 多个 Agent 同时编辑会互踩文件、互相覆盖未提交改动。`git worktree` 让每个分支拥有**独立的工作目录**，多个 Agent 真正并行不冲突。

**目录布局**：
```
<repo-root>\      ← 主仓库 = main 分支（你自己开发用）
<worktree-parent>\                   ← 所有并行 worktree 的统一父目录
├── REQ-010-qr-recognize\               ← task/REQ-010-qr-recognize 的工作目录
├── REQ-011-number-annotate\
└── ...
```

每个 worktree 是**完整的一份项目副本**：独立 `bin/`、`obj/`、独立 `artifacts/publish/.../BYH.exe`，但共享同一个 `.git/`（在主仓库里），分支切换和合并都很快。

---

## 4. 启动一个并行 Agent —— 标准流程

### 4.1 命令版（手动）

```bash
# 1. 在主仓库创建 worktree + 新分支（从 main 拉起）
cd /<repo-root>
git worktree add -b task/REQ-010-qr-recognize  ../byh-worktrees/REQ-010-qr-recognize  main

# 2. 进入 worktree（这就是给第 N 个 Agent 用的工作目录）
cd /<worktree-parent>/REQ-010-qr-recognize

# 3. 预热构建（首次必做：让 bin/obj 就位）
dotnet build -c Debug
# 或要刷新产物：
dotnet publish src/SelectionAssistant.App/SelectionAssistant.App.csproj -c Release -r win-x64
cp src/SelectionAssistant.App/bin/Release/net10.0-windows/win-x64/publish/BYH.exe \
   artifacts/publish/win-x64-nativeuia/BYH.exe
```

### 4.2 脚本版（推荐）

```powershell
# 在主仓库根目录运行
pwsh tools/new-worktree.ps1 task/REQ-010-qr-recognize
# 等价于上面 4.1 的全部步骤，自动 cd 到新 worktree
```

启动后，把 `<worktree-parent>\REQ-010-qr-recognize\` 这个路径丢给第 N 个 Agent（ZCode session / omp worker / 任意 coding agent）作为它的工作目录即可。它可以：
- 自由编辑任何文件，不影响主仓库和其他 worktree
- 独立 `dotnet build` / `dotnet test`（独立 bin/obj）
- 双击该 worktree 内的 `BYH.cmd` 直接运行那个分支的 BYH.exe

---

## 5. 完成合并回 main —— 标准流程

```bash
# 1. 在 worktree 里提交所有改动
cd /<worktree-parent>/REQ-010-qr-recognize
git add .
git commit -m "feat(REQ-010): add QR code recognition"

# 2. 回到主仓库（main 分支），合并
cd /<repo-root>
git merge --no-ff task/REQ-010-qr-recognize -m "merge: REQ-010 QR code recognition"

# 3. 清理 worktree 和分支
git worktree remove ../byh-worktrees/REQ-010-qr-recognize
git branch -d task/REQ-010-qr-recognize
```

`--no-ff` 保留合并提交，方便事后追溯"哪个 REQ 是哪次合并进来的"。

---

## 6. 多 worktree 并行时的运行时注意事项

| 场景 | 是否安全 |
|---|---|
| 多个 worktree 同时 `dotnet build` / `test` | ✅ 完全独立，互不影响 |
| 多个 worktree 同时编辑不同文件 | ✅ 合并时无冲突 |
| 多个 worktree 同时编辑同一文件 | ⚠️ 合并时走 git 三方合并，按常规冲突解决 |
| **同时运行多个 BYH.exe**（不同 worktree 的） | ❌ **禁止** —— 全局快捷键会冲突，行为未定义 |
| 切换正在运行的 BYH.exe | 先退出当前实例，再启动另一个 |

**密钥共享**：所有 worktree 的 BYH.exe 都读同一份 `%LOCALAPPDATA%\BYH\secrets\` 和 `%LOCALAPPDATA%\BYH\providers.json`，**无需每个 worktree 重新配置密钥**。

---

## 7. 产物（BYH.exe）的版本控制策略

- `artifacts/publish/win-x64-nativeuia/BYH.exe` **纳入 git**（见 `.gitignore` 明确保留 `artifacts/`）
- 目的：每个 worktree checkout 后立即可运行，不必先 publish
- **改了源码后**：在 worktree 里跑 `dotnet publish`，把新 exe 拷到约定路径，和源码改动**一起 commit**，这样合并后 main 的产物也是新的
- 历史会含二进制 diff，但因 BYH.exe ~28 MB 且变化不频繁，单人项目可接受。若日后膨胀，再迁 Git LFS（见下）

---

## 8. 常用诊断命令

```bash
# 看所有 worktree
git -C /<repo-root> worktree list

# 看当前在哪个 worktree
git rev-parse --show-toplevel

# 看分支图
git -C /<repo-root> log --oneline --graph --all -20

# 强制清理已删除目录的 worktree 注册
git -C /<repo-root> worktree prune
```

---

## 9. 未来扩展（暂不做）

- **远端备份**：`git remote add origin <url>` + `git push -u origin main`。当前为纯本地 git，需要时随时加。
- **Git LFS**：若 BYH.exe 频繁更新导致仓库膨胀（>500 MB），`git lfs install` + `git lfs track "artifacts/publish/**/*.exe"`，重新提交。
- **CI**：本地脚本已足够；要上 GitHub Actions 时再加 `.github/workflows/`。
