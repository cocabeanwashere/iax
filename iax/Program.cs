using System.Collections.Concurrent;
using System.ComponentModel;
using System.Web;

namespace iax;

class Program
{
    static async Task Main(string[] args)
    {
        var downloaders = new List<Downloader>();
        var console = new ActiveConsole(downloaders);
        var take = 0;
        var parallel = 1;
        try 
        {
            var collection = args.FirstOrDefault();
            var path = "./";
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "-p")
                {
                    parallel = int.Parse(args[++i]);
                }
                if (args[i] == "-t")
                {
                    take = int.Parse(args[++i]);
                }
                if (args[i] == "-o")
                {
                    path = args[++i];
                }
            }

            if (System.Diagnostics.Debugger.IsAttached) {

            }

            if (collection == null)
            {
                console.WriteLine("Please provide a collection name");
                return;
            }

            var dir = $"https://archive.org/download/{collection}";
            var idleDownloaders = new ConcurrentBag<Downloader>();
            var semaphore = new SemaphoreSlim(parallel);
            var context = new Context(take, semaphore, downloaders, idleDownloaders, console);
            for (int i = 0; i < parallel; i++)
            {
                var downloader = new Downloader(context);
                downloaders.Add(downloader);
                idleDownloaders.Add(downloader);
            }
            var walker = new Walker(context);
            await walker.WalkAsync(dir, path);

            while (downloaders.Any(x=>x.GetStatus().active))
            {
                await Task.Delay(1000);
            }
        }
        catch (Exception ex) {
            console.WriteLine(ex.Message);
            throw;
        }   
    }
}

class Context {
    internal readonly ConcurrentBag<Downloader> IdleDownloaders;
    internal readonly ActiveConsole ActiveConsole;
    internal readonly int Take;
    internal int Taken = 0;
    internal SemaphoreSlim Semaphore;
    internal readonly List<Downloader> Downloaders;

    internal Context(int take, SemaphoreSlim semaphore, List<Downloader> downloaders, ConcurrentBag<Downloader> idleDownloaders, ActiveConsole console)
    {
        Take = take;
        Semaphore = semaphore;
        Downloaders = downloaders;
        IdleDownloaders = idleDownloaders;
        ActiveConsole = console;
    }

}


class Walker {
    private readonly Context context;

    internal Walker(Context context)
    {
        this.context = context;

    }
    internal async Task WalkAsync(string dir, string path)
    {
        try {
            if (context.Take > 0 && context.Taken > context.Take)
            {
                return;
            }
            
            HttpClient client = new HttpClient();
            var response = await client.GetAsync(dir);
            HtmlAgilityPack.HtmlDocument doc = new HtmlAgilityPack.HtmlDocument();
            client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie"));
            doc.LoadHtml(await response.Content.ReadAsStringAsync());
            var rows = doc.DocumentNode.Descendants(0).Where(node => node.Name == "table" && node.Attributes.Contains("class") && node.Attributes["class"].Value == "directory-listing-table").FirstOrDefault()?.Descendants(0).Where(node => node.Name == "tr");
            if (rows == null)
            {
                context.ActiveConsole.WriteLine($"No files found in {dir}");
                return;
            }

            Parallel.ForEach(rows, async row =>
            {
                if (context.Take > 0 && context.Taken > context.Take)
                {
                    return;
                }
                var first = row.SelectSingleNode("td");
                if (first != null && first.InnerText.Contains("Go to parent directory"))
                {
                    return;
                }
                var link = row.SelectSingleNode("td/a");
                if (link != null)
                {
                    var href = link.GetAttributeValue("href", "");
                    if (href.EndsWith("/"))
                    {
                        await WalkAsync(dir + "/" + href, path + link.InnerText);
                    }
                    else
                    {
                        var fileUrl = $"{dir}/{href}";
                        context.Semaphore.Wait();
                        context.IdleDownloaders.TryTake(out var downloader);
                        //downloader can't be null due to semaphore
                        await downloader!.DownloadAsync(path, client, fileUrl, dir);
                        context.IdleDownloaders.Add(downloader);
                        context.Semaphore.Release();
                    }
                }
            });
        }
        catch (Exception ex) {
            context.ActiveConsole.WriteLine(ex.Message);
            throw;
        }   
    }
}

class Downloader {

    private readonly Context context;
    private object sync = new object();
    private static int bufferSize = 10 * 1024;
    private bool active = false;
    private long? length = null;
    private long? read = null;
    private long? duration = null;
    private string? filename = null;

    internal Downloader(Context context)
    {
        this.context = context;
    }

    internal (bool active, string? filename, long? length, long? read, long? duration) GetStatus() {
        lock (sync) {
            return (active, filename, length, read, duration);
        }
    }

