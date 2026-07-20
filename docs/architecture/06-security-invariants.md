# 06 · 安全不变量

> **这些规则违反必出 bug 或安全漏洞。改任何模块前过一遍。**

---

## 1. API 密钥绝不进明文 JSON

- DPAPI 加密（CurrentUser scope），存 `%LOCALAPPDATA%\BYH\secrets\*.bin`。
- providers.json 只存 `secret://` 引用，绝不存密钥值。
- `DpapiSecretStore.SetAsync(reference, value)` / `GetAsync(reference)`。
- 设置页密钥输入框：保存后清空；不回显；可切换显隐（`PasswordChar`）。
- **改 Provider 配置时**：ApiKeyReference 是引用，不是密钥本身。

## 2. HTTP 默认禁重定向

- `HttpClientHandler.AllowAutoRedirect = false`。
- 无 TLS 禁用选项（不提供"跳过证书校验"）。
- 防 SSRF（服务端返回重定向到内网地址）。

## 3. URL 拼接 URI-aware

- `ProviderUriBuilder`：用 `Uri` 组合 BaseUrl + ChatPath，防路径注入（如 `../` 穿越）。
- 不用字符串拼接 URL。

## 4. 钩子始终放行（绝不吞事件）

- `LowLevelMouseHook.HookCallback` **永远 CallNextHookEx**，return 它的结果。
- 绝不 return 非 0 来吞事件——会破坏源应用的右键菜单、拖拽等。
- 钩子只**观察**，不**修改**事件流。

## 5. 0 警告（TrimMode=full）

- 不用反射绑定（`x:CompileBindings="False"` 破坏 NativeAOT）。
- DataTemplate 绑定类型必须 **public top-level**（private nested 编译绑定失败 AVLN2000）。
- 不用 `JsonSerializer.Serialize<T>`（反射）；用 Utf8JsonWriter 手写。
- 构建目标：`0 警告 0 错误`。

## 6. 配置文件原子写入

- temp 文件 + `File.Move`（原子移动）。
- 失败时清理 temp。
- 防写一半崩溃导致配置损坏。

## 7. WS_EX_NOACTIVATE（工具条不抢焦点）

- ToolbarWindow 用 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`。
- 显示用 `SW_SHOWNOACTIVATE` + `SetWindowPos(SWP_NOACTIVATE)`。
- **永不 SetForegroundWindow**——会激活窗口、抢焦点、选中高亮消失、整个产品失效。

## 8. 钩子回调绝不碰 UI

- 钩子在原生线程；Avalonia UI 单线程模型。
- 必须切回 UI 线程，否则 `InvalidOperationException`。

## 9. 取词进程安全

- 终端只注入 `Ctrl+Insert`（不是 Ctrl+C，防中断信号）。
- 高完整性/管理员目标进程：安全降级（不注入，只读 UIA）。
- 完整进程策略链（`IProcessCapturePolicyProvider`）。

## 10. chord grace window 绝不 Activate()

- chord 的右键弹源应用右键菜单抢焦点；grace window 内若 Activate() 抢回 → 重入循环冻结 UI 线程。
- 正确：grace window 内只忽略 Deactivated，靠 Topmost 保持可见。详见 `02-windowing.md`。

## 11. 单实例锁

- `Program.Main` 命名 Mutex `Global\BYH_ByYourHand_SingleInstance`。
- 第二个实例发现 Mutex 已占 → 立即退出（不启动第二个托盘/钩子）。
- 探针分支（`--probe-*`/`--set-secret`）跳过锁（它们是 CLI 工具）。
