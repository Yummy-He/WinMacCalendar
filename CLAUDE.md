# WinMacCalendar — macOS 风格 Windows 桌面日历小组件

## 项目概述

在 Windows 11 上创建一个固定在桌面的日历小组件，参考 macOS 日历小组件设计。通过 CalDAV 协议与 iCloud/Apple 日历实现双向同步。采用 WPF .NET 9 轻量原生方案，追求小体积、低内存占用。

## ⚠️ 平台注意事项

**在本机 (Windows 11) 开发，终端环境为 Git Bash（`bin\bash.exe`）。**

- Git Bash 支持大部分 Linux 命令但底层是 Windows，路径使用 `I:/WinMacCalendar` 格式
- `dotnet` 等命令可直接运行，CLI 参数与 Linux 版一致
- 文件操作优先用 `/` 路径分隔符，避免 `\` 被 bash 转义
- 遇到 `cwd was reset` 提示时，记得下次执行 Bash 时用 `cd` 切回项目目录
- 不用 `sudo`、`systemctl` 等 Linux 专属命令
- 发布产物是 `.exe`，不是 Linux 二进制

## 技术栈

| 层 | 技术 |
|---|------|
| 运行时 | .NET 9.0 (WPF) |
| 语言 | C# 12 |
| UI 框架 | WPF + Win32 P/Invoke |
| 本地存储 | SQLite (Microsoft.Data.Sqlite) |
| 日历同步 | CalDAV (RFC 4791) → iCloud |
| 加密 | Windows DPAPI |
| 目标平台 | Windows 11 (兼容 Windows 10 1903+) |

## 项目结构

```
WinMacCalendar/
├── WinMacCalendar.csproj         — 项目文件 (.NET 9, WPF)
├── App.xaml/.cs                  — 应用入口、单实例检测
├── MainWindow.xaml/.cs           — 桌面小组件窗口（透明无边框）
├── Widget/
│   ├── CalendarWidget.xaml/.cs   — 控件容器，管理尺寸切换
│   ├── SmallView.xaml/.cs        — 紧凑尺寸：日期 + 下个事件
│   ├── MediumView.xaml/.cs       — 列表尺寸：多日事件列表
│   ├── LargeView.xaml/.cs        — 月视图尺寸
│   ├── WeekView.xaml/.cs         — 周视图尺寸
│   └── WidgetViewModel.cs        — 数据绑定 ViewModel
├── CalDAV/
│   ├── CalDavClient.cs           — CalDAV 协议实现
│   ├── SyncEngine.cs             — 增量同步调度
│   ├── ICloudAuthenticator.cs    — iCloud 认证
│   └── Models/                   — CalDAV 数据模型
├── Storage/
│   ├── Database.cs               — SQLite 初始化与迁移
│   ├── EventRepository.cs        — 事件 CRUD
│   └── SettingsRepository.cs     — 配置读写
├── Services/
│   ├── Calendar/
│   │   └── LunarCalendar.cs      — 农历日期计算
│   ├── CredentialManager.cs      — DPAPI 凭证加解密
│   ├── TrayService.cs            — 托盘图标管理
│   └── WindowManager.cs          — 桌面层级管理 (Win32)
├── Settings/
│   └── SettingsWindow.xaml/.cs   — 设置面板
├── Models/
│   ├── CalendarEvent.cs          — 事件领域模型
│   ├── WidgetSize.cs             — 尺寸枚举
│   └── AppSettings.cs            — 设置模型
├── Converters/
│   └── CalendarConverters.cs     — XAML 值转换器
└── Resources/                    — 图标等资源
```

## 核心设计决策

### 桌面钉住
使用 Win32 P/Invoke 将窗口固定在桌面最底层：
- `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` 扩展样式
- `SetWindowPos(HWND_BOTTOM)` 维持 z-order
- 定时器检测并修正被桌面刷新打乱的层级
- 参考 TranslucentTB 的窗口管理策略

### 四种尺寸
| 尺寸 | 窗口大小 | 内容 |
|------|---------|------|
| 紧凑 | ~220×240 | 日期（大字体）+ 下一个事件 + 农历日期 |
| 列表 | ~340×440 | 当前/接下来 3-5 天的事件列表 + 农历 |
| 周视图 | ~360×500 | 7 天周视图网格 + 农历标注 |
| 月视图 | ~380×520 | 完整月历 + 农历 + 节假日标注 |

### 农历显示（对齐 macOS 行为）
macOS 日历在「设置 → 辅助日历 → 农历」启用后，每个日期格子右下角会显示对应农历日数（如「初一」「廿八」等）。我们复刻这一行为：
- 在月视图/周视图每个日期格子中，公历日期旁显示农历日数
- 公历 1 日对应的格子显示农历月份名（如「正月」「二月」）
- 冬至、夏至、春分、秋分四个关键节气可选项高亮
- 除夕和高亮
- **不需要**：二十四节气全量展示、宜忌、黄历等 — 那不是 macOS 日历的行为

实现：`LunarCalendar.cs` 使用天文算法完全离线计算，无需网络。

### 日历同步
- **协议**：CalDAV (RFC 4791)，iCloud 服务器 `caldav.icloud.com`
- **认证**：Apple ID + App-Specific Password (Basic Auth)
- **同步策略**：首次全量 → sync-token 增量轮询 (60s 间隔)
- **方向**：双向同步，冲突以 iCloud 服务端为准

## 开发命令

```bash
# 确保在项目目录
cd I:/WinMacCalendar

# 还原依赖
dotnet restore

# 构建
dotnet build

# 运行
dotnet run

# 发布（框架依赖，体积小，推荐）
dotnet publish -c Release -o ./publish

# 发布（自包含，可独立运行但体积较大）
dotnet publish -c Release -o ./publish --self-contained -p:PublishTrimmed=true
```

## Git 工作流

- 主分支：`main`
- 远程仓库：`git@github.com:Yummy-He/WinMacCalendar.git`
- 提交信息用中文，格式：`类型: 简述`
  - `feat:` 新功能
  - `fix:` 修复
  - `refactor:` 重构
  - `style:` UI/样式调整
  - `docs:` 文档

## 设计原则

1. **轻量优先**：避免引入大型依赖，能用系统自带的就用系统自带
2. **Win11 原生感**：使用 Mica/Acrylic 材质，Segoe UI Variable 字体，跟随系统主题
3. **离线可用**：本地 SQLite 缓存保证无网络时完整可用
4. **安全存储**：敏感凭证通过 Windows DPAPI 加密
5. **无第三方通信**：CalDAV 直连 iCloud，不经过任何中间服务器
