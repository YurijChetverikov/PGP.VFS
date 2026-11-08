using Google.FlatBuffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using System.Collections.Frozen;
using System.Globalization;
using System.Reflection.Metadata;
using ZstdSharp;

namespace PGP.VFS
{
    public partial class VFS : IAsyncDisposable
    {
        public const uint MIN_PACK_SIZE = 65535; // Almost 64KB
        public readonly uint MAX_PACK_SIZE = uint.MaxValue; // Almost 4GB
        private readonly ILogger<VFS> _logger;

        private Dictionary<Guid, VFS.File> _files = new Dictionary<Guid, VFS.File>();
        private Dictionary<byte, VFS.Pack> _packs = new Dictionary<byte, VFS.Pack>();
        private VFS.Metadata _tableMetadata;

        private readonly VFSMode _mode = VFSMode.PACKS;
        private readonly string _rootFolder;
        private readonly string _filesTablePath;

        private const byte MIN_COMPRESSION_LEVEL = 1;
        private const byte MAX_COMPRESSION_LEVEL = 22;

        public string RootFolder
        {
            get => _rootFolder;
        }

        private FrozenDictionary<byte, SafeFileHandle> _packsHandles = FrozenDictionary<byte, SafeFileHandle>.Empty;

        private readonly Lock _lockObject = new();
        private bool _isFilesTableLocked = false;
        Compressor _compressor = new Compressor(level: 19);

        /// <summary>
        /// Constructor that needs only when we creating a brand new VFS
        /// </summary>
        /// <param name="rootFolder"></param>
        /// <param name="filesTablePath"></param>
        /// <param name="maxPackSize"></param>
        private VFS(string rootFolder, string filesTablePath, uint maxPackSize, ILogger<VFS>? logger = null)
        {
            _logger = logger ?? NullLogger<VFS>.Instance;

            _mode = VFSMode.SOURCES;
            if (maxPackSize < MIN_PACK_SIZE)
                throw new ArgumentException($"maxPackSize can't be less than MIN_PACK_SIZE ({MIN_PACK_SIZE})");
            if (rootFolder == null)
                throw new ArgumentNullException("rootFolder can't be null");
            if (filesTablePath == null)
                throw new ArgumentNullException("filesTablePath can't be null");
            if (Path.GetFileName(filesTablePath).ToUpper() != "FILESTABLE.DAT")
                throw new ArgumentException("filesTablePath has invalid name");

            _rootFolder = rootFolder;
            _filesTablePath = filesTablePath;
            MAX_PACK_SIZE = maxPackSize;

            DateTime currentTime = DateTime.UtcNow;
            
            _tableMetadata = new VFS.Metadata(
                currentTime,
                currentTime,
                0,
                0
            );
        }

        private VFS(VFSMode mode, string rootFolder, string filesTablePath, ILogger<VFS> logger, uint maxPackSize)
        {
            _logger = logger ?? NullLogger<VFS>.Instance;
            _mode = mode;
            _filesTablePath = filesTablePath;
            _rootFolder = rootFolder;
            MAX_PACK_SIZE = maxPackSize;
            _tableMetadata = new Metadata(DateTime.Now, DateTime.Now, 0, 0);
        }


        #region Public

        public static async Task<VFS> CreateAsync(string rootFolder, string fileTableFolder, ILogger<VFS> logger, uint maxPackSize = uint.MaxValue)
        {
            string filesTableFilename = Path.Combine(fileTableFolder, "FILESTABLE.DAT");

            if (System.IO.File.Exists(filesTableFilename))
                throw new IOException($"Can't create VFS: there is already file at \"{filesTableFilename}\"");

            var created = new VFS(rootFolder, filesTableFilename, maxPackSize, logger);
            await created.writeFilesTableAsync(true);

            return created;
        }

