using K4os.Compression.LZ4;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

class Program
{
    static int processedFiles = 0;
    static int totalFiles = 0;
    static object progressLock = new object();

    static void Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "-h" || args[0] == "--help"))
        {
            PrintHelp();
            return;
        }

        if (args.Length == 0)
        {
            Console.WriteLine("Usage: ripzip <file_or_folder>");
            Console.WriteLine("Use ripzip -h for help.");
            return;
        }

        string input = args[0];

        if (!File.Exists(input) && !Directory.Exists(input))
        {
            Console.WriteLine("Error: file or folder not found.");
            return;
        }

        string output = input.TrimEnd(Path.DirectorySeparatorChar) + ".zip";

        var sw = System.Diagnostics.Stopwatch.StartNew();

        List<string> files = new();
        string baseDir = null;

        if (File.Exists(input))
        {
            files.Add(input);
        }
        else
        {
            baseDir = Path.GetFullPath(input);
            files.AddRange(Directory.GetFiles(baseDir, "*", SearchOption.AllDirectories));
        }

        totalFiles = files.Count;

        using var fs = File.Create(output);
        var options = new ZipWriterOptions(CompressionType.None);
        using var writer = new ZipWriter(fs, options);

        var queue = new BlockingCollection<(string EntryName, byte[] Data)>(Environment.ProcessorCount * 4);

        var writerTask = Task.Run(() =>
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                using var ms = new MemoryStream(item.Data);
                writer.Write(item.EntryName, ms);
            }
        });

        Parallel.ForEach(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            file =>
            {
                try
                {
                    string entryName = baseDir == null
                        ? Path.GetFileName(file)
                        : Path.GetRelativePath(baseDir, file);

                    byte[] data = File.ReadAllBytes(file);

                    if (IsAlreadyCompressed(file))
                    {
                        queue.Add((entryName, data));
                        UpdateProgress();
                        return;
                    }

                    byte[] compressed = CompressSmart(data);
                    queue.Add((entryName, compressed));
                    UpdateProgress();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error on '{file}': {ex.Message}");
                }
            });

        queue.CompleteAdding();
        writerTask.Wait();

        sw.Stop();
        Console.WriteLine($"\nCompleted in {sw.Elapsed.TotalSeconds:F2} seconds");
    }

    static void PrintHelp()
    {
        Console.WriteLine("ripzip");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  ripzip <file_or_folder>    Compress");
        Console.WriteLine("  ripzip -h   Show this help");
    }

    static void UpdateProgress()
    {
        int done = Interlocked.Increment(ref processedFiles);
        double percent = (double)done / totalFiles * 100.0;

        int barWidth = 30;
        int filled = (int)(percent / 100.0 * barWidth);

        string bar = "[" + new string('#', filled) + new string('-', barWidth - filled) + "]";

        lock (progressLock)
        {
            Console.CursorLeft = 0;
            Console.Write($"{bar} {percent:0.0}% ({done}/{totalFiles})");
        }
    }

    static bool IsAlreadyCompressed(string file)
    {
        string ext = Path.GetExtension(file).ToLower();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".mp4" or ".mp3" or ".zip" or ".rar" or ".7z" or ".pdf" or ".exe" or ".dll" => true,
            _ => false
        };
    }

    static byte[] CompressSmart(byte[] input)
    {
        if (input.Length < 64 * 1024)
            return Deflate(input, CompressionLevel.Fastest);

        if (LooksLikeText(input))
            return Deflate(input, CompressionLevel.Optimal);

        return LZ4Fast(input);
    }

    static bool LooksLikeText(byte[] data)
    {
        int count = Math.Min(2000, data.Length);
        for (int i = 0; i < count; i++)
        {
            if (data[i] == 0) return false;
        }
        return true;
    }

    static byte[] LZ4Fast(byte[] input)
    {
        int max = LZ4Codec.MaximumOutputSize(input.Length);
        byte[] output = new byte[max];
        int encoded = LZ4Codec.Encode(input, 0, input.Length, output, 0, max);
        Array.Resize(ref output, encoded);
        return output;
    }

    static byte[] Deflate(byte[] input, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, level))
        {
            ds.Write(input, 0, input.Length);
        }
        return ms.ToArray();
    }
}
