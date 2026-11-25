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
- 右键组件可退出程序。
- 监视 Windows 任务栏变化，并在 Explorer 重启后尝试重新挂接组件。

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
dotnet publish .\TaskbarMusicControls\TaskbarMusicControls.csproj `
  -c Release -r win-x64 --self-contained true
```

发布文件位于 `TaskbarMusicControls/bin/Release/` 下对应的目标框架和运行时目录中。将完整发布目录打包分享，不要只复制主程序文件。

## 使用

1. 启动支持 Windows 媒体会话的播放器并开始播放。
2. 启动组件；它会自动检测任务栏并读取当前媒体会话。
3. 右键任务栏组件切换布局、选择歌词来源或退出。

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

带时间戳的 LRC 可用于逐句同步；纯文本歌词不能逐句同步。在线获取的歌词会缓存在 `Lyrics\Cache` 子目录中。

### 配置文件

组件设置位于 `%LOCALAPPDATA%\TaskbarMusicControls`：

- `display-mode.txt`：显示布局。
- `lyrics-source.txt`：歌词来源选择（`Automatic`、`Netease` 或 `Lrclib`）。
- `Lyrics\Offsets`：按歌曲保存的歌词偏移。
- `Lyrics\Cache`：在线歌词缓存。
- `Logs\playback-sync.log`：播放位置校正诊断日志（达到大小上限后滚动备份）。

## 网络与隐私

选择在线歌词来源后，组件会使用当前歌曲的标题、艺人、专辑和时长向对应歌词服务查询。Automatic 模式会在本地歌词未命中时访问网易云音乐，仍未命中时再查询 LRCLIB。查询结果会写入本地缓存。组件不需要账号登录。

## 已知限制

- 组件通过将窗口挂接到 Windows Explorer 的任务栏窗口来实现融合显示；Windows 没有通用的第三方任务栏音乐组件接口，因此 Windows 更新或 Explorer 行为变化可能影响兼容性。
- 歌曲信息、播放位置和控制能力取决于播放器通过 GSMTC 上报的数据；不同播放器支持程度不同。
- 歌词同步精度还受歌词时间戳和录音版本影响。

## 许可证

当前仓库尚未包含许可证文件。公开发布前，请添加适合项目的开源许可证；没有许可证时，其他人通常不能默认获得复制、修改或再发布项目的权限。
