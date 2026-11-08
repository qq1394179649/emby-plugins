using System.ComponentModel;
using Emby.Web.GenericEdit;

namespace EmbyMediaMerge
{
    /// <summary>插件设置项，Emby 4.10 插件页通过 GenericEdit 渲染为表单。</summary>
    public class PluginConfiguration : EditableOptionsBase
    {
        public override string EditorTitle => "Media Merge 设置";

        public override string EditorDescription =>
            "把 TMDB 相同的重复电影条目合并为一条多版本记录：仅修改库内条目关联，不移动、不删除任何文件，可随时拆分还原。";

        [DisplayName("启用插件")]
        [Description("总开关。关闭后事件监听与定时增量都不处理，但计划任务仍可手动执行。")]
        public bool Enabled { get; set; } = true;

        [DisplayName("干跑模式（只出报告不改库）")]
        [Description("开启时只统计与写报告，不执行任何合并。首次上线建议保持开启，确认分组无误后再关闭。")]
        public bool DryRun { get; set; } = true;

        [DisplayName("单次最多合并组数")]
        [Description("每次任务/事件批次最多合并多少组，用于灰度。0 表示不限制。")]
        public int MaxGroupsPerRun { get; set; } = 20;

        [DisplayName("仅合并片名一致的组")]
        [Description("开启后只处理组内条目名称完全相同的组，片名有差异的组（多为命名差异或误配）只在报告中列出，需人工确认。")]
        public bool OnlyNameMatch { get; set; } = true;

        [DisplayName("仅同库合并")]
        [Description("开启后只合并同一媒体库内的重复条目，跨库（如华语电影与外语电影各一份）不在本插件处理。")]
        public bool OnlySameLibrary { get; set; } = true;

        [DisplayName("优先保留有观看记录的条目")]
        [Description("开启后优先让带播放进度/收藏/已看标记的条目作为主条目，避免用户历史丢失。")]
        public bool KeepPlayedFirst { get; set; } = true;

        [DisplayName("优先保留的分辨率")]
        [Description("作为主版本的首选分辨率，可选 auto / 2160p / 1080p / 720p。auto 表示按观看记录、海报、命名规范综合评分。")]
        public string PreferResolution { get; set; } = "auto";

        [DisplayName("媒体库名称白名单")]
        [Description("逗号分隔，留空表示全部媒体库。例如：华语电影,外语电影,动画电影。")]
        public string Libraries { get; set; } = "";

        [DisplayName("排除的 TMDB ID")]
        [Description("逗号分隔。这些影片即使重复也不合并，用于规避已知误配（如不同影片被识别成同一 TMDB）。")]
        public string ExcludeTmdb { get; set; } = "";

        [DisplayName("新条目入库自动合并")]
        [Description("事件驱动：新条目入库后延迟数秒，若与库中已有条目 TMDB 相同则自动合并，无需等定时任务。")]
        public bool MonitorItemAdded { get; set; } = true;

        [DisplayName("事件去抖等待（秒）")]
        [Description("入库事件后等待时间，避开一次入库产生多条事件与文件尚未完全落地的情况。")]
        public int MonitorDebounceSeconds { get; set; } = 10;

        [DisplayName("定时增量扫描")]
        [Description("可选兜底：按周期处理近期入库造成的重复，事件驱动漏单时可开启，默认关闭。")]
        public bool IncrementalScanEnabled { get; set; } = false;

        [DisplayName("增量扫描周期（分钟）")]
        [Description("定时增量扫描间隔，最小 10 分钟。")]
        public int IncrementalIntervalMinutes { get; set; } = 60;

        [DisplayName("报告展示组数")]
        [Description("每次任务写入报告时最多列出多少组明细，0 表示全部。")]
        public int ReportTopN { get; set; } = 200;

        [DisplayName("诊断 TMDB ID")]
        [Description("填写后，启动时把该 TMDB 的所有条目（Id、路径、观看记录、是否已合并）打印到日志，留空关闭。")]
        public string DiagnosticTmdb { get; set; } = "";
    }
}
