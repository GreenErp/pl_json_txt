using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public class M3UChecker {
    public class M3UEntry {
        public List<string> MetaLines = new List<string>();
        public string Url;
        public string UserAgent;
        public string Referrer;
    }

    public static void Main(string[] args) {
        string inputPath = args.Length > 0 ? args[0] : "index.m3u";
        string outputPath = args.Length > 1 ? args[1] : "index1.m3u";
        int maxConcurrency = 150;
        if (args.Length > 2) {
            int.TryParse(args[2], out maxConcurrency);
            if (maxConcurrency <= 0) maxConcurrency = 150;
        }

        int timeoutSeconds = 5;
        if (args.Length > 3) {
            int.TryParse(args[3], out timeoutSeconds);
            if (timeoutSeconds <= 0) timeoutSeconds = 5;
        }

        if (!File.Exists(inputPath)) {
            Console.WriteLine("Error: Input file '" + inputPath + "' does not exist.");
            return;
        }

        Console.WriteLine("Reading entries from: " + inputPath);
        var entries = LoadEntries(inputPath);
        Console.WriteLine("Total entries parsed: " + entries.Count);

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
        ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
        ServicePointManager.DefaultConnectionLimit = 500;

        var handler = new HttpClientHandler() {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };

        var client = new HttpClient(handler) {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        int total = entries.Count;
        int checkedCount = 0;
        int workingCount = 0;

        var fileLock = new object();
        using (var outputWriter = new StreamWriter(outputPath, false, new UTF8Encoding(false))) {
            outputWriter.WriteLine("#EXTM3U");
            outputWriter.Flush();

            var semaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = new List<Task>();

            Console.WriteLine(string.Format("Starting verification: Concurrency = {0}, Timeout = {1}s...", maxConcurrency, timeoutSeconds));
            var startTime = DateTime.UtcNow;

            foreach (var entry in entries) {
                semaphore.Wait();
                tasks.Add(Task.Run(async () => {
                    try {
                        bool isWorking = await CheckUrlAsync(client, entry, timeoutSeconds);
                        if (isWorking) {
                            Interlocked.Increment(ref workingCount);
                            lock (fileLock) {
                                foreach (var meta in entry.MetaLines) {
                                    outputWriter.WriteLine(meta);
                                }
                                outputWriter.WriteLine(entry.Url);
                                outputWriter.Flush();
                            }
                        }
                    } catch {
                    } finally {
                        int c = Interlocked.Increment(ref checkedCount);
                        if (c % 500 == 0 || c == total) {
                            var elapsed = DateTime.UtcNow - startTime;
                            int currentWorking = workingCount;
                            Console.WriteLine(string.Format("[{0}/{1}] ({2:P1}) Working: {3} | Elapsed: {4:mm\\:ss}", 
                                c, total, (double)c / total, currentWorking, elapsed));
                        }
                        semaphore.Release();
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            var totalTime = DateTime.UtcNow - startTime;
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine("Verification complete in " + totalTime.TotalSeconds.ToString("F1") + " seconds.");
            Console.WriteLine("Total checked: " + checkedCount + " | Valid streams: " + workingCount);
            Console.WriteLine("Saved working playlist to: " + outputPath);
            Console.WriteLine("--------------------------------------------------");
        }
    }

    public static async Task<bool> CheckUrlAsync(HttpClient client, M3UEntry entry, int timeoutSeconds) {
        string url = entry.Url.Trim();
        if (string.IsNullOrEmpty(url)) return false;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
            
            try {
                using (var cts = new CancellationTokenSource(timeoutSeconds * 1000))
                using (var req = new HttpRequestMessage(HttpMethod.Get, url)) {
                    string ua = !string.IsNullOrEmpty(entry.UserAgent) 
                        ? entry.UserAgent 
                        : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                    req.Headers.TryAddWithoutValidation("User-Agent", ua);

                    if (!string.IsNullOrEmpty(entry.Referrer)) {
                        req.Headers.TryAddWithoutValidation("Referer", entry.Referrer);
                    }

                    using (var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)) {
                        int code = (int)res.StatusCode;
                        if (code < 200 || code >= 400) return false;

                        string contentType = res.Content.Headers.ContentType != null 
                            ? res.Content.Headers.ContentType.MediaType 
                            : "";

                        byte[] buffer = new byte[512];
                        int bytesRead = 0;
                        try {
                            using (var stream = await res.Content.ReadAsStreamAsync()) {
                                bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                            }
                        } catch {
                            return false;
                        }

                        return IsValidStream(url, contentType, buffer, bytesRead);
                    }
                }
            } catch {
                return false;
            }
        } else if (url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("srt://", StringComparison.OrdinalIgnoreCase)) {
            try {
                var uri = new Uri(url);
                int port = uri.Port > 0 ? uri.Port : (url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ? 1935 : 9000);
                using (var tcp = new TcpClient()) {
                    var connectTask = tcp.ConnectAsync(uri.Host, port);
                    var timeoutTask = Task.Delay(timeoutSeconds * 1000);
                    var completed = await Task.WhenAny(connectTask, timeoutTask);
                    return completed == connectTask && tcp.Connected;
                }
            } catch {
                return false;
            }
        }
        return false;
    }

    public static bool IsValidStream(string url, string contentType, byte[] buffer, int bytesRead) {
        if (bytesRead == 0) return false;

        string ct = (contentType ?? "").ToLowerInvariant();

        // 1. Explicit HTML/XML error/web pages are not video streams
        if (ct.Contains("text/html") || ct.Contains("application/xhtml") || ct.Contains("application/json")) {
            return false;
        }

        string textSnippet = Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimStart();
        string lowerText = textSnippet.ToLowerInvariant();

        // 2. Reject HTML document patterns or tags
        if (lowerText.StartsWith("<!doctype html") || 
            lowerText.StartsWith("<html") || 
            lowerText.StartsWith("<head") || 
            lowerText.StartsWith("<body") ||
            lowerText.StartsWith("<script") ||
            (lowerText.StartsWith("<!--") && (lowerText.Contains("<html") || lowerText.Contains("<!doctype")))) {
            return false;
        }

        // 3. Reject common HTML error titles or phrases if text contains HTML markup
        if (lowerText.Contains("<title>") || lowerText.Contains("cloudflare") || lowerText.Contains("access denied") || lowerText.Contains("404 not found")) {
            if (lowerText.Contains("<html") || lowerText.Contains("<body>") || lowerText.Contains("<head>")) {
                return false;
            }
        }

        // 4. If URL ends with or contains .m3u8 or contentType has mpegurl:
        // A valid M3U8 MUST start with #EXTM3U or contain #EXTINF or #EXT-X-
        if (url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0 || ct.Contains("mpegurl")) {
            return textSnippet.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
                   textSnippet.Contains("#EXT-X-") ||
                   textSnippet.Contains("#EXTINF");
        }

        // 5. If URL is .mpd (DASH manifest)
        if (url.IndexOf(".mpd", StringComparison.OrdinalIgnoreCase) >= 0 || ct.Contains("dash+xml")) {
            return textSnippet.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                   textSnippet.IndexOf("<MPD", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 6. Direct video or audio Content-Type
        if (ct.StartsWith("video/") || ct.StartsWith("audio/")) {
            return true;
        }

        // 7. Binary video signatures:
        // MPEG-TS sync byte (0x47)
        if (buffer[0] == 0x47) return true;
        // MP4 signature (ftyp)
        if (bytesRead >= 8 && textSnippet.Substring(0, Math.Min(12, textSnippet.Length)).Contains("ftyp")) return true;
        // Matroska / MKV / WebM (0x1A 0x45 0xDF 0xA3)
        if (bytesRead >= 4 && buffer[0] == 0x1A && buffer[1] == 0x45 && buffer[2] == 0xDF && buffer[3] == 0xA3) return true;
        // FLV signature
        if (bytesRead >= 3 && buffer[0] == 0x46 && buffer[1] == 0x4C && buffer[2] == 0x56) return true;

        // 8. General M3U playlist content
        if (textSnippet.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)) return true;

        // 9. Generic binary stream (application/octet-stream) that does not look like web markup
        if (ct.Contains("application/octet-stream") && !lowerText.Contains("<html") && !lowerText.Contains("<?xml")) {
            return true;
        }

        return false;
    }

    public static List<M3UEntry> LoadEntries(string filePath) {
        var list = new List<M3UEntry>();
        var currentMeta = new List<string>();

        using (var reader = new StreamReader(filePath, Encoding.UTF8)) {
            string line;
            while ((line = reader.ReadLine()) != null) {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (trimmed.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase) ||
                    (currentMeta.Count > 0 && trimmed.StartsWith("#", StringComparison.OrdinalIgnoreCase))) {
                    currentMeta.Add(line);
                } else if (!trimmed.StartsWith("#", StringComparison.OrdinalIgnoreCase)) {
                    var entry = new M3UEntry();
                    entry.Url = trimmed;
                    entry.MetaLines.AddRange(currentMeta);

                    // Extract custom headers if present
                    foreach (var m in currentMeta) {
                        Match uaMatch = Regex.Match(m, "http-user-agent=[\"']?([^\"'\r\n]+)[\"']?", RegexOptions.IgnoreCase);
                        if (uaMatch.Success) {
                            entry.UserAgent = uaMatch.Groups[1].Value.Trim();
                        }
                        Match refMatch = Regex.Match(m, "http-referrer=[\"']?([^\"'\r\n]+)[\"']?", RegexOptions.IgnoreCase);
                        if (refMatch.Success) {
                            entry.Referrer = refMatch.Groups[1].Value.Trim();
                        }
                    }

                    list.Add(entry);
                    currentMeta.Clear();
                }
            }
        }
        return list;
    }
}
