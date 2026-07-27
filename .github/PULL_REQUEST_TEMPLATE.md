## Summary

<!-- What does this PR change, and why? One or two sentences. -->

## Checklist

- [ ] `dotnet build SelectionAssistant.slnx -c Release` → 0 warnings, 0 errors
- [ ] `dotnet test SelectionAssistant.slnx` → all green
- [ ] If i18n was touched: keys added to all three of `Strings.cs` / `Strings_en.cs` / `Strings_zh_CN.cs`, verified in both EN and ZH UI
- [ ] If P/Invoke was touched: NativeAOT-safe (`[LibraryImport]` with explicit `EntryPoint` where needed), no reflection
- [ ] No secrets / API keys / absolute user paths in the diff
- [ ] Commit messages follow the `type(scope): summary` convention

## Notes for the reviewer

<!-- Anything non-obvious? Risks? Manual verification steps needed on a real Windows machine? -->
