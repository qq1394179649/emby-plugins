# Strm Size Probe

Emby 插件：扫描 `.strm` 媒体项，探测其指向的真实文件大小与媒体信息（时长 / 码率 / 分辨率），按配置写回 Emby 媒体库，让详情页显示正确体积与比特率（Emby 原生对 `.strm` 只读体积为 0、无码率）。

- 插件 Id：`7f3c1a9e-4b62-4f1d-9a3e-2c8d5b7e6a10`
- 插件名：`Strm Size Probe`（配置页标题「Strm Size Probe 设置」）
- 日志前缀：`[StrmSize]`
- 计划任务：`媒体库 → Strm 大小扫描`（Key `StrmSizeProbe`）
- 当前版本：`1.1.0.0`

## 工作原理

1. 遍历媒体库中的视频条目，筛选 `.strm` 路径；
2. 读取 strm 文件内容得到真实 URL（多行取首个非空，自动去 BOM）；
3. 体积：`GET` + `Range: bytes=0-0`，从 `Content-Range: bytes 0-0/N` 取总长度；服务端忽略 Range 返回 200 时退回 `Content-Length`；
4. 媒体信息：调用 Emby 内置 `ffprobe`（默认 `/bin/ffprobe`）只解析容器头部，取
   - `format.duration` → 时长
   - `format.bit_rate` → 总码率
   - 视频流 `codec_name / width / height / bit_rate / avg_frame_rate / profile / pix_fmt`
   - 音频流 `codec_name / channels / sample_rate / bit_rate / language`
5. 写回 Emby：
   - `item.Size` ← 真实字节数
   - `item.RunTimeTicks` ← 时长（时长未知的 strm 也能显示片长）
   - `IItemRepository.SaveMediaStreams(itemId, streams)` ← 媒体流，详情页「媒体信息」据此显示视频/音频码率、分辨率、编码
6. 结果写入 CSV 报告；`WriteToLibrary=false` 时只出报告不写库。

**只读源文件头部，不下载完整内容**（Range 只取 0 字节，ffprobe 仅按 `-analyzeduration` 读若干 MB 头部），不移动、不删除任何文件。

## 触发方式

| 方式 | 说明 |
| --- | --- |
| 启动扫描 | 服务启动后自动执行一次扫描（受「单次扫描条数」限制） |
| 手动任务 | 控制台 → 计划任务 → 「Strm 大小扫描」→ 运行 |
| 事件驱动 | 订阅媒体库 `ItemAdded`，新 strm 入库后去抖 N 秒自动探测体积 + 码率 |
| 定时增量 | 可选开关，按周期按入库时间倒序补最近条目，兜底事件漏单 |

## 1.1.0 优化点

- 新增 ffprobe 媒体信息探测：总码率、视频码率、分辨率、编码、时长，写回 Emby 媒体流后详情页可见比特率；
- 新增「跳过已完整的条目」：体积与视频码率都齐的条目不再发任何请求，全库重复扫描的请求量大幅下降，且「单次扫描条数」能真正向后推进；
- ffprobe 独立并发上限，避免与体积探测抢网盘带宽；
- 写库去重：已有媒体流与本次探测结果一致时不重复写库，减少 `library.db` 写入；
- 报告新增列：`probe_ms / duration_s / total_bitrate_kbps / video_codec / video_bitrate_kbps / resolution / audio_codecs`；
- 体积探测简化：统一用 Range 单请求，去掉多余的 HEAD 往返；
- 事件驱动与增量路径同样补码率，不再只补体积；
- 码率探测失败不影响体积写回（两者独立降级）。

## 配置项

| 配置 | 默认值 | 说明 |
| --- | --- | --- |
| 启用插件 | `true` | 关闭后不自动扫描 |
| 启动时自动扫描 | `true` | 启动后跑一次 |
| 并发数 | `4` | 同时探测体积的 HTTP 连接数，建议 4-8 |
| 单条超时（秒） | `15` | 单次体积请求超时 |
| 单次扫描条数 | `50` | 每轮最多探测条数，0 表示不限制；全库补齐可设 `100000` |
| 媒体库名称 | 空 | 逗号分隔，留空表示全部媒体库 |
| 写入媒体库 | `false` | 写回体积 / 时长 / 媒体流，默认关闭仅出报告 |
| 仅补齐缺失值 | `true` | 只写缺失项，已有值不覆盖 |
| 单次最多写入条数 | `0` | 0 表示不限制，用于分批灰度 |
| 小于该值标记异常（MB） | `100` | 报告中标为可疑 |
| 跳过已完整的条目 | `true` | 体积 + 视频码率都齐的条目直接跳过 |
| 探测媒体信息（码率/时长/分辨率） | `true` | ffprobe 头部解析，写回媒体流 |
| ffprobe 路径 | 空 | 留空自动查找，Docker 官方镜像为 `/bin/ffprobe` |
| 头部解析时长（秒） | `5` | `-analyzeduration`，越大越准、流量越多 |
| 媒体信息探测超时（秒） | `45` | 单条 ffprobe 超时 |
| 媒体信息探测并发 | `2` | ffprobe 独立并发，0 表示自动（并发数一半，最多 3） |
| 新条目入库自动探测 | `true` | ItemAdded 事件驱动 |
| 事件去抖等待（秒） | `8` | 入库后等待再探测 |
| 事件队列上限 | `200` | 超出丢弃并记日志 |
| 定时增量扫描 | `false` | 兜底开关 |
| 增量扫描周期（分钟） | `15` | 最小 5 |
| 每轮增量条数 | `500` | 按入库时间倒序检查最近条目 |
| 诊断条目ID | 空 | 填 ItemId 后启动时打印该条目 Size / MediaSources / 已存媒体流 |

## 报告与日志

- 报告目录：`<Emby data>/EmbyStrmSize/`（容器内通常为 `/config/data/EmbyStrmSize/`）
- 报告列：`path,url,probe_bytes,probe_gb,emby_bytes,http,error,probe_ms,duration_s,total_bitrate_kbps,video_codec,video_bitrate_kbps,resolution,audio_codecs`
- 日志过滤 `[StrmSize]`，每轮输出 `files / probed / ok / fail / noEmbySize / sizeMismatch / bitrateOk / mediaFail / skippedKnown / written / writeFailed`

## 建议使用姿势

1. 首次保持 `写入媒体库=false`，跑一次「Strm 大小扫描」，看 `fail` 比例与 `bitrateOk` 占比；
2. 网盘限流明显时下调并发到 2-3、超时加到 30s，`头部解析时长` 保持 5；
3. 报告正常后开启写入，保留「仅补齐缺失值」，灰度可设 `单次最多写入条数=500`；
4. 全库补齐时把 `单次扫描条数` 设为 `100000`，并保持「跳过已完整的条目」开启，中断后重跑会自动续上。

## 已知限制

- Emby 重新刮削 / 刷新元数据时可能重算 `Size` 与媒体流，需再次运行任务补齐；
- 依赖服务端返回 `Content-Length` / `Content-Range`，两者都不给时体积探测记为 fail；
- 部分网盘直链对 ffprobe 的多段请求不友好，码率可能探测失败（体积仍会正常写回）；
- 只处理 `.strm`，不处理直连挂载的普通视频文件；
- 需要 Emby 刷新缓存，个别客户端需重新进入详情页才能看到新大小与码率。

MIT License。
