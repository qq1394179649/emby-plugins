using System.ComponentModel;
using Emby.Web.GenericEdit;

namespace EmbyStrmSize
{
    /// <summary>插件设置项，Emby 4.10 插件页通过 GenericEdit 渲染为表单。</summary>
    public class PluginConfiguration : EditableOptionsBase
    {
        public override string EditorTitle => "Strm Size Probe 设置";

        public override string EditorDescription =>
            "扫描 .strm 媒体项，用 HTTP Range 请求探测真实体积；可选择写回 Emby 媒体库大小。";

        [DisplayName("启用插件")]
        [Description("关闭后不会自动扫描。")]
        public bool Enabled { get; set; } = true;

        [DisplayName("启动时自动扫描")]
        [Description("服务器启动后自动执行一次扫描。")]
        public bool AutoScanOnStartup { get; set; } = true;

        [DisplayName("并发数")]
        [Description("同时探测的 HTTP 连接数，建议 4-8。")]
        public int Concurrency { get; set; } = 4;

        [DisplayName("单条超时（秒）")]
        [Description("单次探测的超时时间。")]
        public int TimeoutSeconds { get; set; } = 15;

        [DisplayName("单次扫描条数")]
        [Description("0 表示不限制。全库补齐可设为 100000。")]
        public int MaxItemsPerRun { get; set; } = 50;

        [DisplayName("媒体库名称")]
        [Description("逗号分隔，留空表示全部媒体库。")]
        public string Libraries { get; set; } = "";

        [DisplayName("写入媒体库")]
        [Description("把探测到的真实大小写回 Emby 的 Size 字段，详情页与列表页都会显示。默认关闭，仅出报告。")]
        public bool WriteToLibrary { get; set; } = false;

        [DisplayName("仅补齐缺失值")]
        [Description("开启后只写入 Emby 当前未识别（Size 为空或 0）的条目，已有值一律跳过不覆盖。")]
        public bool WriteOnlyWhenMissing { get; set; } = true;

        [DisplayName("单次最多写入条数")]
        [Description("0 表示不限制。用于分批灰度，避免一次写入过多。")]
        public int MaxWritePerRun { get; set; } = 0;

        [DisplayName("小于该值标记异常（MB）")]
        [Description("探测结果小于该值时在报告中标记为可疑。")]
        public int SuspectBelowMb { get; set; } = 100;

        [DisplayName("新条目入库自动探测")]
        [Description("事件驱动：订阅媒体库 ItemAdded，新 .strm 条目入库后立即探测大小，只处理 Size 为空或 0 的条目，无需等定时扫描。")]
        public bool MonitorItemAdded { get; set; } = true;

        [DisplayName("事件去抖等待（秒）")]
        [Description("入库事件后等待若干秒再探测，避开入库过程中的重复事件与文件尚未落地的情况。")]
        public int MonitorDebounceSeconds { get; set; } = 8;

        [DisplayName("事件队列上限")]
        [Description("同时排队等待探测的条目上限，超出后丢弃并记日志，防止大批量入库时压垮网盘。")]
        public int MonitorMaxQueue { get; set; } = 200;

        [DisplayName("定时增量扫描")]
        [Description("可选开关：按周期检查最近入库的条目，只补 Size 为空或 0 的 .strm，作为事件驱动漏单的兜底。")]
        public bool IncrementalScanEnabled { get; set; } = false;

        [DisplayName("增量扫描周期（分钟）")]
        [Description("定时增量扫描的间隔，最小 5 分钟。")]
        public int IncrementalIntervalMinutes { get; set; } = 15;

        [DisplayName("每轮增量条数")]
        [Description("每轮按入库时间倒序最多检查多少条最新条目，0 表示使用 500。")]
        public int IncrementalBatchSize { get; set; } = 500;

        [DisplayName("诊断条目ID")]
        [Description("启动时把该条目的 Size / MediaSources 打印到日志，留空关闭。")]
        public string DiagnosticItemId { get; set; } = "";
    }
}
