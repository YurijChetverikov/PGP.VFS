namespace PGP.VFS
{
    [Flags]
    public enum VFSFileFlags : byte
    {
        None = 0b0000_0000,
        IsCompressed = 0b0000_0001,
        IsDeleted = 0b0000_0010,
        IsWrited = 0b0000_0100,
        IsSavedInFilesTable = 0b0000_1000,
        Reserved4 = 0b0001_0000,
        Reserved5 = 0b0010_0000,
        Reserved6 = 0b0100_0000,
        Reserved7 = 0b1000_0000,
    }
}
