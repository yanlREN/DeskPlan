# 第三方组件与许可证

DeskPlan 项目代码采用根目录中的 MIT 许可证。第三方组件保留其自身的版权与许可。本文件及 `docs/licenses/` 随自包含发布包分发。

| 组件 | 发布包版本 | 许可与原始声明 |
| --- | --- | --- |
| .NET Runtime | 10.0.14 | [MIT](docs/licenses/dotnet-runtime-LICENSE.txt)；[运行时第三方声明](docs/licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt) |
| .NET Windows Desktop Runtime（WPF / Windows Forms） | 10.0.14 | [MIT](docs/licenses/dotnet-desktop-LICENSE.txt)；[附带声明](docs/licenses/dotnet-desktop-THIRD-PARTY-NOTICES.txt) |
| Microsoft.Data.Sqlite.Core | 10.0.14 | [MIT](docs/licenses/Microsoft.Data.Sqlite-LICENSE.txt)，Microsoft Corporation |
| SQLitePCLRaw.core / provider.e_sqlite3 / config.e_sqlite3 / bundle_e_sqlite3 | 3.0.5 | [Apache License 2.0](docs/licenses/SQLitePCLRaw-LICENSE.txt)，Copyright 2014–2025 SourceGear, LLC |
| SQLite 原生数据库引擎（SQLite NuGet 包） | 3.53.4 | [Public Domain 声明](docs/licenses/sqlite-LICENSE.txt) |

.NET 与 SQLitePCLRaw 的组件未作修改；DeskPlan 通过其公开接口调用。SQLitePCLRaw 许可中允许的商标或权利范围不因本项目 MIT 许可而改变。

声明来源为实际构建使用的 NuGet / SDK 分发文件，以及对应版本的官方项目许可证：

- [.NET Runtime](https://github.com/dotnet/runtime)
- [.NET Windows Desktop](https://github.com/dotnet/windowsdesktop)
- [Microsoft.Data.Sqlite / EF Core](https://github.com/dotnet/efcore/tree/v10.0.14)
- [SQLitePCL.raw v3.0.5](https://github.com/ericsink/SQLitePCL.raw/tree/v3.0.5)
- [SQLite copyright](https://sqlite.org/copyright.html)

开发与测试工具由各自 NuGet 包提供许可，不作为运行所需测试组件打入发行 ZIP。以后升级依赖时，需要同步核对本文件与随附许可文本。