    internal async Task DownloadAsync(string path, HttpClient client, string fileUrl, string referrer)
    {
        try {
            var filenameRaw = fileUrl.Split('/').Last();
            filename = HttpUtility.UrlDecode(filenameRaw);
            FileInfo existing = new FileInfo(path + filename);
            FileInfo info = new FileInfo(path + filename + ".iax");
            FileStream output;
            DateTimeOffset? date = null;
            length = null;
            read = null;
            duration = null;
            active = true;

            try {
                var headRequest = new HttpRequestMessage(HttpMethod.Head, fileUrl);
                headRequest.Headers.Referrer = new Uri(referrer);
                var head = await client.SendAsync(headRequest);
                length = head.Content.Headers.ContentLength ?? 0;
                date = head.Content.Headers.LastModified; ;

                if (existing.Exists || info.Exists)
                {
                    if (info.Exists && info.Length == length && date.HasValue && info.LastWriteTime == date.Value.DateTime)
                    {
                        File.Move(path + filename + ".iax", path + filename);
                        context.ActiveConsole.WriteLine($"File {filename} already downloaded and is up to date");
                        return;
                    }
                    if (existing.Exists && existing.Length == length && date.HasValue && existing.LastWriteTime == date.Value.DateTime)
                    {
                        context.ActiveConsole.WriteLine($"File {filename} already downloaded and is up to date");

                        return;
                    }

                    if (date.HasValue && info.Exists && info.LastWriteTime != date.Value.DateTime)
                    {
                        info.Delete();
                        info = new FileInfo(path + filename + ".iax");
                    }
                }
            }
            catch 
            {
                context.ActiveConsole.WriteLine($"Failed to get header info for {this.filename}");
                return;
            }
            if (Directory.Exists(path) == false)
            {
                Directory.CreateDirectory(path);
            }
            output = File.OpenWrite(path + filename + ".iax");
            var x = new FileInfo(path + filename + ".iax");
            if (date.HasValue)
            {
                x.LastWriteTime = date.Value.DateTime;
            }

            var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);

            if (info.Exists && info.Length > 0)
            {
                //resume download
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(info.Length, null);
                context.ActiveConsole.WriteLine($"Resuming download of {this.filename} {info.Length}/{this.length}");
            }
            
            try
            {
                Interlocked.Increment(ref context.Taken);
                request.Headers.Referrer = new Uri(referrer);
                var response = await client.SendAsync(request);
                var file = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[bufferSize];
                var readBuffer = 0;
                var start = DateTime.Now;

                read = 0;
                while ((readBuffer = await file.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, readBuffer);
                    read += readBuffer;
                    duration = (long)(DateTime.Now - start).TotalMilliseconds;
                }
            }
            catch (Exception ex)
            {
                context.ActiveConsole.WriteLine($"Error downloading  {this.read?.ToString() ?? "??"}/{this.length?.ToString() ?? "??"} {(this.duration.HasValue?TimeSpan.FromMilliseconds((double)this.duration).ToString():"??")}");
                context.ActiveConsole.WriteLine(ex.Message);
                return;
            }
            finally
            {
                if (output != null)
                {
                    output.Flush();
                    output.Close();
                    x = new FileInfo(path + filename + ".iax");
                    if (date.HasValue)
                    {
                        x.LastWriteTime = date.Value.DateTime;
                    }
                }
                lock (sync)
                {
                    active = false;
                }
            }
            File.Move(path + filename + ".iax", path + filename, true);
            //uneccessary?
            x = new FileInfo(path + filename);
            if (date.HasValue)
            {
                x.LastWriteTime = date.Value.DateTime;
            }
            context.ActiveConsole.WriteLine($"Downloaded {this.filename}  {this.read?.ToString() ?? "??"}/{this.length?.ToString() ?? "??"} {(this.duration.HasValue?TimeSpan.FromMilliseconds((double)this.duration).ToString():"??")}");
        }
        catch (Exception ex) {
            context.ActiveConsole.WriteLine(ex.Message);
            throw;
        }   
    }
}

internal class ActiveConsole {
    BackgroundWorker worker = new BackgroundWorker();
    ManualResetEventSlim reset = new ManualResetEventSlim();
    IList<Downloader> downloaders;
    ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
    internal ActiveConsole(IList<Downloader> downloaders)
    {
        this.downloaders = downloaders ?? throw new ArgumentNullException(nameof(downloaders));
        if (!Console.IsOutputRedirected) {
            this.downloaders = downloaders;
            worker.DoWork += Worker_DoWork;
            worker.RunWorkerAsync();
        }
    }
    internal void WriteLine(string message)
    {
        if (Console.IsOutputRedirected) {
            Console.WriteLine(message);
        }
        else {
            messages.Enqueue(message);
            reset.Set();
        }
    }

    internal void Worker_DoWork(object? sender, DoWorkEventArgs e)
    {
        int lastLength = 0;
        while (true)
        {
            try {
                int lastRemainingToCover = lastLength;
                //Reset position to Active Downloads.
                Console.SetCursorPosition(0, Console.CursorTop - lastLength);
                while (messages.TryDequeue(out var message))
                {
                    Console.WriteLine(message.PadRight(Console.WindowWidth - 1));
                    lastRemainingToCover--;
                }
                lastLength = 1;
                Console.WriteLine("Active downloads:".PadRight(Console.WindowWidth - 1));
                foreach (var downloader in downloaders.Select(x=>x.GetStatus()).OrderBy(x=>x.filename))
                {
                    if (downloader.active)
                    {
                        Console.WriteLine($"{downloader.filename} {downloader.read?.ToString() ?? "??"}/{downloader.length?.ToString() ?? "??"} {(downloader.duration.HasValue?TimeSpan.FromMilliseconds((double)downloader.duration).ToString():"??")}".PadRight(Console.WindowWidth - 1));
                        lastLength++;
                    }
                }
                //Clear remaining lines
                for (int i = lastLength;  i < lastRemainingToCover; i++) {
                    Console.WriteLine("".PadRight(Console.WindowWidth - 1));
                    lastLength++;
                }
                
                reset.Wait(1000);
            }
            catch (Exception ex) {
                Console.WriteLine(ex.Message);
                throw;
            }
        }
    }
}