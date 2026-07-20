# 05 · 配置持久化

> **改 providers.json / prompt-templates.json / 密钥存储 / 原子写前先读本文件。**

---

## 职责一句话

所有用户配置的加载/保存：providers.json（Provider 配置）、prompt-templates.json（自定义功能）、capture-policies.json（进程取词策略）、DPAPI 加密密钥。全部原子写入，Utf8JsonWriter 手写（AOT 安全）。

## 关键文件

| 文件 | 职责 |
|---|---|
| `Infrastructure/Configuration/ByhApplicationPaths.cs` | 用户数据路径（%LOCALAPPDATA%\BYH） |
| `Infrastructure/Configuration/ProviderConfiguration.cs` | providers.json 数据模型 + Loader |
| `Infrastructure/Configuration/ProviderConfigurationLoader.cs` | 加载（schemaVersion/大小/数量校验） |
| `Infrastructure/Configuration/PromptTemplatesStore.cs` | prompt-templates.json 加载/保存 |
| `Infrastructure/Configuration/CapturePolicyConfigurationLoader.cs` | capture-policies.json 加载 |
| `Platform.Windows/Secrets/DpapiSecretStore.cs` | DPAPI 密钥存储（CurrentUser scope） |

## 用户数据位置

```
%LOCALAPPDATA%\BYH\
  ├── providers.json              ← Provider 配置（密钥只存 secret:// 引用）
  ├── prompt-templates.json       ← 自定义功能（翻译/总结/解释 + 自定义）
  ├── capture-policies.json       ← 进程取词策略
  ├── secrets\*.bin               ← DPAPI 加密密钥
  └── logs\BYH.log                ← 运行日志
```

## 原子写入模式（所有配置统一）

```csharp
string tempPath = path + ".tmp";
using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
{
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    // ... 手写 JSON ...
    writer.Flush();
}
if (File.Exists(path)) File.Delete(path);
File.Move(tempPath, path);  // 原子移动
```

- 失败时清理 temp 文件。
- 异常包装成 `ProviderConfigurationException`（统一错误类型）。
- schemaVersion 校验（防未来不兼容）。
- 大小上限（providers/prompt-templates 64KB）+ 数量上限。

## JSON 手写（AOT 安全）

**绝不用 `JsonSerializer.Serialize<T>`**（反射，破坏 NativeAOT TrimMode=full）。统一用 `Utf8JsonWriter` 手写每个字段。读取用 `JsonDocument.Parse` + 手动 `TryGetProperty`。

## 不变量 / 踩坑

- **密钥绝不进明文 JSON**——DPAPI 加密，JSON 只存 `secret://` 引用。
- 原子写（temp + Move）——防写一半崩溃。
- Utf8JsonWriter 手写——不用反射序列化。
- schemaVersion 校验——加载时校验，不匹配抛异常。
- 缺文件/损坏 → 返回内置默认（不崩溃）。

## 改动检查清单

- [ ] 加配置字段：Utf8JsonWriter 手写 Write；读取用 TryGetProperty；可选字段缺失用默认。
- [ ] 改密钥：DPAPI CurrentUser；绝不写明文 JSON。
- [ ] 改原子写：temp + Move；失败清理 temp。
- [ ] 加 schema：schemaVersion + 1；旧版兼容（缺字段用默认）。
