using Microsoft.Extensions.Logging;
using PGP.VFS;

namespace PGP.VFS.TestApp
{
    internal class Program
    {
        private static string[] _filePaths = new string[]
        {
            "../../../Tests/256mb pack VFS",
            "../../../Tests/512mb pack VFS",
            "../../../Tests/4gb pack VFS"
        };

        private static ILogger<Program>? _logger;

        static async Task Main(string[] args)
        {
            ILoggerFactory ilf = LoggerFactory.Create(builder =>
            {
                builder.AddConsole().SetMinimumLevel(LogLevel.Debug);
            });

            _logger = ilf.CreateLogger<Program>();

            // Create

            // 256 MB max pack size VFS
            //VFS vfs = await VFS.CreateAsync(Path.GetFullPath(_filePaths[0]), Path.GetFullPath(_filePaths[0]), ilf.CreateLogger<VFS>(), 268435456);

            // 512 MB max pack size VFS
            //VFS vfs = await VFS.CreateAsync(Path.GetFullPath(_filePaths[1]), Path.GetFullPath(_filePaths[1]), ilf.CreateLogger<VFS>(), 536870912);

            // 4 GB max pack size VFS
            //VFS vfs = await VFS.CreateAsync(Path.GetFullPath(_filePaths[2]), Path.GetFullPath(_filePaths[2]), ilf.CreateLogger<VFS>());

            // Open

            // 256 MB max pack size VFS
            VFS vfs = await VFS.OpenAsync(VFSMode.PACKS, Path.GetFullPath(_filePaths[0]), Path.GetFullPath(_filePaths[0]), ilf.CreateLogger<VFS>());

            // 512 MB max pack size VFS
            //VFS vfs = await VFS.OpenAsync(VFSMode.SOURCES, Path.GetFullPath(_filePaths[1]), Path.GetFullPath(_filePaths[1]), ilf.CreateLogger<VFS>());

            // 4 GB max pack size VFS
            //VFS vfs = await VFS.OpenAsync(VFSMode.SOURCES, Path.GetFullPath(_filePaths[2]), Path.GetFullPath(_filePaths[2]), ilf.CreateLogger<VFS>());

            Console.WriteLine("Supported commands:");
            Console.WriteLine("q - quits the application");
            Console.WriteLine($"addc [FILENAME] - adds new file with \"IsCompressed\" flag. Relative to \"{vfs.RootFolder}\"");
            Console.WriteLine($"add [FILENAME] - adds new file. Relative to \"{vfs.RootFolder}\"");
            Console.WriteLine($"r [FILEGUID] - removes a file");
            Console.WriteLine($"d - dumps all files (except deleted)");
            Console.WriteLine($"da - dumps all files");
            Console.WriteLine($"p - perform pack");
            Console.WriteLine($"def - perform defragmentation");
            Console.WriteLine($"e [FILEGUID] - export file from pack");
            Console.WriteLine($"m - print MAX_PACK_SIZE");

            bool needToQuit = false;

            while (true)
            {
                Console.WriteLine($"\nEnter command:");

                string[] parts = Console.ReadLine().Split(' ');

                string firstPart = "";
                string lastPart = "";

                if (parts.Length == 0)
                {
                    Console.WriteLine("Invalid");
                    continue;
                }
                else
                {
                    firstPart = parts[0];
                    if (parts.Length > 1)
                    {
                        lastPart = string.Join(' ', parts.Skip(1));
                    }
                }

                try
                {
                    switch (firstPart)
                    {
                        case "q":
                            needToQuit = true;
                            break;
                        case "m":
                            Console.WriteLine(vfs.MAX_PACK_SIZE);
                            break;
                        case "add":
                            if (parts.Length == 2)
                            {
                                Console.WriteLine($"File GUID: {(await vfs.AddFile(lastPart, VFSFileFlags.None)).ToString()}");
                            }
                            else Console.WriteLine("Invalid");
                            break;
                        case "addc":
                            Console.WriteLine($"File GUID: {(await vfs.AddFile(lastPart)).ToString()}");
                            break;
                        case "r":
                            vfs.DeleteFile(Guid.Parse(lastPart));
                            break;
                        case "e":
                            Guid g = Guid.Parse(lastPart);
                            VFS.File? f = vfs.GetFile(g);
                            if (f == null)
                            {
                                Console.WriteLine("File not found");
                                continue;
                            }

                            Stream s = vfs.GetFileStream(g);
                            using (FileStream fs = new FileStream(Path.Combine(vfs.RootFolder, $"[{DateTime.UtcNow.Ticks.ToString()}]_" + f.SourceRelativePath.Replace("\\", "--")), FileMode.Create))
                            {
                                await s.CopyToAsync(fs);
                                await fs.FlushAsync();
                            }
                            break;
                        case "def":
                            await vfs.RunDefragmentation();
                            break;
                        case "p":
                            if (parts.Length == 1)
                            {
                                await vfs.PackAsync();
                            }
                            else
                            {
                                byte level = byte.Parse(lastPart);
                                await vfs.PackAsync(level);
                            }
                            break;
                        case "d":
                            Console.WriteLine("ID|FLAGS|PACK INDEX|PACK OFFSET|RELATIVE FILENAME");

                            foreach (var item in vfs.DumpFiles())
                            {
                                Console.WriteLine($"| {item.Key.ToString()} | {item.Value.PackIndex.ToString("D3")} | {item.Value.PackOffset.ToString("D10")} | {item.Value.Flags} | \"{item.Value.SourceRelativePath}\" ");
                            }
                            break;
                        case "da":
                            Console.WriteLine("ID|FLAGS|PACK INDEX|PACK OFFSET|RELATIVE FILENAME");

                            foreach (var item in vfs.DumpFiles(true))
                            {
                                Console.WriteLine($"| {item.Key.ToString()} | {item.Value.PackIndex.ToString("D3")} | {item.Value.PackOffset.ToString("D10")} | {item.Value.Flags} | \"{item.Value.SourceRelativePath}\" ");
                            }
                            break;
                        default:
                            Console.WriteLine("Invalid");
                            continue;
                    }

                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }



                if (needToQuit) break;
            }

            await vfs.DisposeAsync();
        }
    }
}
