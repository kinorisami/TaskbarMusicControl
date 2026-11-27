# Taskbar Music Controls

一个运行在 Windows 任务栏上的轻量音乐控制与歌词组件。它通过 Windows Global System Media Transport Controls（GSMTC）读取当前媒体会话，把播放控制、歌曲信息和歌词直接画进任务栏，不占用桌面空间，也不需要切换窗口。

## 功能

**媒体与播放**

- 显示歌曲标题、艺人和专辑封面。
- 控制上一首、播放/暂停、下一首；具体可用操作取决于播放器上报的媒体会话能力。
- 左键点击封面可以唤起播放器窗口，再点一次收回；中键点击封面复制「标题 - 艺人」。
- 同时运行多个播放器时，可以指定跟踪哪一个。

**歌词**

- 逐句同步显示歌词，支持原词与翻译双轨。
- 歌词来源可配置：本地文件、网易云音乐、LRCLIB，并按顺序查找。
- 单曲可以锁定到某一个来源，不受全局设置影响。
- 歌词偏移按歌曲单独保存，修正不同歌词版本的打轴误差。

**显示**

- 三种布局：完整、封面与控制按钮、仅封面。
- 跟随任务栏的深色/浅色外观自动换色。
- 任务栏空间不足时自动压缩到更紧凑的布局，空间恢复后自动展开。

**任务栏位置**

- 可以挂到任意一台显示器的任务栏上。
- Windows 11 图标居中时任务栏有两块空档，可以分别选择区块，再选择贴左还是贴右。

**托盘与显隐**

- 通知区域有托盘图标：左键显示/隐藏组件，右键打开完整菜单。
- 支持三种隐藏方式：手动隐藏、无播放时隐藏、暂停超时隐藏。

## 显示布局

<img width="612" height="84" alt="image" src="https://github.com/user-attachments/assets/d86606ec-a80e-455d-9d88-4528991e1a54" />
<img width="612" height="84" alt="image" src="https://github.com/user-attachments/assets/c57f909d-c22b-4a53-a262-70e97c4e52c5" />
<img width="2560" height="84" alt="image" src="https://github.com/user-attachments/assets/39170e90-c827-454b-97fe-f72b75d06370" />
<img width="2560" height="84" alt="image" src="https://github.com/user-attachments/assets/68f5bed3-bc58-409a-b910-d75494a29516" />


| 布局 | 内容 |
|---|---|
| **完整**（默认） | 歌词铺满整块区域；鼠标移入时，播放控制、进度条、复制与翻译按钮从上方展开。没有歌词时显示歌曲名和艺人。 |
| **封面与控制按钮** | 封面 +右侧控制列，不显示歌词。 |
| **仅封面** | 只有专辑封面，鼠标悬停时显示播放/暂停按钮。 |


歌词只在**完整**布局下显示：另外两种布局不放歌词按钮，也放不下歌词面板。切换布局不会改变已经打开的歌词开关，切回完整布局时会重新显示歌词。

## 系统要求

- **Windows 11**（主要支持并测试的平台）。
- Windows 10 未经测试：组件依赖 Windows 11 任务栏的 UI Automation 结构，在 Windows 10 上可能无法定位任务栏。
- 播放器需要向 Windows 提供 GSMTC 媒体会话（网易云音乐、Spotify、浏览器播放器等大多支持）；不提供该会话的播放器无法显示媒体信息或控制播放。
- 从源码构建需要 .NET 10 SDK；使用发布包则不需要安装任何东西。

## 下载与安装

在 Releases 页面下载发布包，解压后直接运行 `TaskbarMusicControls.exe`，无需安装：

| 发布包 | 说明 |
|---|---|
| `TaskbarMusicControls-v1.0.0-win-x64-standalone` | **推荐**。运行时已打包进去，目标机器无需安装任何东西。 |
| `TaskbarMusicControls-v1.0.0-win-x64-net10` | 体积更小，但目标机器需要预装 .NET 10 桌面运行时。 |

