using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyStrmSize
{
    public class ProbeResult
    {
        public long? Size;
        public int HttpStatus;
        public string Error;
        public bool Ok => Size.HasValue;
    }

    public static class StrmProber
    {
        private static readonly HttpClient Http = CreateClient();

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
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; emby-strm-size/1.0)");
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
                    r.Error = ct.IsCancellationRequested ? "cancelled" : "timeout";
                    return r;
                }
                catch (Exception ex)
                {
                    r.Error = ex.GetType().Name + ": " + ex.Message;
                    return r;
                }
            }
        }
    }
}
