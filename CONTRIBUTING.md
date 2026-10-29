# 参与 DeskPlan

欢迎通过 Issue 提交问题或功能建议。修改代码前，可以先说明使用场景和预期行为；修复较小问题可直接提交 Pull Request。

## 本地开发

使用 Windows、.NET 10 SDK 和 PowerShell 7。运行 `pwsh -File ./scripts/Build.ps1` 构建、测试和生成发布包；`pwsh -File ./scripts/Run.ps1` 启动源码。

请使用隔离数据目录进行调试：

```powershell
pwsh -File ./scripts/Run.ps1 -DataDirectory "$env:TEMP\DeskPlan-Dev"
pwsh -File ./scripts/Build.ps1 -SmokeTest -NoPackage
```

业务或数据规则变更应补充相应测试。窗口行为修改需验证手动打开、模式切换、托盘及单实例唤出；自动测试覆盖范围和人工观察请分别记录，避免把 HWND 成功挂接视为全部桌面交互已经通过。

## 提交内容

- 说明问题、触发步骤、修改后的行为和验证方式。
- 保持现有数据兼容；迁移及恢复先保留备份。
- 不提交个人数据库、备份、日志、凭据、生成的二进制或 `artifacts/`。
- 更新面向用户的操作说明与 CHANGELOG，版本号统一在 `Directory.Build.props` 修改。

## 发布维护

通过构建脚本生成自包含 ZIP、两份离线 HTML 手册及 SHA-256 清单。发布前检查 ZIP 内容，并对发布 EXE 使用新的隔离目录执行 `--smoke-test`。将经验证的提交打 tag，GitHub Actions 通过后创建正式 Release，上传发布文件并核对摘要。正式发布后不要修改同一版本的二进制内容；后续改动使用新版本号。

代码贡献按仓库的 MIT 许可证提供。