- 同一时刻只允许运行一个实例，重复启动会被拒绝。
- 如果希望开机自动启动，右键组件（或托盘图标）→ `Start with Windows`。
- 组件不会创建桌面快捷方式或开始菜单项；需要固定时请自行把 exe 固定到任务栏/开始菜单。

## 使用

### 右键菜单

右键组件或托盘图标打开：

| 菜单项 | 作用 |
|---|---|
| `Layout` | 切换三种显示布局 |
| `Lyric source` | `Lock to X` / `Locked to X`：把当前歌曲锁定到某个歌词源（再点一次取消）。`Refresh lyrics (this song)`：重新联网查询当前歌曲。`Local files first`：本地文件优先。`NetEase → LRCLIB` / `LRCLIB → NetEase`：两个在线源的查找顺序 |
| `Lyric timing (this song)` | 当前歌曲的歌词偏移：−1.0 s / −0.2 s / +0.2 s / +1.0 s，以及归零 |
| `Taskbar` | 选择挂到哪一台显示器的任务栏 |
| `Block` | 选择任务栏上的哪一块空档：图标左侧还是右侧（仅在图标居中时出现） |
| `Alignment` | 在所选空档里贴左端还是右端 |
| `Player` | 存在多个播放器时，选择跟踪哪一个（`Follow the current player` 表示跟随系统当前会话） |
| `Visibility` | 手动隐藏、无播放时隐藏、暂停超时隐藏；隐藏时会显示当前原因 |
| `Start with Windows` | 开机自动启动 |
| `Exit Taskbar Music Widget` | 退出程序 |

### 托盘图标

- **左键**：显示 / 隐藏组件。
- **右键**：打开与组件相同的菜单。
- 鼠标悬停提示会说明状态：`Taskbar Music Widget`、`… - by hand`、`… - no media`、`… - paused`，或任务栏没有空间时的 `… - no room on the taskbar`。

任务栏放不下组件、或组件被隐藏时，托盘图标是回到设置的唯一入口。

### 鼠标操作

| 操作                     | 效果                                       |
| ------------------------ | ------------------------------------------ |
| 左键点击封面             | 唤起播放器窗口；再点一次收回               |
| 中键点击封面             | 复制「标题 - 艺人」                        |
| 右键                     | 打开菜单                                   |
| Ctrl + 滚轮              | 按 ±0.2 秒调整当前歌曲的歌词偏移           |
| 鼠标移入完整布局的文字区 | 展开控制条（进度条、复制、翻译、播放控制） |

### 隐藏与显示

三种隐藏方式可以组合使用，优先级从高到低：

1. **手动隐藏**（`Hide the widget`）：一直保持隐藏，直到再次打开。
2. **无播放时隐藏**（`Hide when nothing is playing`）：没有任何播放器提供媒体会话时隐藏。
3. **暂停超时隐藏**（`Hide when paused`）：暂停后超过所选时间（15 秒 / 30 秒 / 1 分钟 / 5 分钟）隐藏。

隐藏时组件只是变成透明，不会退出程序，也不会停止跟踪播放器；重新开始播放或按托盘左键即可恢复。用托盘左键恢复一个「因无播放而隐藏」的组件时，它会保持显示，直到下次开始播放。

隐藏期间歌词同步与布局轮询定时器会停止，以减少空闲开销。

## 歌词

### 本地歌词

本地歌词放在：

```text
%LOCALAPPDATA%\TaskbarMusicControls\Lyrics
```

支持 `.lrc` 和 `.txt`，优先查找以下文件名：

```text
艺人 - 歌曲名.lrc
歌曲名.lrc
艺人 - 歌曲名.txt
歌曲名.txt
```

带时间戳的 LRC 可以逐句同步；纯文本歌词只能整篇显示、不能跟随进度。同一时间戳的连续两行会作为原词和译词上下显示：