        public static async Task<VFS> OpenAsync(VFSMode mode, string rootFolder, string filesTableFolder, ILogger<VFS> logger)
        {
            string filesTablePath = Path.Combine(filesTableFolder, "FILESTABLE.DAT");

            if (rootFolder == null)
                throw new ArgumentNullException("rootFolder can't be null");
            if (filesTableFolder == null)
                throw new ArgumentNullException("filesTableFolder can't be null");
            if (System.IO.File.Exists(filesTablePath) == false)
                throw new ArgumentException("filesTableFolder does not contains FILATABLE.DAT");

            byte[] filesTableBuffer = await System.IO.File.ReadAllBytesAsync(filesTablePath);
            ByteBuffer filesTableByteBuffer = new ByteBuffer(filesTableBuffer);

            PGPEngine.VFS.FilesTable ft = PGPEngine.VFS.FilesTable.GetRootAsFilesTable(filesTableByteBuffer);

            var vfs = new VFS(mode, rootFolder, filesTablePath, logger, ft.MaxPackSize);

            for (int i = 0; i < ft.PacksLength; i++)
            {
                var pack = ft.Packs(i);

                if (pack.HasValue == false)
                {
                    vfs._logger.LogWarning($"While parsing files table: has null pack at index {i}");
                    continue;
                }

                if (vfs._packs.ContainsKey(pack.Value.Index))
                {
                    vfs._logger.LogError($"While parsing files table: has more that 1 pack with pack index {pack.Value.Index}");
                    continue;
                }

                vfs._packs.Add(pack.Value.Index, new VFS.Pack(

                    pack.Value.Index,
                    pack.Value.FreeOffset
                ));
            }

            for (int i = 0; i < ft.FilesLength; i++)
            {
                var file = ft.Files(i);

                if (file.HasValue == false)
                {
                    vfs._logger.LogWarning($"While parsing files table: has null file at index {i}");
                    continue;
                }
                if (file.Value.Id.HasValue == false)
                {
                    vfs._logger.LogWarning($"While parsing files table: has file with null id at index {i}");
                    continue;
                }

                Guid id = new Guid(Utils.ConvertGUID(file.Value.Id.Value.A, file.Value.Id.Value.B));

                if (vfs._files.ContainsKey(id) == true)
                {
                    throw new Exception($"While parsing files table: has more that 1 file with id {id.ToString()}");
                }

                if (file.Value.Meta.HasValue == false)
                {
                    throw new Exception($"While parsing files table: file with id {id.ToString()} has null Metadata");
                }

                if (file.Value.PackIndex > 0)
                {
                    // Means that file packed into some pack. So we had to check if file is in pack's size bounds
                    if (vfs._packs.ContainsKey(file.Value.PackIndex) == false)
                        throw new Exception($"While parsing files table: file with id {id.ToString()} referers to pack with index {file.Value.PackIndex} that doesn't exist");

                    if (file.Value.Meta.Value.CompressedSize + file.Value.PackOffset > vfs.MAX_PACK_SIZE)
                        throw new Exception($"While parsing files table: file with id {id.ToString()} packed in pack with index {file.Value.PackIndex} is out of pack's size bounds");
                }

                vfs._files.Add(id, new VFS.File(

                    id,
                    new VFS.Metadata(

                        DateTimeOffset.FromUnixTimeMilliseconds(file.Value.Meta.Value.CreatedTsUtc).UtcDateTime,
                        DateTimeOffset.FromUnixTimeMilliseconds(file.Value.Meta.Value.UpdatedTsUtc).UtcDateTime,
                        file.Value.Meta.Value.UncompressedSize,
                        file.Value.Meta.Value.CompressedSize
                    ),
                    file.Value.SourceRelativePath,
                    (VFSFileFlags)file.Value.Flags,
                    file.Value.PackIndex,
                    file.Value.PackOffset
                ));
            }

            vfs._tableMetadata = new VFS.Metadata(
                DateTimeOffset.FromUnixTimeMilliseconds(ft.Meta.Value.CreatedTsUtc).UtcDateTime,
                DateTimeOffset.FromUnixTimeMilliseconds(ft.Meta.Value.UpdatedTsUtc).UtcDateTime,
                ft.Meta.Value.UncompressedSize,
                ft.Meta.Value.CompressedSize
            );

            if (mode == VFSMode.PACKS)
            {
                // Open packs streams

                Dictionary<byte, SafeFileHandle> packStreams = new Dictionary<byte, SafeFileHandle>();

                foreach (var packIndex in vfs._packs.Keys)
                {
                    string packPath = Path.Combine(vfs._rootFolder, $"PACK{packIndex.ToString("D3")}.PACK");

                    SafeFileHandle handle = System.IO.File.OpenHandle(
                        packPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None);

                    packStreams.Add(packIndex, handle);
                }

                vfs._packsHandles = packStreams.ToFrozenDictionary();
            }

            return vfs;
        }



