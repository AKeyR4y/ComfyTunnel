# 第三方组件

应用自行实现界面、解析、配置和连接管理，没有复制 FinalShell 或其他 SSH 管理器的源码。

| 组件 | 当前版本 | 许可 |
| --- | --- | --- |
| SSH.NET | 2026.0.0 | MIT |
| BouncyCastle.Cryptography | 2.7.0 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT |
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT |
| .NET / WPF / Windows Forms | 自包含运行时，随固定 SDK 发布 | MIT 及其第三方声明 |

许可原文保存在 `licenses/`，也作为资源嵌入 EXE，可在「连接记录 → 第三方许可」查看。具体依赖解析版本记录在各项目的 `packages.lock.json`。

测试专用 Node 服务使用 ssh2 1.17.0（MIT）和 ws 8.22.1（MIT），其锁文件位于 `tests/fixture/package-lock.json`；这些组件不进入应用 EXE。