```text
[00:12.00]Original lyric
[00:12.00]译文
```

### 在线歌词

在线来源为网易云音乐和 LRCLIB，按下面的设置决定顺序：

- **本地文件优先（`Local files first` 勾选）**：先查本地文件，再查两个在线来源的**缓存**，都没有时才联网请求两个接口。
- **来源优先（不勾选）**：按顺序处理每一个来源 —— 先查该来源的缓存，未命中再联网请求该接口，然后才轮到下一个来源。

被 `Lock to X` 锁定的歌曲只使用锁定的来源（先缓存后接口），不再参考上面的顺序。`Refresh lyrics` 会跳过缓存重新查询，但不会删除已有缓存；查询失败时继续使用缓存结果。

在线结果按「艺人 / 歌曲 / 专辑」生成哈希缓存，保存在 `%LOCALAPPDATA%\TaskbarMusicControls\Lyrics\Cache`，每个来源一个文件。组件只在本地没有歌词时才会访问网络，不需要账号登录。

### 歌词偏移

不同版本的歌词文件可能整体偏早或偏晚，可以按歌曲微调：

- 右键 → `Lyric timing (this song)` 选择步进；
- 或按住 Ctrl 在组件上滚动滚轮，每次 ±0.2 秒。

偏移按歌曲保存，切换歌曲后各自的偏移互不影响。

## 数据与日志

全部数据位于 `%LOCALAPPDATA%\TaskbarMusicControls`：

| 路径 | 内容 |
|---|---|
| `settings.json` | 全部设置，见下表 |
| `Lyrics\` | 本地歌词文件，可自行放入 |
| `Lyrics\Cache\<hash>.<source>.json` | 在线歌词缓存，每个来源一个文件 |
| `Lyrics\Cache\<hash>.meta.json` | 单曲状态：歌词偏移、锁定的来源、最后使用的来源 |
| `Logs\` | 诊断日志，见下 |

`settings.json` 字段：

| 字段 | 含义 |
|---|---|
| `DisplayMode` | 显示布局：`Full` / `CoverAndControls` / `CoverOnly` |
| `TaskbarDeviceId` | 选中的任务栏（显示器） |
| `Block` | 任务栏空档：`Left` / `Right` |
| `Alignment` | 空档内的贴边方式：`Left` / `Right` |
| `TaskbarCentered` | 自动探测到的任务栏图标对齐方式，只读 |
| `LyricsPreferLocal` | 是否本地文件优先 |
| `LyricsOrder` | 在线来源顺序：`NeteaseFirst` / `LrclibFirst` |
| `LyricsMode` / `ShowTranslation` | 上次的歌词开关与翻译开关 |
| `HideWhenIdle` | 无播放时隐藏 |
| `ManualHidden` | 手动隐藏状态 |
| `PauseHideSeconds` | 暂停超时隐藏的秒数，`0` 表示关闭 |
| `PlayerId` | 跟踪的播放器，空表示跟随系统当前会话 |
| `StartWithWindows` | 开机自启 |

这是唯一的设置文件：读写都只走它。文件缺少 `Version` 时会被忽略并使用默认值。

日志文件：

- `Logs\taskbar-layout.log`：任务栏布局探测与定位。
- `Logs\playback-sync.log`：播放位置校正。
- `Logs\artwork.log`：封面读取与切换。

调试构建会记录全部过程，发布版只在出错时记录 `ERROR` 行，因此发布版的日志通常是空的。日志按大小自动轮转。

## 网络与隐私

- 组件不需要账号、不上传任何数据。
- 只有启用了在线歌词来源、且本地与缓存都没有命中时，才会用当前的歌曲标题、艺人、专辑和时长向歌词服务发起查询。
- 查询结果只保存在本机缓存中。

## 已知限制

- 组件通过把窗口挂接到 Windows Explorer 的任务栏窗口来实现融合显示；Windows 没有通用的第三方任务栏组件接口，因此 Windows 更新或 Explorer 行为变化可能影响兼容性。
- 挂接属于跨进程操作，Windows 会阻止权限级别不同的进程之间挂接：如果资源管理器（Explorer）以管理员身份运行，组件也需要以管理员身份运行，反之亦然。挂接失败时组件会弹窗说明，而不是静默消失。
- 任务栏上的可用区域依赖 UI Automation 扫描；Explorer 不暴露预期控件时，组件可能无法定位或只能使用更紧凑的布局。
- 打开开始菜单、通知中心等系统浮层期间，Explorer 会暂时不再把任务栏作为顶层窗口暴露出来。组件会保持显示，并在浮层关闭后主动刷新布局。
- 关闭承载组件的显示器，或 Explorer 重启时，组件会重新挂接到仍在的任务栏上，通常需要半秒到一秒。
- 歌曲信息、播放位置和控制能力取决于播放器通过 GSMTC 上报的数据；不同播放器支持程度不同。
- 歌词同步精度还受歌词时间戳与录音版本影响。
- 发布版不记录常规诊断日志；遇到问题时如需要排查，可换用 Debug 构建。

## 从源码运行

在仓库根目录打开 PowerShell：

```powershell
dotnet run --project .\TaskbarMusicControls\TaskbarMusicControls.csproj
```

使用 Visual Studio 时，打开 `TaskbarMusicControls/TaskbarMusicControls.csproj`，选择 `Debug` 或 `Release` 配置后运行。

## 构建

```powershell
dotnet restore .\TaskbarMusicControls\TaskbarMusicControls.csproj
dotnet build .\TaskbarMusicControls\TaskbarMusicControls.csproj -c Release
```

## 发布

生成适用于 64 位 Windows 的独立发布目录（目标机器无需预装 .NET Desktop Runtime）：

```powershell
dotnet publish .\TaskbarMusicControls\TaskbarMusicControls.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\TaskbarMusicControls-v1.0.0-win-x64-standalone
```

生成依赖已安装的 .NET Desktop Runtime 的单文件发布目录：

```powershell
dotnet publish .\TaskbarMusicControls\TaskbarMusicControls.csproj -c Release -r win-x64 `
  --no-self-contained -p:PublishSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\TaskbarMusicControls-v1.0.0-win-x64-net10
```