        /// <summary>
        /// Adds file to filesTable
        /// </summary>
        /// <param name="relativeFilename"></param>
        /// <returns>Guid? file's ID</returns>
        /// <exception cref="FileNotFoundException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        public async Task<Guid> AddFile(string relativeFilename, VFSFileFlags flags = VFSFileFlags.IsCompressed)
        {
            lock (_lockObject)
            {
                if (_isFilesTableLocked)
                    throw new InvalidOperationException("VFS is busy");

                _isFilesTableLocked = true;
            }

            try
            {
                if (relativeFilename == null)
                    throw new ArgumentNullException($"Can't add file \"{relativeFilename}\": file with relative path can't be null");

                Guid guid = Guid.NewGuid();

                string absoluteFilepath = Path.Combine(_rootFolder, relativeFilename);

                FileInfo fileInfo = new FileInfo(absoluteFilepath);

                if (fileInfo.Exists == false)
                {
                    throw new FileNotFoundException($"Can't add file: file with relative path {relativeFilename} doesn't exist");
                }

                if (fileInfo.Length > MAX_PACK_SIZE)
                {
                    if (flags.Has(VFSFileFlags.IsCompressed))
                    {
                        ulong compressedPredictionSize = ZstdSharp.Compressor.GetCompressBoundLong((ulong)fileInfo.Length);
                        if (compressedPredictionSize > MAX_PACK_SIZE)
                        {
                            throw new Exception($"Can't add file: file \"{relativeFilename}\" size ({fileInfo.Length} ; compress bounds: {compressedPredictionSize}) > MAX_PACK_SIZE ({MAX_PACK_SIZE})");
                        }
                        else
                        {
                            _logger.LogWarning($"Packing file \"{relativeFilename}\" might cause an error if it compressed size > MAX_PACK_SIZE ({MAX_PACK_SIZE}). Current compress bounds: {compressedPredictionSize}");
                        }
                    }
                    else
                    {
                        throw new Exception($"Can't add file: file \"{relativeFilename}\" size ({fileInfo.Length}) > MAX_PACK_SIZE ({MAX_PACK_SIZE})");
                    }
                }

                _files.Add(guid, new VFS.File(
                    guid,
                    new VFS.Metadata(
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        (uint)fileInfo.Length,
                        (uint)fileInfo.Length
                    ),
                    relativeFilename,
                    flags,
                    0,
                    0
                ));

                return guid;
            }
            catch
            {
                throw;
            }
            finally
            {
                lock (_lockObject)
                {
                    _isFilesTableLocked = false;
                }
            }
        }

        public void DeleteFile(Guid id)
        {
            lock (_lockObject)
            {
                if (_isFilesTableLocked)
                    throw new InvalidOperationException("VFS is busy");

                _isFilesTableLocked = true;
            }

            try
            {
                if (_files.ContainsKey(id) == false) throw new FileNotFoundException();

                (_files[id] as IFile).Flags = _files[id].Flags.Set(VFSFileFlags.IsDeleted);
            }
            catch
            {
                throw;
            }
            finally
            {
                lock (_lockObject)
                {
                    _isFilesTableLocked = false;
                }
            }
        }




        public VFS.File? GetFile(Guid id)
        {
            lock (_lockObject)
            {
                if (_isFilesTableLocked)
                    throw new InvalidOperationException("VFS is busy");

                _isFilesTableLocked = true;
            }

            try
            {
                if (_files.ContainsKey(id) == false) return null;
                return _files[id];
            }
            catch
            {
                throw;
            }
            finally
            {
                lock (_lockObject)
                {
                    _isFilesTableLocked = false;
                }
            }            
        }

        public Stream GetFileStream(Guid id)
        {
            lock (_lockObject)
            {
                if (_isFilesTableLocked)
                    throw new InvalidOperationException("VFS is busy");

                _isFilesTableLocked = true;
            }

            try
            {
                if (_files.ContainsKey(id) == false) throw new FileNotFoundException();

                var foundedFile = _files[id];

                if (foundedFile.Flags.Has(VFSFileFlags.IsDeleted) == true) throw new IOException("File was deleted");
                if (foundedFile.Flags.Has(VFSFileFlags.IsWrited) == false && _mode == VFSMode.PACKS) throw new IOException("File isn't writed into pack");

                if (_mode == VFSMode.PACKS)
                {
                    var foundedPackHandle = _packsHandles[foundedFile.PackIndex];

                    if (foundedFile.Flags.Has(VFSFileFlags.IsCompressed))
                    {
                        var packStream = new PackReadStream(foundedPackHandle, foundedFile.PackOffset, foundedFile.Metadata.CompressedSize);

                        var boundedStream = new BoundedStream(packStream, foundedFile.Metadata.CompressedSize);

                        return new DecompressionStream(boundedStream, leaveOpen: false);
                    }
                    else
                    {
                        var packStream = new PackReadStream(foundedPackHandle, foundedFile.PackOffset, foundedFile.Metadata.UncompressedSize);

                        return new BoundedStream(packStream, foundedFile.Metadata.UncompressedSize);
                    }
                }
                else
                {
                    return new FileStream(Path.Combine(_rootFolder, foundedFile.SourceRelativePath), FileMode.Open);
                }
            }
            catch
            {
                throw;
            }
            finally
            {
                lock (_lockObject)
                {
                    _isFilesTableLocked = false;
                }
            }
        }

