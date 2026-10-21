using System.Runtime.InteropServices;

namespace HostPinger.Benchmarks.Infrastructure
{
    /// <summary>
    /// Puts a file into the operating system's page cache, or takes it out, for the benchmarks that
    /// measure a read against a cold cache as well as a warm one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cold is the case that mattered in production. A count over the attempts took a fifth of a
    /// second with the file in memory and about forty seconds without it, and the graph of a two-month
    /// outage most of a minute; an installed service's database is usually not in memory, since the
    /// rest of the machine pushes it out between reads. A warm benchmark alone would have shown none
    /// of that.
    /// </para>
    /// <para>
    /// Linux only: posix_fadvise with POSIX_FADV_DONTNEED drops a file's clean pages, after an fsync
    /// has made them all clean. It cannot reach a cache below the operating system, such as a virtual
    /// machine's host caching its disk, so on a CI runner cold means as cold as the guest can make it.
    /// </para>
    /// </remarks>
    internal static class PageCache
    {
        private const int PosixFadviseDontNeed = 4;

        public static void Evict(string path)
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("Dropping a file from the page cache is only implemented on Linux.");
            }

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            var descriptor = (int)handle.DangerousGetHandle();
            if (Fsync(descriptor) != 0)
            {
                throw new IOException($"fsync of {path} failed with errno {Marshal.GetLastPInvokeError()}.");
            }

            var error = PosixFadvise(descriptor, 0, 0, PosixFadviseDontNeed);
            if (error != 0)
            {
                throw new IOException($"posix_fadvise of {path} failed with errno {error}.");
            }
        }

        public static void Load(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            while (stream.Read(buffer) > 0)
            {
            }
        }

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        private static extern int Fsync(int fd);

        /// <summary>Returns the error number itself rather than setting errno.</summary>
        [DllImport("libc", EntryPoint = "posix_fadvise")]
        private static extern int PosixFadvise(int fd, long offset, long length, int advice);
    }
}
