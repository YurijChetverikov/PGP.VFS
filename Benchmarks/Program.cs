using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Microsoft.Extensions.Logging.Abstractions;
using PGP.VFS; // Замените, если ваш namespace называется иначе

namespace PGP.VFS.Benchmarks;

// disableGcTimer: true — важно для I/O, чтобы сборщик мусора не искажал время
// ShortRunJob — ускоряет прогон (3 итерации вместо 15), чтобы не ждать полчаса
[MemoryDiagnoser]
//[ShortRunJob]
public class VfsReadBenchmarks
{
    private string _rootFolder = null!;
    private string _tableFolder = null!;
    private VFS _vfs = null!;

    private Guid _compressed1MB;
    private Guid _uncompressed1MB;

    [GlobalSetup]
    public void Setup()
    {
        // Создаём временные папки для теста
        _rootFolder = Path.Combine(Path.GetTempPath(), $"vfs_bench_root_{Guid.NewGuid():N}");
        _tableFolder = Path.Combine(Path.GetTempPath(), $"vfs_bench_table_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootFolder);
        Directory.CreateDirectory(_tableFolder);

        // 1. Создаём файлы-источники на диске (режим SOURCES)
        // Заполняем случайными данными, чтобы Zstd не сжал нули до нереальных размеров
        CreateSourceFile("compressed_1mb.bin", 1024 * 1024);
        CreateSourceFile("uncompressed_1mb.bin", 1024 * 1024);

        // 2. Инициализируем VFS, добавляем файлы и пакуем
        _vfs = VFS.CreateAsync(_rootFolder, _tableFolder, NullLogger<VFS>.Instance)
                   .GetAwaiter().GetResult();

        _compressed1MB = _vfs.AddFile("compressed_1mb.bin", VFSFileFlags.IsCompressed).GetAwaiter().GetResult();
        _uncompressed1MB = _vfs.AddFile("uncompressed_1mb.bin", VFSFileFlags.None).GetAwaiter().GetResult();

        // _vfs.CommitChangesAsync().GetAwaiter().GetResult();

        // Пакуем с уровнем по умолчанию (15)
        _vfs.PackAsync().GetAwaiter().GetResult();

        _vfs.DisposeAsync().GetAwaiter().GetResult();

        // 3. Переоткрываем в PACKS — целевой режим для чтения
        _vfs = VFS.OpenAsync(VFSMode.PACKS, _rootFolder, _tableFolder, NullLogger<VFS>.Instance)
                   .GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_rootFolder, recursive: true); } catch { }
        try { Directory.Delete(_tableFolder, recursive: true); } catch { }
    }

    private void CreateSourceFile(string name, int size)
    {
        var path = Path.Combine(_rootFolder, name);
        var data = new byte[size];
        new Random(42).NextBytes(data); // Фиксированный seed для воспроизводимости
        File.WriteAllBytes(path, data);
    }

    // --- A1: Латентность открытия потока (без чтения) ---
    [Benchmark]
    public void GetFileStream_Open_Compressed_1MB()
    {
        using var _ = _vfs.GetFileStream(_compressed1MB);
    }

    [Benchmark]
    public void GetFileStream_Open_Uncompressed_1MB()
    {
        using var _ = _vfs.GetFileStream(_uncompressed1MB);
    }

    // --- A2: Полное чтение (включая декомпрессию) ---
    [Benchmark]
    public void GetFileStream_FullRead_Compressed_1MB()
    {
        using var stream = _vfs.GetFileStream(_compressed1MB);
        stream.CopyTo(Stream.Null); // Обязательно дочитываем до конца!
    }

    [Benchmark]
    public void GetFileStream_FullRead_Uncompressed_1MB()
    {
        using var stream = _vfs.GetFileStream(_uncompressed1MB);
        stream.CopyTo(Stream.Null);
    }

    // --- A5: Lookup ---
    [Benchmark]
    public object? GetFile_Lookup()
    {
        return _vfs.GetFile(_compressed1MB);
    }
}

public class Program
{
    public static void Main(string[] args)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(Job.Default // Используем Job.Default как основу
        .WithToolchain(InProcessNoEmitToolchain.Instance) // Явно задаем toolchain
        .WithWarmupCount(5)     // 5 прогревочных запусков
        .WithIterationCount(10) // 10 основных итераций
    );

        BenchmarkRunner.Run<VfsReadBenchmarks>(config);
    }
}