        public FrozenDictionary<Guid, VFS.File> DumpFiles(bool dumpDeletedFiles = false)
        {
            lock (_lockObject)
            {
                if (_isFilesTableLocked)
                    throw new InvalidOperationException("VFS is busy");

                _isFilesTableLocked = true;
            }

            try
            {
                if (dumpDeletedFiles)
                    return _files.ToFrozenDictionary();

                return _files.Where(f => f.Value.Flags.Has(VFSFileFlags.IsDeleted) == false).ToFrozenDictionary();
            }
            catch
            {
                throw;
            }
            finally
            {
                lock (_lockObject)
                {
                    _isFilesTableLocked = false;
                }
            }
        }


        public async Task RunDefragmentation(bool continuePrevious = true)
        {
            if (_mode == VFSMode.PACKS) throw new InvalidOperationException("Can run defragmentation only in SOURCES mode");

            lock (_lockObject)
            {
                if (_isFilesTableLocked)
                    throw new InvalidOperationException("VFS is busy");

                _isFilesTableLocked = true;
            }

            var allAliveFiles = _files
                .Where(f => f.Value.Flags.Has(VFSFileFlags.IsWrited) && f.Value.Flags.Has(VFSFileFlags.IsDeleted) == false);

            var allDeletedFiles = _files
                .Where(f => f.Value.Flags.Has(VFSFileFlags.IsWrited) && f.Value.Flags.Has(VFSFileFlags.IsDeleted) == true);

            try
            {
                Dictionary<Guid, FileChangesRecord> fileChanges = new Dictionary<Guid, FileChangesRecord>();
                Dictionary<byte, (bool exist, uint freeOffset)> packChanges = new Dictionary<byte, (bool, uint)>();

                byte restoredPackIndex = 0;
                var previousDefragPackBufferFile = Directory.EnumerateFiles(_rootFolder, "*.PACK.BUFF");

                if (previousDefragPackBufferFile.Count() > 1)
                    throw new NotSupportedException("Can't restore previous defragmentation state: there is more that 1 buffer pack file");
                else if (previousDefragPackBufferFile.Count() == 1)
                {
                    string numberPart = Path.GetFileNameWithoutExtension(previousDefragPackBufferFile.First()).Substring(4, 3);
                    restoredPackIndex = byte.Parse(numberPart);

                    _logger.LogInformation($"Found previous defragmentation result. Continuing from pack #{numberPart}");
                }

                foreach (byte packIndex in _packs.Keys)
                {
                    fileChanges.Clear();
                    string newPackName = Path.Combine(_rootFolder, $"PACK{packIndex.ToString("D3")}.PACK.BUFF");
                    string oldPackName = Path.Combine(_rootFolder, $"PACK{packIndex.ToString("D3")}.PACK");

                    if (restoredPackIndex != 0)
                    {
                        if (packIndex != restoredPackIndex) continue;
                        // Detele previous pack buffer because it may be corrupt
                        System.IO.File.Delete(newPackName);
                    }

                    _logger.LogInformation($"Processing pack #{packIndex}...");

                    var packAliveFiles = allAliveFiles.Where(f => f.Value.PackIndex == packIndex).OrderBy(f => f.Value.PackOffset);
                    var packDeletedFiles = allDeletedFiles.Where(f => f.Value.PackIndex == packIndex);

                    using (FileStream oldPackStream = new FileStream(oldPackName, FileMode.Open))
                    {
                        using (FileStream newPackStream = new FileStream(newPackName, FileMode.Create))
                        {
                            uint startOffset = 0;
                            uint length = 0;
                            byte[] readed = new byte[length];

                            if (packAliveFiles.Count() == 0)
                            {
                                packChanges.Add(packIndex, (false, 0));
                                continue;
                            }

                            startOffset = packAliveFiles.First().Value.PackOffset;

                            foreach (var file in packAliveFiles)
                            {
                                if (file.Value.PackOffset == startOffset + length)
                                {
                                    // Stride of alive files
                                    fileChanges.Add(file.Key, new FileChangesRecord()
                                    {
                                        PackIndex = packIndex,
                                        PackOffset = (uint)newPackStream.Position + length
                                    });
                                    if (file.Value.Flags.Has(VFSFileFlags.IsCompressed))
                                    {
                                        length += file.Value.Metadata.CompressedSize;
                                    }
                                    else
                                    {
                                        length += file.Value.Metadata.UncompressedSize;
                                    }
                                }
                                else
                                {
                                    oldPackStream.Seek(startOffset, SeekOrigin.Begin);

                                    readed = new byte[length];
                                    await oldPackStream.ReadExactlyAsync(readed);
                                    await oldPackStream.FlushAsync();
                                    await newPackStream.WriteAsync(readed);
                                    await newPackStream.FlushAsync();

                                    fileChanges.Add(file.Key, new FileChangesRecord()
                                    {
                                        PackIndex = packIndex,
                                        PackOffset = (uint)newPackStream.Position
                                    });

                                    startOffset = file.Value.PackOffset;
                                    if (file.Value.Flags.Has(VFSFileFlags.IsCompressed))
                                    {
                                        length = file.Value.Metadata.CompressedSize;
                                    }
                                    else
                                    {
                                        length = file.Value.Metadata.UncompressedSize;
                                    }
                                }
                            }
                            oldPackStream.Seek(startOffset, SeekOrigin.Begin);
                            readed = new byte[length];

                            await oldPackStream.ReadExactlyAsync(readed);
                            await oldPackStream.FlushAsync();
                            await newPackStream.WriteAsync(readed);
                            await newPackStream.FlushAsync();

                            packChanges.Add(packIndex, (true, (uint)newPackStream.Position));
                        }
                    }

                    _logger.LogInformation($"Commiting changes...");

                    foreach (var change in fileChanges)
                    {
                        (_files[change.Key] as VFS.IFile).PackOffset = change.Value.PackOffset;
                    }
                    foreach (var change in packDeletedFiles)
                    {
                        _files.Remove(change.Key);
                    }
                    foreach (var change in packChanges)
                    {
                        if (change.Value.exist == false)
                        {
                            _packs.Remove(change.Key);
                            continue;
                        }
                        (_packs[change.Key] as VFS.IPack).FreeOffset = change.Value.freeOffset;
                    }

                    await writeFilesTableAsync(true);

                    _logger.LogInformation($"Deleting old pack file...");

                    System.IO.File.Delete(oldPackName);
                    if (packChanges[packIndex].exist == false)
                    {
                        System.IO.File.Delete(newPackName);
                    }
                    else
                    {
                        System.IO.File.Move(newPackName, oldPackName);
                    }
                }

                _logger.LogInformation($"Done");
            }
            catch
            {
                throw;
            }
            finally
            {
                lock (_lockObject)
                {
                    _isFilesTableLocked = false;
                }
            }
        }

