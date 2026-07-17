# WinMacCalendar — 实现计划

> 项目位置：`I:\WinMacCalendar` | 远程仓库：`git@github.com:Yummy-He/WinMacCalendar.git`

## Context

在 Windows 11 上创建一个固定在桌面的日历小组件，参考 macOS 日历小组件的多尺寸设计，通过 CalDAV 协议与 iCloud/Apple 日历实现双向同步。采用轻量原生方案（WPF .NET 9），追求小体积、低内存占用（对标 TranslucentTB）。

**环境：** .NET 9.0.306 已安装，Windows 11 Pro for Workstations。

---

## 一、整体架构

```
WinMacCalendar (WPF .NET 9)
├── WidgetWindow          — 桌面钉住的无边框透明窗口
├── CalendarUI            — 四尺寸日历渲染 (Small/Medium/Week/Large)
├── LunarCalendar         — 农历日数显示（对齐 macOS 行为）
├── CalDAVSyncEngine      — iCloud CalDAV 双向同步引擎
├── LocalDataStore        — SQLite 本地缓存 + 配置存储
├── TrayIcon              — 系统托盘图标 & 右键菜单
├── SettingsWindow        — Apple ID / 同步 / 外观设置
└── CredentialManager     — Windows DPAPI 加密存储密码
```

---

## 二、项目结构

