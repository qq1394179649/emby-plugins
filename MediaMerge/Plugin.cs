using System;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;

namespace EmbyMediaMerge
{
    /// <summary>多版本合并插件：把同一影片的重复条目合并为一条多版本记录（不移动、不删除任何文件）。</summary>
    public class Plugin : BasePluginSimpleUI<PluginConfiguration>
    {
        public const string PluginName = "Media Merge (多版本合并)";
        public static readonly Guid PluginId = new Guid("a3d19c74-2f8b-4e57-8a61-5c9e0b4d7f22");

        public static Plugin Instance { get; private set; }
        public static ILogger Log { get; private set; }

        public Plugin(IApplicationHost applicationHost, ILogManager logManager)
            : base(applicationHost)
        {
            Instance = this;
            Log = logManager.GetLogger("MediaMerge");
            try
            {
                var o = GetOptions();
                Log.Info("[MediaMerge] plugin constructed, id={0}, enabled={1}, dryRun={2}, maxGroups={3}",
                    PluginId, o.Enabled, o.DryRun, o.MaxGroupsPerRun);
            }
            catch (Exception ex)
            {
                Log.ErrorException("[MediaMerge] ctor options load failed", ex);
            }
        }

        public override string Name => PluginName;

        public override Guid Id => PluginId;

        public override string Description =>
            "Merge duplicate movie items that share the same TMDB id into a single multi-version record (library-level only, files untouched).";

        public PluginConfiguration Configuration => GetOptions();
    }
}
