# 歌词

本地歌词目录：`%LocalAppData%\TaskbarMusicControls\Lyrics`

支持 `.lrc` 和 `.txt`。文件名优先使用 `艺人 - 歌曲名.lrc`，也可以只用 `歌曲名.lrc`。
LRC 时间戳用于跟随播放进度；同一时间戳的连续两行会作为原词和译词上下显示：

```text
[00:12.00]Original lyric
[00:12.00]译文
```

本地没有匹配歌词时，先查询网易云，再回退到 LRCLIB；在线歌词不可用或都没有匹配结果时，控件继续显示歌曲名和艺人名。
在线歌词会按来源及艺人、歌曲、专辑生成本地 JSON 缓存，保存在
`%LOCALAPPDATA%\TaskbarMusicControls\Lyrics\Cache`。后续先读网易云缓存，再尝试网易云直连接口，然后读 LRCLIB 缓存并回退到 LRCLIB。

网易云采用非官方 Web API 的歌曲搜索与歌词接口；网易云更改或限制接口时，会自动继续尝试 LRCLIB。
