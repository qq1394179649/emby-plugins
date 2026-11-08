using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace EmbyMediaMerge
{
    /// <summary>
    /// 核心服务：识别 TMDB 相同的重复电影条目，调用 ILibraryManager.MergeItems 合并为多版本，
    /// 并保留合并历史以便随时拆分还原。不触碰任何文件。
    /// </summary>
    public class MergeService : IServerEntryPoint
    {
        private readonly ILibraryManager _lm;
        private readonly IUserManager _um;
        private readonly IUserDataManager _udm;
        private readonly IApplicationPaths _paths;
        private readonly ILogger _log;

        private CancellationTokenSource _cts;
        private int _busy;
        private DateTime _lastIncremental = DateTime.MinValue;
        private DateTime _lastEventRun = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, DateTime> _pending = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private List<MediaBrowser.Controller.Entities.User> _users = new List<MediaBrowser.Controller.Entities.User>();

        private sealed class UserAgg { public int Play; public bool Played; public bool Fav; public long Pos; }
        private Dictionary<string, UserAgg> _agg = new Dictionary<string, UserAgg>(StringComparer.OrdinalIgnoreCase);

        private static string KeyOf(BaseItem item)
        {
            try
            {
                var k = item.UserDataKey;
                if (!string.IsNullOrWhiteSpace(k)) return k;
            }
            catch { }
            return item.Id.ToString("N");
        }

        private void LoadUserAggregates()
        {
            var agg = new Dictionary<string, UserAgg>(StringComparer.OrdinalIgnoreCase);
            int total = 0, usersOk = 0;
            try
            {
                var users = (_um.GetUsers(new UserQuery())?.Items ?? Array.Empty<MediaBrowser.Controller.Entities.User>());
                foreach (var u in users)
                {
                    try
                    {
                        long iid = _um.GetInternalId(u.Id);
                        if (iid <= 0) continue;
                        var rows = _udm.GetAllUserData(iid);
                        if (rows == null) continue;
                        usersOk++;
                        foreach (var d in rows)
                        {
                            if (d == null || string.IsNullOrWhiteSpace(d.Key)) continue;
                            total++;
                            UserAgg a;
                            if (!agg.TryGetValue(d.Key, out a)) { a = new UserAgg(); agg[d.Key] = a; }
                            a.Play += d.PlayCount;
                            if (d.Played) a.Played = true;
                            if (d.IsFavorite) a.Fav = true;
                            if (d.PlaybackPositionTicks > a.Pos) a.Pos = d.PlaybackPositionTicks;
                        }
                    }
                    catch (Exception ex) { _log.Warn("[MediaMerge] user data load failed: " + ex.Message); }
                }
                _log.Info("[MediaMerge] user data loaded: users={0} rows={1} keys={2}", usersOk, total, agg.Count);
            }
            catch (Exception ex) { _log.Warn("[MediaMerge] load user data failed: " + ex.Message); }
            _agg = agg;
            _log.Info("[MediaMerge] user data keys loaded: {0}", agg.Count);
        }

        private static readonly System.Text.RegularExpressions.Regex NameNoise = new System.Text.RegularExpressions.Regex(
            @"(\d{3,4}p|x264|x265|h\.?264|h\.?265|HEVC|BluRay|Blu-ray|WEB-DL|WEBRip|HDTV|AAC|AC3|DTS|TrueHD|REMUX|\.strm|\.mkv|\.mp4|\.iso)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex CjkChars = new System.Text.RegularExpressions.Regex(
            @"[\u4e00-\u9fa5]", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex YearTag = new System.Text.RegularExpressions.Regex(
            @"\((19|20)\d{2}\)", System.Text.RegularExpressions.RegexOptions.Compiled);

        public static MergeService Instance { get; private set; }

        public MergeService(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager,
            IApplicationPaths paths, ILogManager logManager)
        {
            _lm = libraryManager;
            _um = userManager;
            _udm = userDataManager;
            _paths = paths;
            _log = logManager.GetLogger("MediaMerge");
            Instance = this;
        }

        public sealed class DupGroup
        {
            public string Tmdb;
            public string Name;
            public string LibraryName;
            public List<BaseItem> Items = new List<BaseItem>();
            public bool NameConsistent;
            public bool AlreadyMerged;
            public BaseItem Primary;
            public string Action = "skip";
        }

        public sealed class RunResult
        {
            public int Scanned;
            public int Groups;
            public int Mergeable;
            public int Merged;
            public int Skipped;
            public int Failed;
            public bool DryRun;
            public string Trigger;
            public string ReportPath;
            public List<DupGroup> Preview = new List<DupGroup>();
        }

        #region 生命周期

        public void Run()
        {
            var cfg = SafeCfg();
            _log.Info("[MediaMerge] service started (enabled={0}, dryRun={1}, maxGroups={2}, onlyName={3}, onlySameLib={4}, monitor={5}/{6}s, incremental={7}/{8}min)",
                cfg.Enabled, cfg.DryRun, cfg.MaxGroupsPerRun, cfg.OnlyNameMatch, cfg.OnlySameLibrary,
                cfg.MonitorItemAdded, cfg.MonitorDebounceSeconds, cfg.IncrementalScanEnabled, cfg.IncrementalIntervalMinutes);

            try
            {
                _lm.ItemAdded += OnItemAdded;
                _log.Info("[MediaMerge] library ItemAdded monitor attached");
            }
            catch (Exception ex) { _log.ErrorException("[MediaMerge] attach ItemAdded failed", ex); }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(() => SupervisorLoop(token), token);

            if (!string.IsNullOrWhiteSpace(cfg.DiagnosticTmdb))
            {
                Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                    try { RunDiagnostic(cfg.DiagnosticTmdb.Trim()); } catch (Exception ex) { _log.ErrorException("[MediaMerge] diag failed", ex); }
                }, token);
            }
        }

        public void Dispose()
        {
            try { _lm.ItemAdded -= OnItemAdded; } catch { }
            try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
        }

        private PluginConfiguration SafeCfg()
        {
            try { return Plugin.Instance != null ? Plugin.Instance.Configuration : new PluginConfiguration(); }
            catch { return new PluginConfiguration(); }
        }

        #endregion

        #region 事件驱动

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                var cfg = SafeCfg();
                if (!cfg.Enabled || !cfg.MonitorItemAdded) return;
                var item = e == null ? null : e.Item;
                if (item == null) return;
                if (!(item is MediaBrowser.Controller.Entities.Movies.Movie)) return;
                var tmdb = item.GetProviderId("Tmdb");
                if (string.IsNullOrWhiteSpace(tmdb)) return;
                _pending[tmdb] = DateTime.UtcNow;
                _log.Info("[MediaMerge] event queued tmdb={0} path={1}", tmdb, item.Path);
            }
            catch (Exception ex) { _log.ErrorException("[MediaMerge] OnItemAdded failed", ex); }
        }

        private async Task SupervisorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                try
                {
                    var cfg = SafeCfg();
                    if (!cfg.Enabled) { _pending.Clear(); continue; }

                    // 事件批次
                    if (cfg.MonitorItemAdded && !_pending.IsEmpty)
                    {
                        var debounce = TimeSpan.FromSeconds(Math.Max(3, cfg.MonitorDebounceSeconds));
                        var oldest = _pending.Values.Min();
                        if (DateTime.UtcNow - oldest >= debounce && DateTime.UtcNow - _lastEventRun >= debounce)
                        {
                            var keys = new HashSet<string>(_pending.Keys, StringComparer.OrdinalIgnoreCase);
                            _pending.Clear();
                            _lastEventRun = DateTime.UtcNow;
                            var r = RunOnce(cfg, keys, null, "event", false);
                            _log.Info("[MediaMerge] event batch done: groups={0}, merged={1}, failed={2}, dryRun={3}", r.Groups, r.Merged, r.Failed, r.DryRun);
                        }
                    }

                    // 定时增量
                    if (cfg.IncrementalScanEnabled)
                    {
                        var interval = TimeSpan.FromMinutes(Math.Max(10, cfg.IncrementalIntervalMinutes));
                        if (DateTime.UtcNow - _lastIncremental >= interval)
                        {
                            _lastIncremental = DateTime.UtcNow;
                            var since = DateTime.UtcNow - interval - TimeSpan.FromMinutes(15);
                            var r = RunOnce(cfg, null, since, "increment", false);
                            _log.Info("[MediaMerge] incremental done: groups={0}, merged={1}, failed={2}", r.Groups, r.Merged, r.Failed);
                        }
                    }

                    // 拆分控制文件：data/EmbyMediaMerge/split.txt，内容为 TMDB 或条目 Id，一行一个
                    HandleSplitFile();
                }
                catch (Exception ex)
                {
                    _log.ErrorException("[MediaMerge] supervisor loop error", ex);
                }
            }
        }

        #endregion

        #region 主流程

        public RunResult RunOnce(PluginConfiguration cfg, HashSet<string> tmdbFilter, DateTime? createdAfter, string trigger, bool forceReport)
        {
            var result = new RunResult { Trigger = trigger, DryRun = cfg.DryRun };
            if (cfg == null) cfg = SafeCfg();
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                _log.Warn("[MediaMerge] another run in progress, skip ({0})", trigger);
                result.Skipped = -1;
                return result;
            }

            try
            {
                if (cfg.KeepPlayedFirst) LoadUserAggregates();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var groups = CollectGroups(cfg, tmdbFilter, createdAfter, out var scanned);
                result.Scanned = scanned;
                result.Groups = groups.Count;
                result.Preview = groups.Take(Math.Max(0, cfg.ReportTopN <= 0 ? groups.Count : cfg.ReportTopN)).ToList();

                var maxGroups = cfg.MaxGroupsPerRun <= 0 ? int.MaxValue : cfg.MaxGroupsPerRun;
                int processed = 0;
                foreach (var g in groups)
                {
                    if (processed >= maxGroups) { g.Action = "limit"; result.Skipped++; continue; }
                    if (!g.NameConsistent && cfg.OnlyNameMatch) { g.Action = "name-mismatch"; result.Skipped++; continue; }
                    if (g.AlreadyMerged) { g.Action = "already"; result.Skipped++; continue; }

                    result.Mergeable++;

                    g.Primary = SelectPrimary(g.Items, cfg);

                    if (cfg.DryRun)
                    {
                        g.Action = "dryrun";
                        continue;
                    }

                    if (g.Primary == null) { g.Action = "no-primary"; result.Failed++; continue; }

                    try
                    {
                        var ordered = new List<BaseItem> { g.Primary };
                        ordered.AddRange(g.Items.Where(i => i.Id != g.Primary.Id));
                        _lm.MergeItems(ordered.ToArray());
                        g.Action = "merged";
                        result.Merged++;
                        processed++;
                        _log.Info("[MediaMerge] merged tmdb={0} name={1} primary={2} versions={3}", g.Tmdb, g.Name, g.Primary.Id, g.Items.Count);
                        AppendHistory(new
                        {
                            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            trigger,
                            tmdb = g.Tmdb,
                            name = g.Name,
                            library = g.LibraryName,
                            primaryId = g.Primary.Id,
                            itemIds = g.Items.Select(i => i.Id).ToArray(),
                            paths = g.Items.Select(i => i.Path).ToArray()
                        });
                    }
                    catch (Exception ex)
                    {
                        g.Action = "failed";
                        result.Failed++;
                        _log.ErrorException("[MediaMerge] merge failed tmdb=" + g.Tmdb, ex);
                    }
                }

                result.ReportPath = WriteReport(cfg, result, sw.Elapsed);
                _log.Info("[MediaMerge] run done trigger={0} scanned={1} groups={2} mergeable={3} merged={4} skipped={5} failed={6} dryRun={7} elapsed={8}ms",
                    trigger, result.Scanned, result.Groups, result.Mergeable, result.Merged, result.Skipped, result.Failed, result.DryRun, sw.ElapsedMilliseconds);
                return result;
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private List<DupGroup> CollectGroups(PluginConfiguration cfg, HashSet<string> tmdbFilter, DateTime? createdAfter, out int scanned)
        {
            var wanted = Split(cfg.Libraries);
            var exclude = new HashSet<string>(Split(cfg.ExcludeTmdb), StringComparer.OrdinalIgnoreCase);

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie" },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                EnableTotalRecordCount = false
            };

            var all = _lm.GetItemList(query) ?? Array.Empty<BaseItem>();
            scanned = all.Length;

            var filtered = all.Where(i =>
            {
                var t = i.GetProviderId("Tmdb");
                if (string.IsNullOrWhiteSpace(t)) return false;
                if (exclude.Contains(t)) return false;
                if (tmdbFilter != null && tmdbFilter.Count > 0 && !tmdbFilter.Contains(t)) return false;
                if (wanted.Count > 0)
                {
                    var lib = GetLibraryName(i);
                    if (!wanted.Any(w => string.Equals(w, lib, StringComparison.OrdinalIgnoreCase))) return false;
                }
                return true;
            }).ToList();

            var groups = new List<DupGroup>();
            foreach (var byTmdb in filtered.GroupBy(i => i.GetProviderId("Tmdb"), StringComparer.OrdinalIgnoreCase))
            {
                // 同库限制：按库名再切一刀
                IEnumerable<IEnumerable<BaseItem>> buckets;
                if (cfg.OnlySameLibrary)
                {
                    buckets = byTmdb.GroupBy(i => GetLibraryName(i) ?? "", StringComparer.OrdinalIgnoreCase)
                                    .Select(g => (IEnumerable<BaseItem>)g.ToList()).ToList();
                }
                else
                {
                    buckets = new List<IEnumerable<BaseItem>> { byTmdb.ToList() };
                }

                foreach (var bucket in buckets)
                {
                    var items = bucket.ToList();
                    if (items.Count < 2) continue;
                    if (createdAfter.HasValue && !items.Any(i => i.DateCreated >= createdAfter.Value)) continue;

                    var g = new DupGroup
                    {
                        Tmdb = byTmdb.Key,
                        Name = items.Select(i => (i.Name ?? "").Trim()).GroupBy(n => n).OrderByDescending(x => x.Count()).First().Key,
                        LibraryName = GetLibraryName(items[0]),
                        Items = items,
                        NameConsistent = items.Select(i => (i.Name ?? "").Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
                        AlreadyMerged = items.Select(i => i.PresentationUniqueKey ?? "").Distinct().Count() == 1
                    };
                    groups.Add(g);
                }
            }

            // 合并潜力大的排在前面，便于灰度与报告阅读
            return groups.OrderByDescending(g => g.Items.Count).ThenBy(g => g.Name).ToList();
        }

        private string GetLibraryName(BaseItem item)
        {
            try
            {
                var folders = _lm.GetCollectionFolders(item);
                var f = folders == null ? null : folders.FirstOrDefault();
                return f == null ? "" : (f.Name ?? "");
            }
            catch { return ""; }
        }

        private BaseItem SelectPrimary(List<BaseItem> items, PluginConfiguration cfg)
        {
            if (items == null || items.Count == 0) return null;
            var prefer = (cfg.PreferResolution ?? "auto").Trim().ToLowerInvariant();
            var scored = items.Select(i => new { Item = i, Score = Score(i, cfg, prefer) })
                              .OrderByDescending(x => x.Score)
                              .ThenBy(x => x.Item.Id)
                              .ToList();
            return scored[0].Item;
        }

        private double Score(BaseItem item, PluginConfiguration cfg, string prefer)
        {
            double s = 0;

            if (cfg.KeepPlayedFirst)
            {
                try
                {
                    UserAgg a;
                    if (_agg.TryGetValue(KeyOf(item), out a) && a != null)
                    {
                        if (a.Play > 0) s += Math.Min(a.Play, 3) * 10;
                        if (a.Played) s += 6;
                        if (a.Fav) s += 12;
                        if (a.Pos > 0 && !a.Played) s += 4;
                    }
                }
                catch { }
            }

            try { if (item.HasImage(ImageType.Primary, 0)) s += 6; } catch { }
            try { if (item.HasImage(ImageType.Backdrop, 0)) s += 2; } catch { }

            // 名称干净度：客户端详情页展示的是主条目的 Name，优先保留"干净标题"
            try
            {
                var name = item.Name ?? "";
                if (!NameNoise.IsMatch(name)) s += 5;
                else s -= 3;
                if (CjkChars.IsMatch(name)) s += 2;
                if (YearTag.IsMatch(name)) s += 1;
                if (name.Length > 40) s -= 2;
                if (name.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)) s -= 3;
            }
            catch { }

            var path = item.Path ?? "";
            if (path.IndexOf("[tmdbid-", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("{tmdbid=", StringComparison.OrdinalIgnoreCase) >= 0) s += 4;

            if (prefer != "auto" && prefer.Length > 0 && path.IndexOf(prefer, StringComparison.OrdinalIgnoreCase) >= 0) s += 15;

            try
            {
                if (item.Size > 0) s += Math.Min(item.Size / 1.0e12, 5.0);
                if (path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)) s += 0.5;
            }
            catch { }

            return s;
        }

        #endregion

        #region 诊断 / 拆分 / 报告

        public void RunDiagnostic(string key)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie" },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                EnableTotalRecordCount = false
            };
            var all = _lm.GetItemList(query) ?? Array.Empty<BaseItem>();
            var hits = all.Where(i =>
            {
                var t = i.GetProviderId("Tmdb");
                return string.Equals(t, key, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Id.ToString(), key, StringComparison.OrdinalIgnoreCase);
            }).ToList();

            _log.Info("[MediaMerge] DIAG key={0} hits={1}", key, hits.Count);
            foreach (var i in hits)
            {
                var play = 0;
                try
                {
                    var usersForDiag = _users.Count > 0 ? _users : ((_um.GetUsers(new UserQuery())?.Items) ?? Array.Empty<MediaBrowser.Controller.Entities.User>()).ToList();
                    foreach (var u in usersForDiag) { var d = _udm.GetUserData(u, i); if (d != null) play += d.PlayCount; }
                }
                catch { }
                _log.Info("[MediaMerge] DIAG id={0} name={1} lib={2} key={3} playCount={4} size={5} path={6}",
                    i.Id, i.Name, GetLibraryName(i), i.PresentationUniqueKey, play, i.Size, i.Path);
            }
        }

        private void HandleSplitFile()
        {
            try
            {
                var dir = DataDir;
                var f = Path.Combine(dir, "split.txt");
                if (!File.Exists(f)) return;
                var lines = File.ReadAllLines(f).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
                File.Delete(f);
                foreach (var key in lines)
                {
                    BaseItem item = null;
                    if (long.TryParse(key, out var id)) item = _lm.GetItemById(id);
                    var target = item;
                    if (target == null)
                    {
                        var query = new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { "Movie" },
                            Recursive = true,
                            GroupByPresentationUniqueKey = false,
                            EnableTotalRecordCount = false
                        };
                        target = (_lm.GetItemList(query) ?? Array.Empty<BaseItem>())
                            .FirstOrDefault(i => string.Equals(i.GetProviderId("Tmdb"), key, StringComparison.OrdinalIgnoreCase));
                    }
                    if (target == null) { _log.Warn("[MediaMerge] split target not found: {0}", key); continue; }
                    try
                    {
                        _lm.SplitItems(target);
                        _log.Info("[MediaMerge] split done id={0} name={1}", target.Id, target.Name);
                        AppendHistory(new { time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), trigger = "split", tmdb = target.GetProviderId("Tmdb"), name = target.Name, primaryId = target.Id });
                    }
                    catch (Exception ex) { _log.ErrorException("[MediaMerge] split failed: " + key, ex); }
                }
            }
            catch (Exception ex) { _log.ErrorException("[MediaMerge] split file handling failed", ex); }
        }

        public string DataDir
        {
            get
            {
                var root = Plugin.Instance != null && !string.IsNullOrWhiteSpace(Plugin.Instance.DataFolderPath)
                    ? Plugin.Instance.DataFolderPath
                    : Path.Combine(_paths.DataPath, "EmbyMediaMerge");
                Directory.CreateDirectory(root);
                return root;
            }
        }

        private void AppendHistory(object record)
        {
            try
            {
                var line = JsonSerializer.Serialize(record);
                File.AppendAllText(Path.Combine(DataDir, "merge_history.jsonl"), line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) { _log.ErrorException("[MediaMerge] history write failed", ex); }
        }

        private string WriteReport(PluginConfiguration cfg, RunResult r, TimeSpan elapsed)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# 多版本合并报告");
                sb.AppendLine();
                sb.AppendLine("| 项目 | 值 |");
                sb.AppendLine("| --- | --- |");
                sb.AppendLine("| 时间 | " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " |");
                sb.AppendLine("| 触发 | " + r.Trigger + " |");
                sb.AppendLine("| 干跑模式 | " + (r.DryRun ? "是（未改库）" : "否") + " |");
                sb.AppendLine("| 扫描电影条目 | " + r.Scanned + " |");
                sb.AppendLine("| 重复组数 | " + r.Groups + " |");
                sb.AppendLine("| 可合并条目 | " + r.Mergeable + " |");
                sb.AppendLine("| 本次合并组数 | " + r.Merged + " |");
                sb.AppendLine("| 跳过 | " + r.Skipped + " |");
                sb.AppendLine("| 失败 | " + r.Failed + " |");
                sb.AppendLine("| 耗时 | " + (int)elapsed.TotalSeconds + " s |");
                sb.AppendLine();

                if (r.Preview.Count > 0)
                {
                    sb.AppendLine("## 分组明细");
                    sb.AppendLine();
                    sb.AppendLine("| TMDB | 片名 | 媒体库 | 条数 | 片名一致 | 已合并 | 处理 | 主条目 |");
                    sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
                    foreach (var g in r.Preview)
                    {
                        sb.AppendLine(string.Format("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} |",
                            g.Tmdb, Esc(g.Name), Esc(g.LibraryName), g.Items.Count,
                            g.NameConsistent ? "是" : "否", g.AlreadyMerged ? "是" : "否", g.Action,
                            g.Primary == null ? "-" : g.Primary.Id.ToString()));
                    }
                    sb.AppendLine();
                    sb.AppendLine("## 条目路径");
                    sb.AppendLine();
                    foreach (var g in r.Preview)
                    {
                        sb.AppendLine("### [" + g.Tmdb + "] " + g.Name + "（" + g.LibraryName + "）");
                        foreach (var i in g.Items)
                        {
                            sb.AppendLine("- " + i.Id + " | " + (i.Path ?? ""));
                        }
                        sb.AppendLine();
                    }
                }

                var name = "report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + r.Trigger + ".md";
                var full = Path.Combine(DataDir, name);
                File.WriteAllText(full, sb.ToString(), Encoding.UTF8);
                return full;
            }
            catch (Exception ex)
            {
                _log.ErrorException("[MediaMerge] report write failed", ex);
                return "";
            }
        }

        private static string Esc(string s) => (s ?? "").Replace("|", "\\|");

        private static List<string> Split(string s)
        {
            return (s ?? "").Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        #endregion
    }
}
