# Taskbar Music Controls

一个运行在 Windows 任务栏上的轻量音乐控制与歌词组件。它通过 Windows Global System Media Transport Controls（GSMTC）读取当前媒体会话，并把播放控制、歌曲信息和歌词显示在任务栏区域。

## 功能

- 显示歌曲标题、艺人、专辑封面和播放状态。
- 控制上一首、播放/暂停、下一首；具体可用操作取决于播放器提供的媒体会话能力。
- 三种显示布局：完整信息、封面与控制按钮、仅封面（悬停封面显示播放/暂停）。
- 显示同步歌词，支持原词和翻译双轨。
- 歌词偏移可按歌曲单独设置，修正不同歌词版本的打轴误差。
- 右键组件可选择歌词来源：
  - **Automatic**：依次查找本地歌词、网易云音乐、LRCLIB。
  - **NetEase Music**：只使用网易云音乐接口及对应缓存。
  - **LRCLIB**：只使用 LRCLIB 接口及对应缓存。
- 右键组件可切换显示布局、选择歌词来源、调整歌词偏移、选择要挂载的任务栏/显示器，以及退出程序。
- 监视 Windows 任务栏变化，并在 Explorer 重启后尝试重新挂接组件。

## 显示布局
<img width="2560" height="84" alt="image" src="https://github.com/user-attachments/assets/4e5039e8-d40f-463c-9961-0be89d8f883f" />
<img width="2560" height="84" alt="image" src="https://github.com/user-attachments/assets/39170e90-c827-454b-97fe-f72b75d06370" />
<img width="2560" height="84" alt="image" src="https://github.com/user-attachments/assets/68f5bed3-bc58-409a-b910-d75494a29516" />

歌词只在**完整信息**布局下显示：另外两种布局不放歌词按钮，也放不下歌词面板。切换布局不会改变已经打开的歌词开关，切回完整信息布局时会重新显示歌词。

## 系统要求

- Windows 10 版本 1809（内部版本 17763）或更新版本。
- .NET 10 SDK（从源码构建时）。
- 播放器需要向 Windows 提供 GSMTC 媒体会话；不提供该会话的播放器无法显示媒体信息或控制播放。

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
  -o .\TaskbarMusicControls-v0.2.0-win-x64-standalone
```

生成依赖已安装的 .NET Desktop Runtime 的单文件发布目录：

```powershell
dotnet publish .\TaskbarMusicControls\TaskbarMusicControls.csproj -c Release -r win-x64 `
  --no-self-contained -p:PublishSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\TaskbarMusicControls-v0.2.0-win-x64-net10
```

两种发布目录都生成在项目根目录下：

- `TaskbarMusicControls-v0.2.0-win-x64-standalone`：**独立发布**，运行时已打包进去，目标机器无需安装任何东西 —— 分发给用户请用这个。
- `TaskbarMusicControls-v0.2.0-win-x64-net10`：**依赖 .NET 10 桌面运行时**，体积更小，但目标机器没有安装 .NET 10 Desktop Runtime 时会弹出“需要下载运行时”的提示。

## 使用

1. 启动支持 Windows 媒体会话的播放器并开始播放。
2. 启动组件；它会自动检测任务栏并读取当前媒体会话。
3. 右键任务栏组件切换布局、选择歌词来源、调整歌词偏移、选择任务栏或退出。
4. 鼠标停在组件上时按住 Ctrl 滚动滚轮，可以按 ±0.2 秒微调当前歌曲的歌词偏移，不必反复打开菜单。

### 本地歌词

本地歌词保存在：

```text
%LOCALAPPDATA%\TaskbarMusicControls\Lyrics
```

支持 `.lrc` 和 `.txt`。自动模式下优先查找以下文件名：

```text
艺人 - 歌曲名.lrc
歌曲名.lrc
艺人 - 歌曲名.txt
歌曲名.txt
```

LRC 时间戳用于跟随播放进度；同一时间戳的连续两行会作为原词和译词上下显示：

```text
[00:12.00]Original lyric
[00:12.00]译文
```

带时间戳的 LRC 可用于逐句同步；纯文本歌词不能逐句同步。

本地没有匹配歌词时，先查询网易云，再回退到 LRCLIB；在线歌词不可用或都没有匹配结果时，控件继续显示歌曲名和艺人名。在线歌词会按来源及艺人、歌曲、专辑生成本地 JSON 缓存，保存在 `%LOCALAPPDATA%\TaskbarMusicControls\Lyrics\Cache`。后续先读网易云缓存，再尝试网易云直连接口，然后读 LRCLIB 缓存并回退到 LRCLIB。

网易云采用非官方 Web API 的歌曲搜索与歌词接口；网易云更改或限制接口时，会自动继续尝试 LRCLIB。

### 配置文件

组件设置位于 `%LOCALAPPDATA%\TaskbarMusicControls`：

- `settings.json`：全部设置——`DisplayMode`（显示布局）、`TaskbarDeviceId`（选中的任务栏/显示器）、`LyricsPreferLocal`（本地文件是否优先）与 `LyricsOrder`（`NeteaseFirst` 或 `LrclibFirst`）。单曲锁定源与歌词偏移保存在该曲的 `.meta.json` 里。
  这是唯一的设置文件：读写都只走它。文件里没有 `Version` 时会被忽略并使用默认值（暂不提供旧格式迁移）。
- `Lyrics\Cache\<hash>.meta.json`：单曲状态——歌词偏移、该曲锁定的歌词源、上次使用的源与匹配度。
- `Lyrics\Cache`：在线歌词缓存。
- `Logs\playback-sync.log`：播放位置校正诊断日志（仅 Debug 构建）。
- `Logs\taskbar-layout.log`：任务栏布局探测诊断日志（除错误信息外仅 Debug 构建；错误行在所有构建中都会记录）。
- `Logs\artwork.log`：封面读取与封面切换诊断日志（除错误信息外仅 Debug 构建；错误行在所有构建中都会记录）。

## 网络与隐私

选择在线歌词来源后，组件会使用当前歌曲的标题、艺人、专辑和时长向对应歌词服务查询。Automatic 模式会在本地歌词未命中时访问网易云音乐，仍未命中时再查询 LRCLIB。查询结果会写入本地缓存。组件不需要账号登录。

## 已知限制

- 组件通过将窗口挂接到 Windows Explorer 的任务栏窗口来实现融合显示；Windows 没有通用的第三方任务栏音乐组件接口，因此 Windows 更新或 Explorer 行为变化可能影响兼容性。
- 挂接窗口属于跨进程操作，Windows 会阻止权限级别不同的进程之间挂接：如果资源管理器（Explorer）以管理员身份运行，组件也需要以管理员身份运行，反之亦然。挂接失败时组件会弹窗提示，而不是静默消失。
- 同一时刻只允许运行一个组件实例：重复启动会被拒绝，避免两个组件叠在同一位置。
- 打开开始菜单等系统浮层期间，Explorer 会暂时不再把任务栏作为顶层窗口暴露出来（UI Automation 也读不到任务栏内容）。组件会保持显示，并在浮层关闭后主动刷新一次布局。
- 歌曲信息、播放位置和控制能力取决于播放器通过 GSMTC 上报的数据；不同播放器支持程度不同。
- 歌词同步精度还受歌词时间戳和录音版本影响。
- 任务栏可用区域依赖 UI Automation 扫描；部分 Explorer 版本不暴露预期控件时，组件可能无法定位。


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

