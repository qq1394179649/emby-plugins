using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;

namespace EmbyStrmSize
{
    public class ProbeResult
    {
        public long? Size;
        public int HttpStatus;
        public long ElapsedMs;
        public string Error;
        public bool Ok => Size.HasValue;
    }

    /// <summary>ffprobe 探测结果：体积/时长/码率/视频流信息。</summary>
    public class MediaProbeResult
    {
        public long? Size;
        public double? DurationSeconds;
        public int? TotalBitrate;
        public string Container;
        public string Error;
        public long ElapsedMs;
        public List<MediaStream> Streams = new List<MediaStream>();

        public bool HasVideo => Streams.Any(s => s.Type == MediaStreamType.Video);
        public MediaStream Video => Streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        public int? VideoBitrate => Video != null ? Video.BitRate : null;
        public bool Ok => DurationSeconds.HasValue || Streams.Count > 0;
    }

    public static class StrmProber
    {
        private static readonly HttpClient Http = CreateClient();
        private static string _ffprobeCache;

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false
            };
            var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; emby-strm-size/1.1)");
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            return c;
        }

        /// <summary>Reads the first non-empty line of a .strm file (an absolute URL).</summary>
        public static string ReadStrmUrl(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    var s = line.Trim().TrimStart('\uFEFF');
                    if (s.Length > 0) return s;
                }
            }
            return null;
        }

        /// <summary>GET Range: bytes=0-0 and read the real object size from Content-Range / Content-Length.</summary>
        public static async Task<ProbeResult> ProbeAsync(string url, int timeoutSeconds, CancellationToken ct)
        {
            var r = new ProbeResult();
            if (string.IsNullOrWhiteSpace(url)) { r.Error = "empty url"; return r; }
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            { r.Error = "not http url"; return r; }

            var sw = Stopwatch.StartNew();
            using (var perAttempt = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                perAttempt.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds)));
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
                        using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perAttempt.Token).ConfigureAwait(false))
                        {
                            r.HttpStatus = (int)resp.StatusCode;
                            sw.Stop(); r.ElapsedMs = sw.ElapsedMilliseconds;
                            if ((int)resp.StatusCode >= 400)
                            {
                                r.Error = "http " + r.HttpStatus;
                                return r;
                            }

                            var cr = resp.Content.Headers.ContentRange;
                            if (cr != null && cr.HasLength && cr.Length.HasValue && cr.Length.Value > 0)
                            {
                                r.Size = cr.Length.Value;
                                return r;
                            }

                            var cl = resp.Content.Headers.ContentLength;
                            if (cl.HasValue && cl.Value > 0)
                            {
                                // A 200 answer to a one-byte range means the whole body length was reported.
                                r.Size = cl.Value;
                                return r;
                            }

                            r.Error = "no length (status " + r.HttpStatus + ")";
                            return r;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    sw.Stop(); r.ElapsedMs = sw.ElapsedMilliseconds;
                    r.Error = ct.IsCancellationRequested ? "cancelled" : "timeout";
                    return r;
                }
                catch (Exception ex)
                {
                    sw.Stop(); r.ElapsedMs = sw.ElapsedMilliseconds;
                    r.Error = ex.GetType().Name + ": " + ex.Message;
                    return r;
                }
            }
        }

        /// <summary>
        /// 用 ffprobe 读取远程流头部：时长、总码率、视频/音频流信息（分辨率、编码、码率）。
        /// 只解析容器头部，不做完整解码。
        /// </summary>
        public static async Task<MediaProbeResult> ProbeMediaAsync(string url, string ffprobePath, int analyzeSeconds, int timeoutSeconds, CancellationToken ct)
        {
            var r = new MediaProbeResult();
            if (string.IsNullOrWhiteSpace(url)) { r.Error = "empty url"; return r; }

            var exe = ResolveFfprobe(ffprobePath);
            if (exe == null) { r.Error = "ffprobe not found"; return r; }

            var analyzeUs = Math.Max(1, analyzeSeconds) * 1000000L;
            var probeSize = Math.Max(1, analyzeSeconds) * 2000000L;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in new[]
            {
                "-v", "error", "-hide_banner", "-print_format", "json", "-show_format", "-show_streams",
                "-analyzeduration", analyzeUs.ToString(CultureInfo.InvariantCulture),
                "-probesize", probeSize.ToString(CultureInfo.InvariantCulture),
                "-rw_timeout", (Math.Max(5, timeoutSeconds) * 1000000L).ToString(CultureInfo.InvariantCulture),
                url
            }) psi.ArgumentList.Add(a);

            var sw = Stopwatch.StartNew();
            Process proc = null;
            try
            {
                proc = Process.Start(psi);
                if (proc == null) { r.Error = "ffprobe start failed"; return r; }

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds) + analyzeSeconds + 5));
                    try
                    {
                        await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        try { proc.Kill(true); } catch { }
                        sw.Stop(); r.ElapsedMs = sw.ElapsedMilliseconds;
                        r.Error = ct.IsCancellationRequested ? "cancelled" : "ffprobe timeout";
                        return r;
                    }
                }

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                sw.Stop(); r.ElapsedMs = sw.ElapsedMilliseconds;
                if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
                {
                    r.Error = "ffprobe exit " + proc.ExitCode + ": " + Trim(stderr, 200);
                    return r;
                }
                Parse(stdout, r);
                if (!r.Ok && r.Error == null) r.Error = "no metadata: " + Trim(stderr, 160);
                return r;
            }
            catch (Exception ex)
            {
                sw.Stop(); r.ElapsedMs = sw.ElapsedMilliseconds;
                r.Error = ex.GetType().Name + ": " + ex.Message;
                return r;
            }
            finally
            {
                try { proc?.Dispose(); } catch { }
            }
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static void Parse(string json, MediaProbeResult r)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                JsonElement fmt;
                if (root.TryGetProperty("format", out fmt))
                {
                    r.Size = GetLong(fmt, "size");
                    r.TotalBitrate = ToInt(GetLong(fmt, "bit_rate"));
                    r.Container = GetString(fmt, "format_name");
                    var dur = GetDouble(fmt, "duration");
                    if (dur.HasValue && dur.Value > 0) r.DurationSeconds = dur.Value;
                }

                JsonElement streams;
                if (!root.TryGetProperty("streams", out streams) || streams.ValueKind != JsonValueKind.Array) return;

                foreach (var s in streams.EnumerateArray())
                {
                    var codecType = GetString(s, "codec_type");
                    var attachedPic = false;
                    JsonElement disp;
                    if (s.TryGetProperty("disposition", out disp)) attachedPic = GetLong(disp, "attached_pic") == 1;

                    if (string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        if (attachedPic) continue; // 封面图，不是真正的视频流
                        var v = new MediaStream
                        {
                            Type = MediaStreamType.Video,
                            Index = (int)(GetLong(s, "index") ?? r.Streams.Count),
                            Codec = GetString(s, "codec_name"),
                            Profile = GetString(s, "profile"),
                            PixelFormat = GetString(s, "pix_fmt"),
                            Width = ToInt(GetLong(s, "width")),
                            Height = ToInt(GetLong(s, "height")),
                            BitRate = ToInt(GetLong(s, "bit_rate")),
                            AverageFrameRate = ToFloat(GetDouble(s, "avg_frame_rate")),
                            RealFrameRate = ToFloat(GetDouble(s, "r_frame_rate")),
                            Level = ToInt(GetLong(s, "level")),
                            IsDefault = GetLong(disp, "default") == 1,
                            DisplayTitle = BuildDisplayTitle(GetLong(s, "width"), GetLong(s, "height"), GetString(s, "codec_name"))
                        };
                        if (!string.IsNullOrEmpty(v.Codec)) r.Streams.Add(v);
                    }
                    else if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        var a = new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            Index = (int)(GetLong(s, "index") ?? r.Streams.Count),
                            Codec = GetString(s, "codec_name"),
                            Profile = GetString(s, "profile"),
                            Channels = ToInt(GetLong(s, "channels")),
                            ChannelLayout = GetString(s, "channel_layout"),
                            SampleRate = ToInt(GetLong(s, "sample_rate")),
                            BitRate = ToInt(GetLong(s, "bit_rate")),
                            IsDefault = GetLong(disp, "default") == 1,
                            Language = GetTag(s, "language"),
                            DisplayTitle = GetString(s, "codec_name")
                        };
                        if (!string.IsNullOrEmpty(a.Codec)) r.Streams.Add(a);
                    }
                }
            }

            // 部分 MKV 的容器头不给视频流码率，用「总码率 - 音频码率」估算，
            // 否则这批条目永远被判为缺码率，每轮重复起 ffprobe。
            var video = r.Streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var hasVideoRate = video != null && video.BitRate.HasValue && video.BitRate.Value > 0;
            if (!hasVideoRate && video != null && r.TotalBitrate.HasValue && r.TotalBitrate.Value > 0)
            {
                var audioSum = r.Streams.Where(s => s.Type == MediaStreamType.Audio && s.BitRate.HasValue && s.BitRate.Value > 0)
                                        .Sum(s => s.BitRate.Value);
                var est = r.TotalBitrate.Value - audioSum;
                if (est < r.TotalBitrate.Value / 4) est = r.TotalBitrate.Value;
                if (est > 0) video.BitRate = est;
            }
        }

        private static string BuildDisplayTitle(long? w, long? h, string codec)
        {
            var name = (codec ?? "").ToUpperInvariant();
            if (name == "H264") name = "H.264";
            else if (name == "HEVC") name = "HEVC";
            else if (name == "AV1") name = "AV1";
            string res = null;
            if (h.HasValue && h.Value > 0)
            {
                var height = h.Value;
                if (height >= 2000) res = "4K";
                else if (height >= 1000) res = "1080p";
                else if (height >= 700) res = "720p";
                else if (height >= 500) res = "576p";
                else if (height >= 400) res = "480p";
            }
            if (res != null && name.Length > 0) return res + " " + name;
            return name.Length > 0 ? name : res;
        }

        private static string GetTag(JsonElement s, string key)
        {
            JsonElement tags;
            if (!s.TryGetProperty("tags", out tags) || tags.ValueKind != JsonValueKind.Object) return null;
            var v = GetString(tags, key);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        private static string GetString(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : (v.ValueKind == JsonValueKind.Null ? null : v.ToString());
        }

        private static long? GetLong(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
            if (v.ValueKind == JsonValueKind.String)
            {
                long parsed;
                if (long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
                double d;
                if (double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return (long)d;
            }
            return null;
        }

        private static double? GetDouble(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (s.Contains("/"))
                {
                    // "24000/1001"
                    var parts = s.Split('/');
                    double a, b;
                    if (parts.Length == 2
                        && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b)
                        && b > 0) return a / b;
                    return null;
                }
                double parsed;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            }
            return null;
        }

        private static int? ToInt(long? v)
        {
            if (!v.HasValue) return null;
            if (v.Value <= 0) return null;
            return v.Value > int.MaxValue ? int.MaxValue : (int)v.Value;
        }

        private static float? ToFloat(double? v)
        {
            if (!v.HasValue || v.Value <= 0) return null;
            return (float)v.Value;
        }

        /// <summary>定位 ffprobe：优先配置，其次常见路径，最后 PATH。</summary>
        public static string ResolveFfprobe(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var c = configured.Trim();
                if (File.Exists(c)) return c;
            }
            if (_ffprobeCache != null) return _ffprobeCache;
            foreach (var p in new[] { "/bin/ffprobe", "/usr/bin/ffprobe", "/usr/local/bin/ffprobe", "/system/ffprobe", "/opt/emby-server/bin/ffprobe" })
            {
                try { if (File.Exists(p)) { _ffprobeCache = p; return p; } } catch { }
            }
            try
            {
                var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':');
                foreach (var dir in paths.Where(d => d.Length > 0))
                {
                    var p = Path.Combine(dir, "ffprobe");
                    if (File.Exists(p)) { _ffprobeCache = p; return p; }
                }
            }
            catch { }
            return null;
        }
    }
}
