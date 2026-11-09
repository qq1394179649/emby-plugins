using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace EmbyStrmSize
{
    /// <summary>Background service: probe every .strm size, optionally write it back to the library.</summary>
    public class SizeScanService : IServerEntryPoint
    {
        private readonly IApplicationPaths _paths;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _log;
        private CancellationTokenSource _cts;
        private int _running;
        private readonly ConcurrentDictionary<string, byte> _pending = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastIncremental = DateTime.MinValue;

        public static SizeScanService Instance { get; private set; }

        public SizeScanService(IApplicationPaths paths, ILibraryManager libraryManager, IItemRepository itemRepository, ILogManager logManager)
        {
            _paths = paths;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _log = logManager.GetLogger("StrmSize"); Instance = this;
        }

        private sealed class WriteCandidate
        {
            public BaseItem Item;
            public long? Size;
            public long? RunTimeTicks;
            public List<MediaStream> Streams;
            public string Path;
        }

        private void RunDiagnostic(PluginConfiguration cfg)
        {
            var dataDir = Plugin.Instance != null ? Plugin.Instance.DataFolderPath : "";
            var idStr = cfg.DiagnosticItemId;
            string found = null;
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(dataDir)) candidates.Add(Path.Combine(dataDir, "diag.txt"));
            candidates.Add("/config/data/diag.txt");
            candidates.Add("/config/data/EmbyStrmSize/diag.txt");
            foreach (var f in candidates)
            {
                try { if (File.Exists(f)) { var t = File.ReadAllText(f).Trim(); found = f; if (t.Length > 0) idStr = t; break; } }
                catch { }
            }
            _log.Info("[StrmSize] DIAG begin dataDir={0} file={1} idStr={2}", dataDir, found ?? "none", idStr ?? "");
            if (string.IsNullOrWhiteSpace(idStr)) return;
            BaseItem item = null;
            if (long.TryParse(idStr.Trim(), out var longId)) item = _libraryManager.GetItemById(longId);
            else if (Guid.TryParse(idStr.Trim(), out var guidId)) item = _libraryManager.GetItemById(guidId);
            if (item == null) { _log.Info("[StrmSize] DIAG item not found: {0}", idStr); return; }
            _log.Info("[StrmSize] DIAG item id={0} name={1} item.Size={2} path={3}", item.Id, item.Name, item.Size, item.Path);
            var sources = item.GetMediaSources(false, false, null);
            _log.Info("[StrmSize] DIAG sources count={0}", sources == null ? -1 : sources.Count);
            if (sources != null)
            {
                foreach (var s in sources)
                    _log.Info("[StrmSize] DIAG source id={0} size={1} container={2} path={3} bitrate={4}", s.Id, s.Size, s.Container, s.Path, s.Bitrate);
            }
            _log.Info("[StrmSize] DIAG stored streams count={0}", GetStoredStreams(item).Count);
            foreach (var s in GetStoredStreams(item))
                _log.Info("[StrmSize] DIAG stream type={0} index={1} codec={2} bitrate={3} w={4} h={5}", s.Type, s.Index, s.Codec, s.BitRate, s.Width, s.Height);
        }

        public void Run()
        {
            var cfg = Plugin.Instance != null ? Plugin.Instance.Configuration : new PluginConfiguration();
            _log.Info("[StrmSize] service started (enabled={0}, autoScan={1}, write={2}, onlyMissing={3}, monitorAdded={4}, debounce={5}s, incremental={6}/{7}min, mediaProbe={8}, ffprobe={9})",
                cfg.Enabled, cfg.AutoScanOnStartup, cfg.WriteToLibrary, cfg.WriteOnlyWhenMissing,
                cfg.MonitorItemAdded, cfg.MonitorDebounceSeconds, cfg.IncrementalScanEnabled, cfg.IncrementalIntervalMinutes,
                cfg.EnableMediaProbe, StrmProber.ResolveFfprobe(cfg.FfprobePath) ?? "NOT FOUND");
            try { RunDiagnostic(cfg); } catch (Exception ex) { _log.ErrorException("[StrmSize] diag failed", ex); }
            if (!cfg.Enabled) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // 事件驱动：常驻订阅，是否真正处理由配置在运行时决定，改配置即可即时生效
            try
            {
                _libraryManager.ItemAdded += OnItemAdded;
                _log.Info("[StrmSize] library ItemAdded monitor attached");
            }
            catch (Exception ex) { _log.ErrorException("[StrmSize] attach ItemAdded failed", ex); }

            // 定时增量：可选，内部按周期自检，开启/关闭与周期修改无需重启
            Task.Run(() => IncrementalSupervisor(token), token);

            if (!cfg.AutoScanOnStartup) return;
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
                await ScanOnce(token).ConfigureAwait(false);
            }, token);
        }

        public void Dispose()
        {
            try { _libraryManager.ItemAdded -= OnItemAdded; } catch { }
            try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                var cfg = Plugin.Instance != null ? Plugin.Instance.Configuration : null;
                if (cfg == null || !cfg.Enabled || !cfg.MonitorItemAdded) return;
                var item = e == null ? null : e.Item;
                if (item == null) return;
                var path = item.Path;
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)) return;
                if (item.Size > 0 && (!cfg.EnableMediaProbe || HasVideoBitrate(item))) return; // 体积和码率都齐了就不重复探测

                var maxQueue = cfg.MonitorMaxQueue > 0 ? cfg.MonitorMaxQueue : 200;
                if (_pending.Count >= maxQueue)
                {
                    _log.Warn("[StrmSize] monitor queue full ({0}), dropped: {1}", _pending.Count, path);
                    return;
                }
                if (!_pending.TryAdd(path, 1)) return; // 同一路径重复事件合并

                _log.Info("[StrmSize] new item detected: id={0} name={1} path={2}", item.Id, item.Name, path);
                var itemId = item.Id;
                var token = _cts != null ? _cts.Token : CancellationToken.None;
                Task.Run(async () =>
                {
                    try
                    {
                        var delay = Math.Max(0, Math.Min(600, cfg.MonitorDebounceSeconds));
                        if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                        await ProbeOneAsync(path, itemId, null, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _log.ErrorException("[StrmSize] event probe failed: " + path, ex); }
                    finally { _pending.TryRemove(path, out _); }
                }, token);
            }
            catch (Exception ex) { _log.ErrorException("[StrmSize] ItemAdded handler failed", ex); }
        }

        private async Task IncrementalSupervisor(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false); }
                catch { break; }
                try
                {
                    var cfg = Plugin.Instance != null ? Plugin.Instance.Configuration : null;
                    if (cfg == null || !cfg.Enabled || !cfg.IncrementalScanEnabled) continue;
                    var mins = Math.Max(5, cfg.IncrementalIntervalMinutes);
                    if ((DateTime.UtcNow - _lastIncremental).TotalMinutes < mins) continue;
                    _lastIncremental = DateTime.UtcNow;
                    await IncrementalOnce(cfg, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.ErrorException("[StrmSize] incremental scan failed", ex); }
            }
        }

        private async Task IncrementalOnce(PluginConfiguration cfg, CancellationToken ct)
        {
            var limit = cfg.IncrementalBatchSize > 0 ? cfg.IncrementalBatchSize : 500;
            BaseItem[] latest;
            try
            {
                latest = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[] { "Movie", "Episode", "Video" },
                    OrderBy = new[] { ("DateCreated", SortOrder.Descending) },
                    Limit = limit
                });
            }
            catch (Exception ex)
            {
                _log.ErrorException("[StrmSize] incremental query failed", ex);
                return;
            }

            var targets = latest.Where(i => i.Path != null
                                            && i.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                                            && (i.Size <= 0 || (cfg.EnableMediaProbe && !HasVideoBitrate(i)))).ToList();
            _log.Info("[StrmSize] incremental scan: checked {0} latest items, {1} strm missing size/bitrate", latest.Length, targets.Count);
            if (targets.Count == 0) return;

            int ok = 0, fail = 0;
            var conc = Math.Max(1, Math.Min(16, cfg.Concurrency));
            using (var sem = new SemaphoreSlim(conc))
            {
                var tasks = targets.Select(async it =>
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (await ProbeOneAsync(it.Path, it.Id, cfg, ct).ConfigureAwait(false)) Interlocked.Increment(ref ok);
                        else Interlocked.Increment(ref fail);
                    }
                    finally { sem.Release(); }
                }).ToList();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            _log.Info("[StrmSize] incremental done: probed={0} ok={1} fail={2}", targets.Count, ok, fail);
        }

        /// <summary>探测单条 .strm 并按配置写回，返回是否探测成功。</summary>
        private async Task<bool> ProbeOneAsync(string path, Guid itemId, PluginConfiguration cfg, CancellationToken ct)
        {
            if (cfg == null) cfg = Plugin.Instance != null ? Plugin.Instance.Configuration : new PluginConfiguration();
            BaseItem item = null;
            try { item = _libraryManager.GetItemById(itemId); } catch { }
            if (item == null) item = FindItem(path);
            if (item == null)
            {
                _log.Warn("[StrmSize] probe skipped, item not found: {0}", path);
                return false;
            }

            string url;
            try { url = StrmProber.ReadStrmUrl(path); }
            catch (Exception ex)
            {
                _log.Warn("[StrmSize] read strm failed: {0} ({1})", path, ex.Message);
                return false;
            }

            var pr = await StrmProber.ProbeAsync(url, cfg.TimeoutSeconds, ct).ConfigureAwait(false);
            long? size = pr.Size;
            if ((!size.HasValue || size.Value <= 0) && pr.HttpStatus == 0)
            {
                _log.Warn("[StrmSize] probe failed: {0} http={1} err={2}", path, pr.HttpStatus, pr.Error ?? "-");
                return false;
            }

            MediaProbeResult mp = null;
            if (cfg.EnableMediaProbe && !HasVideoBitrate(item))
            {
                mp = await StrmProber.ProbeMediaAsync(url, cfg.FfprobePath, cfg.AnalyzeSeconds, cfg.MediaProbeTimeoutSeconds, ct).ConfigureAwait(false);
                if (mp.Size.HasValue && (!size.HasValue || size.Value <= 0)) size = mp.Size;
                if (!mp.Ok)
                    _log.Warn("[StrmSize] media probe failed: {0} err={1}", path, mp.Error ?? "-");
            }

            if (!size.HasValue || size.Value <= 0)
            {
                _log.Warn("[StrmSize] no size resolved: {0} http={1} err={2}", path, pr.HttpStatus, pr.Error ?? "-");
                return false;
            }

            if (!cfg.WriteToLibrary)
            {
                _log.Info("[StrmSize] probed {0} bytes, bitrate={1} (write off): {2}", size.Value, mp?.TotalBitrate?.ToString() ?? "-", path);
                return true;
            }

            return WriteBack(item, size, mp, cfg, ct);
        }

        /// <summary>把探测结果回写 Emby：体积、时长、媒体流（含码率/分辨率）。</summary>
        private bool WriteBack(BaseItem item, long? size, MediaProbeResult mp, PluginConfiguration cfg, CancellationToken ct)
        {
            try
            {
                var needSize = size.HasValue && size.Value > 0 && !(cfg.WriteOnlyWhenMissing && item.Size > 0);
                long? ticks = null;
                if (mp != null && mp.DurationSeconds.HasValue && mp.DurationSeconds.Value > 0)
                    ticks = (long)(mp.DurationSeconds.Value * TimeSpan.TicksPerSecond);

                var needRuntime = ticks.HasValue && !(cfg.WriteOnlyWhenMissing && item.RunTimeTicks > 0);
                var streams = mp != null && mp.Streams.Count > 0 ? mp.Streams : null;

                var existing = GetStoredStreams(item);
                var oldVideo = existing.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                var newVideo = streams == null ? null : streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                var streamsIdentical = oldVideo != null && newVideo != null
                                       && oldVideo.BitRate == newVideo.BitRate
                                       && oldVideo.Width == newVideo.Width
                                       && oldVideo.Height == newVideo.Height;
                var needStreams = streams != null && !streamsIdentical
                                  && !(cfg.WriteOnlyWhenMissing && HasVideoBitrate(item));
                if (needStreams)
                {
                    // 保留既有字幕/附件流，避免整表覆盖时丢信息
                    foreach (var keep in existing.Where(s => s.Type != MediaStreamType.Video && s.Type != MediaStreamType.Audio))
                    {
                        if (streams.All(s => s.Index != keep.Index)) streams.Add(keep);
                    }
                }

                if (!needSize && !needRuntime && !needStreams)
                {
                    _log.Info("[StrmSize] skip write, values present: {0}", item.Path ?? item.Name);
                    return true;
                }

                if (needSize) item.Size = size.Value;
                if (needRuntime) item.RunTimeTicks = ticks.Value;
                if (needSize || needRuntime) item.UpdateToRepository(ItemUpdateType.MetadataEdit);

                if (needStreams)
                {
                    if (item.InternalId <= 0) item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                    _itemRepository.SaveMediaStreams(item.InternalId, streams, ct);
                }

                _log.Info("[StrmSize] written item={0} size={1} runtime={2} streams={3} videoBitrate={4}: {5}",
                    item.Name, needSize ? size.Value.ToString() : "-",
                    needRuntime ? TimeSpan.FromTicks(ticks.Value).ToString() : "-",
                    needStreams ? streams.Count.ToString() : "-",
                    mp?.VideoBitrate?.ToString() ?? "-", item.Path ?? "");
                return true;
            }
            catch (Exception ex)
            {
                _log.ErrorException("[StrmSize] write failed: " + (item.Path ?? item.Name), ex);
                return false;
            }
        }

        public string ReportDir
        {
            get
            {
                var dir = Path.Combine(_paths.DataPath, "EmbyStrmSize");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public List<string> CollectStrmFiles(PluginConfiguration cfg)
        {
            var wanted = (cfg.Libraries ?? "")
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            var roots = new List<string>();
            foreach (var folder in _libraryManager.GetVirtualFolders())
            {
                if (wanted.Count > 0 && !wanted.Any(w => string.Equals(w, folder.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (folder.Locations == null) continue;
                roots.AddRange(folder.Locations);
            }

            var files = new List<string>();
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(root)) { if (root.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)) files.Add(root); continue; }
                    if (!Directory.Exists(root)) { _log.Warn("[StrmSize] library path not visible in container: {0}", root); continue; }
                    files.AddRange(Directory.EnumerateFiles(root, "*.strm", SearchOption.AllDirectories));
                }
                catch (Exception ex)
                {
                    _log.ErrorException("[StrmSize] enumerate failed: " + root, ex);
                }
            }
            return files;
        }

        private BaseItem FindItem(string path)
        {
            try { return _libraryManager.FindByPath(path, false); }
            catch { return null; }
        }

        private List<MediaStream> GetStoredStreams(BaseItem item)
        {
            try
            {
                if (item == null || item.InternalId <= 0) return new List<MediaStream>();
                return _itemRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.InternalId }, CancellationToken.None)
                       ?? new List<MediaStream>();
            }
            catch { return new List<MediaStream>(); }
        }

        private bool HasVideoBitrate(BaseItem item)
        {
            try
            {
                return GetStoredStreams(item).Any(s => s.Type == MediaStreamType.Video && s.BitRate.HasValue && s.BitRate.Value > 0);
            }
            catch { return false; }
        }

        private long? GetEmbySize(BaseItem item)
        {
            try
            {
                if (item == null) return null;
                if (item.Size > 0) return item.Size;
                var sources = item.GetMediaSources(false, false, null);
                if (sources == null || sources.Count == 0) return null;
                var s = sources[0];
                if (s.Size.HasValue && s.Size.Value > 0) return s.Size.Value;
                return null;
            }
            catch { return null; }
        }

        public async Task ScanOnce(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                _log.Warn("[StrmSize] scan already running, skip");
                return;
            }
            try
            {
                await ScanCore(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private async Task ScanCore(CancellationToken ct)
        {
            var cfg = Plugin.Instance != null ? Plugin.Instance.Configuration : new PluginConfiguration();
            // 调试开关：存在 write_on.txt 时强制开启写库（不改用户配置）
            try
            {
                var force = Path.Combine(ReportDir, "write_on.txt");
                if (File.Exists(force))
                {
                    cfg.WriteToLibrary = true;
                    var lines = File.ReadAllLines(force);
                    if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out var forceMax) && forceMax > 0) cfg.MaxItemsPerRun = forceMax;
                    if (lines.Length > 1 && int.TryParse(lines[1].Trim(), out var forceWriteMax) && forceWriteMax > 0) cfg.MaxWritePerRun = forceWriteMax;
                    _log.Warn("[StrmSize] write_on.txt found, WriteToLibrary forced ON (maxItems={0}, maxWrite={1})", cfg.MaxItemsPerRun, cfg.MaxWritePerRun);
                }
            }
            catch { }

            // ffprobe 更重，单独限流，避免扫描时把网盘连接打满
            var mediaConc = Math.Max(1, Math.Min(3, cfg.MediaProbeConcurrency > 0 ? cfg.MediaProbeConcurrency : (cfg.Concurrency / 2)));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _log.Info("[StrmSize] scan started (write={0}, onlyMissing={1}, maxWrite={2}, mediaProbe={3}, mediaConc={4}, skipKnown={5})",
                cfg.WriteToLibrary, cfg.WriteOnlyWhenMissing, cfg.MaxWritePerRun, cfg.EnableMediaProbe, mediaConc, cfg.SkipKnownItems);

            var all = CollectStrmFiles(cfg);
            _log.Info("[StrmSize] {0} .strm files found, concurrency {1}", all.Count, cfg.Concurrency);

            var rows = new ConcurrentBag<string[]>();
            var candidates = new ConcurrentBag<WriteCandidate>();
            int done = 0, ok = 0, fail = 0, noEmby = 0, mismatch = 0, withBitrate = 0, mediaFail = 0, skippedKnown = 0, started = 0;
            var conc = Math.Max(1, Math.Min(32, cfg.Concurrency));

            var queue = new ConcurrentQueue<string>(all);
            Func<Task> worker = async () =>
            {
                string f;
                while (queue.TryDequeue(out f))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var item = FindItem(f);
                        if (cfg.SkipKnownItems && item != null && item.Size > 0 && (!cfg.EnableMediaProbe || HasVideoBitrate(item)))
                        {
                            var n0 = Interlocked.Increment(ref skippedKnown);
                            if (n0 % 500 == 0) _log.Info("[StrmSize] skipped known {0}", n0);
                            continue;
                        }

                        if (cfg.MaxItemsPerRun > 0 && Interlocked.Increment(ref started) > cfg.MaxItemsPerRun)
                            continue;

                        string url = null, err = null;
                        long? size = null;
                        int status = 0;
                        long sizeMs = 0;
                        MediaProbeResult mp = null;
                        try
                        {
                            url = StrmProber.ReadStrmUrl(f);
                            var pr = await StrmProber.ProbeAsync(url, cfg.TimeoutSeconds, ct).ConfigureAwait(false);
                            size = pr.Size; status = pr.HttpStatus; err = pr.Error; sizeMs = pr.ElapsedMs;
                            if (cfg.EnableMediaProbe && (!size.HasValue || (item != null && !HasVideoBitrate(item))))
                            {
                                mp = await StrmProber.ProbeMediaAsync(url, cfg.FfprobePath, cfg.AnalyzeSeconds, cfg.MediaProbeTimeoutSeconds, ct).ConfigureAwait(false);
                                if (mp.Size.HasValue && (!size.HasValue || size.Value <= 0)) size = mp.Size;
                                if (mp.Ok) Interlocked.Increment(ref withBitrate);
                                else { Interlocked.Increment(ref mediaFail); if (err == null) err = "media:" + (mp.Error ?? "unknown"); }
                            }
                        }
                        catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }

                        var embySize = GetEmbySize(item);
                        if (size.HasValue) ok++; else fail++;
                        if (!embySize.HasValue) noEmby++;
                        else if (size.HasValue && Math.Abs(embySize.Value - size.Value) > 1024 * 1024) mismatch++;

                        if (cfg.WriteToLibrary && size.HasValue && size.Value > 0 && item != null)
                        {
                            candidates.Add(new WriteCandidate
                            {
                                Item = item,
                                Size = size,
                                RunTimeTicks = mp != null && mp.DurationSeconds.HasValue && mp.DurationSeconds.Value > 0
                                    ? (long?)(long)(mp.DurationSeconds.Value * TimeSpan.TicksPerSecond) : null,
                                Streams = mp != null && mp.Streams.Count > 0 ? mp.Streams : null,
                                Path = f
                            });
                        }

                        rows.Add(new[]
                        {
                            f, url ?? "", size.HasValue ? size.Value.ToString(CultureInfo.InvariantCulture) : "",
                            size.HasValue ? (size.Value / 1073741824.0).ToString("F2", CultureInfo.InvariantCulture) : "",
                            embySize.HasValue ? embySize.Value.ToString(CultureInfo.InvariantCulture) : "",
                            status.ToString(CultureInfo.InvariantCulture), err ?? "",
                            sizeMs.ToString(CultureInfo.InvariantCulture),
                            mp != null && mp.DurationSeconds.HasValue ? mp.DurationSeconds.Value.ToString("F0", CultureInfo.InvariantCulture) : "",
                            mp != null && mp.TotalBitrate.HasValue ? (mp.TotalBitrate.Value / 1000).ToString(CultureInfo.InvariantCulture) : "",
                            mp != null && mp.Video != null ? (mp.Video.Codec ?? "") : "",
                            mp != null && mp.VideoBitrate.HasValue ? (mp.VideoBitrate.Value / 1000).ToString(CultureInfo.InvariantCulture) : "",
                            mp != null && mp.Video != null && mp.Video.Width.HasValue ? mp.Video.Width + "x" + mp.Video.Height : "",
                            mp != null ? string.Join("/", mp.Streams.Where(s => s.Type == MediaStreamType.Audio).Select(s => s.Codec).Distinct()) : ""
                        });

                        var n = Interlocked.Increment(ref done);
                        if (n % 200 == 0) _log.Info("[StrmSize] progress {0} ok={1} fail={2} bitrate={3}", n, ok, fail, withBitrate);
                    }
                    finally { }
                }
            };

            var workers = Enumerable.Range(0, conc).Select(_ => Task.Run(worker, ct)).ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var csvPath = Path.Combine(ReportDir, "strm_size_" + stamp + ".csv");
            using (var w = new StreamWriter(csvPath, false, new UTF8Encoding(true)))
            {
                await w.WriteLineAsync("path,url,probe_bytes,probe_gb,emby_bytes,http,error,probe_ms,duration_s,total_bitrate_kbps,video_codec,video_bitrate_kbps,resolution,audio_codecs").ConfigureAwait(false);
                foreach (var r in rows.OrderBy(r => r[0], StringComparer.OrdinalIgnoreCase))
                {
                    await w.WriteLineAsync(string.Join(",", r.Select(Escape))).ConfigureAwait(false);
                }
            }

            int written = 0, skippedHasValue = 0, writeFailed = 0;
            if (cfg.WriteToLibrary && !candidates.IsEmpty)
            {
                _log.Info("[StrmSize] writing back to library: {0} candidates", candidates.Count);
                foreach (var c in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (cfg.MaxWritePerRun > 0 && written >= cfg.MaxWritePerRun)
                    {
                        _log.Info("[StrmSize] write cap reached ({0}), remaining skipped", cfg.MaxWritePerRun);
                        break;
                    }
                    if (c.Item.Size == c.Size && c.Streams == null && !c.RunTimeTicks.HasValue) { skippedHasValue++; continue; }
                    if (WriteBack(c.Item, c.Size, BuildResult(c), cfg, ct)) written++;
                    else writeFailed++;
                    if (written % 200 == 0) _log.Info("[StrmSize] written {0} items", written);
                }
            }

            var suspectMb = Math.Max(0, cfg.SuspectBelowMb);
            var suspect = rows.Count(r => r[2].Length > 0 && long.TryParse(r[2], out var b) && b < suspectMb * 1048576L);
            sw.Stop();
            _log.Info("[StrmSize] scan done in {0:F1}s: files={1} probed={2} ok={3} fail={4} noEmbySize={5} sizeMismatch={6} suspectUnder{7}MB={8} bitrateOk={9} mediaFail={10} skippedKnown={11} written={12} skipped={13} writeFailed={14} report={15}",
                sw.Elapsed.TotalSeconds, all.Count, done, ok, fail, noEmby, mismatch, suspectMb, suspect,
                withBitrate, mediaFail, skippedKnown, written, skippedHasValue, writeFailed, csvPath);
        }

        private static MediaProbeResult BuildResult(WriteCandidate c)
        {
            if (c.Streams == null && !c.RunTimeTicks.HasValue) return null;
            return new MediaProbeResult { Streams = c.Streams ?? new List<MediaStream>(), DurationSeconds = c.RunTimeTicks.HasValue ? c.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : (double?)null };
        }

        private static string Escape(string v)
        {
            v = v ?? "";
            if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
    }
}