```
WinMacCalendar/
├── WinMacCalendar.csproj         — 项目文件 (.NET 9, WPF)
├── App.xaml/.cs                  — 应用入口、单实例检测
├── MainWindow.xaml/.cs           — 桌面小组件窗口（透明无边框）
├── Widget/
│   ├── CalendarWidget.xaml/.cs   — 控件容器，管理尺寸切换
│   ├── SmallView.xaml/.cs        — 紧凑尺寸：日期 + 下个事件 + 农历
│   ├── MediumView.xaml/.cs       — 列表尺寸：多日事件列表 + 农历
│   ├── WeekView.xaml/.cs         — 周视图：7天网格 + 农历标注
│   ├── LargeView.xaml/.cs        — 月视图：完整月历 + 农历 + 节假日
│   └── WidgetViewModel.cs        — 数据绑定 ViewModel
├── CalDAV/
│   ├── CalDavClient.cs           — CalDAV 协议实现 (PROPFIND/REPORT/PUT/DELETE)
│   ├── SyncEngine.cs             — 增量同步调度 (sync-token)
│   ├── ICloudAuthenticator.cs    — iCloud Basic Auth 认证
│   └── Models/
│       ├── CalDavCalendar.cs     — 日历对象
│       └── CalDavEvent.cs        — 事件对象 (.ics 解析)
├── Storage/
│   ├── Database.cs               — SQLite 初始化与迁移
│   ├── EventRepository.cs        — 事件 CRUD
│   └── SettingsRepository.cs     — 配置读写
├── Services/
│   ├── Calendar/
│   │   └── LunarCalendar.cs      — 农历日数计算
│   ├── CredentialManager.cs      — DPAPI 凭证加解密
│   ├── TrayService.cs            — 托盘图标管理
│   └── WindowManager.cs          — 桌面层级管理 (Win32 P/Invoke)
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

---

## 三、核心技术方案

### 3.1 桌面钉住（WindowManager）

参考 TranslucentTB 的实现思路，使用 Win32 P/Invoke 将窗口固定在桌面层级：

```csharp
// 关键 Win32 API
SetWindowLong(hwnd, GWL_EXSTYLE, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)
SetWindowPos(hwnd, HWND_BOTTOM, ...)  // 保持在最底层
// 响应 WM_WINDOWPOSCHANGING 阻止被其他窗口管理器移动 z-order
```

窗口特性：
- `WindowStyle="None"` — 无边框
- `AllowsTransparency="True"` — 透明背景，支持圆角
- `Topmost="False"` — 不遮挡其他窗口
- `ShowInTaskbar="False"` — 不在任务栏显示
- `ResizeMode="CanResize"` — 但边框隐藏，尺寸通过右键菜单切换
- Win11 圆角：`WindowChrome` + `CornerRadius` 或依赖 Win11 自动 DWM 圆角

### 3.2 四种尺寸设计

| 尺寸 | 名称 | 窗口大小 | 内容 |
|------|------|---------|------|
| Small | 紧凑 | ~220×240 | 日期（大字体）+ 农历日期 + 下一个即将到来的事件标题 |
| Medium | 列表 | ~340×440 | 日期 + 农历 + 当前/接下来 3-5 天的事件列表 |
| Week | 周视图 | ~360×500 | 7 天周视图网格，每格显示事件条 + 农历标注 |
| Large | 月视图 | ~380×520 | 完整月历网格 + 农历日期 + 节假日标记 + 选中日期事件 |

> 窗口大小后续可根据实际效果灵活调整。

风格方向：
- **背景**：Win11 Mica Alt 材质（半透明、随桌面壁纸变色），比 macOS 的毛玻璃更低调
- **圆角**：12-16px 圆角，匹配 Win11 设计语言
- **字体**：Segoe UI Variable（Win11 系统字体），中文和英文都自然
- **配色**：跟随系统浅色/深色模式，使用 Win11 系统刷色
- **交互**：右键菜单切换尺寸/设置/刷新，左键拖拽移动

### 3.3 日历渲染

WPF 原生绘制，不使用 WebView2：
- **月视图**：6×7 Grid 构建日历网格，自定义 `CalendarCell` 控件，每个格子显示公历日期 + 农历日期 + 事件圆点
- **周视图**：7 列 Grid，每列显示当天的事件条 + 农历日期
- **列表视图**：`ItemsControl` + 自定义 `EventCard` 模板
- **紧凑视图**：`StackPanel` 垂直布局 + `TextBlock` 绑定
- 今日日期高亮使用系统 Accent Color
- 事件圆点指示器（类似 macOS，但用 Win11 色板）
- 节气日用特殊颜色标记

### 3.4 农历显示（对齐 macOS 行为）

macOS 日历在「设置 → 辅助日历 → 农历」启用后，月视图每个日期格子右下角会显示对应的农历日数（如「初一」「廿八」），公历 1 日对应的格子显示月份名（如「正月」「二月」）。我们复刻这一行为。

**农历算法：**
- 使用天文算法完全离线计算，无需网络
- 基于朔日数据推算农历月日
- 精度范围：1900-2100 年

**显示内容：**
- 月视图/周视图每个日期格子右下角显示农历日数（初一～三十）
- 公历 1 日对应的格子显示农历月份名
- 除夕高亮为红色
- **不需要**：二十四节气全量展示、宜忌、黄历等 — 那不是 macOS 日历的行为

实现方案将只放在 `Services/Calendar/LunarCalendar.cs` 中完成。

### 3.5 CalDAV 同步引擎

**连接 iCloud：**
```
Server URL:  https://caldav.icloud.com/
Auth Method: Basic Auth
Username:    用户的 Apple ID 邮箱
Password:    应用专用密码 (App-Specific Password)
Discovery:   通过 .well-known/caldav → principal → calendar-home-set → calendars
```

**同步策略：**
1. **首次全量同步**：拉取所有日历下的事件（范围：过去1月～未来12月）
2. **增量同步**：使用 CalDAV `sync-collection` REPORT + `sync-token`，每 60 秒检查一次
3. **双向写入**：
   - 在小组件中创建/编辑/删除事件 → `PUT`/`DELETE` 推送到 iCloud
   - iCloud 变更 → 增量同步拉回
4. **冲突处理**：以 iCloud 为准（服务端优先），本地为影子副本

**CalDAV 操作映射：**
| 操作 | HTTP 方法 | CalDAV 路径 |
|------|----------|-------------|
| 发现 Principal | PROPFIND | `/` |
| 获取日历列表 | PROPFIND | `/{principal}/calendars/` |
| 获取事件列表 | REPORT (calendar-query) | `/{calendar}/` |
| 增量同步 | REPORT (sync-collection) | `/{calendar}/` |
| 创建事件 | PUT | `/{calendar}/{uid}.ics` |
| 更新事件 | PUT | `/{calendar}/{uid}.ics` |
| 删除事件 | DELETE | `/{calendar}/{uid}.ics` |

**认证安全：**
- 应用专用密码使用 Windows DPAPI (`ProtectedData.Protect`) 加密存储在本地
- 只在发起 CalDAV 请求时解密到内存
- 不与任何第三方服务器通信（纯本地应用）

### 3.6 本地存储

使用 **SQLite** (via `Microsoft.Data.Sqlite`)：
```sql
-- 事件缓存表
CREATE TABLE Events (
    Id TEXT PRIMARY KEY,        -- CalDAV UID
    CalendarId TEXT NOT NULL,
    Title TEXT NOT NULL,
    Description TEXT,
    Location TEXT,
    StartDate TEXT NOT NULL,    -- ISO 8601
    EndDate TEXT NOT NULL,
    IsAllDay INTEGER DEFAULT 0,
    RecurrenceRule TEXT,
    SyncToken TEXT,
    LastModified TEXT
);

