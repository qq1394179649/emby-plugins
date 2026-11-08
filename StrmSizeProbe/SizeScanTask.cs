using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace EmbyStrmSize
{
    /// <summary>手动可触发的计划任务：管理 → 计划任务 → "Strm 大小扫描"。</summary>
    public class SizeScanTask : IScheduledTask
    {
        public string Name => "Strm 大小扫描";

        public string Key => "StrmSizeProbe";

        public string Description => "探测 .strm 对应源文件真实大小，并按配置决定是否回写 Emby 媒体库";

        public string Category => "媒体库";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress?.Report(0);
            var svc = SizeScanService.Instance;
            if (svc == null)
            {
                Plugin.Log?.Info("[StrmSize] scheduled task: service not ready");
                return;
            }
            Plugin.Log?.Info("[StrmSize] scheduled task started");
            await svc.ScanOnce(cancellationToken).ConfigureAwait(false);
            progress?.Report(100);
            Plugin.Log?.Info("[StrmSize] scheduled task finished");
        }
    }
}
