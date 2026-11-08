using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Text;

namespace PGP.VFS
{
    public class PackReadStream : Stream
    {
        private readonly SafeFileHandle _handle;
        private readonly long _startOffset;
        private readonly long _length;
        private long _position;

        public PackReadStream(SafeFileHandle handle, long startOffset, long length)
        {
            _handle = handle;
            _startOffset = startOffset;
            _length = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException("Changing cursor position is not supported");
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = _length - _position;
            if (remaining <= 0) return 0;

            int toRead = (int)Math.Min(buffer.Length, remaining);
            // Читаем напрямую с нужного оффсета, не меняя Position самого файла
            int read = RandomAccess.Read(_handle, buffer.Slice(0, toRead), _startOffset + _position);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        protected override void Dispose(bool disposing)
        {
            // ВНИМАНИЕ! Мы НЕ закрываем _handle (SafeFileHandle), 
            // потому что он принадлежит всей VFS и используется другими потоками.
            // Мы просто вызываем базовый Dispose, чтобы освободить ресурсы самого Stream.
            base.Dispose(disposing);
        }
    }
}