-- 日历表
CREATE TABLE Calendars (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Color TEXT,
    SyncToken TEXT,
    IsEnabled INTEGER DEFAULT 1
);

-- 设置表
CREATE TABLE Settings (
    Key TEXT PRIMARY KEY,
    Value TEXT
);
```

### 3.7 系统托盘

```
托盘图标右键菜单：
├── 尺寸 →
│   ├── 紧凑 (Small)
│   ├── 列表 (Medium)
│   ├── 周视图 (Week)
│   └── 月视图 (Large)
├── 立即同步
├── 设置...
├── 关于
└── 退出
```

### 3.8 设置面板

一个独立的 WPF 窗口，包含：
- **Apple ID** 标签页：Apple ID 邮箱输入、App-Specific Password 输入、连接测试按钮
- **日历选择** 标签页：列出 iCloud 中的日历，勾选需要显示的日历、每个日历分配颜色
- **外观** 标签页：默认尺寸、是否跟随系统主题、透明度调节、农历显示开关
- **同步** 标签页：同步间隔、手动同步按钮、上次同步时间
- **开机自启**：写入注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

---

## 四、开发步骤

### Step 1: 项目骨架
- 创建 WPF 项目 `WinMacCalendar`
- 配置 `.csproj`：`TargetFramework=net9.0-windows`、`UseWPF=true`、`PublishTrimmed=true`
- NuGet 包：`Microsoft.Data.Sqlite`

### Step 2: 桌面窗口
- 实现 `WindowManager.cs`（Win32 P/Invoke 封装）
- 创建 `MainWindow` 无边框透明窗口 + 桌面钉住
- 窗口拖拽移动、双击回到默认位置
- 应用 Win11 Mica 背景效果

### Step 3: 农历模块
- 实现 `LunarCalendar.cs` 天文农历计算
- 显示农历日数（初一～三十）和月份名

### Step 4: 日历 UI（四种视图）
- `CalendarWidget` 用户控件 + ViewModel
- `SmallView` / `MediumView` / `WeekView` / `LargeView` 四个视图
- 月视图和周视图日历网格渲染 + 农历标注
- 事件卡片样式
- 尺寸切换动画（Window resize + 内容淡入淡出）

### Step 5: CalDAV 客户端
- `CalDavClient.cs`：PROPFIND、REPORT、PUT、DELETE 协议实现
- iCalendar (.ics) 解析与生成
- iCloud 认证与 Principal 发现

### Step 6: 同步引擎 + 本地存储
- SQLite 数据库初始化和仓储层
- `SyncEngine` 首次全量同步 → 增量轮询
- 事件变更推送到 iCloud

### Step 7: 设置 + 托盘
- 设置窗口（Apple ID 配置、日历选择、外观）
- 系统托盘图标 + 右键菜单
- 开机自启

### Step 8: 打磨
- 深色/浅色模式自适应
- 过渡动画
- 错误处理与重试
- 打包发布

---

## 五、关键技术要点

1. **桌面层级稳定性**：Win11 下 `HWND_BOTTOM` 可能被桌面刷新打乱，需要定时器（每 5 秒）检测并修正 z-order
2. **DPAPI 加密**：使用 `System.Security.Cryptography.ProtectedData`，加密结果绑定当前 Windows 用户账户
3. **单实例应用**：使用 `Mutex` 防止重复启动
4. **离线可用**：本地 SQLite 缓存保证断网时日历仍可查看
5. **iCalendar 时区**：正确解析和生成 `VTIMEZONE` 组件，确保跨时区事件准确性
6. **农历精度**：天文算法需要验证精度，确保 1900-2100 年范围内结果与中国官方农历一致

---

## 六、验证计划

1. **窗口行为**：启动后窗口出现在桌面右下角、不遮挡任务栏、Win+D 后仍然可见、重启后位置保持
2. **CalDAV 连接**：在设置面板输入 Apple ID + App-Specific Password → 点击测试连接 → 显示日历列表
3. **同步验证**：在 iPhone/iPad 日历创建一个事件 → 60 秒内小组件显示该事件
4. **双向同步**：在小组件创建事件 → iPhone 日历中可见
5. **四种尺寸**：右键切换无报错、UI 正确渲染、窗口尺寸正确
6. **农历**：月视图/周视图中农历日期显示正确、节气标注正确
7. **系统主题**：切换 Windows 深色/浅色模式 → 小组件自适应
