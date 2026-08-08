using System.Net;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Turns a host name into addresses. An interface only because <see cref="Dns"/> is static, and
    /// what <see cref="PingSender"/> decides on the way a lookup fails is worth testing.
    /// </summary>
    public interface INameResolver
    {
        /// <summary>
        /// Resolves the name, or throws. Cancellation is best effort: the platform resolvers cannot
        /// be interrupted, so the token bounds how long the caller waits rather than the lookup.
        /// </summary>
        Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
    }

    /// <summary>The real resolver. Deliberately holds no logic of its own.</summary>
    public sealed class SystemNameResolver : INameResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Dns.GetHostAddressesAsync(host, cancellationToken);
    }
}
