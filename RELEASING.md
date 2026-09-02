# BazaarLab 发布规范

每个 GitHub Release 必须使用以下结构，不能只写版本号或笼统的“bug fixes”。

```markdown
## 功能更新

- 列出玩家能够感知的新增或调整内容。
- 如果没有新增功能，明确写“本版没有新增功能”。

## 修复与兼容性

- 列出主要修复、行为变化和依赖兼容性。

## 版本校验

- Commit SHA: `<完整的 40 位 Git commit SHA>`
- `BazaarLab-v<version>-Windows-x64.zip` SHA-256: `<64 位 SHA-256>`
```

发布前必须完成：

1. 完整运行仓库根目录的 `build.ps1`，确保零错误；
2. 使用 `installer/build-package.ps1` 生成安装包；
3. 对最终上传的 ZIP 重新计算 SHA-256；
4. 确认 Release 对应的 Commit SHA 与 tag 指向一致；
5. 将功能更新、完整 Commit SHA 和 ZIP SHA-256 写入 Release 说明后再发布。
