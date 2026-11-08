```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13450HX 2.40GHz, 1 CPU, 16 logical and 10 physical cores
  [Host] : .NET 10.0.13, X64 NativeAOT x86-64-v3

Toolchain=InProcessNoEmitToolchain  IterationCount=10  WarmupCount=5  

```
| Method                                  | Mean          | Error        | StdDev       | Gen0   | Allocated |
|---------------------------------------- |--------------:|-------------:|-------------:|-------:|----------:|
| GetFileStream_Open_Compressed_1MB       |     233.51 ns |     9.716 ns |     6.427 ns | 0.0236 |     248 B |
| GetFileStream_Open_Uncompressed_1MB     |      39.19 ns |     0.515 ns |     0.340 ns | 0.0099 |     104 B |
| GetFileStream_FullRead_Compressed_1MB   | 259,389.70 ns | 7,136.039 ns | 4,720.048 ns |      - |     249 B |
| GetFileStream_FullRead_Uncompressed_1MB |  77,471.64 ns |   495.152 ns |   327.512 ns |      - |     104 B |
| GetFile_Lookup                          |      28.06 ns |     0.033 ns |     0.022 ns |      - |         - |
