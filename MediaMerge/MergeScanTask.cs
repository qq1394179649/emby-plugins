using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace EmbyMediaMerge
{
    /// <summary>手动触发的计划任务：管理 → 计划任务 → "多版本合并扫描"。</summary>
    public class MergeScanTask : IScheduledTask
    {
        public string Name => "多版本合并扫描";

        public string Key => "MediaMergeScan";

        public string Description => "扫描 TMDB 相同的重复电影条目并合并为多版本；干跑模式下只出报告。";

        public string Category => "媒体库";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress?.Report(0);
            var svc = MergeService.Instance;
            if (svc == null)
            {
                Plugin.Log?.Info("[MediaMerge] scheduled task: service not ready");
                return Task.CompletedTask;
            }

            var cfg = Plugin.Instance != null ? Plugin.Instance.Configuration : new PluginConfiguration();
            Plugin.Log?.Info("[MediaMerge] scheduled task started (dryRun={0}, maxGroups={1}, onlyName={2})",
                cfg.DryRun, cfg.MaxGroupsPerRun, cfg.OnlyNameMatch);

            var result = svc.RunOnce(cfg, null, null, "manual", true);
            progress?.Report(100);
            Plugin.Log?.Info("[MediaMerge] scheduled task finished: groups={0} merged={1} report={2}",
                result.Groups, result.Merged, result.ReportPath ?? "-");
            return Task.CompletedTask;
        }
    }
}
