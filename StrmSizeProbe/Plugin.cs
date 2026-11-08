using System;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;

namespace EmbyStrmSize
{
    /// <summary>
    /// 使用 BasePluginSimpleUI 接入 Emby 4.10 新版插件设置页（GenericEdit），
    /// 配置持久化为 config/plugins/configurations/{Name}.json。
    /// </summary>
    public class Plugin : BasePluginSimpleUI<PluginConfiguration>
    {
        public const string PluginName = "Strm Size Probe";
        public static readonly Guid PluginId = new Guid("7f3c1a9e-4b62-4f1d-9a3e-2c8d5b7e6a10");

        public static Plugin Instance { get; private set; }
        public static ILogger Log { get; private set; }

        public Plugin(IApplicationHost applicationHost, ILogManager logManager)
            : base(applicationHost)
        {
            Instance = this;
            Log = logManager.GetLogger("StrmSize");
            try
            {
                var opts = GetOptions();
                Log.Info("[StrmSize] plugin constructed (GenericEdit UI), id={0}, enabled={1}, write={2}, onlyMissing={3}",
                    PluginId, opts.Enabled, opts.WriteToLibrary, opts.WriteOnlyWhenMissing);
            }
            catch (Exception ex)
            {
                Log.ErrorException("[StrmSize] plugin ctor options load failed", ex);
            }
        }

        public override string Name => PluginName;

        public override Guid Id => PluginId;

        public override string Description =>
            "Probes the real byte size of .strm media (HTTP Range 0-0) and optionally writes it back to the Emby library.";

        /// <summary>当前生效的配置（每次读取自动重载 json）。</summary>
        public PluginConfiguration Configuration => GetOptions();

        public override PluginInfo GetPluginInfo()
        {
            // base 内部依赖 SetAttributes 之后才非空的 Version/Id，不能在构造期调用
            return base.GetPluginInfo();
        }
    }
}