两种发布目录都生成在项目根目录下：

- `TaskbarMusicControls-v1.0.0-win-x64-standalone`：**独立发布**，运行时已打包进去，目标机器无需安装任何东西 —— 分发给用户请用这个。
- `TaskbarMusicControls-v1.0.0-win-x64-net10`：**依赖 .NET 10 桌面运行时**，体积更小，但目标机器没有安装 .NET 10 Desktop Runtime 时会弹出“需要下载运行时”的提示。

## 致谢与依赖 (Credits)

本项目未引入第三方 NuGet 包。歌词与媒体数据来自以下服务：

- 网易云音乐：通过其非官方 Web API（`music.163.com/api/search/get`、`music.163.com/api/song/lyric`）实时获取歌词，版权归网易云音乐及相关版权方所有。
- LRCLIB（https://lrclib.net）：作为歌词来源，提供公开歌词检索接口。
- Windows GSMTC（Global System Media Transport Controls）：用于读取当前媒体会话与播放状态。

感谢上述服务与接口的提供者。

## 免责声明 (Disclaimer)

1. 本项目**仅限个人学习、技术研究与交流使用**，严禁用于任何商业用途或大规模数据抓取。
2. 本项目不向第三方分发任何音频文件、歌词或封面图片；歌词与封面均实时获取自对应服务，仅在用户本机缓存歌词文本用于离线显示，其版权归网易云音乐、LRCLIB 及相关版权方所有。
3. 请在下载后 24 小时内删除测试数据。如因违规使用本项目而产生的任何法律纠纷，均由使用者自行承担，与本项目作者无关。