        public async Task PackAsync(byte compressionLevel = 15)
        {
            if (_mode == VFSMode.PACKS) throw new InvalidOperationException("Can start packing only in SOURCES mode");

            if (compressionLevel < MIN_COMPRESSION_LEVEL) compressionLevel = MIN_COMPRESSION_LEVEL;
            if (compressionLevel > MAX_COMPRESSION_LEVEL) compressionLevel = MAX_COMPRESSION_LEVEL;

            lock (_lockObject)
            {
                if (_isFilesTableLocked)
                    throw new InvalidOperationException("VFS is busy");

                _isFilesTableLocked = true;
            }

            DateTime currentTime = DateTime.UtcNow;

            _logger.LogInformation($"Packing started. Compression level: {compressionLevel}");

            // Алгоритм:
            // Идём от больших файлов к малым

            // Opening all the existing packs
            Dictionary<byte, FileStream> packStreams = new Dictionary<byte, FileStream>();

            // Also we store free offsets of all the packs here so that if an error occurs
            // while writing files, the information about the packs will not be included in the files table
            // Key - pack id; value - (is pack existed before packing, free offset)
            Dictionary<byte, (bool, uint)> packsFreeOffsets = new Dictionary<byte, (bool, uint)>();
            try
            {
                // If we can't open at least one pack file - this is big problem.
                // We need to tell user about that.

                foreach (byte packIndex in _packs.Keys)
                {
                    string packPath = Path.Combine(_rootFolder, $"PACK{packIndex.ToString("D3")}.PACK");

                    FileStream stream = new FileStream(
                        packPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.None);

                    stream.Seek(0, SeekOrigin.End);

                    // If pack has less that 512 bytes free space - close it
                    if (MAX_PACK_SIZE - stream.Position >= 512)
                    {
                        packStreams.Add(packIndex, stream);
                        packsFreeOffsets.Add(packIndex, (true, (uint)stream.Position));
                    }
                    else
                    {
                        stream.Close();
                    }
                }
            }
            catch
            {
                foreach (var ps in packStreams)
                {
                    if (ps.Value != null)
                        await ps.Value.DisposeAsync();
                }

                lock (_lockObject)
                {
                    _isFilesTableLocked = false;
                }

                throw;
            }

            // Iterate through files that added only in file table

            // Sorting files by it's size:
            // If file needs to be compressed - check it's CompressBound, otherwise check it's Metadata.UncompressedSize

            var filesToCompile = _files.Where(
                x => x.Value.PackIndex == 0 &&
                x.Value.Flags.Has(VFSFileFlags.IsDeleted) == false &&
                x.Value.Flags.Has(VFSFileFlags.IsWrited) == false
                ).OrderByDescending(x

                => (x.Value.Flags.Has(VFSFileFlags.IsCompressed) ?
                (ZstdSharp.Compressor.GetCompressBoundLong(x.Value.Metadata.UncompressedSize)) :
                (x.Value.Metadata.UncompressedSize))

                );


            int totalFileCount = filesToCompile.Count();

            _logger.LogInformation($"Files to pack: {filesToCompile.Count()}");

            int counter = 1;

            // Store information that we have to write down to files table after we succesfully done 
            // writing files to packs.

            // If any exceptions occur - we simply not write down that changes to files table
            List<FileChangesRecord> filesToWriteChangedInfo = new List<FileChangesRecord>();

            try
            {
                var dublicates = filesToCompile
                    .GroupBy(f => f.Value.SourceRelativePath)
                    .Where(g => g.Count() > 1);

                if (dublicates.Any())
                {
                    int dublicatesCount = dublicates.Count();
                    int i = 0;
                    _logger.LogWarning($"There are {dublicatesCount} files that refer to the same file names:");
                    foreach (var dublicateName in dublicates)
                    {
                        i++;
                        _logger.LogWarning($"[{i}/{dublicatesCount}]: \"{dublicateName.First().Value.SourceRelativePath}\":");
                        foreach (var dublicateFile in dublicateName)
                        {
                            _logger.LogWarning($"Id: {dublicateFile.Value.Id.ToString()} Disk size: {dublicateFile.Value.Metadata.UncompressedSize} Added UTC: {dublicateFile.Value.Metadata.CreatedUtc.ToString("HH:mm:ss dd/MM/yyyy", CultureInfo.InvariantCulture)} Updated UTC: {dublicateFile.Value.Metadata.UpdatedUtc.ToString("HH:mm:ss dd/MM/yyyy", CultureInfo.InvariantCulture)}");
                        }
                    }

                    throw new OperationCanceledException("Can't pack files that refer to the same file names");
                }


                foreach (var file in filesToCompile)
                {
                    _logger.LogInformation($"[{counter++}/{totalFileCount}] Packing file with id \"{file.Value.Id.ToString()}\" source path: \"{file.Value.SourceRelativePath}\" flags: \"{file.Value.Flags}\":");

                    using (FileStream fs = new FileStream(Path.Combine(_rootFolder, file.Value.SourceRelativePath), FileMode.Open))
                    {
                        uint finalFileSizeToWrite = 0;
                        byte selectedPackIndex = 0;

                        if (file.Value.Flags.Has(VFSFileFlags.IsCompressed))
                        {
                            ulong maxCompressedFilesize = ZstdSharp.Compressor.GetCompressBoundLong(file.Value.Metadata.UncompressedSize);

                            if (maxCompressedFilesize > uint.MaxValue)
                                throw new Exception($"File with id \"{file.Key.ToString()}\" has compressed ({maxCompressedFilesize}) > MAX_PACK_SIZE ({MAX_PACK_SIZE})");

                            finalFileSizeToWrite = (uint)maxCompressedFilesize;
                        }
                        else
                        {
                            finalFileSizeToWrite = file.Value.Metadata.UncompressedSize;
                        }

                        List<byte> packIndicesToClose = new List<byte>();

                        foreach (var ps in packStreams)
                        {
                            if (ps.Value.Position + finalFileSizeToWrite <= MAX_PACK_SIZE)
                            {
                                selectedPackIndex = ps.Key;
                                break;
                            }

                            // If pack has less that 512 bytes free space - close it
                            if (MAX_PACK_SIZE - ps.Value.Position < 512)
                            {
                                await ps.Value.DisposeAsync();
                                await ps.Value.FlushAsync();
                                packIndicesToClose.Add(ps.Key);
                            }
                        }

                        foreach (var i in packIndicesToClose)
                        {
                            packStreams.Remove(i);
                        }

                        if (selectedPackIndex == 0)
                        {
                            // We must create a new pack for this file because there are no packs with free space that can fit file

                            byte newPackIndex = getNewPackIndex(packsFreeOffsets.Keys.ToHashSet());

                            if (newPackIndex == 0)
                                throw new Exception($"Can't compile file with id \"{file.Key.ToString()}\": Maximum amount of packs exceeded");

                            var newPack = new FileStream(
                                Path.Combine(_rootFolder, $"PACK{newPackIndex.ToString("D3")}.PACK"),
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.None);

                            packStreams.Add(newPackIndex, newPack);
                            packsFreeOffsets.Add(newPackIndex, (false, 0));
                            //_packs.Add(newPackIndex, new Pack(newPackIndex, 0));

                            selectedPackIndex = newPackIndex;
                        }

                        if (selectedPackIndex == 0)
                            throw new Exception($"Can't compile file with id \"{file.Key.ToString()}\": Maximum amount of packs exceeded");

                        var selectedPackStream = packStreams[selectedPackIndex];

                        long startPackOffset = selectedPackStream.Position;
                        uint writedSize = 0;

                        FileChangesRecord fileChangesToAdd = new FileChangesRecord()
                        {
                            Id = file.Key,
                            Flags = file.Value.Flags,
                            PackIndex = selectedPackIndex,
                            PackOffset = (uint)(startPackOffset)
                        };

                        if (file.Value.Flags.Has(VFSFileFlags.IsCompressed))
                        {
                            using (var compressionStream = new CompressionStream(selectedPackStream, _compressor, leaveOpen: true, preserveCompressor: true))
                            {
                                await fs.CopyToAsync(compressionStream);
                                await compressionStream.FlushAsync();
                                await selectedPackStream.FlushAsync();
                            }

                            writedSize = (uint)(selectedPackStream.Position - startPackOffset);
                            fileChangesToAdd.CompressedSize = writedSize;
                        }
                        else
                        {
                            await fs.CopyToAsync(selectedPackStream);
                            await selectedPackStream.FlushAsync();
                            writedSize = file.Value.Metadata.UncompressedSize;
                            fileChangesToAdd.UncompressedSize = writedSize;
                        }

                        packsFreeOffsets[selectedPackIndex] = (packsFreeOffsets[selectedPackIndex].Item1, packsFreeOffsets[selectedPackIndex].Item2 + writedSize);
                        filesToWriteChangedInfo.Add(fileChangesToAdd);

                        _logger.LogInformation($"Packed. Total writed size: {writedSize}");
                    }
                }

                // If everething okay - save new filetable

                _logger.LogInformation($"IO success. Writing changes...");

                foreach (var packChanges in packsFreeOffsets)
                {
                    // If this is new pack - just creating it. Otherwise - updating existing
                    if (packChanges.Value.Item1 == false)
                    {
                        _packs.Add(packChanges.Key, new Pack(packChanges.Key, packChanges.Value.Item2));
                    }
                    else
                    {
                        (_packs[packChanges.Key] as IPack).FreeOffset = packChanges.Value.Item2;
                    }
                }

                foreach (var fileChanges in filesToWriteChangedInfo)
                {
                    (_files[fileChanges.Id] as IFile).Flags = _files[fileChanges.Id].Flags.Set(VFSFileFlags.IsWrited);

                    (_files[fileChanges.Id] as IFile).PackIndex = fileChanges.PackIndex;
                    (_files[fileChanges.Id] as IFile).PackOffset = fileChanges.PackOffset;

                    if (fileChanges.CompressedSize == 0)
                    {
                        ((_files[fileChanges.Id] as IFile).Metadata as IMetadata).UncompressedSize = fileChanges.UncompressedSize;
                    }
                    else if (fileChanges.UncompressedSize == 0)
                    {
                        ((_files[fileChanges.Id] as IFile).Metadata as IMetadata).CompressedSize = fileChanges.CompressedSize;
                    }
                }

                // Also removing all deleted files that are not writed

                foreach (var file in _files.Where(f => f.Value.Flags.Has(VFSFileFlags.IsDeleted) && f.Value.Flags.Has(VFSFileFlags.IsWrited) == false))
                {
                    _files.Remove(file.Key);
                }

                await writeFilesTableAsync(true);
            }
            catch
            {
                throw;
            }
            finally
            {
                foreach (var ps in packStreams)
                {
                    if (ps.Value != null)
                    {
                        await ps.Value.FlushAsync();
                        await ps.Value.DisposeAsync();
                    }
                }

                packStreams.Clear();

                lock (_lockObject)
                {
                    _isFilesTableLocked = false;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">When VFS is busy</exception>
        public async Task CommitChangesAsync()
        {
            // Also removing all deleted files that are not writed

            foreach (var file in _files.Where(f => f.Value.Flags.Has(VFSFileFlags.IsDeleted) && f.Value.Flags.Has(VFSFileFlags.IsWrited) == false))
            {
                _files.Remove(file.Key);
            }

            await writeFilesTableAsync();
        }

       

        

        #endregion


        /// <summary>
        /// Gets you next byte value - pack index.
        /// If there are 255 packs - it returns 0
        /// </summary>
        /// <returns>New pack index</returns>
        private byte getNewPackIndex(HashSet<byte>? existingButNotSavedInTablePacks = null)
        {
            for (byte i = 1; i < byte.MaxValue; i++)
            {
                if (_packs.ContainsKey(i) == false)
                {
                    if (existingButNotSavedInTablePacks == null) return i;
                    if (existingButNotSavedInTablePacks.Contains(i) == false) return i;
                }
            }
            return 0;
        }

        private async Task writeFilesTableAsync(bool force = false)
        {
            if (force == false)
            {
                lock (_lockObject)
                {
                    if (_isFilesTableLocked)
                        throw new InvalidOperationException("VFS is busy");

                    _isFilesTableLocked = true;
                }
            }


            var builder = new FlatBufferBuilder(1024);

            Offset<PGPEngine.VFS.File>[] filesOffsets = new Offset<PGPEngine.VFS.File>[_files.Count];

            int index = 0;
            long totalCompressedSize = 0;
            long totalUncompressedSize = 0;
            foreach (var f in _files.Values)
            {
                (f as IFile).Flags = f.Flags.Set(VFSFileFlags.IsSavedInFilesTable);
                var sourceRelativePath = builder.CreateSharedString(f.SourceRelativePath);

                PGPEngine.VFS.File.StartFile(builder);

                (long a, long b) guidLongs = Utils.ConvertGUID(f.Id);
                PGPEngine.VFS.File.AddId(builder, PGPEngine.Shared.GUID.CreateGUID(builder, guidLongs.a, guidLongs.b));
                PGPEngine.VFS.File.AddSourceRelativePath(builder, sourceRelativePath);
                PGPEngine.VFS.File.AddPackIndex(builder, f.PackIndex);
                PGPEngine.VFS.File.AddFlags(builder, (byte)f.Flags);
                PGPEngine.VFS.File.AddPackOffset(builder, f.PackOffset);
                PGPEngine.VFS.File.AddMeta(builder, PGPEngine.VFS.Metadata.CreateMetadata(builder,
                    (long)((f.Metadata.CreatedUtc - DateTime.UnixEpoch).TotalMilliseconds),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    f.Metadata.CompressedSize,
                    f.Metadata.UncompressedSize
                ));

                totalCompressedSize += f.Metadata.CompressedSize;
                totalUncompressedSize += f.Metadata.UncompressedSize;

                var fileOffset = PGPEngine.VFS.File.EndFile(builder);
                filesOffsets[index] = fileOffset;

                index++;
            }

            var filesOffset = PGPEngine.VFS.FilesTable.CreateFilesVector(builder, filesOffsets);


            Offset<PGPEngine.VFS.Pack>[] packsOffsets = new Offset<PGPEngine.VFS.Pack>[_packs.Count];

            index = 0;

            foreach (var p in _packs.Values)
            {
                PGPEngine.VFS.Pack.StartPack(builder);
                PGPEngine.VFS.Pack.AddIndex(builder, p.Index);
                PGPEngine.VFS.Pack.AddFreeOffset(builder, p.FreeOffset);

                var packOffset = PGPEngine.VFS.Pack.EndPack(builder);
                packsOffsets[index] = packOffset;

                index++;
            }

            var packsOffset = PGPEngine.VFS.FilesTable.CreatePacksVector(builder, packsOffsets);


            PGPEngine.VFS.FilesTable.StartFilesTable(builder);
            
            PGPEngine.VFS.FilesTable.AddFiles(builder, filesOffset);
            PGPEngine.VFS.FilesTable.AddPacks(builder, packsOffset);
            PGPEngine.VFS.FilesTable.AddMeta(builder, PGPEngine.VFS.Metadata.CreateMetadata(builder,
                (long)((_tableMetadata.CreatedUtc - DateTime.UnixEpoch).TotalMilliseconds),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                0,
                0
                ));

            PGPEngine.VFS.FilesTable.AddMaxPackSize(builder, MAX_PACK_SIZE);

            var filesTableOffset = PGPEngine.VFS.FilesTable.EndFilesTable(builder);

            builder.Finish(filesTableOffset.Value);
            byte[] finalBytes = builder.SizedByteArray();

            System.IO.File.WriteAllBytes(_filesTablePath, finalBytes);

            if (force == false)
            {
                lock (_lockObject)
                {
                    _isFilesTableLocked = false;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _compressor.Dispose();

            foreach (var ph in _packsHandles)
            {
                ph.Value.Dispose();
            }

            await CommitChangesAsync();
        }
    }
}
