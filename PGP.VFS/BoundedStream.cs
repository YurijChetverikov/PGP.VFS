namespace PGP.VFS
{
    internal class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _position;

        internal BoundedStream(Stream inner, long length)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _position;
            if (remaining <= 0) return 0;
            int toRead = (int)Math.Min(count, remaining);
            int read = _inner.Read(buffer, offset, toRead);
            _position += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
