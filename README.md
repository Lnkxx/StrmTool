# StrmTool for Jellyfin（2.4.3.x 分支）

Jellyfin 插件：为 `.strm` 文件提前提取并保存媒体技术信息（编码、分辨率、音轨、字幕、章节），让详情页显示完整，并减少 STRM 播放前的等待。

本仓库的默认分支为 `jellyfin-2.4.3.x`，面向 Jellyfin 10.11；上游当前默认分支面向 Emby。

本仓库基于 [jinlin-teck/StrmTool](https://github.com/jinlin-teck/StrmTool) 2.4.0 修改，沿用 GPL-3.0 许可。主要增加了四件事：

- **跳过播放前的强制探测**：缓存有效时避免重复远程探测；一次实播对比中，服务端等待从约 13 秒缩短至亚秒级。
- **元数据恢复**：Jellyfin 把 STRM 的媒体信息冲掉后，自动从缓存恢复，外挂字幕也一并补回。
- **ISO 原盘探测**：远程 ISO 不用下载整盘，就能得到视频、音轨、字幕、章节和码率。
- **探测可靠性**：结果写入后读回核对，失败时不留下半截数据。

适配 Jellyfin 10.11.x，在 10.11.11 上实际运行。

---

## 为什么 STRM 起播慢

播放器点下播放后，先向 Jellyfin 请求播放信息（PlaybackInfo）。Jellyfin 在回答之前有一条固定规则（`MediaSourceManager.GetPlaybackMediaSources`）：

```csharp
// If file is strm or main media stream is missing, force a metadata refresh with remote probing
if (allowMediaProbe && mediaSources[0].Type != MediaSourceType.Placeholder
    && (item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || ...))
{
    await item.RefreshMetadata(new MetadataRefreshOptions(_directoryService)
    {
        EnableRemoteContentProbe = true,
        MetadataRefreshMode = MetadataRefreshMode.FullRefresh
    }, cancellationToken);
}
```

只要路径以 `.strm` 结尾，就先做一次完整刷新加远程 ffprobe，不管库里是否已经有完整的媒体信息。播放器要等它做完才能拿到回答。

这次探测还有两个副作用：

- 它把条目的大小写成 STRM 文本文件自身的大小（几十到一百字节），并可能丢掉码率、位深等细节。
- 它重写的流列表里没有 STRM 旁边的外挂字幕。

这段逻辑在 Jellyfin `release-10.11.z`、`v12.4` 和 `master` 上相同，本分支针对上述行为进行处理。上游相关讨论：

- [jellyfin/jellyfin#17050](https://github.com/jellyfin/jellyfin/pull/17050)：只在缺少媒体流时才重探 STRM
- [jellyfin/jellyfin#17790](https://github.com/jellyfin/jellyfin/pull/17790)：修复播放刷新时外挂字幕被丢掉
- [jellyfin/jellyfin#18482](https://github.com/jellyfin/jellyfin/issues/18482)：播放时重复调用元数据提供方导致延迟

### 实测

环境：一台家用 NAS 上的 Jellyfin 10.11.11，STRM 指向网盘 302 直链，播放器为 SenPlayer、网易爆米花、Infuse。数据取自 302 服务约 12 天的访问日志，计算的是“Jellyfin 的 ffprobe 第一次请求该文件”到“播放器拿到地址”的间隔。

| 类型 | 播放次数 | 中位数 | p75 | 最长 |
|---|---|---|---|---|
| 电影 | 53 | 13.8 秒 | 21.8 秒 | 88.8 秒 |
| 剧集 | 51 | 4.9 秒 | 7.3 秒 | 29.5 秒 |

同一环境里，其他环节都很快：302 首次取地址 0.4–1.9 秒，网盘 CDN 首字节 0.2–0.3 秒，ffmpeg 从打开到第一帧约 1.5 秒。这次优化针对重复探测这一段等待。

装上 2.4.3.7 后，用同一部 88 GB 的 4K 电影、同一个播放器起播：播放信息在 42 毫秒内返回，播放器在约 0.2 秒后请求地址，播放前不再有 ffprobe 请求。同一部电影当天早些时候的那次播放，这段等待约 13 秒。

这是单次对比，不是统计结论，也不是点击播放到出现画面的完整耗时。大样本的中位数要运行一段时间后才能给出。后续 Infuse 实播中也确认跳过了探测，但用户仍感受到约 7–8 秒起播；播放器读取、缓冲及其他网络环节仍会影响体验。

---

## 跳过播放前探测是怎么做的

### 做法

Jellyfin 的 `GetPlaybackMediaSources` 本身带一个参数 `allowMediaProbe`，为 false 时不探测，直接用库里存的信息回答。这个参数在 Jellyfin 里只控制上面那一次刷新。

Jellyfin 启动时先注册自己的服务，再调用各插件的 `IPluginServiceRegistrator`，后注册的生效。插件利用这一点，把 `IMediaSourceManager` 换成一个包装层：

- 接口的 21 个方法里，20 个原样转发给 Jellyfin 自己的实现；
- 只有 `GetPlaybackMediaSources`，在下面的规则成立时把 `allowMediaProbe` 改成 false 再转发。

Jellyfin 自己的实现仍然只有一个实例，由容器创建和释放。不修改 Jellyfin 的任何文件，不直接写数据库。

### 什么时候跳过

全部满足才跳过，任何一条不满足就按 Jellyfin 原来的方式探测：

1. 开关“跳过播放前探测”打开。
2. 路径以 `.strm` 结尾，并在探测路径过滤的范围内。
3. 同目录的 `.strmtool.json` 缓存有效：记录的 STRM 内容 SHA-256 与当前文件一致，大小、时长、流信息齐全，视频流有宽高。
4. 远程 ISO 的缓存已是当前探测版本。
5. 条目里保存的播放地址与 STRM 当前内容一致。Jellyfin 只在探测时更新这个地址；STRM 被原地替换后，不重探会播到旧地址。
6. 库里有内嵌的视频流和音频流。
7. 条目没有被重置：大小、时长、分辨率都在，流的细节没丢，磁盘上有效的外挂字幕都在库里。

第 7 条不满足时，先用缓存恢复条目（最多等 5 秒），恢复成功再跳过。

因此新入库、还没有缓存的 STRM 照常探测；STRM 内容变了也照常探测。

### 出错时的行为

- 判断规则出任何异常，按原参数调用，行为与不装插件相同。
- 包装层创建失败，或者 Jellyfin 的服务注册形态与预期不同，则不替换，Jellyfin 保持原样。
- 关掉开关立即生效，不用重启。

### 同样受益的调用

除了播放信息，Jellyfin 在提取内嵌字幕和附件时也走同一个入口并要求探测。包装层对它们用同一条规则，所以播放过程中取字幕也不会再触发探测。

### 日志

```
[INF] StrmTool.StrmPlaybackMediaSourceManager: Playback probe gate installed over "Emby.Server.Implementations.Library.MediaSourceManager"
[INF] StrmTool.StrmPlaybackMediaSourceManager: Playback probe skipped for "影片名": cache and stored streams are current
[INF] StrmTool.StrmPlaybackMediaSourceManager: Playback probe kept for "影片名": NoValidCache
```

`kept` 后面是保留探测的原因：

| 原因 | 含义 |
|---|---|
| `NoValidCache` | 没有缓存，或缓存与当前 STRM 内容不符 |
| `IsoUpgradePending` | 远程 ISO 的缓存来自旧版探测，需要重探一次 |
| `ShortcutStale` | 条目里的播放地址与 STRM 内容不一致 |
| `StreamsIncomplete` | 库里缺视频流或音频流 |
| `NeedsRestore` | 条目被重置，且未能在等待时间内恢复 |

---

## 其他功能

### 媒体信息提取与缓存

- STRM 入库后在后台向远程地址探测一次，把结果写进 Jellyfin。
- 结果同时保存为 STRM 同目录下的 `<文件名>.strmtool.json`，带 STRM 内容的 SHA-256。重建媒体库时可以直接从缓存导入，不用重新探测。
- 提供计划任务 `Extract Strm Media Info`，可手动运行或定时执行。

### 元数据恢复

Jellyfin 在扫库或刷新时可能把 STRM 条目重置。插件监听条目更新，发现下列情况就从缓存恢复：

- 大小被写成 STRM 文本文件的大小；
- 时长或分辨率丢失；
- 流的码率、位深等细节丢失；
- STRM 旁边的外挂字幕没有出现在流列表里。

只恢复“被重置”的值。库里的真实值与缓存不同（两次探测结果不一致）时，保留库里的值。

外挂字幕的识别：STRM 同目录下、文件名以“STRM 文件名加点”开头的 `.srt` / `.ass` / `.ssa` / `.vtt`。语言和 default / forced / sdh 标记交给 Jellyfin 自己的解析器。是否可用按 FFmpeg 的探测规则判断，GBK、UTF-16 等编码都接受；文件开头残缺或 ASS 头损坏的不会加入。

### ISO 原盘探测

- **本地 ISO**：使用带 `libbluray` 的静态 `ffprobe`，等价于
  ```bash
  ffprobe -v error -print_format json -show_format -show_streams \
    -show_chapters -show_programs 'bluray:/path/to/disc.iso'
  ```
- **远程 HTTP(S) ISO**：通过 HTTP Range 按需读取 UDF 文件系统，解析主播放列表，得到视频、音轨、字幕和章节，并抽样估算码率。不下载整盘。
  - 服务器必须返回 `206 Partial Content`、一致的 `Content-Range` 和强 ETag；不满足时保留已有结果并报告失败。
  - 识别规则：STRM 内的地址以 `.iso` 结尾，或 STRM 文件名以 `.iso.strm` 结尾。
  - **色彩和位深是根据播放列表里的标记和蓝光规格推断的，不是逐帧实测。**
- SMB / NFS 上的 ISO 建议先挂载成本地路径。

### 探测可靠性

- 探测结果先作为候选，写入 Jellyfin 后读回核对，一致才写缓存。
- 写入过程中 STRM 内容或路径发生变化，则放弃这次结果。
- 同一个条目的探测和恢复互斥，不会互相覆盖。
- 失败按类型处理：网络问题有限重试；被拒绝或限流后进入冷却，不反复请求。
- 写缓存文件时通知 Jellyfin 的文件监视器忽略，避免每写一个缓存就触发一次媒体库刷新。

---

## 安装

从 [Releases](https://github.com/Lnkxx/StrmTool/releases) 下载 `StrmTool-2.4.3.7-jellyfin10.11.zip` 并解压。

1. 在 Jellyfin 的 `plugins` 目录下新建文件夹 `StrmTool`。
2. 把发布包里的 `StrmTool.dll`、`meta.json` 和四个 `DiscUtils.*.dll` 放进去。
3. 重启 Jellyfin。

升级时只需替换 `StrmTool.dll` 和 `meta.json`，然后重启。建议先备份旧的两个文件，回滚时放回去再重启即可。

启动日志里出现 `Loaded plugin: "StrmTool" "2.4.3.7"` 和 `Playback probe gate installed over …` 即表示加载成功。

## 配置

在插件详情页点“设置”。

| 配置项 | 默认 | 说明 |
|---|---|---|
| 自动提取新入库 strm 文件 | 开 | 新增的 STRM 自动在后台探测 |
| 启用媒体信息缓存 | 开 | 把探测结果保存为 `.strmtool.json` |
| 刷新延迟（毫秒） | 1000 | 每次探测后的等待时间，避免给远程服务器造成压力 |
| 最大并发提取数 | 5 | 范围 1–50，**修改后需重启** |
| 无视是否已有媒体流 | 关 | 计划任务对已有媒体信息的条目也重新处理（仍可用缓存） |
| 无视缓存 | 关 | 计划任务直接向远程探测，不读缓存 |
| 元数据恢复超时（分钟） | 5 | 单次恢复允许的最长时间 |
| 启用 ISO / BDMV 原盘探测 | 开 | |
| 自定义 ffprobe 路径 | 空 | 带 `libbluray` 的静态 ffprobe 的绝对路径，仅本地 ISO 需要 |
| ISO 探测超时（秒） | 90 | 范围 10–600 |
| 跳过播放前探测 | 开 | 见上文，**修改后立即生效** |
| 探测路径过滤 | 空 | 目录、单个 `.strm` 路径，或 `@/路径/清单.txt`（每行一个 `.strm` 绝对路径）。留空处理全部 |

除“最大并发提取数”外，其余配置修改后不用重启。

## 已知限制

- 当前线上验收覆盖 SenPlayer 的一次电影播放及后续 Infuse 日志。剧集、ISO、无缓存新入库条目的完整线上验收及优化后的大样本统计仍待完成。
- STRM 指纹校验确认的是指针文本未变；相同 URL 背后的远程文件若被替换，需要主动刷新缓存。

- “跳过播放前探测”依赖缓存。从未被本插件探测过的 STRM 不会被跳过，要先跑一次计划任务或等自动提取完成。
- 包装层按 Jellyfin 10.11 的 `IMediaSourceManager` 接口编写。这个接口在 10.11.6、10.11.11 和 12.4 的源码里相同；Jellyfin 以后改动它时，插件需要跟着改并重新编译。
- 在 12.4 上使用需要用 .NET 10 和 12.4 的包重新编译，本仓库目前只提供 10.11 的构建。
- 起播时间里播放器自身连接、缓冲、出第一帧的部分不受本插件影响。
- 远程 ISO 的色彩、位深为推断值。

## 从源码构建

需要 .NET 9 SDK。

```bash
dotnet build StrmTool/StrmTool.csproj -c Release
```

产物在 `StrmTool/bin/Release/net9.0/`。

### 离线检查

每个检查工程都是一个可直接运行的控制台程序，不需要 Jellyfin：

```bash
dotnet run --project PlaybackProbeChecks -c Release
```

| 工程 | 内容 | 检查数 |
|---|---|---|
| `PlaybackProbeChecks` | 跳过探测的判断规则、包装层转发、容器注册、恢复后跳过 | 135 |
| `ExternalSubtitleChecks` | 外挂字幕的识别与合并 | 28 |
| `ReliabilityChecks` | 恢复触发条件、读回核对、失败分类 | 46 |
| `CommitChecks` | 候选结果淘汰、提交与回滚模拟 | 37 |
| `RegressionChecks` | 基本行为回归 | 14 |
| `IsoChecks` | 播放列表解析与 ISO 字段映射 | 58 |
| `RangeChecks` | HTTP Range 读取、限流与取消 | 9 |

## 致谢与许可

- 原项目：[jinlin-teck/StrmTool](https://github.com/jinlin-teck/StrmTool)。需要从 OpenList / Alist 批量生成 STRM 的，可以看原作者的 [openlist-strm](https://github.com/jinlin-teck/openlist-strm)。
- 远程 ISO 的 UDF 读取使用 [DiscUtils](https://github.com/DiscUtils/DiscUtils)。
- 许可证：GPL-3.0，见 `LICENSE`。

---

# StrmTool for Jellyfin (2.4.3.x branch)

A Jellyfin plugin that probes `.strm` files ahead of time and stores their technical media info (codecs, resolution, audio tracks, subtitles, chapters), so details pages are complete and redundant pre-playback probing can be avoided.

Based on [jinlin-teck/StrmTool](https://github.com/jinlin-teck/StrmTool) 2.4.0, GPL-3.0. Runs on Jellyfin 10.11.x (used on 10.11.11).

## What this branch adds

- **Skips the forced probe before playback.** Jellyfin re-probes every `.strm` before each playback (`MediaSourceManager.GetPlaybackMediaSources`), whether or not its streams are already stored, and the player waits for it. The plugin wraps `IMediaSourceManager` through `IPluginServiceRegistrator` and passes `allowMediaProbe = false` when its cache proves the stored info is current. Every other member is forwarded unchanged; nothing in Jellyfin is patched.
- **Metadata restore.** When Jellyfin resets an STRM item (pointer-file size, lost runtime, dimensions, stream detail or external subtitles), it is restored from the cache.
- **ISO probing.** Local ISO through a libbluray-enabled `ffprobe`; remote HTTP ISO through Range requests and UDF parsing, without downloading the image. Color and bit depth of remote ISOs are inferred from playlist flags, not measured.
- **Probe reliability.** Results are written, read back and verified before being cached; failures are classified, retried within limits, and never leave partial data.

## When the playback probe is skipped

All of these must hold; otherwise Jellyfin probes as usual:

1. The "Skip the probe before playback" switch is on.
2. The path ends in `.strm` and is inside the probe path filter.
3. The sidecar `.strmtool.json` is valid: its SHA-256 matches the current STRM content, and size, runtime and streams are present.
4. A remote ISO cache is at the current probe stage.
5. The URL stored on the item equals the STRM's current content.
6. Embedded video and audio streams are stored.
7. The item has not been reset. If it has, it is restored from the cache first (waiting up to 5 seconds).

Any failure of the rule falls back to the original call. Turning the switch off takes effect immediately.

## Measured

On one home server (Jellyfin 10.11.11, STRM files pointing at 302 redirects to cloud storage), over about 12 days the wait between Jellyfin's first ffprobe request and the player receiving its URL had a median of 13.8 s for movies (n=53) and 4.9 s for episodes (n=51). With 2.4.3.7, one playback of an 88 GB movie returned playback info in 42 ms and no probe request was made. That is a single comparison, not a statistic.

## Install

Copy `StrmTool.dll`, `meta.json` and the four `DiscUtils.*.dll` files into `plugins/StrmTool` and restart Jellyfin. The startup log should show `Playback probe gate installed over …`.

## Build

```bash
dotnet build StrmTool/StrmTool.csproj -c Release
```

Requires the .NET 9 SDK. The `*Checks` projects are console programs that run without Jellyfin.
