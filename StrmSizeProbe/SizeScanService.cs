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
        private readonly ILogger _log;
        private CancellationTokenSource _cts;
        private int _running;
        private readonly ConcurrentDictionary<string, byte> _pending = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastIncremental = DateTime.MinValue;

        public static SizeScanService Instance { get; private set; }

        public SizeScanService(IApplicationPaths paths, ILibraryManager libraryManager, ILogManager logManager)
        {
            _paths = paths;
            _libraryManager = libraryManager;
            _log = logManager.GetLogger("StrmSize"); Instance = this;
        }

        private sealed class WriteCandidate
        {
            public BaseItem Item;
            public long Size;
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
                    _log.Info("[StrmSize] DIAG source id={0} size={1} container={2} path={3}", s.Id, s.Size, s.Container, s.Path);
            }
        }

        public void Run()
        {
            var cfg = Plugin.Instance != null ? Plugin.Instance.Configuration : new PluginConfiguration();
            _log.Info("[StrmSize] service started (enabled={0}, autoScan={1}, write={2}, onlyMissing={3}, monitorAdded={4}, debounce={5}s, incremental={6}/{7}min)",
                cfg.Enabled, cfg.AutoScanOnStartup, cfg.WriteToLibrary, cfg.WriteOnlyWhenMissing,
                cfg.MonitorItemAdded, cfg.MonitorDebounceSeconds, cfg.IncrementalScanEnabled, cfg.IncrementalIntervalMinutes);
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
                if (item.Size > 0) return; // 只处理缺失值，已有大小不重复探测

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
                                            && i.Size <= 0).ToList();
            _log.Info("[StrmSize] incremental scan: checked {0} latest items, {1} strm missing size", latest.Length, targets.Count);
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
            if (!pr.Size.HasValue || pr.Size.Value <= 0)
            {
                _log.Warn("[StrmSize] probe failed: {0} http={1} err={2}", path, pr.HttpStatus, pr.Error ?? "-");
                return false;
            }

            if (!cfg.WriteToLibrary)
            {
                _log.Info("[StrmSize] probed {0} bytes (write off): {1}", pr.Size.Value, path);
                return true;
            }
            if (cfg.WriteOnlyWhenMissing && item.Size > 0)
            {
                _log.Info("[StrmSize] skip write, size exists ({0}): {1}", item.Size, path);
                return true;
            }
            try
            {
                item.Size = pr.Size.Value;
                item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                _log.Info("[StrmSize] size written {0} bytes: {1}", pr.Size.Value, path);
            }
            catch (Exception ex)
            {
                _log.ErrorException("[StrmSize] write failed: " + path, ex);
                return false;
            }
            return true;
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

        private long? GetEmbySize(string path)
        {
            try
            {
                var item = FindItem(path);
                if (item == null) return null;
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _log.Info("[StrmSize] scan started (write={0}, onlyMissing={1}, maxWrite={2})",
                cfg.WriteToLibrary, cfg.WriteOnlyWhenMissing, cfg.MaxWritePerRun);

            var files = CollectStrmFiles(cfg);
            if (cfg.MaxItemsPerRun > 0 && files.Count > cfg.MaxItemsPerRun) files = files.Take(cfg.MaxItemsPerRun).ToList();
            _log.Info("[StrmSize] {0} .strm files queued, concurrency {1}", files.Count, cfg.Concurrency);

            var rows = new ConcurrentBag<string[]>();
            var candidates = new ConcurrentBag<WriteCandidate>();
            int done = 0, ok = 0, fail = 0, noEmby = 0, mismatch = 0;
            var conc = Math.Max(1, Math.Min(32, cfg.Concurrency));
            using (var sem = new SemaphoreSlim(conc))
            {
                var tasks = files.Select(async f =>
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        string url = null, err = null;
                        long? size = null;
                        int status = 0;
                        try
                        {
                            url = StrmProber.ReadStrmUrl(f);
                            var pr = await StrmProber.ProbeAsync(url, cfg.TimeoutSeconds, ct).ConfigureAwait(false);
                            size = pr.Size; status = pr.HttpStatus; err = pr.Error;
                        }
                        catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }

                        var embySize = GetEmbySize(f);
                        if (size.HasValue) ok++; else fail++;
                        if (!embySize.HasValue) noEmby++;
                        else if (size.HasValue && Math.Abs(embySize.Value - size.Value) > 1024 * 1024) mismatch++;

                        if (cfg.WriteToLibrary && size.HasValue && size.Value > 0)
                        {
                            var item = FindItem(f);
                            if (item != null) candidates.Add(new WriteCandidate { Item = item, Size = size.Value, Path = f });
                        }

                        rows.Add(new[]
                        {
                            f, url ?? "", size.HasValue ? size.Value.ToString(CultureInfo.InvariantCulture) : "",
                            size.HasValue ? (size.Value / 1073741824.0).ToString("F2", CultureInfo.InvariantCulture) : "",
                            embySize.HasValue ? embySize.Value.ToString(CultureInfo.InvariantCulture) : "",
                            status.ToString(CultureInfo.InvariantCulture), err ?? ""
                        });

                        var n = Interlocked.Increment(ref done);
                        if (n % 200 == 0) _log.Info("[StrmSize] progress {0}/{1} ok={2} fail={3}", n, files.Count, ok, fail);
                    }
                    finally { sem.Release(); }
                }).ToList();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var csvPath = Path.Combine(ReportDir, "strm_size_" + stamp + ".csv");
            using (var w = new StreamWriter(csvPath, false, new UTF8Encoding(true)))
            {
                await w.WriteLineAsync("path,url,probe_bytes,probe_gb,emby_bytes,http,error").ConfigureAwait(false);
                foreach (var r in rows.OrderBy(r => r[0], StringComparer.OrdinalIgnoreCase))
                {
                    await w.WriteLineAsync(string.Join(",", r.Select(Escape))).ConfigureAwait(false);
                }
            }

            int written = 0, skippedHasValue = 0, writeFailed = 0;
            if (cfg.WriteToLibrary && !candidates.IsEmpty)
            {
                _log.Info("[StrmSize] writing sizes back to library: {0} candidates", candidates.Count);
                foreach (var c in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (cfg.MaxWritePerRun > 0 && written >= cfg.MaxWritePerRun)
                    {
                        _log.Info("[StrmSize] write cap reached ({0}), remaining skipped", cfg.MaxWritePerRun);
                        break;
                    }
                    try
                    {
                        var current = c.Item.Size;
                        if (cfg.WriteOnlyWhenMissing && current > 0) { skippedHasValue++; continue; }
                        if (current == c.Size) { skippedHasValue++; continue; }
                        c.Item.Size = c.Size;
                        c.Item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                        written++;
                        if (written % 200 == 0) _log.Info("[StrmSize] written {0} items", written);
                    }
                    catch (Exception ex)
                    {
                        writeFailed++;
                        _log.ErrorException("[StrmSize] write failed: " + c.Path, ex);
                    }
                }
            }

            var suspectMb = Math.Max(0, cfg.SuspectBelowMb);
            var suspect = rows.Count(r => r[2].Length > 0 && long.TryParse(r[2], out var b) && b < suspectMb * 1048576L);
            sw.Stop();
            _log.Info("[StrmSize] scan done in {0:F1}s: files={1} ok={2} fail={3} noEmbySize={4} sizeMismatch={5} suspectUnder{6}MB={7} written={8} skipped={9} writeFailed={10} report={11}",
                sw.Elapsed.TotalSeconds, files.Count, ok, fail, noEmby, mismatch, suspectMb, suspect, written, skippedHasValue, writeFailed, csvPath);
        }

        private static string Escape(string v)
        {
            v = v ?? "";
            if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
    }
}
